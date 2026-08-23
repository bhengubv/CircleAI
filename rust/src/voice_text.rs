//! Ports of the five text-side voice modules:
//!
//! - `src/CircleAI.Voice/SentenceSplitter.cs`
//! - `src/CircleAI.Voice/LanguageSpanSplitter.cs`
//! - `src/CircleAI.Voice/GeezRomanizer.cs`
//! - `src/CircleAI.Voice/ToneShaper.cs`
//! - `src/CircleAI.Voice/NchltPhonemizer.cs`
//!
//! Parity is asserted against `fixtures/voice_sentence_splitter.json`,
//! `voice_language_spans.json`, `voice_geez_romanizer.json`,
//! `voice_tone_shaper.json` and `voice_nchlt_phonemizer.json`, which the C#
//! reference generates.

use std::collections::{BTreeMap, HashMap};

// ── SentenceSplitter ────────────────────────────────────────────────────────
//
// Why this has to exist: the voices in use here were trained on text with the
// punctuation stripped out, so their vocabularies contain no '.', ',', '?' or
// ':' at all. Feeding a paragraph in one pass produces one unbroken run of
// speech — no pause between sentences, because there is no token that could
// encode one. The pause has to come from outside the model.
//
// It splits at SENTENCE boundaries only, never at commas. Each synthesis is an
// independent utterance and a VITS model ends every utterance with falling,
// sentence-final prosody, so cutting at a comma would make each clause land like
// a finished sentence — worse prosody than the run-on it was meant to fix.

/// One unit of speech, plus the silence that should follow it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SpeechSegment {
    /// The text to synthesise. Never empty or whitespace.
    pub text: String,
    /// Silence to append after this segment, in milliseconds. 0 for the final
    /// segment — trailing silence at the end of a passage serves nothing.
    pub trailing_pause_ms: i32,
}

// Pause lengths are the perceptual point of this module, so they are named
// rather than buried. A full stop reads longer than a colon; a paragraph break
// longer than either.
const SENTENCE_PAUSE_MS: i32 = 280;
const CLAUSE_PAUSE_MS: i32 = 200; // ':' and ';' — a lighter break
const PARAGRAPH_PAUSE_MS: i32 = 400;
const FORCED_PAUSE_MS: i32 = 60; // an over-long run cut for latency

/// Beyond this many characters a segment is cut even without punctuation. A
/// single unbroken clause of this size is already several seconds of audio, and
/// on a phone the whole segment must render before ANY of it can play. The cut
/// is taken at a word boundary and given only a token pause.
pub const MAX_CHARS_PER_SEGMENT: usize = 220;

/// Characters that end a sentence, across the scripts we speak.
///
/// A Latin-only list silently under-splits every language that punctuates
/// differently. Measured on the P30: Hindi, Bengali and Urdu produced THREE
/// segments from the same five-sentence text that gave six in eleven other
/// languages, because Devanagari and Bengali end sentences with the danda and
/// Urdu with its own full stop — none of which were listed. The paragraph ran
/// together exactly as it did before the splitter existed, for about a billion
/// people, and nothing failed loudly enough to notice.
const TERMINATORS: &[char] = &[
    '.', '!', '?', ':', ';', // Latin / Cyrillic / Greek
    '\u{0964}', '\u{0965}', // danda, double danda — Devanagari, Bengali, Gurmukhi
    '\u{06D4}', '\u{061F}', '\u{061B}', // Arabic script — Urdu, Arabic, Persian
    '\u{3002}', '\u{FF01}', '\u{FF1F}', // CJK ideographic + fullwidth
    '\u{FF0E}', '\u{FF1A}', '\u{FF1B}', // fullwidth
    '\u{1362}', // Ethiopic — Amharic, Tigrinya
    '\u{17D4}', // Khmer khan
    '\u{104A}', '\u{104B}', // Myanmar little/section
];

/// Terminators that can legitimately appear inside a token, and so need a
/// following space before they may be read as ending a sentence.
const MAY_OCCUR_INSIDE_A_TOKEN: &[char] = &['.', ':', ';'];

const CLOSERS: &[char] = &['"', '\'', ')', ']'];

fn is_terminator(c: char) -> bool {
    TERMINATORS.contains(&c)
}

