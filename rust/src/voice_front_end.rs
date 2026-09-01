//! The voice front end: features, phonemes, wake words, and the loop.
//!
//! THIS IS THE PART THAT MUST MATCH A MODEL EXACTLY. Everywhere else in the
//! port a reasonable interpretation is fine; here a filterbank a fraction off
//! what a model was trained on does not fail - it transcribes confident
//! nonsense, and the blame lands on the model.
//!
//! THE FOUR THAT ARE ALWAYS WRONG, verified against the C# fixtures rather than
//! reasoned about:
//!
//!   * Kaldi's default window is POVEY, not Hann: `(0.5 - 0.5cos)^0.85`. The
//!     exponent is the whole difference and it is easy to miss in a formula.
//!
//!   * `high_freq = -400` does NOT mean 400 Hz. A negative value is an OFFSET
//!     FROM NYQUIST, so at 16 kHz it means 7600. Reading it as a frequency puts
//!     every mel bin in the wrong place and the model hears a voice with no top
//!     end.
//!
//!   * `snip_edges = false` CENTRES the frames and MIRRORS at the boundaries.
//!     Zero-padding instead is the obvious implementation and puts a brightness
//!     ramp on the first and last frame of every utterance - a sharp step is
//!     broadband energy the model reads as a consonant.
//!
//!   * The log floor is `f32::EPSILON`, 1.19e-7 - NOT `f64::EPSILON`, which is
//!     about ten million times smaller. The floor changes every silent frame's
//!     value.

use std::collections::{HashMap, HashSet};
use std::f64::consts::PI;

// ─────────────────────────────────────────────────────────────────────────────
// Features

/// How the filterbank is configured. Kaldi's defaults, named.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct KaldiFbankOptions {
    pub sample_rate_hz: u32,
    pub frame_length_ms: f64,
    pub frame_shift_ms: f64,
    pub mel_bins: usize,
    pub low_freq: f64,
    /// NEGATIVE MEANS AN OFFSET FROM NYQUIST. -400 at 16 kHz is 7600 Hz, not
    /// 400. Reading it as a frequency puts every mel bin in the wrong place.
    pub high_freq: f64,
    pub pre_emphasis: f64,
    pub dither: f64,
    /// False CENTRES the frames and MIRRORS at the boundaries. Zero-padding
    /// instead puts a brightness ramp on the first and last frame of every
    /// utterance.
    pub snip_edges: bool,
    pub remove_dc_offset: bool,
}

impl Default for KaldiFbankOptions {
    fn default() -> Self {
        Self {
            sample_rate_hz: 16_000,
            frame_length_ms: 25.0,
            frame_shift_ms: 10.0,
            mel_bins: 80,
            low_freq: 20.0,
            high_freq: -400.0,
            pre_emphasis: 0.97,
            dither: 0.0,
            snip_edges: false,
            remove_dc_offset: true,
        }
    }
}

/// Kaldi-compatible log-mel filterbank features.
///
/// "Compatible" is the whole requirement. A model trained on Kaldi features and
/// fed something almost-Kaldi does not error; it degrades, and the degradation
/// looks like a bad microphone.
#[derive(Debug, Clone)]
pub struct KaldiFbank {
    pub options: KaldiFbankOptions,
}

impl KaldiFbank {
    /// `f32::EPSILON`. Not `f64::EPSILON`, which is about ten million times
    /// smaller and changes every silent frame's value.
    pub const LOG_FLOOR: f64 = f32::EPSILON as f64;

    pub fn new(options: KaldiFbankOptions) -> Self {
        Self { options }
    }

    pub fn frame_length(&self) -> usize {
        (self.options.sample_rate_hz as f64 * self.options.frame_length_ms / 1000.0).round() as usize
    }

    pub fn frame_shift(&self) -> usize {
        (self.options.sample_rate_hz as f64 * self.options.frame_shift_ms / 1000.0).round() as usize
    }

    /// The next power of two at or above the frame length.
    pub fn fft_size(&self) -> usize {
        let mut n = 1usize;
        while n < self.frame_length() {
            n <<= 1;
        }
        n
    }

    /// The resolved upper edge, with the negative-means-offset rule applied.
    pub fn high_frequency(&self) -> f64 {
        let nyquist = self.options.sample_rate_hz as f64 / 2.0;
        if self.options.high_freq <= 0.0 {
            nyquist + self.options.high_freq
        } else {
            self.options.high_freq
        }
    }

    /// The POVEY window: `(0.5 - 0.5cos)^0.85`.
    ///
    /// The 0.85 exponent is Kaldi's default and is the entire difference from a
    /// Hann window. Missing it is subtle enough to survive a review and large
    /// enough to move every feature value.
    pub fn povey_window(length: usize) -> Vec<f64> {
        (0..length)
            .map(|i| {
                (0.5 - 0.5 * (2.0 * PI * i as f64 / (length as f64 - 1.0)).cos()).powf(0.85)
            })
            .collect()
    }

    pub fn mel_of(hz: f64) -> f64 {
        1127.0 * (1.0 + hz / 700.0).ln()
    }

    pub fn hz_of(mel: f64) -> f64 {
        700.0 * ((mel / 1127.0).exp() - 1.0)
    }

    /// The triangular mel filters, as (start bin, weights).
    ///
    /// Kaldi's filters are built on the MEL scale with equal spacing there, and
    /// each triangle spans from the previous centre to the next - so
    /// neighbouring filters OVERLAP BY HALF. Non-overlapping filters are a
    /// common simplification and they lose energy between bins.
    pub fn mel_banks(&self) -> Vec<(usize, Vec<f64>)> {
        let fft_size = self.fft_size();
        let bins = fft_size / 2 + 1;
        let bin_hz = self.options.sample_rate_hz as f64 / fft_size as f64;
        let low_mel = Self::mel_of(self.options.low_freq);
        let high_mel = Self::mel_of(self.high_frequency());
        let step = (high_mel - low_mel) / (self.options.mel_bins as f64 + 1.0);

        (0..self.options.mel_bins)
            .map(|m| {
                let left = Self::hz_of(low_mel + m as f64 * step);
                let centre = Self::hz_of(low_mel + (m + 1) as f64 * step);
                let right = Self::hz_of(low_mel + (m + 2) as f64 * step);
                let mut start = usize::MAX;
                let mut weights = Vec::new();
                for b in 0..bins {
                    let hz = b as f64 * bin_hz;
                    if hz <= left || hz >= right {
                        continue;
                    }
                    if start == usize::MAX {
                        start = b;
                    }
                    weights.push(if hz <= centre {
                        (hz - left) / (centre - left)
                    } else {
                        (right - hz) / (right - centre)
                    });
                }
                (if start == usize::MAX { 0 } else { start }, weights)
            })
            .collect()
    }

    /// Splits into frames, MIRRORING at the edges when `snip_edges` is false.
    ///
    /// Mirroring, not zero-padding. A zero-padded first frame has a sharp step
    /// at its start, which is broadband energy the model reads as a consonant.
    pub fn frames(&self, samples: &[f64]) -> Vec<Vec<f64>> {
        if samples.is_empty() {
            return Vec::new();
        }
        let length = self.frame_length();
        let shift = self.frame_shift();
        let n = samples.len() as isize;
        let snip = self.options.snip_edges;

        let read = |i: isize| -> f64 {
            if i >= 0 && i < n {
                return samples[i as usize];
            }
            if snip {
                return 0.0;
            }
            if n == 1 {
                return samples[0];
            }
            // Reflect. `-1` maps to sample 1, not sample 0, so the boundary
            // sample is not duplicated - duplicating it flattens the first
            // derivative and shows as a click in the reconstructed signal.
            let mut j = if i < 0 { -i } else { 2 * (n - 1) - i };
            while j < 0 || j >= n {
                j = if j < 0 { -j } else { 2 * (n - 1) - j };
            }
            samples[j as usize]
        };

        let count = if snip {
            if samples.len() < length {
                0
            } else {
                (samples.len() - length) / shift + 1
            }
        } else {
            ((samples.len() as f64 / shift as f64).round() as usize).max(1)
        };
        let offset: isize = if snip { 0 } else { -((length / 2) as isize) };

        (0..count)
            .map(|f| {
                (0..length)
                    .map(|i| read((f * shift) as isize + offset + i as isize))
                    .collect()
            })
            .collect()
    }

