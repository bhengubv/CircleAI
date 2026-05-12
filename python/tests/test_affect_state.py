# test_affect_state.py
#
# Validates AffectState math against all 12 vectors in fixtures/affect_state.json.
# All float comparisons use epsilon = 1e-6 as specified in the fixture schema.

from __future__ import annotations

import json
import pathlib
import pytest

# Adjust sys.path so the package is importable when running pytest from the
# python/ directory (no install required).
import sys
sys.path.insert(0, str(pathlib.Path(__file__).parent.parent / "src"))

from circle_ai.memory import AffectState


# ---------------------------------------------------------------------------
# Fixtures helpers
# ---------------------------------------------------------------------------

FIXTURES_DIR = pathlib.Path(__file__).parent.parent.parent / "fixtures"


def _load_vectors() -> list[dict]:
    with open(FIXTURES_DIR / "affect_state.json", encoding="utf-8") as f:
        data = json.load(f)
    return data["vectors"]


def _make_state(inp: dict) -> AffectState:
    state = AffectState()
    state.curiosity   = float(inp["curiosity"])
    state.engagement  = float(inp["engagement"])
    state.uncertainty = float(inp["uncertainty"])
    state.rapport     = float(inp["rapport"])
    state.energy      = float(inp["energy"])
    return state


EPSILON = 1e-6

VECTORS = _load_vectors()


# ---------------------------------------------------------------------------
# Parametrised test
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("vector", VECTORS, ids=[v["id"] for v in VECTORS])
def test_affect_vector(vector: dict) -> None:
    state = _make_state(vector["input"])
    operation = vector["operation"]
    param = vector.get("operationParam", {})

    if operation == "positive_signal":
        count = param.get("count", 1)
        for _ in range(count):
            state.apply_positive_signal()

    elif operation == "negative_signal":
        count = param.get("count", 1)
        for _ in range(count):
            state.apply_negative_signal()

    elif operation == "positive_then_negative":
        state.apply_positive_signal()
        state.apply_negative_signal()

    elif operation == "negative_then_positive":
        state.apply_negative_signal()
        state.apply_positive_signal()

    elif operation == "idle_decay":
        hours = float(param["hours"])
        state.apply_idle_decay(hours)

    else:
        pytest.fail(f"Unknown operation: {operation!r}")

    expected = vector["expected"]

    assert abs(state.curiosity   - float(expected["curiosity"]))   <= EPSILON, \
        f"[{vector['id']}] curiosity mismatch: {state.curiosity} != {expected['curiosity']}"
    assert abs(state.engagement  - float(expected["engagement"]))  <= EPSILON, \
        f"[{vector['id']}] engagement mismatch: {state.engagement} != {expected['engagement']}"
    assert abs(state.uncertainty - float(expected["uncertainty"])) <= EPSILON, \
        f"[{vector['id']}] uncertainty mismatch: {state.uncertainty} != {expected['uncertainty']}"
    assert abs(state.rapport     - float(expected["rapport"]))     <= EPSILON, \
        f"[{vector['id']}] rapport mismatch: {state.rapport} != {expected['rapport']}"
    assert abs(state.energy      - float(expected["energy"]))      <= EPSILON, \
        f"[{vector['id']}] energy mismatch: {state.energy} != {expected['energy']}"
