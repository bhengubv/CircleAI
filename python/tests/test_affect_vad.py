"""test_affect_vad.py

Validates AffectVad derivation against all vectors in
fixtures/affect_vad_derivation.json. Math must be byte-identical across
language ports — see the fixture's $comment for the contract.
"""
from __future__ import annotations

import json
import math
import pathlib

import pytest

from circle_ai.memory.affect_state import AffectState, AffectVad

FIXTURES_DIR = pathlib.Path(__file__).parent.parent.parent / "fixtures"


def _load_fixture() -> dict:
    with open(FIXTURES_DIR / "affect_vad_derivation.json", encoding="utf-8") as f:
        return json.load(f)


FIXTURE = _load_fixture()
EPSILON = float(FIXTURE["epsilon"])
VECTORS = FIXTURE["vectors"]


def _make_state(inp: dict) -> AffectState:
    state = AffectState()
    state.curiosity   = float(inp["curiosity"])
    state.engagement  = float(inp["engagement"])
    state.uncertainty = float(inp["uncertainty"])
    state.rapport     = float(inp["rapport"])
    state.energy      = float(inp["energy"])
    return state


@pytest.mark.parametrize("vector", VECTORS, ids=[v["id"] for v in VECTORS])
def test_affect_vad_vector(vector: dict) -> None:
    """from_state() must match the canonical derivation for every fixture vector."""
    state = _make_state(vector["input"])
    vad = AffectVad.from_state(state)
    expected = vector["expected"]

    assert math.isclose(vad.valence, float(expected["valence"]), abs_tol=EPSILON), (
        f"[{vector['id']}] valence mismatch: "
        f"got {vad.valence}, expected {expected['valence']}"
    )
    assert math.isclose(vad.arousal, float(expected["arousal"]), abs_tol=EPSILON), (
        f"[{vector['id']}] arousal mismatch: "
        f"got {vad.arousal}, expected {expected['arousal']}"
    )
    assert math.isclose(vad.dominance, float(expected["dominance"]), abs_tol=EPSILON), (
        f"[{vector['id']}] dominance mismatch: "
        f"got {vad.dominance}, expected {expected['dominance']}"
    )


def test_to_vad_returns_same_as_from_state() -> None:
    """AffectState.to_vad() must be equivalent to AffectVad.from_state(state)."""
    state = AffectState()
    state.curiosity   = 0.6
    state.engagement  = 0.9
    state.uncertainty = 0.1
    state.rapport     = 0.8
    state.energy      = 0.7

    via_method = state.to_vad()
    via_factory = AffectVad.from_state(state)

    assert via_method == via_factory