    /// One frame's log-mel energies.
    ///
    /// The order is fixed and each step depends on the last: DC removal, then
    /// pre-emphasis, then the window. Pre-emphasising before removing DC
    /// amplifies an offset into the first sample; windowing before pre-emphasis
    /// applies the filter across the taper.
    pub fn frame_features(
        &self,
        frame: &[f64],
        window: &[f64],
        banks: &[(usize, Vec<f64>)],
    ) -> Vec<f64> {
        let mut work = frame.to_vec();
        if self.options.remove_dc_offset {
            let mean = work.iter().sum::<f64>() / work.len() as f64;
            for v in work.iter_mut() {
                *v -= mean;
            }
        }
        if self.options.pre_emphasis > 0.0 {
            // BACKWARDS, so each sample sees the ORIGINAL previous one.
            // Forwards, the second sample is filtered against an
            // already-filtered first.
            for i in (1..work.len()).rev() {
                work[i] -= self.options.pre_emphasis * work[i - 1];
            }
            work[0] -= self.options.pre_emphasis * work[0];
        }
        for (i, w) in window.iter().enumerate() {
            work[i] *= w;
        }

        let power = Self::power_spectrum(&work, self.fft_size());
        banks
            .iter()
            .map(|(start, weights)| {
                let sum: f64 = weights
                    .iter()
                    .enumerate()
                    .map(|(i, w)| power[start + i] * w)
                    .sum();
                sum.max(Self::LOG_FLOOR).ln()
            })
            .collect()
    }

    /// The power spectrum, by a radix-2 FFT.
    ///
    /// Real input, so only the first `N/2+1` bins are kept - the rest are the
    /// conjugate mirror and carry no information. Keeping them all would double
    /// the energy in every mel bin that spans the midpoint.
    pub fn power_spectrum(frame: &[f64], fft_size: usize) -> Vec<f64> {
        let mut re = vec![0.0f64; fft_size];
        let mut im = vec![0.0f64; fft_size];
        for (i, v) in frame.iter().take(fft_size).enumerate() {
            re[i] = *v;
        }

        // Bit-reversal permutation.
        let mut j = 0usize;
        for i in 1..fft_size {
            let mut bit = fft_size >> 1;
            while j & bit != 0 {
                j ^= bit;
                bit >>= 1;
            }
            j ^= bit;
            if i < j {
                re.swap(i, j);
                im.swap(i, j);
            }
        }

        let mut len = 2usize;
        while len <= fft_size {
            let angle = -2.0 * PI / len as f64;
            let (wr, wi) = (angle.cos(), angle.sin());
            let mut i = 0usize;
            while i < fft_size {
                let (mut cr, mut ci) = (1.0f64, 0.0f64);
                for k in 0..len / 2 {
                    let (ur, ui) = (re[i + k], im[i + k]);
                    let vr = re[i + k + len / 2] * cr - im[i + k + len / 2] * ci;
                    let vi = re[i + k + len / 2] * ci + im[i + k + len / 2] * cr;
                    re[i + k] = ur + vr;
                    im[i + k] = ui + vi;
                    re[i + k + len / 2] = ur - vr;
                    im[i + k + len / 2] = ui - vi;
                    let nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
                i += len;
            }
            len <<= 1;
        }

        (0..fft_size / 2 + 1)
            .map(|b| re[b] * re[b] + im[b] * im[b])
            .collect()
    }

    pub fn compute(&self, samples: &[f64]) -> Vec<Vec<f64>> {
        let window = Self::povey_window(self.frame_length());
        let banks = self.mel_banks();
        self.frames(samples)
            .iter()
            .map(|f| self.frame_features(f, &window, &banks))
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Text in

/// Splits text into sentences.
///
/// ABBREVIATIONS ARE THE WHOLE PROBLEM. "Dr. Nkosi arrived." is one sentence and
/// a splitter that breaks on every full stop makes it two - which a
/// text-to-speech engine then reads with a pause in the wrong place, every time.
pub struct SentenceSplitter;

impl SentenceSplitter {
    /// Common abbreviations that end in a full stop and do not end a sentence.
    const ABBREVIATIONS: &'static [&'static str] = &[
        "mr", "mrs", "ms", "dr", "prof", "rev", "hon", "st", "jr", "sr", "vs",
        "etc", "eg", "ie", "no", "fig", "approx", "dept", "univ",
    ];

    pub fn split(text: &str) -> Vec<String> {
        let chars: Vec<char> = text.chars().collect();
        let mut out = Vec::new();
        let mut current = String::new();
        let mut i = 0usize;
        while i < chars.len() {
            let c = chars[i];
            current.push(c);
            i += 1;
            if !matches!(c, '.' | '!' | '?' | '。') {
                continue;
            }
            // A run of terminators is ONE break - "What?!" is one sentence.
            while i < chars.len() && matches!(chars[i], '.' | '!' | '?') {
                current.push(chars[i]);
                i += 1;
            }
            // No break without whitespace after it: "3.14" and "example.com"
            // are not two sentences.
            if let Some(next) = chars.get(i) {
                if !next.is_whitespace() {
                    continue;
                }
            }
            let trimmed = current.trim_end();
            let last_word: String = trimmed
                .trim_end_matches('.')
                .chars()
                .rev()
                .take_while(|c| c.is_alphabetic())
                .collect::<Vec<_>>()
                .into_iter()
                .rev()
                .collect();
            if trimmed.ends_with('.')
                && Self::ABBREVIATIONS.contains(&last_word.to_lowercase().as_str())
            {
                continue;
            }
            // A single capital before the stop is an initial - "J. M. Coetzee".
            if last_word.len() == 1 && last_word.chars().all(|c| c.is_uppercase()) {
                continue;
            }
            out.push(current.trim().to_string());
            current.clear();
        }
        if !current.trim().is_empty() {
            out.push(current.trim().to_string());
        }
        out
    }
}

/// One run of text in a single script.
#[derive(Debug, Clone, PartialEq)]
pub struct LanguageSpan {
    pub text: String,
    pub script: String,
    pub start: usize,
}

/// Splits text by SCRIPT, so each run can go to the right voice.
///
/// A sentence mixing Latin and Ge'ez read entirely by a Latin voice is
/// unintelligible for half its length, and this is the common case in the
/// languages this is for - a loanword or a name in the middle of a sentence.
pub struct LanguageSpanSplitter;

impl LanguageSpanSplitter {
    pub fn script_of(ch: char) -> &'static str {
        let c = ch as u32;
        match c {
            0x1200..=0x137F => "Ethiopic",
            0x0600..=0x06FF => "Arabic",
            0x0400..=0x04FF => "Cyrillic",
            0x0900..=0x097F => "Devanagari",
            0x4E00..=0x9FFF => "Han",
            0x3040..=0x30FF => "Kana",
            0xAC00..=0xD7AF => "Hangul",
            _ if ch.is_alphabetic() && ch.is_ascii() => "Latin",
            _ if ch.is_alphabetic() => "Latin",
            _ => "Common",
        }
    }

    pub fn split(text: &str) -> Vec<LanguageSpan> {
        let mut out = Vec::new();
        let mut current = String::new();
        let mut script = String::new();
        let mut start = 0usize;
        let mut index = 0usize;
        for ch in text.chars() {
            let s = Self::script_of(ch);
            // "Common" - spaces and punctuation - JOINS the run it is in rather
            // than starting a new one. Splitting on every space would produce a
            // span per word and lose the point of spans entirely.
            if s != "Common" && !script.is_empty() && s != script {
                out.push(LanguageSpan {
                    text: std::mem::take(&mut current),
                    script: script.clone(),
                    start,
                });
                start = index;
            }
            if s != "Common" {
                script = s.to_string();
            }
            current.push(ch);
            index += ch.len_utf8();
        }
        if !current.is_empty() {
            out.push(LanguageSpan {
                text: current,
                script: if script.is_empty() { "Common".into() } else { script },
                start,
            });
        }
        out
    }
}

/// Turns text into phonemes.
pub trait Phonemizer {
    fn is_available(&self) -> bool;
    fn phonemize(&self, text: &str, language: &str) -> String;
}

/// Passes text through unchanged.
///
/// The right answer for a model whose front end takes GRAPHEMES, which several
/// do. Named so that choosing it is a decision rather than a fallback nobody
/// noticed.
#[derive(Debug, Default, Clone, Copy)]
pub struct PassthroughPhonemizer;

impl Phonemizer for PassthroughPhonemizer {
    fn is_available(&self) -> bool {
        true
    }
    fn phonemize(&self, text: &str, _language: &str) -> String {
        text.to_string()
    }
}

/// espeak-ng, OUT OF PROCESS.
///
/// Out of process because espeak is GPL. Linking it would put this whole crate
/// under the GPL; running it as a program and reading its output does not. That
/// is a licensing constraint, not a design preference, and it is why this takes
/// a closure rather than a binding.
pub struct EspeakPhonemizer {
    run: Option<Box<dyn Fn(&str, &str) -> String + Send + Sync>>,
}

impl EspeakPhonemizer {
    pub fn new(run: Option<Box<dyn Fn(&str, &str) -> String + Send + Sync>>) -> Self {
        Self { run }
    }