/// Splits `text` into segments. Returns a single segment when there is no
/// sentence punctuation, and an empty vector for blank input.
///
/// INDEXED BY UTF-16 CODE UNIT, not by `char` or byte, because the reference
/// walks a C# string. Every terminator in the table is in the BMP, so the two
/// agree on where the splits fall — but `MAX_CHARS_PER_SEGMENT` counts units,
/// and a port that counted chars or bytes would cut over-long text elsewhere.
pub fn split_sentences(text: &str) -> Vec<SpeechSegment> {
    let mut segments: Vec<SpeechSegment> = Vec::new();
    if text.trim().is_empty() {
        return segments;
    }

    let units: Vec<u16> = text.encode_utf16().collect();
    let mut current: Vec<u16> = Vec::new();
    let pending = SENTENCE_PAUSE_MS;

    for i in 0..units.len() {
        let c = units[i];

        if c == 13 {
            continue; // '\r'
        }
        if c == 10 {
            // '\n'
            current = flush(&mut segments, &current, PARAGRAPH_PAUSE_MS);
            continue;
        }

        current.push(c);

        if let Some(ch) = char::from_u32(c as u32) {
            if is_terminator(ch) && ends_sentence(&units, i) {
                let pause = if ch == ':' || ch == ';' {
                    CLAUSE_PAUSE_MS
                } else {
                    SENTENCE_PAUSE_MS
                };
                current = flush(&mut segments, &current, pause);
                continue;
            }
        }

        if current.len() >= MAX_CHARS_PER_SEGMENT {
            current = cut_at_word_boundary(&mut segments, &current);
        }
    }

    flush(&mut segments, &current, pending);

    // Nothing should follow the last word — a trailing pause is dead air.
    if let Some(last) = segments.last_mut() {
        last.trailing_pause_ms = 0;
    }

    segments
}

/// True when the terminator at `i` really ends a sentence.
///
/// A period between digits is a decimal ("3.5"), and one followed directly by a
/// letter is usually an abbreviation or a URL — splitting there would cut a word
/// in half and insert a pause inside it.
fn ends_sentence(units: &[u16], i: usize) -> bool {
    let at = |k: usize| char::from_u32(units[k] as u32);

    // Absorb any run of closing punctuation ("...", "?!", ".").
    let mut j = i + 1;
    while j < units.len()
        && at(j).map_or(false, |ch| is_terminator(ch) || CLOSERS.contains(&ch))
    {
        j += 1;
    }

    if j >= units.len() {
        return true; // end of input
    }

    // Only SOME terminators can appear inside a token — '.' in 3.5 and co.za,
    // ':' in 12:30. For those, a following space is what separates a sentence end
    // from a decimal point. The rest cannot occur mid-token in any script, and
    // demanding a space after them would never split Chinese, Japanese, Khmer,
    // Thai or Burmese at all: those scripts write without spaces between words,
    // so their full stop is followed by the next letter.
    let here = match at(i) {
        Some(ch) => ch,
        None => return true,
    };
    if !MAY_OCCUR_INSIDE_A_TOKEN.contains(&here) {
        return true;
    }

    let next = match at(j) {
        Some(ch) => ch,
        None => return true,
    };
    if !next.is_whitespace() {
        return false; // 3.5, e.g., co.za
    }

    if here == '.'
        && i > 0
        && at(i - 1).map_or(false, |ch| ch.is_ascii_digit())
        && j + 1 < units.len()
        && at(j + 1).map_or(false, |ch| ch.is_ascii_digit())
    {
        return false;
    }

    true
}

fn flush(segments: &mut Vec<SpeechSegment>, current: &[u16], pause_ms: i32) -> Vec<u16> {
    let s = String::from_utf16_lossy(current).trim().to_string();
    if s.is_empty() {
        return Vec::new();
    }

    // The terminator STAYS in the segment text, deliberately. It is tempting to
    // strip it — this module has already turned it into a pause, and the MMS
    // voices have no token for it. But the SA-11 voice's vocabulary DOES carry
    // '?' and '.', so it can render a real question rise that no inserted silence
    // could imitate. Stripping would have discarded that from all eleven South
    // African languages to tidy up a log line.

    // A segment of nothing but punctuation has no sound to make, and the voice
    // has no token for it either.
    if !s.chars().any(|ch| ch.is_alphanumeric()) {
        return Vec::new();
    }

    segments.push(SpeechSegment { text: s, trailing_pause_ms: pause_ms });
    Vec::new()
}

/// Cuts an over-long run at the last space, so the break lands between words
/// rather than inside one. With no space to use the run is left intact — a
/// mid-word cut would be audibly worse than a long segment.
fn cut_at_word_boundary(segments: &mut Vec<SpeechSegment>, current: &[u16]) -> Vec<u16> {
    let cut = match current.iter().rposition(|&u| u == 32) {
        Some(c) if c > 0 => c,
        _ => return current.to_vec(),
    };

    let head = String::from_utf16_lossy(&current[..cut]).trim().to_string();
    if !head.is_empty() {
        segments.push(SpeechSegment { text: head, trailing_pause_ms: FORCED_PAUSE_MS });
    }

    current[cut + 1..].to_vec()
}

