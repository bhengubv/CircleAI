//! voice_piper_test.rs
//!
//! Asserts the Rust PiperVoiceConfig / LexiconTokeniser / AudioFormat ports
//! against the same golden files the C# reference generates.
//!
//! The piper fixture carries TWO configs on purpose — one with pad 0 and one
//! with pad 3 — so a port that hard-codes either fails on the other. That is
//! THE PAD RULE, and getting it wrong is what made 42 MMS voices speak fluent
//! nonsense.

use circle_ai::voice::AudioFormat;
use circle_ai::voice_piper::{split_phoneme_string, LexiconTokeniser, PiperVoiceConfig};
use serde::Deserialize;
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;

fn fixtures_dir() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("rust/ has a parent")
        .join("fixtures")
}

fn read_fixture<T: for<'de> Deserialize<'de>>(name: &str) -> T {
    let path = fixtures_dir().join(name);
    let data = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("failed to read {}: {e}", path.display()));
    serde_json::from_str(&data)
        .unwrap_or_else(|e| panic!("failed to parse {}: {e}", path.display()))
}

#[derive(Deserialize)]
struct PiperCase {
    phonemes: Vec<String>,
    ids: Vec<i64>,
    skipped: usize,
    #[serde(rename = "skippedSymbols")]
    skipped_symbols: Vec<String>,
    #[serde(rename = "approximatedSymbols")]
    approximated_symbols: Vec<String>,
}

#[derive(Deserialize)]
struct PiperConfigCase {
    name: String,
    #[serde(rename = "configJson")]
    config_json: HashMap<String, Vec<i64>>,
    #[serde(rename = "padId")]
    pad_id: i64,
    #[serde(rename = "hasPhonemeMap")]
    has_phoneme_map: bool,
    cases: Vec<PiperCase>,
}

#[derive(Deserialize)]
struct SplitCase {
    input: String,
    elements: Vec<String>,
}

#[derive(Deserialize)]
struct PiperFixture {
    configs: Vec<PiperConfigCase>,
    #[serde(rename = "splitPhonemeString")]
    split_phoneme_string: Vec<SplitCase>,
}

#[test]
fn piper_config_matches_reference() {
    let fixture: PiperFixture = read_fixture("voice_piper_config.json");
    assert_eq!(fixture.configs.len(), 2, "both pad conventions must be covered");

    for c in &fixture.configs {
        let cfg = PiperVoiceConfig::new(c.config_json.clone());
        assert_eq!(cfg.pad_id(), c.pad_id, "padId for {}", c.name);
        assert_eq!(cfg.has_phoneme_map(), c.has_phoneme_map, "hasPhonemeMap for {}", c.name);

        for one in &c.cases {
            let got = cfg.phonemes_to_ids(&one.phonemes);
            assert_eq!(got.ids, one.ids, "ids for {:?} in {}", one.phonemes, c.name);
            assert_eq!(got.skipped, one.skipped, "skipped for {:?}", one.phonemes);
            assert_eq!(got.skipped_symbols, one.skipped_symbols,
                       "skippedSymbols for {:?}", one.phonemes);
            assert_eq!(got.approximated_symbols, one.approximated_symbols,
                       "approximatedSymbols for {:?}", one.phonemes);
        }
    }
}

#[test]
fn pad_is_read_from_the_model_not_assumed() {
    // THE PAD RULE. The two fixture configs disagree — 0 in the Piper-layout
    // one, 3 in the MMS-layout one — so a port that hard-codes either fails the
    // other.
    let fixture: PiperFixture = read_fixture("voice_piper_config.json");
    let pads: HashSet<i64> = fixture.configs.iter().map(|c| c.pad_id).collect();
    assert_eq!(pads, HashSet::from([0, 3]), "the fixture must cover BOTH pad conventions");

    for c in &fixture.configs {
        assert_eq!(PiperVoiceConfig::new(c.config_json.clone()).pad_id(), c.pad_id);
    }
}