    /// Strips espeak's `(xx)` language-switch markers.
    ///
    /// espeak emits them when it decides a word belongs to another language, and
    /// they are not phonemes - a model fed them pronounces the brackets.
    pub fn clean(raw: &str) -> String {
        let mut out = String::with_capacity(raw.len());
        let mut depth = 0usize;
        for ch in raw.chars() {
            match ch {
                '(' => depth += 1,
                ')' if depth > 0 => depth -= 1,
                _ if depth == 0 => out.push(ch),
                _ => {}
            }
        }
        out.split_whitespace().collect::<Vec<_>>().join(" ")
    }
}

impl Phonemizer for EspeakPhonemizer {
    fn is_available(&self) -> bool {
        self.run.is_some()
    }
    fn phonemize(&self, text: &str, language: &str) -> String {
        match &self.run {
            Some(run) => Self::clean(&run(text, language)),
            None => text.to_string(),
        }
    }
}

/// espeak with its own data directory, when a host has one.
pub struct NativeEspeakPhonemizer {
    inner: EspeakPhonemizer,
    pub data_path: String,
}

impl NativeEspeakPhonemizer {
    pub fn new(
        run: Option<Box<dyn Fn(&str, &str) -> String + Send + Sync>>,
        data_path: String,
    ) -> Self {
        Self { inner: EspeakPhonemizer::new(run), data_path }
    }

    pub fn has_data(&self) -> bool {
        !self.data_path.is_empty()
    }
}

impl Phonemizer for NativeEspeakPhonemizer {
    fn is_available(&self) -> bool {
        self.inner.is_available()
    }
    fn phonemize(&self, text: &str, language: &str) -> String {
        self.inner.phonemize(text, language)
    }
}

/// Where a pronunciation came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RespellingSource {
    /// The shipped dictionary.
    Lexicon,
    /// Somebody corrected it on this device. Beats everything else.
    Personal,
    /// A rule for a language's loanwords.
    Loanword,
    /// Nothing knew it; the phonemizer guessed.
    Guessed,
}

/// A pronunciation, and where it came from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Respelling {
    pub word: String,
    pub pronunciation: String,
    pub source: RespellingSource,
}

/// Rewrites a word so a voice says it correctly.
///
/// THE ORDER OF SOURCES IS THE DESIGN: personal beats loanword beats lexicon.
/// Somebody who has corrected how their own name is said must not be overruled
/// by a dictionary, ever - that correction is the single most valuable thing a
/// person will ever teach this.
#[derive(Debug, Default)]
pub struct Respeller {
    personal: HashMap<String, String>,
    lexicon: HashMap<String, String>,
    loanwords: HashMap<String, String>,
}

impl Respeller {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn learn(&mut self, word: &str, pronunciation: &str) {
        let key = word.trim().to_lowercase();
        if !key.is_empty() {
            self.personal.insert(key, pronunciation.to_string());
        }
    }

    pub fn add_lexicon<I: IntoIterator<Item = (String, String)>>(&mut self, entries: I) {
        for (k, v) in entries {
            self.lexicon.insert(k.to_lowercase(), v);
        }
    }

    pub fn add_loanwords<I: IntoIterator<Item = (String, String)>>(&mut self, entries: I) {
        for (k, v) in entries {
            self.loanwords.insert(k.to_lowercase(), v);
        }
    }

    pub fn lookup(&self, word: &str) -> Option<Respelling> {
        let key = word.trim().to_lowercase();
        for (map, source) in [
            (&self.personal, RespellingSource::Personal),
            (&self.loanwords, RespellingSource::Loanword),
            (&self.lexicon, RespellingSource::Lexicon),
        ] {
            if let Some(p) = map.get(&key) {
                return Some(Respelling {
                    word: word.to_string(),
                    pronunciation: p.clone(),
                    source,
                });
            }
        }
        None
    }

    /// Rewrites a whole line, leaving unknown words alone.
    ///
    /// Punctuation is stripped for the LOOKUP and put back afterwards, so
    /// "Nkosi," matches "nkosi" and keeps its comma.
    pub fn apply(&self, text: &str) -> String {
        text.split_inclusive(char::is_whitespace)
            .map(|token| {
                let core: String = token
                    .chars()
                    .filter(|c| c.is_alphanumeric())
                    .collect();
                match self.lookup(&core) {
                    Some(found) if !core.is_empty() => token.replace(&core, &found.pronunciation),
                    _ => token.to_string(),
                }
            })
            .collect()
    }
}

/// A word somebody taught this device.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LearnedWord {
    pub word: String,
    pub pronunciation: String,
    pub times_confirmed: u32,
    pub last_used_at_ms: u64,
}

/// How confident the device is about a learned word.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LearningState {
    /// Heard once. Used, and easily overridden.
    Provisional,
    /// Confirmed more than once. Treated as correct.
    Established,
    /// Somebody corrected it back. Never offered again.
    Rejected,
}

/// What this device has learned about how words are said HERE.
///
/// ON DEVICE ONLY. A pronunciation is a fact about a person - their name, their
/// street, their family - and a corrections database that left the device would
/// be a map of who somebody knows.
#[derive(Debug, Default)]
pub struct PersonalRespellings {
    words: HashMap<String, LearnedWord>,
    rejected: HashSet<String>,
}

impl PersonalRespellings {
    /// Two confirmations to become established. One is a coincidence.
    pub const ESTABLISH_AT: u32 = 2;

    pub fn new() -> Self {
        Self::default()
    }

    pub fn confirm(&mut self, word: &str, pronunciation: &str, now_ms: u64) -> LearningState {
        let key = word.trim().to_lowercase();
        if key.is_empty() || self.rejected.contains(&key) {
            return LearningState::Rejected;
        }
        let times = match self.words.get(&key) {
            Some(existing) if existing.pronunciation == pronunciation => existing.times_confirmed + 1,
            _ => 1,
        };
        self.words.insert(
            key,
            LearnedWord {
                word: word.to_string(),
                pronunciation: pronunciation.to_string(),
                times_confirmed: times,
                last_used_at_ms: now_ms,
            },
        );
        if times >= Self::ESTABLISH_AT {
            LearningState::Established
        } else {
            LearningState::Provisional
        }
    }

    /// A rejection is REMEMBERED, so the same wrong guess is not offered again.
    pub fn reject(&mut self, word: &str) {
        let key = word.trim().to_lowercase();
        self.words.remove(&key);
        self.rejected.insert(key);
    }

    pub fn get(&self, word: &str) -> Option<&LearnedWord> {
        self.words.get(&word.trim().to_lowercase())
    }

    pub fn state_of(&self, word: &str) -> LearningState {
        let key = word.trim().to_lowercase();
        if self.rejected.contains(&key) {
            return LearningState::Rejected;
        }
        match self.words.get(&key) {
            Some(w) if w.times_confirmed >= Self::ESTABLISH_AT => LearningState::Established,
            _ => LearningState::Provisional,
        }
    }

    pub fn established(&self) -> HashMap<String, String> {
        self.words
            .iter()
            .filter(|(_, w)| w.times_confirmed >= Self::ESTABLISH_AT)
            .map(|(k, w)| (k.clone(), w.pronunciation.clone()))
            .collect()
    }
}

/// Ge'ez to Latin.
///
/// Ge'ez is an ABUGIDA: each character is a consonant plus a vowel, laid out in
/// seven orders. The sixth order is either a bare consonant or a schwa depending
/// on position - which is the one rule a transliteration table cannot express,
/// and why this is code.
pub struct GeezRomanizer;

