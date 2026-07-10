"""test_theory_of_mind.py

Verifies BeliefTrackerTheoryOfMind (ITheoryOfMind) ported from
CircleAI.Companion — bag-of-belief inference with positional confidence decay.

The headline guarantee is byte-for-byte parity of ``likely_belief_json`` with
.NET's ``JsonSerializer.Serialize(Dictionary<string,double>)`` — including its
HTML-sensitive escaping (``&`` -> ``\\u0026``, ``<``/``>`` -> ``\\u003C``/
``\\u003E``), whole-number doubles as integers, and shortest-round-trip doubles.
Driven by the shared cross-language golden fixture fixtures/theory_of_mind.json.
Mirrors the C# reference (HerJarvisRealImplementations.cs).
"""
from __future__ import annotations

import json
import pathlib

import pytest

from circle_ai.companion.herjarvis_contracts import ITheoryOfMind, OtherMindEstimate
from circle_ai.companion.theory_of_mind import BeliefTrackerTheoryOfMind

_FIXTURE = json.loads(
    (pathlib.Path(__file__).parent.parent.parent / "fixtures" / "theory_of_mind.json").read_text(
        encoding="utf-8"
    )
)

tom = BeliefTrackerTheoryOfMind()


# ── contract ──────────────────────────────────────────────────────────────


def test_implements_interface() -> None:
    assert isinstance(tom, ITheoryOfMind)


async def test_rejects_blank_target() -> None:
    with pytest.raises(ValueError):
        await tom.estimate_async("", "history")
    with pytest.raises(ValueError):
        await tom.estimate_async("   ", "history")


async def test_rejects_none_history() -> None:
    with pytest.raises(ValueError):
        await tom.estimate_async("alice", None)  # type: ignore[arg-type]


# ── golden fixture parity (byte-for-byte with the C# reference) ────────────


@pytest.mark.parametrize("case", _FIXTURE["cases"], ids=lambda c: c["target"])
async def test_matches_golden_fixture(case: dict) -> None:
    est = await tom.estimate_async(case["target"], case["interactionHistoryJson"])
    assert est.target_identifier == case["target"]
    # Byte-for-byte JSON parity — this is the load-bearing wire-format assertion.
    assert est.likely_belief_json == case["expectedLikelyBeliefJson"]
    assert est.confidence == pytest.approx(case["expectedConfidence"], abs=1e-12)


# ── targeted behaviour ────────────────────────────────────────────────────


async def test_no_mental_state_verbs_yields_empty_and_zero_confidence() -> None:
    est = await tom.estimate_async("nobody", "The weather is fine and the sky is clear.")
    assert est.likely_belief_json == "{}"
    assert est.confidence == 0.0


async def test_believe_is_weighted_higher_than_soft_verbs() -> None:
    # A single "believes" contributes weight 1.0 at idx 0 -> value exactly 1.
    strong = await tom.estimate_async("x", "X believes Y")
    assert strong.likely_belief_json == '{"believes:Y":1}'
    # A single "wants" contributes weight 0.7 at idx 0.
    soft = await tom.estimate_async("x", "X wants Y")
    assert soft.likely_belief_json == '{"wants:Y":0.7}'


async def test_positional_decay_reduces_later_beliefs() -> None:
    est = await tom.estimate_async("x", "X believes A. X believes B. X believes C.")
    beliefs = json.loads(est.likely_belief_json)
    # idx 0: 1.0, idx 1: 1/1.1, idx 2: 1/1.2 — strictly decreasing.
    assert beliefs["believes:A"] > beliefs["believes:B"] > beliefs["believes:C"]
    assert beliefs["believes:A"] == pytest.approx(1.0)


async def test_repeated_identical_belief_accumulates() -> None:
    est = await tom.estimate_async("x", "X believes Q. X believes Q. X believes Q.")
    beliefs = json.loads(est.likely_belief_json)
    # Single key, summed across the three decayed contributions.
    assert list(beliefs.keys()) == ["believes:Q"]
    assert beliefs["believes:Q"] == pytest.approx(1.0 + 1.0 / 1.1 + 1.0 / 1.2)


async def test_confidence_is_capped_at_one() -> None:
    # Many strong beliefs push the raw sum over 5 -> confidence clamps to 1.0.
    history = " ".join(f"X believes claim{i}." for i in range(20))
    est = await tom.estimate_async("x", history)
    assert est.confidence == 1.0


async def test_html_sensitive_chars_are_escaped_like_dotnet() -> None:
    est = await tom.estimate_async("erin", "Erin wants tea & coffee <both>.")
    # & < > escape to & < > (uppercase hex), matching STJ.
    assert est.likely_belief_json == '{"wants:tea \\u0026 coffee \\u003Cboth\\u003E":0.7}'


async def test_non_ascii_escapes_to_uppercase_hex() -> None:
    est = await tom.estimate_async("x", "X wants café")
    # é (U+00E9) -> é with UPPERCASE hex digits (Python's json would lowercase).
    assert est.likely_belief_json == '{"wants:caf\\u00E9":0.7}'


async def test_double_quote_in_claim_escapes_as_u0022() -> None:
    est = await tom.estimate_async('x', 'X thinks "quoted" is fine')
    assert est.likely_belief_json == '{"thinks:\\u0022quoted\\u0022 is fine":0.7}'


async def test_claim_terminates_at_sentence_punctuation() -> None:
    # The claim capture group is [^.;!?]+, so it stops at the first . ; ! ?
    est = await tom.estimate_async("x", "X wants coffee; then tea")
    beliefs = json.loads(est.likely_belief_json)
    assert "wants:coffee" in beliefs
    assert not any("tea" in k for k in beliefs)


def test_other_mind_estimate_is_frozen() -> None:
    e = OtherMindEstimate("x", "{}", 0.0)
    with pytest.raises(Exception):
        e.confidence = 0.5  # type: ignore[misc]
