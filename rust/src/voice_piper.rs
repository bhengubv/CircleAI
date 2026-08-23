//! voice_piper — Piper phoneme→id mapping, lexicon tokenising, and the PCM format.
//!
//! Ports of `src/CircleAI.Voice/PiperVoiceConfig.cs`, `LexiconTokeniser.cs` and
//! `AudioFormat.cs`.
//!
//! Parity is asserted against `fixtures/voice_piper_config.json`,
//! `fixtures/voice_lexicon_tokeniser.json` and `fixtures/voice_audio_format.json`.

use std::collections::HashMap;

// AudioFormat IS ALREADY IN THIS PORT — `voice::AudioFormat::PCM16_MONO_16K`.
// It was ported with the original voice module and does not belong here.

/// What a [`PiperVoiceConfig::phonemes_to_ids`] call did, beyond the ids.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PhonemeMapping {
    pub ids: Vec<i64>,
    /// How many symbols the vocabulary had no entry for.
    pub skipped: usize,
    /// WHICH symbols were dropped. A dropped symbol is inaudible, so this list
    /// is the only evidence a front-end is broken.
    pub skipped_symbols: Vec<String>,
    /// Symbols APPROXIMATED rather than spoken exactly — a diacritic the voice
    /// lacks, folded to its base letter. A compromise, not a success.
    pub approximated_symbols: Vec<String>,
}

// Piper's special phoneme symbols (piper-phonemize defaults).
const PAD: &str = "_";
const BOS: &str = "^";
const EOS: &str = "$";

/// A Piper-layout voice's phoneme→id vocabulary and inference settings.
pub struct PiperVoiceConfig {
    map: HashMap<String, Vec<i64>>,
    pub sample_rate: i32,
    pub noise_scale: f32,
    pub length_scale: f32,
    pub noise_w: f32,
    /// e.g. `espeak` (needs a phonemizer) or `text` (graphemes are phonemes).
    pub phoneme_type: String,
}

impl PiperVoiceConfig {
    pub fn new(map: HashMap<String, Vec<i64>>) -> Self {
        Self {
            map,
            sample_rate: 22_050,
            noise_scale: 0.667,
            length_scale: 1.0,
            noise_w: 0.8,
            phoneme_type: "espeak".to_string(),
        }
    }

    /// True when this config has a usable phoneme→id map.
    pub fn has_phoneme_map(&self) -> bool {
        !self.map.is_empty()
    }

    /// THE PAD RULE: the id THIS voice uses for blank.
    ///
    /// It is 0 in sherpa/MMS exports and 3 in Piper-family ones, and pointing it
    /// at an ordinary vocabulary entry is what made 42 MMS voices speak fluent
    /// nonsense. Never assume a constant — read it from the model. Falls back to
    /// 0 only when the vocabulary has no `_` at all.
    pub fn pad_id(&self) -> i64 {
        self.map.get(PAD).and_then(|v| v.first().copied()).unwrap_or(0)
    }

    /// Turn a phoneme sequence into model token ids, in piper-phonemize's exact
    /// layout with interspersed pad:
    /// `[BOS, PAD, id(p1), PAD, id(p2), PAD, …, id(pN), PAD, EOS]`.
    ///
    /// BOS and EOS appear only when the vocabulary HAS them — the MMS-family
    /// exports do not. Unknown symbols are SKIPPED and REPORTED, never fatal.
    pub fn phonemes_to_ids(&self, phonemes: &[String]) -> PhonemeMapping {
        let mut ids: Vec<i64> = Vec::with_capacity(64);
        let mut dropped: Vec<String> = Vec::new();
        let mut approximated: Vec<String> = Vec::new();
        let mut skipped = 0usize;

        if let Some(b) = self.map.get(BOS) {
            ids.extend_from_slice(b);
        }
        let pad = self.map.get(PAD);
        if let Some(p) = pad {
            ids.extend_from_slice(p);
        }

        for phoneme in phonemes {
            match self.map_symbol(phoneme) {
                Some((mapped, was_approx)) => {
                    if was_approx && !approximated.iter().any(|a| a == phoneme) {
                        approximated.push(phoneme.clone());
                    }
                    ids.extend_from_slice(&mapped);
                    if let Some(p) = pad {
                        ids.extend_from_slice(p);
                    }
                }
                None => {
                    skipped += 1;
                    if !dropped.iter().any(|d| d == phoneme) {
                        dropped.push(phoneme.clone());
                    }
                }
            }
        }

        if let Some(e) = self.map.get(EOS) {
            ids.extend_from_slice(e);
        }

        PhonemeMapping {
            ids,
            skipped,
            skipped_symbols: dropped,
            approximated_symbols: approximated,
        }
    }