impl GeezRomanizer {
    const CONSONANTS: &'static [&'static str] = &[
        "h", "l", "ḥ", "m", "ś", "r", "s", "sh", "q", "b", "t", "ch", "ḫ", "n",
        "ñ", "ʾ", "k", "kh", "w", "ʿ", "z", "zh", "y", "d", "j", "g", "ṭ",
        "ch'", "p'", "ts", "ts'", "f", "p",
    ];
    /// The seven vowel orders, in the order the block lays them out.
    const VOWELS: &'static [&'static str] = &["ä", "u", "i", "a", "e", "", "o"];

    pub fn is_ethiopic(text: &str) -> bool {
        text.chars().any(|c| ('\u{1200}'..='\u{137F}').contains(&c))
    }

    pub fn romanize(text: &str) -> String {
        let mut out = String::with_capacity(text.len() * 2);
        for ch in text.chars() {
            let c = ch as u32;
            if !(0x1200..=0x137F).contains(&c) {
                out.push(ch);
                continue;
            }
            let index = (c - 0x1200) as usize;
            let order = index % 8;
            // A character outside the regular grid - a number, a labialised form
            // - is passed through rather than mapped to the wrong consonant.
            match (Self::CONSONANTS.get(index / 8), order < 7) {
                (Some(consonant), true) => {
                    out.push_str(consonant);
                    out.push_str(Self::VOWELS[order]);
                }
                _ => out.push(ch),
            }
        }
        out
    }
}

/// Ge'ez text to phonemes, via romanisation.
pub struct GeezPhonemizer<P: Phonemizer> {
    inner: P,
}

impl<P: Phonemizer> GeezPhonemizer<P> {
    pub fn new(inner: P) -> Self {
        Self { inner }
    }
}

impl<P: Phonemizer> Phonemizer for GeezPhonemizer<P> {
    fn is_available(&self) -> bool {
        true
    }
    fn phonemize(&self, text: &str, language: &str) -> String {
        self.inner.phonemize(&GeezRomanizer::romanize(text), language)
    }
}

/// Where a tone mark comes from.
pub trait ToneSource {
    fn tone_for(&self, word: &str) -> Vec<u8>;
}

/// Applies tone to a phoneme string.
///
/// TONE IS LEXICAL in most of the languages here - it is not intonation, it
/// changes which word was said. A voice that ignores it says a different word
/// with complete confidence, and the listener has no way to tell.
pub struct ToneShaper {
    source: Option<Box<dyn ToneSource + Send + Sync>>,
}

impl ToneShaper {
    pub const HIGH: u8 = 1;
    pub const LOW: u8 = 0;

    pub fn new(source: Option<Box<dyn ToneSource + Send + Sync>>) -> Self {
        Self { source }
    }

    pub fn apply(&self, word: &str, phonemes: &str) -> String {
        let tones = match &self.source {
            Some(s) => s.tone_for(word),
            None => return phonemes.to_string(),
        };
        if tones.is_empty() {
            return phonemes.to_string();
        }
        let mut out = String::with_capacity(phonemes.len() * 2);
        let mut syllable = 0usize;
        for ch in phonemes.chars() {
            out.push(ch);
            if "aeiouäəɛɔ".contains(ch) {
                if let Some(tone) = tones.get(syllable) {
                    out.push(if *tone == Self::HIGH { '\u{0301}' } else { '\u{0300}' });
                }
                syllable += 1;
            }
        }
        out
    }
}

/// Applies a language's loanword rules.
///
/// Rules are applied IN ORDER and each sees the previous one's output, which is
/// what lets a general rule follow a specific one. Applying them all to the
/// original would let two rules both fire on the same span.
pub struct LoanwordRespeller {
    rules: Vec<(String, String)>,
}

impl LoanwordRespeller {
    pub fn new(rules: Vec<(String, String)>) -> Self {
        Self { rules }
    }

    pub fn respell(&self, word: &str) -> String {
        self.rules
            .iter()
            .fold(word.to_string(), |w, (from, to)| w.replace(from.as_str(), to))
    }
}

/// Nguni loanword rules.
///
/// Nguni languages have no consonant clusters and no closed syllables, so an
/// English loanword acquires vowels - "school" becomes "isikole". A voice that
/// says the English form is speaking English inside a Zulu sentence.
pub struct NguniRespeller {
    inner: LoanwordRespeller,
}

impl Default for NguniRespeller {
    fn default() -> Self {
        Self::new()
    }
}

impl NguniRespeller {
    pub fn new() -> Self {
        Self {
            inner: LoanwordRespeller::new(vec![
                ("sk".into(), "isik".into()),
                ("st".into(), "isit".into()),
                ("sp".into(), "isip".into()),
            ]),
        }
    }

    /// A word ending in a consonant gains a vowel, because a Nguni syllable does
    /// not close.
    pub fn respell(&self, word: &str) -> String {
        let mut out = self.inner.respell(word);
        if out
            .chars()
            .last()
            .map(|c| c.is_alphabetic() && !"aeiou".contains(c.to_ascii_lowercase()))
            .unwrap_or(false)
        {
            out.push('i');
        }
        out
    }
}

/// A dictionary-driven phonemizer.
pub struct LexiconPhonemizer {
    entries: HashMap<String, String>,
    fallback: Box<dyn Phonemizer + Send + Sync>,
    tone: Option<ToneShaper>,
}

impl LexiconPhonemizer {
    pub fn new(
        entries: HashMap<String, String>,
        fallback: Box<dyn Phonemizer + Send + Sync>,
        tone: Option<ToneShaper>,
    ) -> Self {
        Self { entries, fallback, tone }
    }
}

impl Phonemizer for LexiconPhonemizer {
    fn is_available(&self) -> bool {
        !self.entries.is_empty() || self.fallback.is_available()
    }

    fn phonemize(&self, text: &str, language: &str) -> String {
        text.split_inclusive(char::is_whitespace)
            .map(|token| {
                let trimmed = token.trim();
                if trimmed.is_empty() {
                    return token.to_string();
                }
                let phonemes = self
                    .entries
                    .get(&trimmed.to_lowercase())
                    .cloned()
                    .unwrap_or_else(|| self.fallback.phonemize(trimmed, language));
                match &self.tone {
                    Some(shaper) => token.replace(trimmed, &shaper.apply(trimmed, &phonemes)),
                    None => token.replace(trimmed, &phonemes),
                }
            })
            .collect()
    }
}

/// Japanese, through Open JTalk's prosody.
pub struct OpenJTalkPhonemizer {
    run: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
}

impl OpenJTalkPhonemizer {
    pub fn new(run: Option<Box<dyn Fn(&str) -> String + Send + Sync>>) -> Self {
        Self { run }
    }
}

impl Phonemizer for OpenJTalkPhonemizer {
    fn is_available(&self) -> bool {
        self.run.is_some()
    }
    fn phonemize(&self, text: &str, _language: &str) -> String {
        match &self.run {
            Some(run) => run(text),
            None => text.to_string(),
        }
    }
}

/// One phoneme with its accent position.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProsodyToken {
    pub phoneme: String,
    pub accent: i32,
}

/// Reads Open JTalk's full-context labels into accent phrases.
///
/// The labels carry ACCENT POSITION, which Japanese needs and a plain phoneme
/// string cannot express - the same phonemes with a different accent are a
/// different word. So the tokeniser keeps the position rather than discarding it
/// with the rest of the label.
pub struct OpenJTalkProsodyTokeniser;

impl OpenJTalkProsodyTokeniser {
    pub fn tokenise(labels: &[String]) -> Vec<ProsodyToken> {
        labels
            .iter()
            .filter_map(|label| {
                let phoneme = label
                    .split('-')
                    .nth(1)?
                    .split('+')
                    .next()?
                    .to_string();
                if phoneme == "sil" || phoneme == "pau" || phoneme.is_empty() {
                    return None;
                }
                let accent = label
                    .split("/A:")
                    .nth(1)
                    .and_then(|rest| {
                        let digits: String = rest
                            .chars()
                            .take_while(|c| c.is_ascii_digit() || *c == '-' || *c == '+')
                            .collect();
                        digits.trim_start_matches('+').parse::<i32>().ok()
                    })
                    .unwrap_or(0);
                Some(ProsodyToken { phoneme, accent })
            })
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Tokenising

/// Which SentencePiece flavour a model was trained with.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SentencePieceKind {
    /// Longest-match pieces with a score. What most TTS front ends use.
    Unigram,
    /// Merge rules. Deterministic, and a different algorithm entirely.
    Bpe,
}

/// One piece in the vocabulary.
#[derive(Debug, Clone, PartialEq)]
pub struct SentencePiece {
    pub piece: String,
    pub id: u32,
    pub score: f32,
}

/// SentencePiece, enough of it to feed a voice model.
///
/// The leading-space marker is `▁` (U+2581), NOT an underscore. They look alike
/// in some fonts and a vocabulary keyed on the wrong one matches nothing, so
/// every token falls back to unknown and the model receives noise.
pub struct SentencePieceTokenizer {
    by_piece: HashMap<String, SentencePiece>,
    pub kind: SentencePieceKind,
    pub unknown_id: u32,
}

impl SentencePieceTokenizer {
    pub const SPACE: char = '▁';