// ── LanguageSpanSplitter ────────────────────────────────────────────────────
//
// People do not speak one language per sentence. "Igama lami ngu-CircleAI" is
// isiZulu with an English name inside it, and read wholly in isiZulu the name
// comes out mangled — the listener hears the machine fail at a word they know
// perfectly well. A multi-lingual model takes ONE language id per utterance, so
// the fix is to cut the text where the language changes and synthesise each run
// under its own id.

/// A run of text to be spoken in one language.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LanguageSpan {
    /// The words, with their spacing preserved.
    pub text: String,
    /// True when this run is the embedded language (English), false for the
    /// surrounding one. The caller maps that to whatever ids its model uses.
    pub is_foreign: bool,
}

/// Splits `text` into spans. Returns a single span when the text is all one
/// language, which is the overwhelmingly common case — callers can check
/// `len() == 1` and take their existing single-language path.
pub fn split_language_spans(text: &str) -> Vec<LanguageSpan> {
    if text.trim().is_empty() {
        return Vec::new();
    }

    let chars: Vec<char> = text.chars().collect();
    let mut spans: Vec<LanguageSpan> = Vec::new();
    let mut current = String::new();
    let mut current_is_foreign: Option<bool> = None;

    let mut i = 0;
    while i < chars.len() {
        // Separators (spaces, punctuation, the hyphen in "ngu-CircleAI") ride
        // along with whatever run they FOLLOW, so a language change never strands
        // a comma on its own or splits mid-punctuation.
        if !chars[i].is_alphanumeric() {
            let sep_start = i;
            while i < chars.len() && !chars[i].is_alphanumeric() {
                i += 1;
            }
            current.extend(&chars[sep_start..i]);
            continue;
        }

        let word_start = i;
        while i < chars.len() && chars[i].is_alphanumeric() {
            i += 1;
        }
        let word: String = chars[word_start..i].iter().collect();
        let foreign = is_foreign_word(&word);

        if let Some(was) = current_is_foreign {
            if was != foreign {
                // The run ends at the last word, not at the separators that follow
                // it — those have already been appended and belong to the join.
                spans.push(LanguageSpan { text: current.clone(), is_foreign: was });
                current.clear();
            }
        }

        current_is_foreign = Some(foreign);
        current.push_str(&word);
    }

    if !current.is_empty() {
        if let Some(was) = current_is_foreign {
            spans.push(LanguageSpan { text: current, is_foreign: was });
        }
    }

    spans
}

/// Rewrites a run into the form a voice can actually pronounce, without changing
/// what is displayed.
///
/// A compound like `CircleAI` is one token to a synthesiser and it has no idea
/// where the words are, so it produces a mumble. Written `Circle AI` it is two
/// things the voice already knows how to say. This is why the name came out
/// garbled even after it was correctly switched to English — the language was
/// right and the word was still unreadable.
pub fn to_spoken_form(text: &str) -> String {
    if text.is_empty() {
        return String::new();
    }

    let chars: Vec<char> = text.chars().collect();

    // 1. Break the compound into words at case boundaries, which is where the
    //    word boundaries genuinely are in this naming style.
    let mut spaced: Vec<char> = Vec::with_capacity(chars.len() + 4);
    for i in 0..chars.len() {
        let c = chars[i];
        if i > 0 && c.is_uppercase() {
            let prev = chars[i - 1];
            let next = if i + 1 < chars.len() { Some(chars[i + 1]) } else { None };

            // lower->Upper is a word boundary (Circle|AI, You|Tube).
            let after_lower = prev.is_lowercase();
            // Upper->Upper->lower ends a run of capitals (API|Key).
            let end_of_acronym =
                prev.is_uppercase() && next.map_or(false, |n| n.is_lowercase());

            if after_lower || end_of_acronym {
                spaced.push(' ');
            }
        }
        spaced.push(c);
    }

    // 2. Punctuate the acronyms. "AI" as a bare token gets read as a word — "ay"
    //    — where "A.I." is read as the letters, which is what it is. The full
    //    stops are for the voice, not the reader.
    let mut out = String::with_capacity(spaced.len() + 8);
    let mut i = 0;
    while i < spaced.len() {
        if !spaced[i].is_uppercase() {
            out.push(spaced[i]);
            i += 1;
            continue;
        }

        let start = i;
        while i < spaced.len() && spaced[i].is_uppercase() {
            i += 1;
        }
        let run = &spaced[start..i];

        // A lone capital is an ordinary word opening ("Sawubona"), not an acronym,
        // and a run followed by lowercase was already split above.
        if run.len() < 2 {
            out.extend(run);
            continue;
        }

        for &ch in run {
            out.push(ch);
            out.push('.');
        }
    }
    out
}

