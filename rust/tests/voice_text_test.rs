//! voice_text_test.rs
//!
//! Asserts the Rust SentenceSplitter / LanguageSpanSplitter / GeezRomanizer /
//! ToneShaper / NchltPhonemizer ports against the same golden files the C#
//! reference generates.
//!
//! Every case in these fixtures is adversarial. The splitter fixture carries a
//! decimal point and a domain name that must NOT split next to a danda and a
//! CJK stop that must; the Ge'ez fixture carries the numerals that used to
//! romanise as syllables; the tone fixture separates the biquad
//! (bit-reproducible) from the coefficient derivation (pow/sin/cos, which no
//! language guarantees to the last bit).

use circle_ai::voice_text::{
    apply_tone_shaper, biquad, is_ethiopic, is_foreign_word, low_shelf_coefficients,
    peaking_coefficients, romanize, split_language_spans, split_sentences, to_spoken_form,
    BiquadCoefficients, NchltPhonemizer, ToneShaperSettings, MAX_CHARS_PER_SEGMENT,
    WARM_TONE_SHAPER,
};
use serde::Deserialize;
use std::path::PathBuf;

fn read_fixture<T: for<'de> Deserialize<'de>>(name: &str) -> T {
    let path = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("rust/ has a parent")
        .join("fixtures")
        .join(name);
    let data = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("failed to read {}: {e}", path.display()));
    serde_json::from_str(&data)
        .unwrap_or_else(|e| panic!("failed to parse {}: {e}", path.display()))
}

fn assert_close(got: f64, want: f64, tol: f64, what: &str) {
    let scale = want.abs().max(1.0);
    assert!(
        (got - want).abs() <= tol * scale,
        "{what}: got {got}, want {want} (tolerance {tol})"
    );
}

// ── SentenceSplitter ────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct SegmentJson {
    text: String,
    #[serde(rename = "trailingPauseMs")]
    trailing_pause_ms: i32,
}

#[derive(Deserialize)]
struct SplitterCase {
    name: String,
    text: String,
    segments: Vec<SegmentJson>,
}

#[derive(Deserialize)]
struct SplitterFixture {
    #[serde(rename = "maxCharsPerSegment")]
    max_chars_per_segment: usize,
    cases: Vec<SplitterCase>,
}

#[test]
fn sentence_splitter_matches_reference() {
    let f: SplitterFixture = read_fixture("voice_sentence_splitter.json");
    assert_eq!(MAX_CHARS_PER_SEGMENT, f.max_chars_per_segment);

    for c in &f.cases {
        let got = split_sentences(&c.text);
        assert_eq!(got.len(), c.segments.len(), "segment count for {}", c.name);
        for (i, want) in c.segments.iter().enumerate() {
            assert_eq!(got[i].text, want.text, "{} segment {i} text", c.name);
            assert_eq!(
                got[i].trailing_pause_ms, want.trailing_pause_ms,
                "{} segment {i} pause",
                c.name
            );
        }
    }
}

#[test]
fn splits_scripts_that_do_not_punctuate_in_latin() {
    // A Latin-only terminator list under-splits for about a billion people and
    // fails silently — the paragraph simply runs together.
    let f: SplitterFixture = read_fixture("voice_sentence_splitter.json");
    for name in ["devanagari-danda", "urdu-full-stop", "cjk-no-space", "khmer-khan"] {
        let c = f.cases.iter().find(|c| c.name == name).expect("case present");
        assert!(split_sentences(&c.text).len() > 1, "{name} must split");
    }
}

#[test]
fn does_not_split_a_decimal_or_a_domain() {
    let f: SplitterFixture = read_fixture("voice_sentence_splitter.json");
    for name in ["decimal-point", "domain-name"] {
        let c = f.cases.iter().find(|c| c.name == name).expect("case present");
        assert_eq!(split_sentences(&c.text).len(), 2, "{name}");
    }
}

#[test]
fn last_segment_has_no_trailing_pause() {
    let f: SplitterFixture = read_fixture("voice_sentence_splitter.json");
    for c in &f.cases {
        if let Some(last) = split_sentences(&c.text).last() {
            assert_eq!(last.trailing_pause_ms, 0, "{}", c.name);
        }
    }
}