    pub fn new(pieces: Vec<SentencePiece>, kind: SentencePieceKind, unknown_id: u32) -> Self {
        Self {
            by_piece: pieces.into_iter().map(|p| (p.piece.clone(), p)).collect(),
            kind,
            unknown_id,
        }
    }

    pub fn len(&self) -> usize {
        self.by_piece.len()
    }

    pub fn is_empty(&self) -> bool {
        self.by_piece.is_empty()
    }

    /// Spaces become the marker, with a marker PREPENDED.
    ///
    /// The leading marker matters - a model trained with it sees "hello" and
    /// "▁hello" as different tokens, and feeding the wrong one changes the
    /// pronunciation of the first word of every sentence.
    pub fn normalise(text: &str) -> String {
        let mut out = String::with_capacity(text.len() + 1);
        out.push(Self::SPACE);
        out.push_str(&text.trim().split_whitespace().collect::<Vec<_>>().join(&Self::SPACE.to_string()));
        out
    }

    /// Longest-match-first, which is what Unigram inference does in practice.
    ///
    /// Shortest-first would tokenise "the" as three characters and produce a
    /// token sequence the model has never seen.
    pub fn encode(&self, text: &str) -> Vec<u32> {
        let normalised = Self::normalise(text);
        let chars: Vec<char> = normalised.chars().collect();
        let mut out = Vec::new();
        let mut i = 0usize;
        while i < chars.len() {
            let mut matched = None;
            for end in (i + 1..=chars.len()).rev() {
                let candidate: String = chars[i..end].iter().collect();
                if let Some(piece) = self.by_piece.get(&candidate) {
                    matched = Some((piece.id, end));
                    break;
                }
            }
            match matched {
                Some((id, end)) => {
                    out.push(id);
                    i = end;
                }
                None => {
                    // An unknown character consumes exactly ONE char, which in
                    // Rust is a whole code point - so an emoji or an Ethiopic
                    // character is never split in half.
                    out.push(self.unknown_id);
                    i += 1;
                }
            }
        }
        out
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Waking

/// A phrase the device wakes on.
#[derive(Debug, Clone, PartialEq)]
pub struct WakePhrase {
    pub phrase: String,
    pub language: String,
    /// Per phrase, because a short phrase needs a higher bar than a long one.
    pub threshold: f32,
}

/// Whether a phrase is usable as a wake word.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WakePhraseVerdict {
    Good,
    /// Under two syllables. Fires on half of ordinary speech.
    TooShort,
    /// Long enough that people will not finish saying it.
    TooLong,
    /// Sounds like something common. "Hey there" wakes on "hey, there you are".
    TooCommon,
}

/// The phrases this device knows, and whether they are any good.
///
/// A WAKE PHRASE IS A TRADE and the phrase book is where it is made honestly. A
/// short phrase is easy to say and fires on the television; a long one never
/// false-fires and nobody finishes it.
#[derive(Debug, Default)]
pub struct WakePhraseBook {
    phrases: HashMap<String, WakePhrase>,
}

impl WakePhraseBook {
    const COMMON: &'static [&'static str] = &["hey there", "ok", "hello", "hi", "yes", "no"];

    pub fn new() -> Self {
        Self::default()
    }

    /// Normalised: case folded, punctuation dropped, spaces collapsed.
    pub fn normalise(phrase: &str) -> String {
        phrase
            .to_lowercase()
            .chars()
            .map(|c| if c.is_alphanumeric() || c.is_whitespace() { c } else { ' ' })
            .collect::<String>()
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" ")
    }

    /// A rough syllable count - vowel groups. Good enough to judge length.
    pub fn syllables(phrase: &str) -> usize {
        let mut count = 0usize;
        let mut in_vowel = false;
        for c in phrase.to_lowercase().chars() {
            let vowel = "aeiouy".contains(c);
            if vowel && !in_vowel {
                count += 1;
            }
            in_vowel = vowel;
        }
        count
    }

    pub fn judge(phrase: &str) -> WakePhraseVerdict {
        let normalised = Self::normalise(phrase);
        if Self::COMMON.contains(&normalised.as_str()) {
            return WakePhraseVerdict::TooCommon;
        }
        match Self::syllables(&normalised) {
            0..=1 => WakePhraseVerdict::TooShort,
            9.. => WakePhraseVerdict::TooLong,
            _ => WakePhraseVerdict::Good,
        }
    }

    /// Refuses a bad phrase rather than accepting it and disappointing later.
    pub fn add(&mut self, phrase: &str, language: &str, threshold: f32) -> WakePhraseVerdict {
        let verdict = Self::judge(phrase);
        if verdict != WakePhraseVerdict::Good {
            return verdict;
        }
        self.phrases.insert(
            Self::normalise(phrase),
            WakePhrase {
                phrase: phrase.to_string(),
                language: language.to_string(),
                threshold,
            },
        );
        verdict
    }

    pub fn matches(&self, heard: &str) -> Option<&WakePhrase> {
        self.phrases.get(&Self::normalise(heard))
    }

    pub fn all(&self) -> Vec<&WakePhrase> {
        self.phrases.values().collect()
    }

    pub fn is_empty(&self) -> bool {
        self.phrases.is_empty()
    }
}

/// Something that might have been the wake word.
#[derive(Debug, Clone, PartialEq)]
pub struct WakeCandidate {
    pub phrase: String,
    pub score: f32,
    pub at_ms: u64,
    /// The audio just before it. A person usually starts the request in the same
    /// breath, and discarding it makes them repeat themselves.
    pub lookback_ms: u64,
}

/// Decides whether a candidate really was the wake word.
pub trait WakeConfirmer {
    fn confirm(&self, candidate: &WakeCandidate) -> bool;
}

/// Confirms everything.
///
/// The right choice when the spotter is already strict, and named so that using
/// it is visible - a device with this and a loose threshold wakes to the
/// television.
#[derive(Debug, Default, Clone, Copy)]
pub struct AlwaysConfirm;

impl WakeConfirmer for AlwaysConfirm {
    fn confirm(&self, _candidate: &WakeCandidate) -> bool {
        true
    }
}

/// Confirms by transcribing the audio and checking what was actually said.
///
/// SLOWER AND FAR MORE ACCURATE, which is the right trade at this point: the
/// spotter has already decided something happened, so the cost is paid rarely
/// and it is what stops the device answering the radio.
pub struct TranscriptConfirmer {
    transcribe: Option<Box<dyn Fn(u64) -> Option<String> + Send + Sync>>,
    book: WakePhraseBook,
}

impl TranscriptConfirmer {
    pub fn new(
        transcribe: Option<Box<dyn Fn(u64) -> Option<String> + Send + Sync>>,
        book: WakePhraseBook,
    ) -> Self {
        Self { transcribe, book }
    }
}

impl WakeConfirmer for TranscriptConfirmer {
    fn confirm(&self, candidate: &WakeCandidate) -> bool {
        let Some(transcribe) = &self.transcribe else {
            return false;
        };
        // A transcriber that failed means UNCONFIRMED, not confirmed. Waking on
        // a failure is how a device that cannot hear becomes a device that is
        // always listening.
        let Some(heard) = transcribe(candidate.lookback_ms) else {
            return false;
        };
        let normalised = WakePhraseBook::normalise(&heard);
        self.book
            .all()
            .iter()
            .any(|p| normalised.contains(&WakePhraseBook::normalise(&p.phrase)))
    }
}

/// Confirms by checking that speech actually STARTED at the candidate.
///
/// A wake word detected in the middle of continuous speech is almost always a
/// false fire - somebody talking about something else. Requiring an onset is
/// cheap and rejects most of them.
pub struct UtteranceOnsetConfirmer {
    energy_before: Option<Box<dyn Fn(u64, u64) -> f32 + Send + Sync>>,
    quiet_threshold: f32,
}