/// Is this token unmistakably foreign (English) inside African-language text?
///
/// Two signals only, both chosen because native orthographies do not produce
/// them:
///
/// - internal capitals — CircleAI, WhatsApp, MTN's brand spellings
/// - all-caps, 2-5 letters — GPS, SMS, ATM, PIN
///
/// isiZulu, isiXhosa, Sesotho and the rest capitalise the first letter of a
/// sentence or a proper noun and nothing else, so neither pattern arises
/// naturally. A sentence-initial capital is therefore NOT a signal, which is why
/// only capitals after position zero count.
///
/// It does NOT try to spot ordinary lowercase English words like "computer" —
/// that needs a lexicon per language pair, and guessing wrong is worse than not
/// guessing: mispronouncing a native word to "fix" a foreign one insults the
/// speaker in their own language.
pub fn is_foreign_word(word: &str) -> bool {
    // UTF-16 units, because the reference measures a C# string's Length. Every
    // word this meets is BMP, so the two agree — but the boundary is exact.
    let len = word.encode_utf16().count();
    if len < 2 {
        return false;
    }

    let mut upper = 0;
    let mut lower = 0;
    let mut has_internal_capital = false;

    for (i, c) in word.chars().enumerate() {
        if !c.is_alphabetic() {
            continue;
        }
        if c.is_uppercase() {
            upper += 1;
            if i > 0 {
                has_internal_capital = true;
            }
        } else {
            lower += 1;
        }
    }

    if has_internal_capital && lower > 0 {
        return true; // CircleAI, WhatsApp
    }
    if upper >= 2 && lower == 0 && len <= 5 {
        return true; // GPS, SMS, ATM
    }
    false
}

// ── GeezRomanizer ───────────────────────────────────────────────────────────
//
// Ethiopic (Ge'ez) script → Latin, because the Amharic and Tigrinya voices do
// not read Ethiopic at all. Meta ships those two MMS models with
// `is_uroman: true`: their vocabularies are 28 and 27 LATIN letters and they
// expect text already transliterated. Measured on the P30, Amharic lost 43
// distinct characters and produced 3.2 s of noise for a 15 s paragraph.
//
// The transliteration is computed, not tabulated, because Unicode lays the
// syllabary out exactly as the script is taught: each consecutive block of EIGHT
// codepoints is one consonant across its vowel orders.

const GEEZ_BASE: u32 = 0x1200;
const GEEZ_ORDERS_PER_CONSONANT: u32 = 8;

/// Last codepoint that follows the eight-orders-per-consonant layout. The
/// syllabary ends here; everything above is lone syllables, marks and numerals,
/// and treating any of it as a row invents a pronunciation.
const GEEZ_LAST_SYLLABLE: u32 = 0x1357;

/// Consonant per 8-codepoint row, in Unicode order. ASCII only: these voices
/// hold 27-28 plain Latin letters, so a transliteration carrying the Ethiopist
/// diacritics would be dropped as surely as the Ethiopic was.
///
/// Six rows are LABIALISED — the consonant carries a built-in /w/. Writing them
/// plain turns "kwa" into "ka", which silently changes the word.
const GEEZ_CONSONANTS: &[&str] = &[
    "h", "l", "h", "m", "s", "r", "s", "sh",
    "q", "qw", "q", "qw", "b", "v", "t", "ch",
    "h", "hw", "n", "ny", "", "k", "kw", "k",
    "kw", "w", "", "z", "zh", "y", "d", "d",
    "j", "g", "gw", "ng", "t", "ch", "p", "ts",
    "ts", "f", "p",
];

/// Vowel per order. The sixth is SILENT — it marks a bare consonant, which is
/// why the greeting romanises with no trailing vowel.
const GEEZ_VOWELS: &[&str] = &["e", "u", "i", "a", "e", "", "o", "wa"];

/// True when `text` contains any Ethiopic character.
pub fn is_ethiopic(text: &str) -> bool {
    text.chars().any(|c| {
        let cp = c as u32;
        (0x1200..=0x139F).contains(&cp)
    })
}