    fn map_symbol(&self, symbol: &str) -> Option<(Vec<i64>, bool)> {
        if let Some(exact) = self.map.get(symbol) {
            return Some((exact.clone(), false));
        }

        // A grapheme voice's vocabulary is built AFTER the training text has been
        // through the model's own cleaner, and every cleaner in use here
        // lower-cases. Such a vocab contains no capitals at all, so matching on
        // the raw character silently discarded every sentence-initial letter —
        // the model received "awubona" for "Sawubona".
        let lower = symbol.to_lowercase();
        if lower != symbol {
            if let Some(l) = self.map.get(&lower) {
                return Some((l.clone(), false));
            }
        }

        // A GRAPHEME CLUSTER the vocabulary stores as separate codepoints.
        // Burmese "ကြို" arrives as ONE symbol while the vocabulary holds each
        // codepoint on its own. Splitting it back keeps every mark, so this must
        // be tried BEFORE any approximation.
        if symbol.chars().count() > 1 {
            let mut parts: Vec<i64> = Vec::new();
            let mut whole = true;
            for ch in symbol.chars() {
                // Zero-width formatting characters shape how text is DRAWN and
                // say nothing about how it sounds. Persian writes them
                // constantly, as do most Indic scripts, and one invisible
                // character was failing the whole cluster.
                if is_format(ch) {
                    continue;
                }
                let s = ch.to_string();
                if let Some(part) = self.map.get(&s).or_else(|| self.map.get(&s.to_lowercase())) {
                    parts.extend_from_slice(part);
                } else {
                    whole = false;
                    break;
                }
            }
            if whole && !parts.is_empty() {
                return Some((parts, false)); // exact — nothing was lost
            }
        }

        // A letter the voice never learned. Dropping it deletes a consonant from
        // the middle of a word, so an approximation is worth more than a hole —
        // so long as it is declared rather than passed off as correct.
        for candidate in approximations(symbol) {
            if let Some(a) = self
                .map
                .get(&candidate)
                .or_else(|| self.map.get(&candidate.to_lowercase()))
            {
                return Some((a.clone(), true));
            }
        }

        None
    }
}

/// Split into grapheme clusters: a base char plus any combining marks that
/// follow it, so "bát" is three elements and not four.
pub fn split_phoneme_string(s: &str) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    let mut cur = String::new();
    for ch in s.chars() {
        if !cur.is_empty() && is_combining_mark(ch) {
            cur.push(ch);
            continue;
        }
        if !cur.is_empty() {
            out.push(std::mem::take(&mut cur));
        }
        cur.push(ch);
    }
    if !cur.is_empty() {
        out.push(cur);
    }
    out
}

fn approximations(symbol: &str) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();

    // Where the vocabulary carries the true phoneme under a different spelling,
    // use it — Tshivenda's ṅ IS /ŋ/, so that substitution loses nothing at all.
    if symbol == "ṅ" || symbol == "Ṅ" {
        out.push("ŋ".to_string());
    }
    if symbol == "š" || symbol == "Š" {
        out.push("ʃ".to_string());
    }

    // Folding a diacritic away is only defensible where the mark modifies a
    // letter that still carries most of the sound without it — Latin š→s, ṱ→t.
    // In Thai, Burmese, Devanagari, Arabic and Vietnamese the marks ARE the
    // vowels and tones; dropping them does not approximate the word, it deletes
    // it. Thai measured 4.3 s instead of ~15 s because every vowel sign was
    // folded off a consonant and filed as a harmless approximation.
    let stripped = strip_diacritics(symbol);
    if stripped.is_empty() || stripped == symbol || !is_latin_base(&stripped) {
        return out;
    }
    out.push(stripped);
    out
}