// ── LanguageSpanSplitter ────────────────────────────────────────────────────

#[derive(Deserialize)]
struct SpanJson {
    text: String,
    #[serde(rename = "isForeign")]
    is_foreign: bool,
}

#[derive(Deserialize)]
struct SpansFixture {
    split: Vec<SplitCaseJson>,
    #[serde(rename = "toSpokenForm")]
    to_spoken_form: Vec<SpokenCaseJson>,
    #[serde(rename = "isForeignWord")]
    is_foreign_word: Vec<ForeignCaseJson>,
}

#[derive(Deserialize)]
struct SplitCaseJson {
    text: String,
    spans: Vec<SpanJson>,
}

#[derive(Deserialize)]
struct SpokenCaseJson {
    input: String,
    output: String,
}

#[derive(Deserialize)]
struct ForeignCaseJson {
    word: String,
    foreign: bool,
}

#[test]
fn language_spans_match_reference() {
    let f: SpansFixture = read_fixture("voice_language_spans.json");

    for c in &f.split {
        let got = split_language_spans(&c.text);
        assert_eq!(got.len(), c.spans.len(), "span count for {}", c.text);
        for (i, want) in c.spans.iter().enumerate() {
            assert_eq!(got[i].text, want.text, "{} span {i} text", c.text);
            assert_eq!(got[i].is_foreign, want.is_foreign, "{} span {i} flag", c.text);
        }
    }

    for c in &f.to_spoken_form {
        assert_eq!(to_spoken_form(&c.input), c.output, "spoken form of {}", c.input);
    }

    for c in &f.is_foreign_word {
        assert_eq!(is_foreign_word(&c.word), c.foreign, "is_foreign_word({})", c.word);
    }

    // The conservatism is the contract, not an accident: an ordinary lowercase
    // English word must NOT be flagged, because guessing wrong mispronounces a
    // native word to fix a foreign one.
    assert!(!is_foreign_word("hello"));
    assert!(!is_foreign_word("Ngiyabonga"));
}

// ── GeezRomanizer ───────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct EthiopicCase {
    text: String,
    ethiopic: bool,
}

#[derive(Deserialize)]
struct RomanizeCase {
    input: String,
    output: String,
}

#[derive(Deserialize)]
struct GeezFixture {
    #[serde(rename = "isEthiopic")]
    is_ethiopic: Vec<EthiopicCase>,
    romanize: Vec<RomanizeCase>,
}

#[test]
fn geez_romanizer_matches_reference() {
    let f: GeezFixture = read_fixture("voice_geez_romanizer.json");

    for c in &f.is_ethiopic {
        assert_eq!(is_ethiopic(&c.text), c.ethiopic, "is_ethiopic({})", c.text);
    }
    for c in &f.romanize {
        assert_eq!(romanize(&c.input), c.output, "romanize({})", c.input);
    }
}

#[test]
fn numerals_are_dropped_not_spoken() {
    // The eight-per-consonant layout stops at U+1357. Sizing the range check off
    // the consonant table swept seven numerals back into the syllabary, and they
    // came out as sound, so nothing failed.
    assert_eq!(romanize("፩፪፫"), "");
    assert_eq!(
        romanize("ፘፙፚ"),
        "ryamyafya",
        "the three LONE syllables are not a row of eight"
    );
}

// ── ToneShaper ──────────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct CoeffPairJson {
    b: Vec<f64>,
    a: Vec<f64>,
}

#[derive(Deserialize)]
struct CoeffEntryJson {
    #[serde(rename = "sampleRate")]
    sample_rate: u32,
    #[serde(rename = "lowShelf")]
    low_shelf: CoeffPairJson,
    peaking: CoeffPairJson,
}

#[derive(Deserialize)]
struct SettingsJson {
    #[serde(rename = "lowShelfHz")]
    low_shelf_hz: f64,
    #[serde(rename = "lowShelfDb")]
    low_shelf_db: f64,
    #[serde(rename = "presenceHz")]
    presence_hz: f64,
    #[serde(rename = "presenceDb")]
    presence_db: f64,
    #[serde(rename = "presenceQ")]
    presence_q: f64,
    #[serde(rename = "lowShelfSlope")]
    low_shelf_slope: f64,
}