#[test]
fn thai_is_not_folded_but_tshivenda_is() {
    // The asymmetry is the whole point. Latin ṱ still sounds like a t with the
    // mark gone; Thai ก's marks ARE the vowels, so folding deletes the word.
    let fixture: PiperFixture = read_fixture("voice_piper_config.json");
    let cfg = PiperVoiceConfig::new(fixture.configs[0].config_json.clone());

    assert_eq!(
        cfg.phonemes_to_ids(&["ṱ".to_string()]).approximated_symbols,
        vec!["ṱ".to_string()],
        "ṱ should fold to a Latin base and be REPORTED as approximate"
    );
    assert_eq!(
        cfg.phonemes_to_ids(&["ก".to_string()]).skipped_symbols,
        vec!["ก".to_string()],
        "Thai must be skipped, not folded"
    );
}

#[test]
fn split_phoneme_string_matches_reference() {
    let fixture: PiperFixture = read_fixture("voice_piper_config.json");
    for c in &fixture.split_phoneme_string {
        assert_eq!(split_phoneme_string(&c.input), c.elements, "clusters for {}", c.input);
    }
}

// ── LexiconTokeniser ────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct LexEntry {
    word: String,
    phonemes: Vec<String>,
}

#[derive(Deserialize)]
struct LexCase {
    text: String,
    ids: Vec<i64>,
    #[serde(rename = "idsWithBlank")]
    ids_with_blank: Vec<i64>,
    unmapped: Vec<String>,
}

#[derive(Deserialize)]
struct LexFixture {
    tokens: HashMap<String, i64>,
    lexicon: Vec<LexEntry>,
    blank: i64,
    cases: Vec<LexCase>,
}

fn load_lexicon() -> (LexiconTokeniser, LexFixture) {
    let fixture: LexFixture = read_fixture("voice_lexicon_tokeniser.json");
    let tokens_text = fixture
        .tokens
        .iter()
        .map(|(s, id)| format!("{s} {id}"))
        .collect::<Vec<_>>()
        .join("\n");
    let lexicon_text = fixture
        .lexicon
        .iter()
        .map(|e| format!("{} {}", e.word, e.phonemes.join(" ")))
        .collect::<Vec<_>>()
        .join("\n");
    let lex = LexiconTokeniser::from_text(&tokens_text, &lexicon_text, fixture.blank)
        .expect("fixture lexicon failed to load");
    (lex, fixture)
}

#[test]
fn lexicon_tokeniser_matches_reference() {
    let (mut lex, fixture) = load_lexicon();
    assert!(!fixture.cases.is_empty(), "fixture has no cases");

    for c in &fixture.cases {
        let bare = lex.encode(&c.text, false);
        assert_eq!(bare, c.ids, "ids for {}", c.text);
        assert_eq!(lex.last_unmapped, c.unmapped, "unmapped for {}", c.text);
        let padded = lex.encode(&c.text, true);
        assert_eq!(padded, c.ids_with_blank, "idsWithBlank for {}", c.text);
    }
}

#[test]
fn lexicon_takes_the_longest_match() {
    // あい, あいさつ and あいかわらず all start the same way. Taking the shortest
    // pronounces a different word.
    let (mut lex, _) = load_lexicon();
    let full = lex.encode("あいさつ", false);
    let short = lex.encode("あい", false);
    assert!(
        full.len() > short.len(),
        "あいさつ matched only the あい prefix — this is shortest-match"
    );
}

// ── AudioFormat ─────────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct Pcm {
    #[serde(rename = "sampleRate")]
    sample_rate: i32,
    channels: i32,
    #[serde(rename = "bitsPerSample")]
    bits_per_sample: i32,
}

#[derive(Deserialize)]
struct AudioFormatFixture {
    #[serde(rename = "pcm16Mono16k")]
    pcm16_mono_16k: Pcm,
}

#[test]
fn audio_format_matches_reference() {
    let fixture: AudioFormatFixture = read_fixture("voice_audio_format.json");
    assert_eq!(AudioFormat::PCM16_MONO_16K.sample_rate, fixture.pcm16_mono_16k.sample_rate);
    assert_eq!(AudioFormat::PCM16_MONO_16K.channels, fixture.pcm16_mono_16k.channels);
    assert_eq!(AudioFormat::PCM16_MONO_16K.bits_per_sample, fixture.pcm16_mono_16k.bits_per_sample);
}