impl UtteranceOnsetConfirmer {
    pub fn new(
        energy_before: Option<Box<dyn Fn(u64, u64) -> f32 + Send + Sync>>,
        quiet_threshold: f32,
    ) -> Self {
        Self { energy_before, quiet_threshold }
    }
}

impl WakeConfirmer for UtteranceOnsetConfirmer {
    fn confirm(&self, candidate: &WakeCandidate) -> bool {
        match &self.energy_before {
            // Quiet BEFORE the candidate means an utterance began there.
            Some(energy) => energy(candidate.at_ms, 400) < self.quiet_threshold,
            None => false,
        }
    }
}

/// Either confirmer is enough.
///
/// OR rather than AND on purpose: a transcript confirmer that times out should
/// not veto an onset that was unambiguous. Requiring both makes the device miss
/// wakes, which is the failure people actually notice.
pub struct EitherConfirmer {
    confirmers: Vec<Box<dyn WakeConfirmer + Send + Sync>>,
}

impl EitherConfirmer {
    pub fn new(confirmers: Vec<Box<dyn WakeConfirmer + Send + Sync>>) -> Self {
        Self { confirmers }
    }
}

impl WakeConfirmer for EitherConfirmer {
    fn confirm(&self, candidate: &WakeCandidate) -> bool {
        self.confirmers.iter().any(|c| c.confirm(candidate))
    }
}

/// A wake detection.
#[derive(Debug, Clone, PartialEq)]
pub struct KwsDetection {
    pub phrase: String,
    pub score: f32,
    pub at_ms: u64,
    pub lookback_ms: u64,
}

/// How close the current audio is, for a UI that shows listening.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct KwsProgress {
    pub score: f32,
    pub threshold: f32,
    pub frames_held: u32,
}

impl KwsProgress {
    pub fn fraction(&self) -> f32 {
        if self.threshold <= 0.0 {
            0.0
        } else {
            (self.score / self.threshold).clamp(0.0, 1.0)
        }
    }
}

/// One keyword the spotter listens for.
#[derive(Debug, Clone, PartialEq)]
pub struct KwsKeyword {
    pub phrase: String,
    pub threshold: f32,
}

/// What the spotter takes in.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KwsInputKind {
    /// Raw samples. The model does its own feature extraction.
    Waveform,
    /// Log-mel features, computed here.
    Fbank,
}

/// How the wake detector is tuned.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct KwsConfig {
    /// Leans towards MISSING. A missed wake is an annoyance; a false wake is a
    /// microphone opening in a room where nobody asked it to.
    pub threshold: f32,
    /// Ignore anything for this long after a detection. Without it one utterance
    /// fires on several consecutive frames and the assistant answers itself.
    pub refractory_ms: u64,
    /// Frames the score must hold. A single frame over is usually a door.
    pub consecutive_frames: u32,
    pub sample_rate_hz: u32,
    pub frame_ms: u64,
    pub input_kind: KwsInputKind,
}

impl Default for KwsConfig {
    fn default() -> Self {
        Self {
            threshold: 0.62,
            refractory_ms: 900,
            consecutive_frames: 2,
            sample_rate_hz: 16_000,
            frame_ms: 30,
            input_kind: KwsInputKind::Fbank,
        }
    }
}

/// Streaming keyword spotting over a scoring closure.
///
/// The hold and the refractory period are counted in AUDIO TIME rather than wall
/// time, so a device that stalls for a garbage collection does not silently
/// change the tuning.
pub struct KwsWakeWordDetector {
    pub config: KwsConfig,
    score: Option<Box<dyn Fn(&[f32]) -> (String, f32) + Send + Sync>>,
    book: WakePhraseBook,
    held: u32,
    elapsed_ms: u64,
    muted_until_ms: u64,
    last: KwsProgress,
}

impl KwsWakeWordDetector {
    pub fn new(
        config: KwsConfig,
        book: WakePhraseBook,
        score: Option<Box<dyn Fn(&[f32]) -> (String, f32) + Send + Sync>>,
    ) -> Self {
        Self {
            last: KwsProgress { score: 0.0, threshold: config.threshold, frames_held: 0 },
            config,
            score,
            book,
            held: 0,
            elapsed_ms: 0,
            muted_until_ms: 0,
        }
    }

    pub fn progress(&self) -> KwsProgress {
        self.last
    }

    pub fn reset(&mut self) {
        self.held = 0;
        self.muted_until_ms = self.elapsed_ms;
    }

    /// One frame in, a detection out or none.
    ///
    /// Time advances by the FRAME LENGTH, not by a clock. A device that pauses
    /// would otherwise appear to have been listening through it.
    pub fn push(&mut self, frame: &[f32]) -> Option<KwsDetection> {
        self.elapsed_ms += self.config.frame_ms;
        let score_fn = self.score.as_ref()?;
        let (phrase, score) = score_fn(frame);

        if self.elapsed_ms < self.muted_until_ms {
            // Still reported, so a UI does not freeze during the refractory
            // period - it just cannot fire.
            self.last = KwsProgress { score, threshold: self.config.threshold, frames_held: 0 };
            return None;
        }

        self.held = if score >= self.config.threshold { self.held + 1 } else { 0 };
        self.last = KwsProgress {
            score,
            threshold: self.config.threshold,
            frames_held: self.held,
        };

        if self.held < self.config.consecutive_frames {
            return None;
        }
        self.held = 0;
        self.muted_until_ms = self.elapsed_ms + self.config.refractory_ms;
        // An EMPTY phrase book accepts any detection, so a build with no
        // configured phrase still wakes rather than being silently deaf.
        if !self.book.is_empty() && self.book.matches(&phrase).is_none() {
            return None;
        }
        Some(KwsDetection {
            phrase,
            score,
            at_ms: self.elapsed_ms,
            lookback_ms: 500,
        })
    }
}

/// A spotter with a confirmation step.
pub struct ConfirmedKeywordSpotter {
    detector: KwsWakeWordDetector,
    confirmer: Box<dyn WakeConfirmer + Send + Sync>,
}

impl ConfirmedKeywordSpotter {
    pub fn new(
        detector: KwsWakeWordDetector,
        confirmer: Box<dyn WakeConfirmer + Send + Sync>,
    ) -> Self {
        Self { detector, confirmer }
    }

    pub fn push(&mut self, frame: &[f32]) -> Option<KwsDetection> {
        let detection = self.detector.push(frame)?;
        let candidate = WakeCandidate {
            phrase: detection.phrase.clone(),
            score: detection.score,
            at_ms: detection.at_ms,
            lookback_ms: detection.lookback_ms,
        };
        self.confirmer.confirm(&candidate).then_some(detection)
    }
}

/// Where a multi-word phrase has got to.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct KwsContextState {
    pub index: usize,
    pub token: String,
}

/// Tracks progress through a multi-word phrase.
///
/// A WRONG TOKEN RESTARTS rather than stepping back one state. Somebody who says
/// "hey... hey B" should wake, and a graph that only steps back one state gets
/// stuck partway through a phrase it will never complete.
#[derive(Debug, Clone)]
pub struct KwsContextGraph {
    tokens: Vec<String>,
    position: usize,
}

impl KwsContextGraph {
    pub fn new(tokens: Vec<String>) -> Self {
        Self { tokens, position: 0 }
    }

    pub fn state(&self) -> KwsContextState {
        KwsContextState {
            index: self.position,
            token: self.tokens.get(self.position).cloned().unwrap_or_default(),
        }
    }

    pub fn is_complete(&self) -> bool {
        self.position >= self.tokens.len()
    }

    pub fn accept(&mut self, token: &str) -> bool {
        if let Some(wanted) = self.tokens.get(self.position) {
            if wanted.eq_ignore_ascii_case(token) {
                self.position += 1;
                return true;
            }
        }
        // Restarting ON the first token rather than resetting to zero blindly,
        // so a repeated first word does not lose its own progress.
        self.position = match self.tokens.first() {
            Some(first) if first.eq_ignore_ascii_case(token) => 1,
            _ => 0,
        };
        false
    }

    pub fn reset(&mut self) {
        self.position = 0;
    }
}

/// Which languages the wake stack covers.
pub struct WakeLanguages;

impl WakeLanguages {
    pub const SUPPORTED: &'static [&'static str] = &[
        "en", "af", "zu", "xh", "st", "tn", "ts", "ve", "nr", "ss", "nso", "sw",
    ];

    pub fn covers(language: &str) -> bool {
        let base = language.split(['-', '_']).next().unwrap_or("").to_lowercase();
        Self::SUPPORTED.contains(&base.as_str())
    }
}