#[derive(Deserialize)]
struct WaveformJson {
    #[serde(rename = "sampleRate")]
    sample_rate: u32,
    input: Vec<f64>,
    output: Vec<f64>,
}

#[derive(Deserialize)]
struct ToneFixture {
    #[serde(rename = "waveformTolerance")]
    waveform_tolerance: f64,
    #[serde(rename = "coefficientTolerance")]
    coefficient_tolerance: f64,
    settings: SettingsJson,
    coefficients: Vec<CoeffEntryJson>,
    waveform: WaveformJson,
    #[serde(rename = "silenceStaysSilent")]
    silence_stays_silent: Vec<f64>,
}

fn to_coeffs(p: &CoeffPairJson) -> BiquadCoefficients {
    BiquadCoefficients {
        b: [p.b[0], p.b[1], p.b[2]],
        a: [p.a[0], p.a[1], p.a[2]],
    }
}

#[test]
fn tone_shaper_uses_the_measured_settings() {
    // Field by field, and NOT against the whole fixture object: the shelf slope
    // is a private constant of the filter, not a setting anyone may pass in.
    let f: ToneFixture = read_fixture("voice_tone_shaper.json");
    let w: ToneShaperSettings = WARM_TONE_SHAPER;
    assert_eq!(w.low_shelf_hz, f.settings.low_shelf_hz);
    assert_eq!(w.low_shelf_db, f.settings.low_shelf_db);
    assert_eq!(w.presence_hz, f.settings.presence_hz);
    assert_eq!(w.presence_db, f.settings.presence_db);
    assert_eq!(w.presence_q, f.settings.presence_q);
    assert_eq!(f.settings.low_shelf_slope, 0.9);
    // The default must be the measured setting, not a fresh set of numbers.
    assert_eq!(ToneShaperSettings::default(), WARM_TONE_SHAPER);
}

#[test]
fn tone_shaper_derives_the_same_coefficients() {
    // 1e-9 relative, not exact: pow, sin and cos are not bit-identical across
    // languages, and pretending otherwise makes a flaky test, not a strict one.
    let f: ToneFixture = read_fixture("voice_tone_shaper.json");
    for c in &f.coefficients {
        let ls = low_shelf_coefficients(&WARM_TONE_SHAPER, c.sample_rate);
        let pk = peaking_coefficients(&WARM_TONE_SHAPER, c.sample_rate);
        for i in 0..3 {
            assert_close(ls.b[i], c.low_shelf.b[i], f.coefficient_tolerance, "lowShelf b");
            assert_close(ls.a[i], c.low_shelf.a[i], f.coefficient_tolerance, "lowShelf a");
            assert_close(pk.b[i], c.peaking.b[i], f.coefficient_tolerance, "peaking b");
            assert_close(pk.a[i], c.peaking.a[i], f.coefficient_tolerance, "peaking a");
        }
    }
}

#[test]
fn tone_shaper_filters_the_fixture_waveform_identically() {
    // The biquad is add and multiply on doubles, so THIS half is expected to
    // agree everywhere. Driving it from the fixture's own coefficients keeps the
    // transcendental functions out of the comparison.
    let f: ToneFixture = read_fixture("voice_tone_shaper.json");
    let entry = f
        .coefficients
        .iter()
        .find(|c| c.sample_rate == f.waveform.sample_rate)
        .expect("coefficients for the waveform's rate");

    let mut x: Vec<f32> = f.waveform.input.iter().map(|&v| v as f32).collect();
    let peak = |v: &[f32]| v.iter().fold(0.0f32, |p, s| p.max(s.abs()));

    let before = peak(&x);
    biquad(&mut x, &to_coeffs(&entry.low_shelf));
    biquad(&mut x, &to_coeffs(&entry.peaking));
    let after = peak(&x);
    if after > 0.0 && after > before {
        let g = before / after;
        for s in x.iter_mut() {
            *s *= g;
        }
    }

    for (i, &want) in f.waveform.output.iter().enumerate() {
        assert_close(x[i] as f64, want, f.waveform_tolerance, &format!("sample {i}"));
    }
}

