"""test_voice_text.py

Asserts the Python SentenceSplitter / LanguageSpanSplitter / GeezRomanizer /
ToneShaper / NchltPhonemizer ports against the same golden files the C#
reference generates.

Every case in these fixtures is adversarial. The splitter fixture carries a
decimal point and a domain name that must NOT split next to a danda and a CJK
stop that must; the Ge'ez fixture carries the numerals that used to romanise as
syllables; the tone fixture separates the biquad (bit-reproducible) from the
coefficient derivation (pow/sin/cos, which no language guarantees to the last
bit).
"""
from __future__ import annotations

import json
import pathlib
import struct

import pytest

from circle_ai.voice_text import (
    MAX_CHARS_PER_SEGMENT,
    WARM,
    NchltPhonemizer,
    apply_tone_shaper,
    biquad,
    is_ethiopic,
    is_foreign_word,
    low_shelf_coefficients,
    peaking_coefficients,
    romanize,
    split_language_spans,
    split_sentences,
    to_spoken_form,
)

# tests/ -> python/ -> CircleAI/ -> fixtures/
FIXTURES = pathlib.Path(__file__).resolve().parents[2] / "fixtures"


def _read(name: str) -> dict:
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


def _fround(v: float) -> float:
    return struct.unpack("<f", struct.pack("<f", v))[0]


@pytest.fixture(scope="module")
def splitter_fixture() -> dict:
    return _read("voice_sentence_splitter.json")


@pytest.fixture(scope="module")
def spans_fixture() -> dict:
    return _read("voice_language_spans.json")


@pytest.fixture(scope="module")
def geez_fixture() -> dict:
    return _read("voice_geez_romanizer.json")


@pytest.fixture(scope="module")
def tone_fixture() -> dict:
    return _read("voice_tone_shaper.json")


@pytest.fixture(scope="module")
def nchlt_fixture() -> dict:
    return _read("voice_nchlt_phonemizer.json")


# ── SentenceSplitter ────────────────────────────────────────────────────────


def test_sentence_splitter_matches_reference(splitter_fixture: dict) -> None:
    assert MAX_CHARS_PER_SEGMENT == splitter_fixture["maxCharsPerSegment"]
    for c in splitter_fixture["cases"]:
        got = [
            {"text": s.text, "trailingPauseMs": s.trailing_pause_ms}
            for s in split_sentences(c["text"])
        ]
        assert got == c["segments"], f"segments for {c['name']}"


def test_splits_scripts_that_do_not_punctuate_in_latin(splitter_fixture: dict) -> None:
    # A Latin-only terminator list under-splits for about a billion people and
    # fails silently — the paragraph simply runs together.
    by_name = {c["name"]: c for c in splitter_fixture["cases"]}
    for name in ("devanagari-danda", "urdu-full-stop", "cjk-no-space", "khmer-khan"):
        assert len(split_sentences(by_name[name]["text"])) > 1, f"{name} must split"


def test_does_not_split_a_decimal_or_a_domain(splitter_fixture: dict) -> None:
    by_name = {c["name"]: c for c in splitter_fixture["cases"]}
    for name in ("decimal-point", "domain-name"):
        assert len(split_sentences(by_name[name]["text"])) == 2, name


def test_last_segment_has_no_trailing_pause(splitter_fixture: dict) -> None:
    for c in splitter_fixture["cases"]:
        got = split_sentences(c["text"])
        if got:
            assert got[-1].trailing_pause_ms == 0, c["name"]


# ── LanguageSpanSplitter ────────────────────────────────────────────────────


def test_language_spans_match_reference(spans_fixture: dict) -> None:
    for c in spans_fixture["split"]:
        got = [
            {"text": s.text, "isForeign": s.is_foreign}
            for s in split_language_spans(c["text"])
        ]
        assert got == c["spans"], f"spans for {c['text']}"


def test_to_spoken_form_matches_reference(spans_fixture: dict) -> None:
    for c in spans_fixture["toSpokenForm"]:
        assert to_spoken_form(c["input"]) == c["output"], f"spoken form of {c['input']}"


def test_is_foreign_word_matches_reference(spans_fixture: dict) -> None:
    for c in spans_fixture["isForeignWord"]:
        assert is_foreign_word(c["word"]) == c["foreign"], c["word"]
    # The conservatism is the contract, not an accident: an ordinary lowercase
    # English word must NOT be flagged, because guessing wrong mispronounces a
    # native word to fix a foreign one.
    assert is_foreign_word("hello") is False
    assert is_foreign_word("Ngiyabonga") is False


# ── GeezRomanizer ───────────────────────────────────────────────────────────


def test_is_ethiopic_matches_reference(geez_fixture: dict) -> None:
    for c in geez_fixture["isEthiopic"]:
        assert is_ethiopic(c["text"]) == c["ethiopic"], repr(c["text"])


def test_romanize_matches_reference(geez_fixture: dict) -> None:
    for c in geez_fixture["romanize"]:
        assert romanize(c["input"]) == c["output"], repr(c["input"])


def test_numerals_are_dropped_not_spoken() -> None:
    # The eight-per-consonant layout stops at U+1357. Sizing the range check off
    # the consonant table swept seven numerals back into the syllabary, and they
    # came out as sound, so nothing failed.
    assert romanize("፩፪፫") == ""
    assert romanize("ፘፙፚ") == "ryamyafya", "the three LONE syllables are not a row"


