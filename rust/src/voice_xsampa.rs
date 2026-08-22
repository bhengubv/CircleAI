//! voice_xsampa — X-SAMPA → IPA, and SentencePiece unigram encoding.
//!
//! Port of `src/CircleAI.Voice/XsampaToIpa.cs` and
//! `src/CircleAI.Voice/SentencePieceUnigram.cs`.
//!
//! Parity is asserted against `fixtures/voice_xsampa_to_ipa.json` and
//! `fixtures/voice_sentencepiece_unigram.json`, which the C# reference
//! generates. If this file and those files disagree, one of them is wrong and
//! the test names the case.

use std::collections::HashMap;
use std::sync::OnceLock;

/// Every phone in the NCHLT Afrikaans dictionary, mapped to IPA.
///
/// Derived from the corpus, not from memory: exactly the distinct phones in
/// `nchlt_afr.dict`, with every IPA character checked against the target
/// voice's own token table before the table was written.
fn table() -> &'static HashMap<&'static str, &'static str> {
    static TABLE: OnceLock<HashMap<&'static str, &'static str>> = OnceLock::new();
    TABLE.get_or_init(|| {
        HashMap::from([
            // Vowels
            ("a", "a"), ("A:", "ɑː"), ("A:r", "ɑːr"),
            ("E", "ɛ"), ("O", "ɔ"), ("@", "ə"),
            ("i", "i"), ("u", "u"), ("y", "y"),
            ("9", "œ"), ("2:", "øː"), ("{", "æ"),

            // Diphthongs — NCHLT gives one token, the voice wants both elements.
            ("9y", "œy"), ("@i", "əi"), ("@u", "əu"),
            ("i@", "iə"), ("u@", "uə"),

            // Consonants
            ("b", "b"), ("d", "d"), ("f", "f"),
            // U+0261 LATIN SMALL LETTER SCRIPT G — the IPA letter, NOT ASCII
            // 'g'. The voice's vocabulary carries ɡ; a plain 'g' would miss and
            // be dropped.
            ("g", "\u{0261}"),
            ("j", "j"), ("k", "k"), ("l", "l"),
            ("m", "m"), ("n", "n"), ("N", "ŋ"),
            ("p", "p"), ("r", "r"), ("s", "s"),
            ("S", "ʃ"), ("t", "t"), ("v", "v"),
            ("w", "w"), ("x", "x"), ("z", "z"),
            ("Z", "ʒ"),

            // APPROXIMATION, DELIBERATE AND THE ONLY ONE. X-SAMPA h\ is ɦ, the
            // voiced glottal fricative Afrikaans uses in "hond". This voice's
            // vocabulary has no ɦ, only h. Voicing is lost; place and manner are
            // right, so the word stays recognisable.
            ("h\\", "h"),
        ])
    })
}

/// The IPA symbols for `xsampa`, plus the phones that could not be mapped.
///
/// Returned together rather than stashed in a static, because an unmapped phone
/// produces NO SOUND and the audio is merely shorter — every acoustic measure
/// still passes. A caller that cannot see the misses cannot refuse.
///
/// LONGEST MATCH ON WHOLE TOKENS. Several entries are multi-character (`A:r`,
/// `@i`, `9y`) and NCHLT emits them as single tokens; matching on the token —
/// never character by character — is what keeps `A:r` from becoming `A` + `:` + `r`.
pub fn xsampa_to_ipa(xsampa: &[&str]) -> (Vec<String>, Vec<String>) {
    let map = table();
    let mut ipa = Vec::with_capacity(xsampa.len() + 8);
    let mut unmapped: Vec<String> = Vec::new();

    for phone in xsampa {
        if phone.trim().is_empty() {
            continue;
        }
        if let Some(mapped) = map.get(phone) {
            // Per-char: the voice tokenises ɑ, ː and r separately, so "ɑːr" must
            // arrive as three symbols, not one.
            for ch in mapped.chars() {
                ipa.push(ch.to_string());
            }
            continue;
        }
        if !unmapped.iter().any(|u| u == phone) {
            unmapped.push((*phone).to_string());
        }
    }

    (ipa, unmapped)
}

/// True when every phone in `xsampa` has a mapping.
pub fn xsampa_can_say_all(xsampa: &[&str]) -> bool {
    let map = table();
    xsampa
        .iter()
        .filter(|p| !p.trim().is_empty())
        .all(|p| map.contains_key(p))
}

/// The X-SAMPA phones this table knows — for tests and diagnostics.
pub fn xsampa_known_phones() -> Vec<String> {
    table().keys().map(|k| (*k).to_string()).collect()
}