#[test]
fn silence_stays_silent() {
    let f: ToneFixture = read_fixture("voice_tone_shaper.json");
    let mut silence = vec![0.0f32; f.silence_stays_silent.len()];
    apply_tone_shaper(&mut silence, f.waveform.sample_rate, &WARM_TONE_SHAPER);
    for (i, &want) in f.silence_stays_silent.iter().enumerate() {
        assert_eq!(silence[i] as f64, want, "silence {i}");
    }
}

#[test]
fn both_filters_are_applied() {
    // A port that dropped the presence dip would still change the waveform, so
    // "it moved" proves nothing — the two stages must differ from each other.
    let f: ToneFixture = read_fixture("voice_tone_shaper.json");
    let input: Vec<f32> = f.waveform.input.iter().map(|&v| v as f32).collect();

    let mut both = input.clone();
    let mut only_shelf = input.clone();
    apply_tone_shaper(&mut both, f.waveform.sample_rate, &WARM_TONE_SHAPER);
    biquad(
        &mut only_shelf,
        &low_shelf_coefficients(&WARM_TONE_SHAPER, f.waveform.sample_rate),
    );

    assert!(
        both.iter().zip(&only_shelf).any(|(a, b)| (a - b).abs() > 1e-4),
        "the presence dip made no difference — it was not applied"
    );
}

// ── NchltPhonemizer ─────────────────────────────────────────────────────────

#[derive(Deserialize)]
struct NchltCase {
    name: String,
    text: String,
    phones: Vec<String>,
    #[serde(rename = "rulePredictedWords")]
    rule_predicted_words: usize,
    #[serde(rename = "unknownGraphemes")]
    unknown_graphemes: Vec<String>,
}

#[derive(Deserialize)]
struct PredictCase {
    word: String,
    phones: Vec<String>,
}

#[derive(Deserialize)]
struct NchltFixture {
    dict: String,
    rules: String,
    #[serde(rename = "phoneMap")]
    phone_map: String,
    #[serde(rename = "graphMap")]
    graph_map: String,
    gnulls: String,
    cases: Vec<NchltCase>,
    #[serde(rename = "predictWord")]
    predict_word: Vec<PredictCase>,
}

impl NchltFixture {
    fn make(&self) -> NchltPhonemizer {
        NchltPhonemizer::from_text(
            &self.dict,
            &self.rules,
            &self.phone_map,
            Some(&self.graph_map),
            Some(&self.gnulls),
        )
    }
}

#[test]
fn nchlt_matches_reference() {
    let f: NchltFixture = read_fixture("voice_nchlt_phonemizer.json");

    for c in &f.cases {
        let mut p = f.make();
        assert_eq!(p.phonemize(&c.text), c.phones, "phones for {}", c.name);
        assert_eq!(
            p.last_rule_predicted_words, c.rule_predicted_words,
            "ruleWords for {}",
            c.name
        );
        assert_eq!(
            p.last_unknown_graphemes, c.unknown_graphemes,
            "unknown for {}",
            c.name
        );
    }

    for c in &f.predict_word {
        let mut p = f.make();
        assert_eq!(p.predict_word(&c.word), c.phones, "predict_word({})", c.word);
    }
}

#[test]
fn the_dictionary_beats_the_rules() {
    // Both paths can pronounce this word. The dictionary must win, and the rule
    // counter must show it did — the counter is the only evidence of which path
    // ran, and a port that always predicted would still return sensible phones.
    let f: NchltFixture = read_fixture("voice_nchlt_phonemizer.json");
    let mut p = f.make();
    p.phonemize("sawubona");
    assert_eq!(p.last_rule_predicted_words, 0, "a catalogued word must not be predicted");
}

#[test]
fn an_unknown_grapheme_is_reported_not_guessed() {
    let f: NchltFixture = read_fixture("voice_nchlt_phonemizer.json");
    let mut p = f.make();
    p.phonemize("azb");
    assert_eq!(p.last_unknown_graphemes, vec!["z"]);
}
