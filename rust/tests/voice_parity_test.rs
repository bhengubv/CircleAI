//! voice_parity_test.rs
//!
//! Asserts the Rust voice port against the SAME golden files the C# reference
//! generates (`tools/voice-fixtures`). Not "does Rust do something sensible" —
//! "does Rust produce identical answers to every other port".
//!
//! The fixtures are adversarial on purpose: the SentencePiece vocabulary is
//! built so greedy longest-match and Viterbi DISAGREE, and the X-SAMPA cases
//! carry a multi-character token, the script-g that is U+0261 rather than ASCII
//! 'g', and a phone that cannot map and must be REPORTED rather than dropped.

use circle_ai::voice_xsampa::{
    xsampa_can_say_all, xsampa_known_phones, xsampa_to_ipa, SentencePieceUnigram,
};
use serde::Deserialize;
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;

fn fixtures_dir() -> PathBuf {
    // rust/ -> CircleAI/ -> fixtures/
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

// ── X-SAMPA → IPA ───────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct XsampaCase {
    xsampa: Vec<String>,
    ipa: Vec<String>,
    unmapped: Vec<String>,
    #[serde(rename = "canSayAll")]
    can_say_all: bool,
}

#[derive(Deserialize)]
struct XsampaFixture {
    #[serde(rename = "knownPhones")]
    known_phones: Vec<String>,
    cases: Vec<XsampaCase>,
}

#[test]
fn xsampa_to_ipa_matches_reference() {
    let fixture: XsampaFixture = read_fixture("voice_xsampa_to_ipa.json");
    assert!(!fixture.cases.is_empty(), "fixture has no cases");

    for case in &fixture.cases {
        let refs: Vec<&str> = case.xsampa.iter().map(String::as_str).collect();
        let (ipa, unmapped) = xsampa_to_ipa(&refs);
        assert_eq!(ipa, case.ipa, "ipa for {:?}", case.xsampa);
        assert_eq!(unmapped, case.unmapped, "unmapped for {:?}", case.xsampa);
        assert_eq!(
            xsampa_can_say_all(&refs),
            case.can_say_all,
            "canSayAll for {:?}",
            case.xsampa
        );
    }
}

#[test]
fn xsampa_known_phones_match_reference() {
    let fixture: XsampaFixture = read_fixture("voice_xsampa_to_ipa.json");
    let expected: HashSet<String> = fixture.known_phones.into_iter().collect();
    let actual: HashSet<String> = xsampa_known_phones().into_iter().collect();
    assert_eq!(
        actual, expected,
        "the phone table itself has drifted from the reference"
    );
}

#[test]
fn script_g_is_u0261_not_ascii_g() {
    // Called out on its own because it is invisible in a diff: the voice's
    // vocabulary carries ɡ (U+0261) and a plain ASCII 'g' silently misses.
    let (ipa, _) = xsampa_to_ipa(&["g"]);
    assert_eq!(ipa, vec!["\u{0261}".to_string()]);
    assert_ne!(ipa, vec!["g".to_string()], "ASCII g would be dropped by the voice");
}

// ── SentencePiece unigram ───────────────────────────────────────────────────

#[derive(Deserialize)]
struct SpCase {
    text: String,
    ids: Vec<i64>,
}

#[derive(Deserialize)]
struct SpFixture {
    vocab: HashMap<String, i64>,
    scores: HashMap<String, f32>,
    cases: Vec<SpCase>,
}

fn load_sp() -> (SentencePieceUnigram, SpFixture) {
    let fixture: SpFixture = read_fixture("voice_sentencepiece_unigram.json");
    let sp = SentencePieceUnigram::new(fixture.vocab.clone(), fixture.scores.clone());
    (sp, fixture)
}

#[test]
fn sentencepiece_matches_reference() {
    let (sp, fixture) = load_sp();
    assert!(!fixture.cases.is_empty(), "fixture has no cases");
    for case in &fixture.cases {
        assert_eq!(sp.encode(&case.text), case.ids, "ids for {:?}", case.text);
    }
}