/// Which language a wake phrase is judged in.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WakeLanguageChoice {
    pub language: String,
    /// True when the language was assumed rather than told. Carried so a
    /// diagnostics screen can say why a phrase behaves oddly.
    pub was_inferred: bool,
}

/// How the wake stack is set up on this device.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct WakeHostCapabilities {
    pub can_run_neural_spotter: bool,
    pub can_transcribe_for_confirmation: bool,
    pub has_voice_activity_detector: bool,
}

/// Which spotter a host ended up with.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum WakeEngine {
    /// The neural spotter. Best, and needs a model.
    Zipformer,
    /// Transcribe everything and look for the phrase. Accurate and expensive.
    Transcript,
    /// Nothing available. The device does not wake; it is pressed.
    None,
}

/// A calibration run's result.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WakeCalibration {
    pub threshold: f32,
    pub false_fires_per_hour: f32,
    pub miss_rate_percent: f32,
    /// Whether the run had enough samples to mean anything.
    pub is_reliable: bool,
}

/// Builds the wake stack this host can actually run.
///
/// IT NEVER RETURNS SOMETHING THAT WILL FAIL LATER. A host with no model gets
/// `WakeEngine::None` and a device that must be pressed, which is a worse
/// experience and an honest one - rather than a spotter that reports ready and
/// then never fires.
pub struct WakeWordFactory;

impl WakeWordFactory {
    pub fn choose(capabilities: WakeHostCapabilities) -> WakeEngine {
        if capabilities.can_run_neural_spotter {
            WakeEngine::Zipformer
        } else if capabilities.can_transcribe_for_confirmation {
            WakeEngine::Transcript
        } else {
            WakeEngine::None
        }
    }