/// Ethiopic → Latin. Characters outside the script pass through untouched, so
/// mixed text (numerals, Latin names, punctuation) survives intact.
pub fn romanize(text: &str) -> String {
    if text.is_empty() {
        return String::new();
    }

    let mut out = String::with_capacity(text.len() * 2);
    for c in text.chars() {
        let cp = c as u32;

        // Ethiopic punctuation, mapped so sentence splitting still works.
        let punct = match cp {
            0x1360 => Some(" "), // section
            0x1361 => Some(" "), // word separator
            0x1362 => Some("."), // full stop
            0x1363 => Some(","), // comma
            0x1364 => Some(";"), // semicolon
            0x1365 => Some(":"), // colon
            0x1366 => Some(":"), // preface colon
            0x1367 => Some("?"), // question mark
            0x1368 => Some(" "), // paragraph separator
            _ => None,
        };
        if let Some(p) = punct {
            out.push_str(p);
            continue;
        }

        // THE EIGHT-PER-CONSONANT LAYOUT STOPS AT U+1357, and the range check has
        // to stop with it. Beyond that the block is no longer a syllabary:
        // U+1358..U+135A are three LONE syllables already in their -a order,
        // U+135D..U+135F are combining marks, and U+1369 onward are the numerals.
        // Sizing the check off the consonant table instead swept seven of those
        // numerals back into the syllabary — and they came out as sound, so
        // nothing failed.
        //
        // Combining marks modify the syllable before them and have no sound of
        // their own, so they are dropped rather than passed through.
        if (0x135D..=0x135F).contains(&cp) {
            continue;
        }
        // The three syllables Unicode assigns singly rather than as a row of
        // eight. They are already in the -a order, so the vowel is part of the
        // value.
        let lone = match cp {
            0x1358 => Some("rya"),
            0x1359 => Some("mya"),
            0x135A => Some("fya"),
            _ => None,
        };
        if let Some(l) = lone {
            out.push_str(l);
            continue;
        }

        if cp < GEEZ_BASE || cp > GEEZ_LAST_SYLLABLE {
            // Numerals and the rarely-used supplement blocks have no sound we can
            // render; anything else is not Ethiopic and is left alone.
            if (0x1369..=0x137C).contains(&cp) {
                continue;
            }
            out.push(c);
            continue;
        }

        let i = cp - GEEZ_BASE;
        let row = (i / GEEZ_ORDERS_PER_CONSONANT) as usize;
        let order = (i % GEEZ_ORDERS_PER_CONSONANT) as usize;

        let consonant = GEEZ_CONSONANTS[row];
        let mut vowel = GEEZ_VOWELS[order];

        if consonant.is_empty() {
            // The glottal and pharyngeal rows write no consonant in Latin, so the
            // vowel IS the character. First order is heard as "a", and the sixth —
            // silent after a real consonant — must still sound here, or the
            // word-initial one disappears entirely.
            if order == 0 {
                vowel = "a";
            } else if vowel.is_empty() {
                vowel = "e";
            }
        }

        out.push_str(consonant);
        out.push_str(vowel);
    }
    out
}

// ── ToneShaper ──────────────────────────────────────────────────────────────
//
// Warmth, after the model has finished.
//
// THE VOICE WAS REPORTED AS TINNY, AND THE SPEAKER COULD NOT FIX IT. Choosing a
// speaker by how well the recogniser understands it has a bias nobody costed:
// word error rate rewards crisp consonants and a bright top end, which is what
// "tinny" describes. Measured across all 130 speakers in the bundle, warmth and
// intelligibility are inversely related. So the speaker is not the lever. The
// waveform is, and it is entirely ours once the model hands it over.
//
// WHY A DIP AND NOT JUST A BOOST. A phone speaker cannot move enough air to
// reproduce a low-shelf boost; on a P30 the bass simply is not there to lift.
// Cutting 2-5 kHz, where harshness lives, works on hardware that cannot do bass,
// which is most of the hardware this ships to. The boost is for headphones. Both
// are applied because the product is used on both.