/// Judges the BASE that remains, not the composed character: Tshivenda ṱ lives
/// at U+1E71, far above the Latin block, yet strips to a plain 't'. Thai วั
/// strips to ว, which is not Latin at all — the case to refuse.
fn is_latin_base(stripped: &str) -> bool {
    !stripped.is_empty() && stripped.chars().all(|c| (c as u32) <= 0x024F)
}

/// Remove combining marks: ṱ → t.
///
/// NO NFD HERE, deliberately. The port takes no third-party crate for Unicode
/// normalisation, so precomposed characters are handled by an explicit table of
/// the letters this catalogue actually meets. That is a NARROWER fold than the
/// reference's — and narrower is the safe direction: an unfolded symbol is
/// skipped and REPORTED, where a wrongly folded one is silently mispronounced.
fn strip_diacritics(s: &str) -> String {
    let mut out = String::with_capacity(s.len());
    for ch in s.chars() {
        if is_combining_mark(ch) {
            continue;
        }
        out.push(fold_latin(ch));
    }
    out
}

fn fold_latin(c: char) -> char {
    match c {
        'á' | 'à' | 'â' | 'ä' | 'ã' | 'å' | 'ā' | 'ă' | 'ą' => 'a',
        'é' | 'è' | 'ê' | 'ë' | 'ē' | 'ĕ' | 'ė' | 'ę' | 'ě' => 'e',
        'í' | 'ì' | 'î' | 'ï' | 'ĩ' | 'ī' | 'ĭ' | 'į' => 'i',
        'ó' | 'ò' | 'ô' | 'ö' | 'õ' | 'ō' | 'ŏ' | 'ő' => 'o',
        'ú' | 'ù' | 'û' | 'ü' | 'ũ' | 'ū' | 'ŭ' | 'ů' | 'ű' | 'ų' => 'u',
        'ñ' | 'ń' | 'ņ' | 'ň' | 'ṅ' | 'ṇ' | 'ṋ' => 'n',
        'ç' | 'ć' | 'ĉ' | 'ċ' | 'č' => 'c',
        'š' | 'ś' | 'ŝ' | 'ş' | 'ṣ' => 's',
        'ť' | 'ţ' | 'ṱ' | 'ṭ' => 't',
        'ď' | 'đ' | 'ḓ' | 'ḍ' => 'd',
        'ž' | 'ź' | 'ż' => 'z',
        'ý' | 'ÿ' | 'ŷ' => 'y',
        'ğ' | 'ĝ' | 'ġ' | 'ģ' => 'g',
        'ł' | 'ĺ' | 'ļ' | 'ľ' => 'l',
        'ř' | 'ŕ' | 'ŗ' => 'r',
        _ => c,
    }
}

fn is_combining_mark(c: char) -> bool {
    // Mn / Mc / Me, by range. Covers the combining blocks this catalogue meets:
    // Latin diacritics, Devanagari matras, Thai vowel signs, Arabic harakat.
    let u = c as u32;
    (0x0300..=0x036F).contains(&u)   // Combining Diacritical Marks
        || (0x0483..=0x0489).contains(&u)
        || (0x0591..=0x05BD).contains(&u)
        || (0x0610..=0x061A).contains(&u)
        || (0x064B..=0x065F).contains(&u) // Arabic harakat
        || (0x0900..=0x0903).contains(&u) // Devanagari signs
        || (0x093A..=0x094F).contains(&u) // Devanagari matras
        || (0x0951..=0x0957).contains(&u)
        || (0x0E31..=0x0E3A).contains(&u) // Thai vowel signs / tone marks
        || (0x0E47..=0x0E4E).contains(&u)
        || (0x102B..=0x103E).contains(&u) // Burmese medials / vowel signs
        || (0x1056..=0x1059).contains(&u)
        || (0x1DC0..=0x1DFF).contains(&u)
        || (0x20D0..=0x20F0).contains(&u)
}