#[test]
fn viterbi_not_greedy() {
    // The fixture vocabulary is built so the two disagree: "▁hello" scores WORSE
    // than "▁hell" + "o". Greedy picks the long piece; Viterbi does not. Without
    // this, a greedy port looks correct.
    let (sp, fixture) = load_sp();
    let want = vec![
        fixture.vocab["▁hell"],
        fixture.vocab["o"],
        fixture.vocab["▁world"],
    ];
    let greedy = vec![fixture.vocab["▁hello"], fixture.vocab["▁world"]];

    let got = sp.encode("hello world");
    assert_eq!(got, want);
    assert_ne!(
        got, greedy,
        "this is the greedy answer — the port is not doing Viterbi"
    );
}

#[test]
fn byte_fallback_keeps_utf8_order() {
    // é is UTF-8 C3 A9. Emitting A9 C3 does not panic — both are real pieces with
    // real ids — the model just says a different character, and only outside
    // ASCII, which is exactly the languages this catalogue serves.
    let (sp, fixture) = load_sp();
    let got = sp.encode("hé");
    assert!(got.len() >= 2, "expected byte fallback pieces, got {got:?}");
    let tail = &got[got.len() - 2..];
    assert_eq!(
        tail,
        &[fixture.vocab["<0xC3>"], fixture.vocab["<0xA9>"]],
        "byte fallback emitted UTF-8 bytes in the wrong order"
    );
}

#[test]
fn empty_text_encodes_to_nothing() {
    let (sp, _) = load_sp();
    assert!(sp.encode("").is_empty());
}

// ── WAV I/O ─────────────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct WavExpected {
    #[serde(rename = "sampleCount")]
    sample_count: usize,
    samples: Vec<f32>,
}

#[derive(Deserialize)]
struct WavCase {
    name: String,
    #[serde(rename = "wavBase64")]
    wav_base64: String,
    expected: WavExpected,
}

#[derive(Deserialize)]
struct WavFixture {
    cases: Vec<WavCase>,
}

/// Minimal base64 decoder — the port takes no third-party crates for this.
fn b64(input: &str) -> Vec<u8> {
    const T: &[u8] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    let mut out = Vec::new();
    let (mut acc, mut bits) = (0u32, 0u32);
    for c in input.bytes() {
        if c == b'=' || c.is_ascii_whitespace() {
            continue;
        }
        let v = T.iter().position(|&t| t == c).expect("valid base64") as u32;
        acc = (acc << 6) | v;
        bits += 6;
        if bits >= 8 {
            bits -= 8;
            out.push((acc >> bits) as u8);
        }
    }
    out
}

fn mono(wav: &circle_ai::voice_wav::Wav) -> Vec<f32> {
    let ch = wav.channels as usize;
    if ch <= 1 {
        return wav.samples.clone();
    }
    wav.samples
        .chunks_exact(ch)
        .map(|f| f.iter().sum::<f32>() / ch as f32)
        .collect()
}

#[test]
fn wav_io_matches_reference() {
    let fixture: WavFixture = read_fixture("voice_wav_io.json");
    assert!(!fixture.cases.is_empty(), "fixture has no cases");

    for case in &fixture.cases {
        let wav = circle_ai::voice_wav::parse(&b64(&case.wav_base64))
            .unwrap_or_else(|e| panic!("{}: {e}", case.name));
        let samples = mono(&wav);
        assert_eq!(samples.len(), case.expected.sample_count, "sampleCount for {}", case.name);
        for (i, want) in case.expected.samples.iter().enumerate() {
            assert!(
                (samples[i] - want).abs() < 1e-6,
                "sample {i} of {}: got {}, want {want}",
                case.name,
                samples[i]
            );
        }
    }
}

#[test]
fn wav_io_walks_chunks_rather_than_assuming_byte_44() {
    // The LIST-chunk case is the one that matters: a reader that assumes data
    // starts at byte 44 reads metadata as audio.
    let fixture: WavFixture = read_fixture("voice_wav_io.json");
    let plain = fixture.cases.iter().find(|c| c.name.contains("plain")).unwrap();
    let listed = fixture.cases.iter().find(|c| c.name.contains("LIST")).unwrap();

    let a = circle_ai::voice_wav::parse(&b64(&plain.wav_base64)).unwrap();
    let b = circle_ai::voice_wav::parse(&b64(&listed.wav_base64)).unwrap();
    assert_eq!(a.samples, b.samples, "a LIST chunk before the data changed the decoded audio");
}