/// Biquad coefficients, already normalised by a0.
#[derive(Debug, Clone, Copy)]
pub struct BiquadCoefficients {
    pub b: [f64; 3],
    pub a: [f64; 3],
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ToneShaperSettings {
    /// Where the low shelf starts lifting, in Hz.
    pub low_shelf_hz: f64,
    /// How much to lift the bottom, in dB.
    pub low_shelf_db: f64,
    /// Centre of the harshness dip, in Hz.
    pub presence_hz: f64,
    /// How much to cut there, in dB. Negative cuts.
    pub presence_db: f64,
    /// Width of the dip. Lower is wider.
    pub presence_q: f64,
}

impl Default for ToneShaperSettings {
    fn default() -> Self {
        Self {
            low_shelf_hz: 320.0,
            low_shelf_db: 4.0,
            presence_hz: 3200.0,
            presence_db: -4.0,
            presence_q: 0.8,
        }
    }
}

/// The measured setting: warmer, with no cost to intelligibility.
pub const WARM_TONE_SHAPER: ToneShaperSettings = ToneShaperSettings {
    low_shelf_hz: 320.0,
    low_shelf_db: 4.0,
    presence_hz: 3200.0,
    presence_db: -4.0,
    presence_q: 0.8,
};

const LOW_SHELF_SLOPE: f64 = 0.9;

fn normalise(mut b: [f64; 3], mut a: [f64; 3]) -> BiquadCoefficients {
    let a0 = a[0];
    for i in 0..3 {
        b[i] /= a0;
        a[i] /= a0;
    }
    BiquadCoefficients { b, a }
}

/// RBJ audio-cookbook low shelf, normalised by a0.
pub fn low_shelf_coefficients(s: &ToneShaperSettings, rate: u32) -> BiquadCoefficients {
    let amp = 10f64.powf(s.low_shelf_db / 40.0);
    let w0 = 2.0 * std::f64::consts::PI * s.low_shelf_hz / rate as f64;
    let alpha = w0.sin() / 2.0 * ((amp + 1.0 / amp) * (1.0 / LOW_SHELF_SLOPE - 1.0) + 2.0).sqrt();
    let c = w0.cos();
    let s2 = 2.0 * amp.sqrt() * alpha;

    normalise(
        [
            amp * ((amp + 1.0) - (amp - 1.0) * c + s2),
            2.0 * amp * ((amp - 1.0) - (amp + 1.0) * c),
            amp * ((amp + 1.0) - (amp - 1.0) * c - s2),
        ],
        [
            (amp + 1.0) + (amp - 1.0) * c + s2,
            -2.0 * ((amp - 1.0) + (amp + 1.0) * c),
            (amp + 1.0) + (amp - 1.0) * c - s2,
        ],
    )
}

/// RBJ audio-cookbook peaking EQ, normalised by a0.
pub fn peaking_coefficients(s: &ToneShaperSettings, rate: u32) -> BiquadCoefficients {
    let amp = 10f64.powf(s.presence_db / 40.0);
    let w0 = 2.0 * std::f64::consts::PI * s.presence_hz / rate as f64;
    let alpha = w0.sin() / (2.0 * s.presence_q);
    let c = w0.cos();

    normalise(
        [1.0 + alpha * amp, -2.0 * c, 1.0 - alpha * amp],
        [1.0 + alpha / amp, -2.0 * c, 1.0 - alpha / amp],
    )
}

/// Direct-form-I biquad, in place.
///
/// THE STATE IS DOUBLE AND THE STORED SAMPLE IS FLOAT, and both halves matter.
/// The filter memory never sees the float rounding — `y1` keeps the
/// full-precision result — so the recursion is identical everywhere. Only what
/// lands in the buffer is narrowed, which is what the next stage then reads.
pub fn biquad(x: &mut [f32], c: &BiquadCoefficients) {
    let (mut x1, mut x2, mut y1, mut y2) = (0.0f64, 0.0f64, 0.0f64, 0.0f64);
    for sample in x.iter_mut() {
        let xn = *sample as f64;
        let yn = c.b[0] * xn + c.b[1] * x1 + c.b[2] * x2 - c.a[1] * y1 - c.a[2] * y2;
        x2 = x1;
        x1 = xn;
        y2 = y1;
        y1 = yn;
        *sample = yn as f32;
    }
}

fn peak(x: &[f32]) -> f32 {
    let mut p = 0.0f32;
    for &v in x {
        let a = v.abs();
        if a > p {
            p = a;
        }
    }
    p
}

/// Filters `waveform` in place with a low shelf and a presence dip in series.
///
/// PEAK IS RESTORED AFTERWARDS. Lifting the low shelf adds energy, and a
/// waveform that already peaked near full scale would clip — which is heard as
/// crackle and would be blamed on the quantised model rather than on this.
/// Scaling back to the original peak keeps the tone change audible and the level
/// unchanged.
pub fn apply_tone_shaper(waveform: &mut [f32], sample_rate: u32, s: &ToneShaperSettings) {
    if waveform.is_empty() || sample_rate == 0 {
        return;
    }

    let before = peak(waveform);
    if before <= 0.0 {
        return; // a silent buffer, and dividing by that peak is NaN
    }

    biquad(waveform, &low_shelf_coefficients(s, sample_rate));
    biquad(waveform, &peaking_coefficients(s, sample_rate));

    let after = peak(waveform);
    if after > 0.0 && after > before {
        // f32 division, because the reference divides two FLOATS here. Widening to
        // f64 makes the gain a few ULP different and the whole tail of the
        // waveform drifts with it.
        let g = before / after;
        for sample in waveform.iter_mut() {
            *sample *= g;
        }
    }
}

// ── NchltPhonemizer ─────────────────────────────────────────────────────────
//
// A fully sovereign, permissive-licence grapheme-to-phoneme front-end for the
// South African languages. NOT espeak-ng (GPLv3 taints the app), NOT phonemeza
// (unlicensed, weights unpublished), and not neural. A faithful port of the
// NCHLT pronunciation predictor (Marelie Davel, pron_predict.pl) driven by the
// NCHLT-inlang resources, © DAC / CSIR / NWU under CC BY 3.0.
//
// Because the rule set covers any word there is no "OOV gap": a word is either
// in the dictionary (exact) or synthesised by the rules, which is what makes
// agglutinative isiZulu tractable.

/// One context rule: grapheme `g` in left/right context → code.
#[derive(Debug, Clone)]
struct Rule {
    order: i64,
    left: String,
    right: String,
    code: String,
}

pub struct NchltPhonemizer {
    dict: HashMap<String, Vec<String>>,
    rules: HashMap<char, Vec<Rule>>,
    phone_map: HashMap<char, String>,
    graph_map: HashMap<char, char>,
    gnulls: Vec<(String, String)>,