fn is_format(c: char) -> bool {
    let u = c as u32;
    u == 0x00AD
        || (0x200B..=0x200F).contains(&u) // ZWSP, ZWNJ, ZWJ, LRM, RLM
        || (0x202A..=0x202E).contains(&u)
        || (0x2060..=0x2064).contains(&u)
        || (0xFEFF == u)
}

// ─────────────────────────────────────────────────────────────────────────────
// LexiconTokeniser
// ─────────────────────────────────────────────────────────────────────────────

/// Turns text into model tokens using a voice's own lexicon files — a
/// word→phoneme table and a phoneme→id table beside the model. No phonemizer
/// process, no second package, no licence wall.
pub struct LexiconTokeniser {
    words: HashMap<String, Vec<i64>>,
    longest: usize,
    /// Blank id, interleaved between tokens when the model expects it.
    pub blank: i64,
    /// Symbols the lexicon had no entry for on the last call.
    pub last_unmapped: Vec<String>,
}

impl LexiconTokeniser {
    /// Build from a voice's `tokens.txt` and `lexicon.txt` content.
    pub fn from_text(tokens_text: &str, lexicon_text: &str, blank: i64) -> Option<Self> {
        // tokens.txt is "<symbol> <id>" per line. The symbol MAY BE A SPACE, so
        // split on the LAST space rather than the first.
        let mut ids: HashMap<String, i64> = HashMap::new();
        for line in tokens_text.lines() {
            let Some(cut) = line.rfind(' ') else { continue };
            if cut == 0 {
                continue;
            }
            let Ok(id) = line[cut + 1..].trim().parse::<i64>() else { continue };
            ids.insert(line[..cut].to_string(), id);
        }
        if ids.is_empty() {
            return None;
        }

        // lexicon.txt is "<word> <phoneme> <phoneme> ...".
        let mut words: HashMap<String, Vec<i64>> = HashMap::new();
        let mut longest = 1usize;
        for line in lexicon_text.lines() {
            let parts: Vec<&str> = line.split_whitespace().collect();
            if parts.len() < 2 {
                continue;
            }
            let seq: Vec<i64> = parts[1..].iter().filter_map(|p| ids.get(*p).copied()).collect();
            if seq.is_empty() {
                continue;
            }
            let n = parts[0].chars().count();
            if n > longest {
                longest = n;
            }
            words.insert(parts[0].to_string(), seq);
        }
        if words.is_empty() {
            return None;
        }

        Some(Self { words, longest, blank, last_unmapped: Vec::new() })
    }

    /// Segment `text` and return the model's tokens.
    ///
    /// LONGEST MATCH FIRST, because these lexicons are word-keyed and the words
    /// overlap: あい, あいさつ and あいかわらず all start the same way, and taking
    /// the shortest would pronounce a different word. Falls back to the single
    /// character when no word matches.
    pub fn encode(&mut self, text: &str, interleave_blank: bool) -> Vec<i64> {
        let mut out: Vec<i64> = Vec::new();
        let mut unmapped: Vec<String> = Vec::new();
        // CHARS, NOT BYTES: these lexicons are keyed on CJK words, and a byte
        // index would cut a character in half and match nothing.
        let chars: Vec<char> = text.chars().collect();

        let mut i = 0usize;
        while i < chars.len() {
            let mut taken = 0usize;
            let max = self.longest.min(chars.len() - i);
            for len in (1..=max).rev() {
                let candidate: String = chars[i..i + len].iter().collect();
                if let Some(seq) = self.words.get(&candidate) {
                    out.extend_from_slice(seq);
                    taken = len;
                    break;
                }
            }
            if taken == 0 {
                if !chars[i].is_whitespace() {
                    unmapped.push(chars[i].to_string());
                }
                taken = 1;
            }
            i += taken;
        }

        self.last_unmapped = unmapped;
        if !interleave_blank {
            return out;
        }

        // add_blank: a blank opens the utterance and follows every token.
        let mut padded: Vec<i64> = Vec::with_capacity(out.len() * 2 + 1);
        padded.push(self.blank);
        for id in out {
            padded.push(id);
            padded.push(self.blank);
        }
        padded
    }
}