// ─────────────────────────────────────────────────────────────────────────────
// SentencePiece unigram
// ─────────────────────────────────────────────────────────────────────────────

/// Cost charged for falling back to raw bytes.
///
/// Any finite penalty works, because fallback only ever competes with "no path
/// at all". It must be worse than a real piece so the lattice never prefers it
/// where a piece exists, and finite so a path always exists.
const FALLBACK_PENALTY: f32 = 10.0;

/// SentencePiece unigram tokeniser — Viterbi over the piece lattice, with byte
/// fallback.
pub struct SentencePieceUnigram {
    ids: HashMap<String, i64>,
    scores: HashMap<String, f32>,
    max_piece_length: usize,
}

impl SentencePieceUnigram {
    /// Build from piece→id and piece→score maps.
    pub fn new(ids: HashMap<String, i64>, scores: HashMap<String, f32>) -> Self {
        let max_piece_length = ids.keys().map(|k| k.chars().count()).max().unwrap_or(1);
        Self { ids, scores, max_piece_length }
    }

    /// Number of pieces in the vocabulary.
    pub fn count(&self) -> usize {
        self.ids.len()
    }

    /// Encode text to token ids.
    ///
    /// VITERBI, NOT GREEDY LONGEST-MATCH. Unigram scores are not monotone in
    /// piece length — a long piece can score worse than the two short pieces
    /// covering the same span — so greedy silently produces plausible-but-wrong
    /// segmentations.
    pub fn encode(&self, text: &str) -> Vec<i64> {
        if text.is_empty() {
            return Vec::new();
        }

        // SentencePiece's own normalisation: spaces become U+2581, with one
        // prepended so the first word is marked word-initial too.
        //
        // NFKC is NOT applied here. The C# reference calls Normalize(FormKC) and
        // Rust has no stdlib normaliser; rather than pull in unicode-normalization
        // for a step no fixture exercises, this port stays byte-identical on
        // already-normalised input and is honest about the gap. Feeding
        // denormalised text to this port and to C# can differ — see the module
        // note in docs/CONTRACTS.md.
        let mut normalised = String::with_capacity(text.len() + 4);
        normalised.push('▁');
        for ch in text.chars() {
            normalised.push(if ch == ' ' { '▁' } else { ch });
        }

        // CHARS, NOT BYTES. Indexing by byte would let a piece boundary land
        // mid-codepoint, producing pieces that match nothing and byte-fallback
        // output that decodes to a different character.
        let chars: Vec<char> = normalised.chars().collect();
        let n = chars.len();

        const UNREACHABLE: f32 = -1e18;
        let mut best = vec![UNREACHABLE; n + 1];
        let mut from_index = vec![0usize; n + 1];
        let mut piece: Vec<Option<String>> = vec![None; n + 1];
        best[0] = 0.0;

        for i in 0..n {
            if best[i] <= UNREACHABLE / 2.0 {
                continue;
            }

            let limit = self.max_piece_length.min(n - i);
            for len in 1..=limit {
                let candidate: String = chars[i..i + len].iter().collect();
                if !self.ids.contains_key(&candidate) {
                    continue;
                }
                let score = best[i] + self.scores.get(&candidate).copied().unwrap_or(0.0);
                if score > best[i + len] {
                    best[i + len] = score;
                    from_index[i + len] = i;
                    piece[i + len] = Some(candidate);
                }
            }

            // Byte fallback for this ONE char, so no input is ever silent.
            let end = i + 1;
            let fallback = best[i] - FALLBACK_PENALTY;
            if fallback > best[end] {
                best[end] = fallback;
                from_index[end] = i;
                piece[end] = None;
            }
        }

        let mut reversed: Vec<i64> = Vec::with_capacity(n);
        let mut i = n;
        while i > 0 {
            let start = from_index[i];
            match &piece[i] {
                Some(p) => {
                    if let Some(id) = self.ids.get(p) {
                        reversed.push(*id);
                    }
                }
                None => {
                    // BACKWARDS, because this whole list is built backwards. The
                    // lattice is walked from the end and flipped once at the
                    // bottom, so a multi-byte character pushed in forward order
                    // comes out byte-reversed: é is UTF-8 C3 A9 and would be
                    // emitted A9 C3. Nothing panics — those are real pieces with
                    // real ids — so the model simply says a different character.
                    let raw: String = chars[start..i].iter().collect();
                    for b in raw.as_bytes().iter().rev() {
                        if let Some(id) = self.ids.get(&format!("<0x{b:02X}>")) {
                            reversed.push(*id);
                        }
                    }
                }
            }
            i = start;
        }

        reversed.reverse();
        reversed
    }
}