    /// A calibration is only reliable with enough listening behind it.
    ///
    /// Reporting a threshold from ten minutes of audio is reporting the room,
    /// not the phrase.
    pub fn calibrate(
        false_fires: u32,
        hours_listened: f32,
        misses: u32,
        attempts: u32,
        threshold: f32,
    ) -> WakeCalibration {
        WakeCalibration {
            threshold,
            false_fires_per_hour: if hours_listened > 0.0 {
                false_fires as f32 / hours_listened
            } else {
                0.0
            },
            miss_rate_percent: if attempts > 0 {
                misses as f32 / attempts as f32 * 100.0
            } else {
                0.0
            },
            is_reliable: hours_listened >= 4.0 && attempts >= 20,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Speaking and hearing

/// What a front end can report about itself.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct TtsFrontEndDiagnostics {
    pub phonemizer: String,
    pub has_lexicon: bool,
    pub has_personal_respellings: bool,
    pub supported_languages: Vec<String>,
}

/// Audio a voice produced.
#[derive(Debug, Clone, PartialEq)]
pub struct SynthesizedAudio {
    pub samples: Vec<f32>,
    pub sample_rate_hz: u32,
}

/// Turns text into audio.
pub trait TtsEngine {
    fn is_available(&self) -> bool;
    fn synthesize(&self, text: &str, language: &str) -> Option<SynthesizedAudio>;
}

/// Produces nothing.
///
/// Returns `None` rather than empty audio: silence and failure look identical to
/// a caller that only checks the length, and one of them should be retried.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullTtsEngine;

impl TtsEngine for NullTtsEngine {
    fn is_available(&self) -> bool {
        false
    }
    fn synthesize(&self, _text: &str, _language: &str) -> Option<SynthesizedAudio> {
        None
    }
}

/// Builds an ONNX session, when a host has a runtime.
pub struct OnnxSessionFactory {
    build: Option<Box<dyn Fn(&str) -> Option<u64> + Send + Sync>>,
    loaded: HashMap<String, u64>,
}

impl OnnxSessionFactory {
    pub fn new(build: Option<Box<dyn Fn(&str) -> Option<u64> + Send + Sync>>) -> Self {
        Self { build, loaded: HashMap::new() }
    }

    pub fn is_available(&self) -> bool {
        self.build.is_some()
    }

    /// CACHED by path. Building a session twice loads the weights twice, and on
    /// a phone there is not room for two.
    pub fn get(&mut self, model_path: &str) -> Option<u64> {
        if let Some(handle) = self.loaded.get(model_path) {
            return Some(*handle);
        }
        let handle = (self.build.as_ref()?)(model_path)?;
        self.loaded.insert(model_path.to_string(), handle);
        Some(handle)
    }

    pub fn release(&mut self) {
        self.loaded.clear();
    }
}

/// An ONNX voice.
pub struct OnnxTtsEngine {
    run: Option<Box<dyn Fn(&str) -> Vec<f32> + Send + Sync>>,
    phonemizer: Box<dyn Phonemizer + Send + Sync>,
    sample_rate_hz: u32,
}

impl OnnxTtsEngine {
    pub fn new(
        run: Option<Box<dyn Fn(&str) -> Vec<f32> + Send + Sync>>,
        phonemizer: Box<dyn Phonemizer + Send + Sync>,
        sample_rate_hz: u32,
    ) -> Self {
        Self { run, phonemizer, sample_rate_hz }
    }
}

impl TtsEngine for OnnxTtsEngine {
    fn is_available(&self) -> bool {
        self.run.is_some()
    }
    fn synthesize(&self, text: &str, language: &str) -> Option<SynthesizedAudio> {
        let run = self.run.as_ref()?;
        Some(SynthesizedAudio {
            samples: run(&self.phonemizer.phonemize(text, language)),
            sample_rate_hz: self.sample_rate_hz,
        })
    }
}

/// Kokoro, which takes graphemes rather than phonemes.
pub struct KokoroTtsEngine {
    run: Option<Box<dyn Fn(&str, &str) -> Vec<f32> + Send + Sync>>,
    voice: String,
    sample_rate_hz: u32,
}

impl KokoroTtsEngine {
    pub fn new(
        run: Option<Box<dyn Fn(&str, &str) -> Vec<f32> + Send + Sync>>,
        voice: String,
        sample_rate_hz: u32,
    ) -> Self {
        Self { run, voice, sample_rate_hz }
    }
}

impl TtsEngine for KokoroTtsEngine {
    fn is_available(&self) -> bool {
        self.run.is_some() && !self.voice.is_empty()
    }
    fn synthesize(&self, text: &str, _language: &str) -> Option<SynthesizedAudio> {
        let run = self.run.as_ref()?;
        Some(SynthesizedAudio {
            samples: run(text, &self.voice),
            sample_rate_hz: self.sample_rate_hz,
        })
    }
}

/// Speaks a long passage phrase by phrase.
///
/// SPLIT SO THE FIRST WORDS START SOONER. Synthesising a whole paragraph before
/// any of it plays is a wait the listener reads as the device being broken; the
/// total time is the same and the perceived time is not.
pub struct PhrasedTtsEngine<E: TtsEngine> {
    inner: E,
    max_characters: usize,
}

impl<E: TtsEngine> PhrasedTtsEngine<E> {
    pub fn new(inner: E, max_characters: usize) -> Self {
        Self { inner, max_characters }
    }

    /// Splits on SENTENCES first and only then on length, so a break never lands
    /// mid-clause where it would sound like a stumble.
    pub fn phrases(&self, text: &str) -> Vec<String> {
        let mut out = Vec::new();
        for sentence in SentenceSplitter::split(text) {
            if sentence.len() <= self.max_characters {
                out.push(sentence);
                continue;
            }
            let mut current = String::new();
            for clause in sentence.split_inclusive([',', ';', ':']) {
                if !current.is_empty() && current.len() + clause.len() > self.max_characters {
                    out.push(std::mem::take(&mut current));
                }
                current.push_str(clause);
            }
            if !current.is_empty() {
                out.push(current);
            }
        }
        out
    }
}

impl<E: TtsEngine> TtsEngine for PhrasedTtsEngine<E> {
    fn is_available(&self) -> bool {
        self.inner.is_available()
    }
    fn synthesize(&self, text: &str, language: &str) -> Option<SynthesizedAudio> {
        let mut all = Vec::new();
        let mut rate = 22_050;
        for phrase in self.phrases(text) {
            let audio = self.inner.synthesize(&phrase, language)?;
            rate = audio.sample_rate_hz;
            all.extend(audio.samples);
        }
        Some(SynthesizedAudio { samples: all, sample_rate_hz: rate })
    }
}

/// A TTS engine that applies respellings before synthesising.
pub struct RespellingTtsEngine<E: TtsEngine> {
    inner: E,
    respeller: Respeller,
}

impl<E: TtsEngine> RespellingTtsEngine<E> {
    pub fn new(inner: E, respeller: Respeller) -> Self {
        Self { inner, respeller }
    }
}

impl<E: TtsEngine> TtsEngine for RespellingTtsEngine<E> {
    fn is_available(&self) -> bool {
        self.inner.is_available()
    }
    fn synthesize(&self, text: &str, language: &str) -> Option<SynthesizedAudio> {
        self.inner.synthesize(&self.respeller.apply(text), language)
    }
}

/// Transcribes audio.
pub trait VoiceTranscriber {
    fn is_available(&self) -> bool;
    fn transcribe(&self, samples: &[f32], sample_rate_hz: u32, language: &str) -> Option<String>;
}

/// Transcribes nothing.
///
/// Returns `None`, not an empty string. An empty transcript is a real result -
/// somebody said nothing - and conflating it with a missing engine makes a
/// device that cannot hear look like a room that is silent.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullVoiceTranscriber;

impl VoiceTranscriber for NullVoiceTranscriber {
    fn is_available(&self) -> bool {
        false
    }
    fn transcribe(&self, _samples: &[f32], _rate: u32, _language: &str) -> Option<String> {
        None
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Who is speaking

/// What a speaker embedder takes in.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SpeakerEmbedderInputKind {
    Waveform,
    Fbank,
}

/// How speaker identity is configured.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SpeakerIdentityConfig {
    /// Cosine similarity. Strict enough that similar voices in one household do
    /// not cross over: mistaking one family member for another is worse than
    /// asking.
    pub threshold: f32,
    /// At least this many samples to enrol. ONE sample enrols the room and the
    /// microphone as much as the voice, and the person then fails to be
    /// recognised anywhere else in the house.
    pub min_enrolment_samples: usize,
    pub input_kind: SpeakerEmbedderInputKind,
}

impl Default for SpeakerIdentityConfig {
    fn default() -> Self {
        Self {
            threshold: 0.72,
            min_enrolment_samples: 2,
            input_kind: SpeakerEmbedderInputKind::Fbank,
        }
    }
}

/// One enrolled voice.
///
/// AN EMBEDDING, NEVER AUDIO. An embedding cannot be played back, which is the
/// difference between a device that recognises a household and one that has
/// recorded it.
#[derive(Debug, Clone, PartialEq)]
pub struct EnrolledSpeaker {
    pub speaker_id: String,
    pub template: Vec<f32>,
    pub sample_count: usize,
}

/// Matches a voice against enrolled ones.
pub trait SpeakerIdentity {
    fn is_available(&self) -> bool;
    /// `None` when nobody matched. Not an empty id, which reads as a match to
    /// somebody with no name.
    fn identify(&self, samples: &[f32]) -> Option<(String, f32)>;
}

/// Speaker identity over an ONNX embedder.
pub struct OnnxSpeakerIdentity {
    embed: Option<Box<dyn Fn(&[f32]) -> Vec<f32> + Send + Sync>>,
    pub config: SpeakerIdentityConfig,
    enrolled: Vec<EnrolledSpeaker>,
}

impl OnnxSpeakerIdentity {
    pub fn new(
        embed: Option<Box<dyn Fn(&[f32]) -> Vec<f32> + Send + Sync>>,
        config: SpeakerIdentityConfig,
    ) -> Self {
        Self { embed, config, enrolled: Vec::new() }
    }

    pub fn cosine(a: &[f32], b: &[f32]) -> f32 {
        if a.is_empty() || a.len() != b.len() {
            return 0.0;
        }
        let dot: f32 = a.iter().zip(b).map(|(x, y)| x * y).sum();
        let na: f32 = a.iter().map(|x| x * x).sum::<f32>().sqrt();
        let nb: f32 = b.iter().map(|y| y * y).sum::<f32>().sqrt();
        if na == 0.0 || nb == 0.0 {
            0.0
        } else {
            dot / (na * nb)
        }
    }

    /// Averages SEVERAL samples into one template, and refuses fewer.
    pub fn enrol(&mut self, speaker_id: &str, samples: &[Vec<f32>]) -> bool {
        let Some(embed) = &self.embed else { return false };
        if samples.len() < self.config.min_enrolment_samples {
            return false;
        }
        let vectors: Vec<Vec<f32>> = samples.iter().map(|s| embed(s)).collect();
        let Some(width) = vectors.iter().map(|v| v.len()).min() else { return false };
        let template = (0..width)
            .map(|i| vectors.iter().map(|v| v[i]).sum::<f32>() / vectors.len() as f32)
            .collect();
        self.enrolled.retain(|e| e.speaker_id != speaker_id);
        self.enrolled.push(EnrolledSpeaker {
            speaker_id: speaker_id.to_string(),
            template,
            sample_count: samples.len(),
        });
        true
    }

    /// Enrolment must be undoable, or it is not consent.
    pub fn forget(&mut self, speaker_id: &str) -> bool {
        let before = self.enrolled.len();
        self.enrolled.retain(|e| e.speaker_id != speaker_id);
        self.enrolled.len() != before
    }
}

impl SpeakerIdentity for OnnxSpeakerIdentity {
    fn is_available(&self) -> bool {
        self.embed.is_some()
    }

    fn identify(&self, samples: &[f32]) -> Option<(String, f32)> {
        let embed = self.embed.as_ref()?;
        if self.enrolled.is_empty() {
            return None;
        }
        let live = embed(samples);
        let (best, score) = self
            .enrolled
            .iter()
            .map(|e| (e, Self::cosine(&live, &e.template)))
            .fold((None, 0.0f32), |(bi, bs), (e, s)| {
                if s > bs {
                    (Some(e), s)
                } else {
                    (bi, bs)
                }
            });
        match best {
            Some(e) if score >= self.config.threshold => Some((e.speaker_id.clone(), score)),
            _ => None,
        }
    }
}

/// How emotion sensing is configured.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct SpeechEmotionConfig {
    /// Under this the answer is UNCERTAIN whatever the numbers say. A coin-flip
    /// dressed as an observation is worse than saying nothing.
    pub confidence_floor: f32,
    pub frame_ms: u64,
}

impl Default for SpeechEmotionConfig {
    fn default() -> Self {
        Self { confidence_floor: 0.45, frame_ms: 30 }
    }
}

/// One reading.
#[derive(Debug, Clone, PartialEq)]
pub struct SpeechEmotionFrame {
    pub label: String,
    pub confidence: f32,
    pub at_ms: u64,
}

/// Affect from the voice itself.
pub trait SpeechEmotionDetector {
    fn is_available(&self) -> bool;
    /// `None` when there is no speech. Inferring emotion from silence reads the
    /// room's air conditioning.
    fn sense(&self, samples: &[f32], speech_present: bool, at_ms: u64) -> Option<SpeechEmotionFrame>;
}

/// PROSODY ONLY - pace, pitch and energy. It never looks at what was said, which
/// is what lets it run without the transcript and without keeping one.
pub struct OnnxSpeechEmotionDetector {
    infer: Option<Box<dyn Fn(&[f32]) -> Vec<(String, f32)> + Send + Sync>>,
    pub config: SpeechEmotionConfig,
}

impl OnnxSpeechEmotionDetector {
    pub fn new(
        infer: Option<Box<dyn Fn(&[f32]) -> Vec<(String, f32)> + Send + Sync>>,
        config: SpeechEmotionConfig,
    ) -> Self {
        Self { infer, config }
    }
}

impl SpeechEmotionDetector for OnnxSpeechEmotionDetector {
    fn is_available(&self) -> bool {
        self.infer.is_some()
    }

    fn sense(&self, samples: &[f32], speech_present: bool, at_ms: u64) -> Option<SpeechEmotionFrame> {
        if !speech_present || samples.is_empty() {
            return None;
        }
        let infer = self.infer.as_ref()?;
        let scores = infer(samples);
        let (label, confidence) = scores
            .into_iter()
            .fold((String::new(), 0.0f32), |(bl, bs), (l, s)| if s > bs { (l, s) } else { (bl, bs) })
            ;
        if confidence < self.config.confidence_floor {
            return Some(SpeechEmotionFrame {
                label: "uncertain".into(),
                confidence,
                at_ms,
            });
        }
        Some(SpeechEmotionFrame { label, confidence, at_ms })
    }
}