    /// Words in the last `phonemize` call that were synthesised by the rule
    /// engine rather than found in the dictionary. A coverage diagnostic, never a
    /// failure — the rules always produce output.
    pub last_rule_predicted_words: usize,

    /// Graphemes in the last call that no rule covered. Skipped, never guessed.
    pub last_unknown_graphemes: Vec<String>,
}

impl NchltPhonemizer {
    /// Build from the file CONTENTS rather than paths, so a caller can load from
    /// an embedded resource or a downloaded bundle with no filesystem in reach.
    pub fn from_text(
        dict_text: &str,
        rules_text: &str,
        phone_map_text: &str,
        graph_map_text: Option<&str>,
        gnulls_text: Option<&str>,
    ) -> Self {
        Self {
            dict: parse_dict(dict_text),
            rules: parse_rules(rules_text),
            phone_map: parse_phone_map(phone_map_text),
            graph_map: graph_map_text
                .filter(|t| !t.is_empty())
                .map_or_else(HashMap::new, parse_graph_map),
            gnulls: gnulls_text
                .filter(|t| !t.is_empty())
                .map_or_else(Vec::new, parse_gnulls),
            last_rule_predicted_words: 0,
            last_unknown_graphemes: Vec::new(),
        }
    }

    pub fn phonemize(&mut self, text: &str) -> Vec<String> {
        self.last_rule_predicted_words = 0;
        self.last_unknown_graphemes.clear();
        if text.trim().is_empty() {
            return Vec::new();
        }

        let mut phones: Vec<String> = Vec::new();
        for word in tokenize(text) {
            if let Some(known) = self.dict.get(&word) {
                phones.extend(known.iter().cloned());
            } else {
                let predicted = self.predict_word(&word);
                phones.extend(predicted);
                self.last_rule_predicted_words += 1;
            }
        }
        phones
    }

    /// Predict a single word's X-SAMPA phones from the context rules — the exact
    /// algorithm of `g2p_word_olist`: for each grapheme take the highest-order
    /// rule whose left/right context matches, emit its code, drop nulls, then
    /// remap codes to X-SAMPA.
    ///
    /// Does NOT clear the unknown-grapheme list, matching the reference:
    /// `phonemize` owns the reset, so a direct call accumulates rather than hiding
    /// what an earlier word already reported.
    pub fn predict_word(&mut self, word: &str) -> Vec<String> {
        if word.is_empty() {
            return Vec::new();
        }

        // Grapheme remap (usually identity) then grapheme-null insertion.
        let mapped = self.map_graphemes(word);
        let w: Vec<char> = self.apply_gnulls(&mapped).chars().collect();

        let mut codes: Vec<char> = Vec::with_capacity(w.len());
        for i in 0..w.len() {
            let g = w[i];
            let g_rules = match self.rules.get(&g) {
                Some(r) => r,
                None => {
                    // Skip an unknown grapheme rather than fabricate a phone.
                    let s = g.to_string();
                    if !self.last_unknown_graphemes.contains(&s) {
                        self.last_unknown_graphemes.push(s);
                    }
                    continue;
                }
            };

            // pat = " " + left-context + "-" + g + "-" + right-context + " "
            let left: String = w[..i].iter().collect();
            let right: String = w[i + 1..].iter().collect();
            let pat = format!(" {}-{}-{} ", left, g, right);

            // Rules are pre-sorted most-specific-first; the first match wins.
            let mut code = '0';
            for r in g_rules {
                if pat.contains(&format!("{}-{}-{}", r.left, g, r.right)) {
                    code = r.code.chars().next().unwrap_or('0');
                    break;
                }
            }
            if code != '0' {
                codes.push(code);
            }
        }

        codes
            .into_iter()
            .map(|c| self.phone_map.get(&c).cloned().unwrap_or_else(|| c.to_string()))
            .collect()
    }

    fn map_graphemes(&self, word: &str) -> String {
        if self.graph_map.is_empty() {
            return word.to_string();
        }
        word.chars().map(|c| *self.graph_map.get(&c).unwrap_or(&c)).collect()
    }

