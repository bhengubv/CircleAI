"""test_voice_piper.py

Asserts the Python PiperVoiceConfig / LexiconTokeniser / AudioFormat ports
against the same golden files the C# reference generates.

The piper fixture carries TWO configs on purpose — one with pad 0 and one with
pad 3 — so a port that hard-codes either fails on the other. That is THE PAD
RULE, and getting it wrong is what made 42 MMS voices speak fluent nonsense.
"""
from __future__ import annotations

import json
import pathlib

import pytest

from circle_ai.voice import PCM16_MONO_16K
from circle_ai.voice_piper import (
    LexiconTokeniser,
    PiperVoiceConfig,
    split_phoneme_string,
)

# tests/ -> python/ -> CircleAI/ -> fixtures/
FIXTURES = pathlib.Path(__file__).resolve().parents[2] / "fixtures"


def _read(name: str) -> dict:
    return json.loads((FIXTURES / name).read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def piper_fixture() -> dict:
    return _read("voice_piper_config.json")


@pytest.fixture(scope="module")
def lex_fixture() -> dict:
    return _read("voice_lexicon_tokeniser.json")


# ── PiperVoiceConfig ────────────────────────────────────────────────────────


def test_piper_config_matches_reference(piper_fixture: dict) -> None:
    configs = piper_fixture["configs"]
    assert len(configs) == 2, "both pad conventions must be covered"

    for c in configs:
        cfg = PiperVoiceConfig(c["configJson"], sample_rate=c["sampleRate"])
        assert cfg.pad_id == c["padId"], f"padId for {c['name']}"
        assert cfg.has_phoneme_map == c["hasPhonemeMap"], f"hasPhonemeMap for {c['name']}"

        for one in c["cases"]:
            got = cfg.phonemes_to_ids(one["phonemes"])
            assert got.ids == one["ids"], f"ids for {one['phonemes']} in {c['name']}"
            assert got.skipped == one["skipped"], f"skipped for {one['phonemes']}"
            assert got.skipped_symbols == one["skippedSymbols"], (
                f"skippedSymbols for {one['phonemes']}"
            )
            assert got.approximated_symbols == one["approximatedSymbols"], (
                f"approximatedSymbols for {one['phonemes']}"
            )


def test_pad_is_read_from_the_model_not_assumed(piper_fixture: dict) -> None:
    # THE PAD RULE. The two fixture configs disagree — 0 in the Piper-layout one,
    # 3 in the MMS-layout one — so a port that hard-codes either fails the other.
    configs = piper_fixture["configs"]
    assert {c["padId"] for c in configs} == {0, 3}, "the fixture must cover BOTH pad conventions"
    for c in configs:
        assert PiperVoiceConfig(c["configJson"]).pad_id == c["padId"]


def test_thai_is_not_folded_but_tshivenda_is(piper_fixture: dict) -> None:
    # The asymmetry is the whole point. Latin ṱ still sounds like a t with the
    # mark gone; Thai ก's marks ARE the vowels, so folding deletes the word
    # rather than approximating it.
    cfg = PiperVoiceConfig(piper_fixture["configs"][0]["configJson"])
    assert cfg.phonemes_to_ids(["ṱ"]).approximated_symbols == ["ṱ"], (
        "ṱ should fold to a Latin base and be REPORTED as approximate"
    )
    assert cfg.phonemes_to_ids(["ก"]).skipped_symbols == ["ก"], "Thai must be skipped, not folded"


def test_split_phoneme_string_matches_reference(piper_fixture: dict) -> None:
    for c in piper_fixture["splitPhonemeString"]:
        assert split_phoneme_string(c["input"]) == c["elements"], f"clusters for {c['input']}"


# ── LexiconTokeniser ────────────────────────────────────────────────────────


def _make_lexicon(fixture: dict) -> LexiconTokeniser:
    tokens_text = "\n".join(f"{s} {i}" for s, i in fixture["tokens"].items())
    lexicon_text = "\n".join(
        f"{e['word']} {' '.join(e['phonemes'])}" for e in fixture["lexicon"]
    )
    lex = LexiconTokeniser.from_text(tokens_text, lexicon_text, fixture["blank"])
    assert lex is not None, "fixture lexicon failed to load"
    return lex


def test_lexicon_tokeniser_matches_reference(lex_fixture: dict) -> None:
    lex = _make_lexicon(lex_fixture)
    assert lex_fixture["cases"], "fixture has no cases"
    for c in lex_fixture["cases"]:
        assert lex.encode(c["text"], interleave_blank=False) == c["ids"], f"ids for {c['text']}"
        assert lex.last_unmapped == c["unmapped"], f"unmapped for {c['text']}"
        assert lex.encode(c["text"], interleave_blank=True) == c["idsWithBlank"], (
            f"idsWithBlank for {c['text']}"
        )


def test_lexicon_takes_the_longest_match(lex_fixture: dict) -> None:
    # あい, あいさつ and あいかわらず all start the same way. Taking the shortest
    # pronounces a different word.
    lex = _make_lexicon(lex_fixture)
    full = lex.encode("あいさつ", interleave_blank=False)
    short = lex.encode("あい", interleave_blank=False)
    assert len(full) > len(short), (
        "あいさつ matched only the あい prefix — this is shortest-match"
    )


# ── AudioFormat ─────────────────────────────────────────────────────────────


def test_audio_format_matches_reference() -> None:
    want = _read("voice_audio_format.json")["pcm16Mono16k"]
    assert PCM16_MONO_16K.sample_rate == want["sampleRate"]
    assert PCM16_MONO_16K.channels == want["channels"]
    assert PCM16_MONO_16K.bits_per_sample == want["bitsPerSample"]
