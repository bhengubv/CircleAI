"""test_voice_parity.py

Asserts the Python voice port against the SAME golden files the C# reference
generates (tools/voice-fixtures). Not "does Python do something sensible" —
"does Python produce identical answers to every other port".

The fixtures are adversarial on purpose: the SentencePiece vocabulary is built
so greedy longest-match and Viterbi DISAGREE, and the X-SAMPA cases carry a
multi-character token, the script-g that is U+0261 rather than ASCII 'g', and a
phone that cannot map and must be REPORTED rather than dropped.
"""
from __future__ import annotations

import base64
import json
import pathlib

import pytest

from circle_ai.voice_wav import parse_wav, to_mono_24k
from circle_ai.voice_xsampa import (
    SentencePieceUnigram,
    xsampa_can_say_all,
    xsampa_known_phones,
    xsampa_to_ipa,
)

# tests/ -> python/ -> CircleAI/ -> fixtures/
FIXTURES = pathlib.Path(__file__).resolve().parents[2] / "fixtures"


def _read(name: str) -> dict:
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def xsampa_fixture() -> dict:
    return _read("voice_xsampa_to_ipa.json")


@pytest.fixture(scope="module")
def sp_fixture() -> dict:
    return _read("voice_sentencepiece_unigram.json")


@pytest.fixture(scope="module")
def sp(sp_fixture: dict) -> SentencePieceUnigram:
    return SentencePieceUnigram(sp_fixture["vocab"], sp_fixture["scores"])


# ── X-SAMPA → IPA ───────────────────────────────────────────────────────────


def test_xsampa_to_ipa_matches_reference(xsampa_fixture: dict) -> None:
    cases = xsampa_fixture["cases"]
    assert cases, "fixture has no cases"
    for case in cases:
        ipa, unmapped = xsampa_to_ipa(case["xsampa"])
        assert ipa == case["ipa"], f"ipa for {case['xsampa']}"
        assert unmapped == case["unmapped"], f"unmapped for {case['xsampa']}"
        assert xsampa_can_say_all(case["xsampa"]) == case["canSayAll"], (
            f"canSayAll for {case['xsampa']}"
        )


def test_xsampa_known_phones_match_reference(xsampa_fixture: dict) -> None:
    assert set(xsampa_known_phones()) == set(xsampa_fixture["knownPhones"]), (
        "the phone table itself has drifted from the reference"
    )


def test_script_g_is_u0261_not_ascii_g() -> None:
    # Called out on its own because it is invisible in a diff: the voice's
    # vocabulary carries ɡ (U+0261) and a plain ASCII 'g' silently misses.
    ipa, _ = xsampa_to_ipa(["g"])
    assert ipa == ["ɡ"]
    assert ipa != ["g"], "ASCII g would be dropped by the voice"


# ── SentencePiece unigram ───────────────────────────────────────────────────


def test_sentencepiece_matches_reference(sp: SentencePieceUnigram, sp_fixture: dict) -> None:
    cases = sp_fixture["cases"]
    assert cases, "fixture has no cases"
    for case in cases:
        assert sp.encode(case["text"]) == case["ids"], f"ids for {case['text']!r}"


def test_viterbi_not_greedy(sp: SentencePieceUnigram, sp_fixture: dict) -> None:
    # The fixture vocabulary is built so the two disagree: "▁hello" scores WORSE
    # than "▁hell" + "o". Greedy picks the long piece; Viterbi does not. Without
    # this, a greedy port looks correct.
    vocab = sp_fixture["vocab"]
    want = [vocab["▁hell"], vocab["o"], vocab["▁world"]]
    greedy = [vocab["▁hello"], vocab["▁world"]]

    got = sp.encode("hello world")
    assert got == want
    assert got != greedy, "this is the greedy answer — the port is not doing Viterbi"


def test_byte_fallback_keeps_utf8_order(sp: SentencePieceUnigram, sp_fixture: dict) -> None:
    # é is UTF-8 C3 A9. Emitting A9 C3 does not raise — both are real pieces with
    # real ids — the model just says a different character, and only outside
    # ASCII, which is exactly the languages this catalogue serves.
    vocab = sp_fixture["vocab"]
    got = sp.encode("hé")
    assert len(got) >= 2, f"expected byte fallback pieces, got {got}"
    assert got[-2:] == [vocab["<0xC3>"], vocab["<0xA9>"]], (
        "byte fallback emitted UTF-8 bytes in the wrong order"
    )


def test_empty_text_encodes_to_nothing(sp: SentencePieceUnigram) -> None:
    assert sp.encode("") == []


# ── WAV I/O ─────────────────────────────────────────────────────────────────


@pytest.fixture(scope="module")
def wav_fixture() -> dict:
    return _read("voice_wav_io.json")


def test_wav_io_matches_reference(wav_fixture: dict) -> None:
    cases = wav_fixture["cases"]
    assert cases, "fixture has no cases"
    for case in cases:
        raw = base64.b64decode(case["wavBase64"])
        mono = to_mono_24k(parse_wav(raw))
        expected = case["expected"]
        assert len(mono) == expected["sampleCount"], f"sampleCount for {case['name']}"
        for i, want in enumerate(expected["samples"]):
            assert abs(mono[i] - want) < 1e-6, f"sample {i} of {case['name']}"


def test_wav_io_walks_chunks_rather_than_assuming_byte_44(wav_fixture: dict) -> None:
    # The LIST-chunk case is the one that matters: a reader that assumes data
    # starts at byte 44 reads metadata as audio.
    cases = {c["name"]: c for c in wav_fixture["cases"]}
    plain = next(c for n, c in cases.items() if "plain" in n)
    listed = next(c for n, c in cases.items() if "LIST" in n)

    a = parse_wav(base64.b64decode(plain["wavBase64"])).samples
    b = parse_wav(base64.b64decode(listed["wavBase64"])).samples
    assert a == b, "a LIST chunk before the data changed the decoded audio"