    fn apply_gnulls(&self, word: &str) -> String {
        let mut w = word.to_string();
        for (from, to) in &self.gnulls {
            w = w.replace(from.as_str(), to.as_str());
        }
        w
    }
}

/// Lower-case and split into word tokens on anything that is not a letter.
/// Diacritics are preserved (Afrikaans ê/ë/ô are real graphemes); digits and
/// punctuation become separators. Number and abbreviation expansion is out of
/// scope and belongs to a text-normalisation pass upstream.
fn tokenize(text: &str) -> Vec<String> {
    let mut words = Vec::new();
    let mut sb = String::new();
    for ch in text.trim().chars() {
        if ch.is_alphabetic() {
            sb.extend(ch.to_lowercase());
        } else if !sb.is_empty() {
            words.push(std::mem::take(&mut sb));
        }
    }
    if !sb.is_empty() {
        words.push(sb);
    }
    words
}

/// Split the way a StreamReader does, so a CRLF file parses identically.
fn nchlt_lines(text: &str) -> impl Iterator<Item = &str> {
    text.split('\n').map(|l| l.strip_suffix('\r').unwrap_or(l))
}

fn parse_dict(text: &str) -> HashMap<String, Vec<String>> {
    let mut dict = HashMap::new();
    for line in nchlt_lines(text) {
        if line.is_empty() {
            continue;
        }
        let tab = match line.find('\t') {
            Some(t) if t > 0 => t,
            _ => continue,
        };
        let word = &line[..tab];
        let pron = line[tab + 1..].trim();
        if pron.is_empty() || dict.contains_key(word) {
            continue; // keep the FIRST variant
        }
        dict.insert(
            word.to_string(),
            pron.split(' ').filter(|p| !p.is_empty()).map(String::from).collect(),
        );
    }
    dict
}

fn parse_rules(text: &str) -> HashMap<char, Vec<Rule>> {
    // BTreeMap by grapheme so the insertion order within each list is the file's,
    // which the stable sort below then preserves for ties.
    let mut by_grapheme: BTreeMap<char, Vec<Rule>> = BTreeMap::new();
    for line in nchlt_lines(text) {
        if line.is_empty() {
            continue;
        }
        // grapheme ; left ; right ; code ; order [ ; count ]
        let f: Vec<&str> = line.split(';').collect();
        if f.len() < 5 || f[0].is_empty() {
            continue;
        }
        let order: i64 = match f[4].trim().parse() {
            Ok(o) => o,
            Err(_) => continue,
        };
        let g = f[0].chars().next().unwrap();
        by_grapheme.entry(g).or_default().push(Rule {
            order,
            left: f[1].to_string(),
            right: f[2].to_string(),
            code: f[3].to_string(),
        });
    }

    // STABLE sort, descending by order. Two rules of equal order must stay in
    // file order — the reference uses LINQ's OrderByDescending, which is stable,
    // and `sort_unstable_by` would disagree on exactly the ties that are most
    // common in a dense rule set. `sort_by` in Rust IS stable.
    by_grapheme
        .into_iter()
        .map(|(g, mut list)| {
            list.sort_by(|a, b| b.order.cmp(&a.order));
            (g, list)
        })
        .collect()
}

fn parse_phone_map(text: &str) -> HashMap<char, String> {
    // Line: "<code>\t<xsampa>"  (code is a single char).
    let mut map = HashMap::new();
    for line in nchlt_lines(text) {
        if line.is_empty() {
            continue;
        }
        let tab = match line.find('\t') {
            Some(t) if t > 0 => t,
            _ => continue,
        };
        let code = &line[..tab];
        if code.chars().count() == 1 {
            map.insert(code.chars().next().unwrap(), line[tab + 1..].to_string());
        }
    }
    map
}

fn parse_graph_map(text: &str) -> HashMap<char, char> {
    // File line: "<funny>\t<std>" — we map std->funny (per remap_dict's gmap).
    let mut map = HashMap::new();
    for line in nchlt_lines(text) {
        if line.is_empty() {
            continue;
        }
        let f: Vec<&str> = line.split('\t').collect();
        if f.len() == 2 && f[0].chars().count() == 1 && f[1].chars().count() == 1 {
            let a = f[0].chars().next().unwrap();
            let b = f[1].chars().next().unwrap();
            if a != b {
                map.insert(b, a);
            }
        }
    }
    map
}

fn parse_gnulls(text: &str) -> Vec<(String, String)> {
    // File line: "<from>;<to>" — insert grapheme-nulls (empty for Nguni).
    let mut list = Vec::new();
    for line in nchlt_lines(text) {
        if line.is_empty() {
            continue;
        }
        let f: Vec<&str> = line.split(';').collect();
        if f.len() == 2 {
            list.push((f[0].to_string(), f[1].to_string()));
        }
    }
    list
}