# ── ToneShaper ──────────────────────────────────────────────────────────────


def _assert_close(got: float, want: float, tol: float, what: str) -> None:
    scale = max(1.0, abs(want))
    assert abs(got - want) <= tol * scale, f"{what}: got {got}, want {want}"


def test_tone_shaper_settings(tone_fixture: dict) -> None:
    # Field by field, and NOT against the whole fixture object: the shelf slope
    # is a private constant of the filter, not a setting anyone may pass in.
    s = tone_fixture["settings"]
    assert WARM.low_shelf_hz == s["lowShelfHz"]
    assert WARM.low_shelf_db == s["lowShelfDb"]
    assert WARM.presence_hz == s["presenceHz"]
    assert WARM.presence_db == s["presenceDb"]
    assert WARM.presence_q == s["presenceQ"]
    assert s["lowShelfSlope"] == 0.9


def test_tone_shaper_coefficients(tone_fixture: dict) -> None:
    # 1e-9 relative, not exact: pow, sin and cos are not bit-identical across
    # languages, and pretending otherwise makes a flaky test rather than a strict
    # one.
    tol = tone_fixture["coefficientTolerance"]
    for c in tone_fixture["coefficients"]:
        got = {
            "lowShelf": low_shelf_coefficients(WARM, c["sampleRate"]),
            "peaking": peaking_coefficients(WARM, c["sampleRate"]),
        }
        for name in ("lowShelf", "peaking"):
            b, a = got[name]
            for i in range(3):
                _assert_close(b[i], c[name]["b"][i], tol, f"{name} b[{i}] @{c['sampleRate']}")
                _assert_close(a[i], c[name]["a"][i], tol, f"{name} a[{i}] @{c['sampleRate']}")


def test_tone_shaper_waveform(tone_fixture: dict) -> None:
    # The biquad is add and multiply on doubles, so THIS half is expected to
    # agree everywhere. Driving it from the fixture's own coefficients keeps the
    # transcendental functions out of the comparison.
    w = tone_fixture["waveform"]
    coeffs = next(c for c in tone_fixture["coefficients"] if c["sampleRate"] == w["sampleRate"])

    x = [_fround(v) for v in w["input"]]
    before = max(abs(v) for v in x)
    biquad(x, coeffs["lowShelf"]["b"], coeffs["lowShelf"]["a"])
    biquad(x, coeffs["peaking"]["b"], coeffs["peaking"]["a"])
    after = max(abs(v) for v in x)
    if after > 0 and after > before:
        g = _fround(before / after)
        x = [_fround(v * g) for v in x]

    for i, want in enumerate(w["output"]):
        _assert_close(x[i], want, tone_fixture["waveformTolerance"], f"sample {i}")


def test_silence_stays_silent(tone_fixture: dict) -> None:
    silence = [0.0] * len(tone_fixture["silenceStaysSilent"])
    apply_tone_shaper(silence, tone_fixture["waveform"]["sampleRate"])
    assert silence == tone_fixture["silenceStaysSilent"]


def test_both_filters_are_applied(tone_fixture: dict) -> None:
    # A port that dropped the presence dip would still change the waveform, so
    # "it moved" proves nothing — the two stages must differ from each other.
    rate = tone_fixture["waveform"]["sampleRate"]
    x = [_fround(v) for v in tone_fixture["waveform"]["input"]]
    only_shelf = list(x)
    apply_tone_shaper(x, rate)
    b, a = low_shelf_coefficients(WARM, rate)
    biquad(only_shelf, b, a)
    assert any(abs(x[i] - only_shelf[i]) > 1e-4 for i in range(len(x))), (
        "the presence dip made no difference — it was not applied"
    )


# ── NchltPhonemizer ─────────────────────────────────────────────────────────


def _make(fixture: dict) -> NchltPhonemizer:
    return NchltPhonemizer.from_text(
        fixture["dict"], fixture["rules"], fixture["phoneMap"],
        fixture["graphMap"], fixture["gnulls"],
    )


def test_nchlt_matches_reference(nchlt_fixture: dict) -> None:
    for c in nchlt_fixture["cases"]:
        p = _make(nchlt_fixture)
        assert p.phonemize(c["text"]) == c["phones"], f"phones for {c['name']}"
        assert p.last_rule_predicted_words == c["rulePredictedWords"], c["name"]
        assert p.last_unknown_graphemes == c["unknownGraphemes"], c["name"]


def test_nchlt_predict_word(nchlt_fixture: dict) -> None:
    for c in nchlt_fixture["predictWord"]:
        assert _make(nchlt_fixture).predict_word(c["word"]) == c["phones"], c["word"]


def test_dictionary_beats_the_rules(nchlt_fixture: dict) -> None:
    # Both paths can pronounce this word. The dictionary must win, and the rule
    # counter must show it did — the counter is the only evidence of which path
    # ran, and a port that always predicted would still return sensible phones.
    p = _make(nchlt_fixture)
    p.phonemize("sawubona")
    assert p.last_rule_predicted_words == 0


def test_unknown_grapheme_is_reported(nchlt_fixture: dict) -> None:
    p = _make(nchlt_fixture)
    p.phonemize("azb")
    assert p.last_unknown_graphemes == ["z"]
