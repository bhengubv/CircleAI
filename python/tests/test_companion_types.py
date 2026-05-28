"""test_companion_types.py

Validates:
 - InterfaceKind enum values
 - CompanionContext, CompanionTurn, CompanionProactiveEvent construction
 - FaceAffectMapper.apply() mutations against facex_biometric_vectors.json
 - FaceCompanionBridge.observe() proactive event logic
"""
from __future__ import annotations

import json
import pathlib
from datetime import datetime, timezone

import pytest

from circle_ai.companion.companion_types import (
    CompanionContext,
    CompanionProactiveEvent,
    CompanionTurn,
    InterfaceKind,
)
from circle_ai.companion import face_affect_mapper, face_companion_bridge
from circle_ai.memory.affect_state import AffectState
from circle_ai.tools.facial_metric_matrix import (
    FaceBoundingBox,
    FaceExpressionClassification,
    FacialMetricMatrix,
)

FIXTURES_DIR = pathlib.Path(__file__).parent.parent.parent / "fixtures"
EPSILON = 1e-5


def _utc() -> datetime:
    return datetime.now(timezone.utc)


def _load_affect_mapper_vectors() -> list[dict]:
    with open(FIXTURES_DIR / "facex_biometric_vectors.json", encoding="utf-8") as f:
        return json.load(f)["affect_mapper_vectors"]


AFFECT_MAPPER_VECTORS = _load_affect_mapper_vectors()

_EXPRESSION_MAP: dict[str, FaceExpressionClassification] = {
    "Happy":     FaceExpressionClassification.HAPPY,
    "Surprised": FaceExpressionClassification.SURPRISED,
    "Confused":  FaceExpressionClassification.CONFUSED,
    "Stressed":  FaceExpressionClassification.STRESSED,
    "Angry":     FaceExpressionClassification.ANGRY,
    "Neutral":   FaceExpressionClassification.NEUTRAL,
    "Unknown":   FaceExpressionClassification.UNKNOWN,
}


def _make_matrix(expression: str, confidence: float) -> FacialMetricMatrix:
    return FacialMetricMatrix(
        landmarks=[0.0] * 136,
        bounding_box=FaceBoundingBox(x=0.0, y=0.0, width=1.0, height=1.0),
        expression=_EXPRESSION_MAP[expression],
        confidence_score=confidence,
    )


def _make_affect(d: dict) -> AffectState:
    s = AffectState()
    s.curiosity   = float(d["curiosity"])
    s.engagement  = float(d["engagement"])
    s.uncertainty = float(d["uncertainty"])
    s.rapport     = float(d["rapport"])
    s.energy      = float(d["energy"])
    return s


# ---------------------------------------------------------------------------
# InterfaceKind
# ---------------------------------------------------------------------------

def test_interface_kind_count() -> None:
    assert len(InterfaceKind) == 7


def test_interface_kind_values() -> None:
    expected = {"Mobile", "Wearable", "Desktop", "Web", "IoT", "Ambient", "Headless"}
    actual   = {member.value for member in InterfaceKind}
    assert actual == expected


# ---------------------------------------------------------------------------
# CompanionContext round-trip
# ---------------------------------------------------------------------------

def test_companion_context_fields() -> None:
    now = _utc()
    ctx = CompanionContext(
        identity_id="id-001",
        display_name="Sipho",
        preferred_language="zu",
        interface=InterfaceKind.MOBILE,
        persona_hints="[User preferences]\nKeep responses brief.\n",
        affect_summary="[Affect state]\nYou are fully engaged.\n",
        recent_memory_snippets=["snippet-1", "snippet-2"],
        active_goals=["Finish project"],
        context_built_at=now,
    )
    assert ctx.identity_id == "id-001"
    assert ctx.display_name == "Sipho"
    assert ctx.preferred_language == "zu"
    assert ctx.interface is InterfaceKind.MOBILE
    assert ctx.context_built_at == now


def test_companion_context_optional_language() -> None:
    ctx = CompanionContext(
        identity_id="anon",
        display_name="Guest",
        preferred_language=None,
        interface=InterfaceKind.IOT,
        persona_hints="",
        affect_summary="",
        recent_memory_snippets=[],
        active_goals=[],
        context_built_at=_utc(),
    )
    assert ctx.preferred_language is None


def test_companion_context_is_frozen() -> None:
    ctx = CompanionContext(
        identity_id="x",
        display_name="X",
        preferred_language=None,
        interface=InterfaceKind.HEADLESS,
        persona_hints="",
        affect_summary="",
        recent_memory_snippets=[],
        active_goals=[],
        context_built_at=_utc(),
    )
    with pytest.raises((AttributeError, TypeError)):
        ctx.display_name = "Y"  # type: ignore[misc]


# ---------------------------------------------------------------------------
# CompanionTurn round-trip
# ---------------------------------------------------------------------------

def test_companion_turn_user() -> None:
    now = _utc()
    turn = CompanionTurn(role="user", content="Hello, B!", timestamp=now)
    assert turn.role == "user"
    assert turn.content == "Hello, B!"
    assert turn.timestamp == now


def test_companion_turn_is_frozen() -> None:
    turn = CompanionTurn(role="user", content="hi", timestamp=_utc())
    with pytest.raises((AttributeError, TypeError)):
        turn.content = "changed"  # type: ignore[misc]


# ---------------------------------------------------------------------------
# CompanionProactiveEvent round-trip
# ---------------------------------------------------------------------------

def test_companion_proactive_event() -> None:
    now = _utc()
    event = CompanionProactiveEvent(
        session_id="sess-42",
        identity_id="id-001",
        interface=InterfaceKind.WEARABLE,
        message="Time to stretch.",
        trigger_name="posture_reminder",
        generated_at=now,
    )
    assert event.session_id == "sess-42"
    assert event.identity_id == "id-001"
    assert event.interface is InterfaceKind.WEARABLE
    assert event.trigger_name == "posture_reminder"
    assert event.generated_at == now


# ---------------------------------------------------------------------------
# FaceAffectMapper — parametrised against fixtures
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("entry", AFFECT_MAPPER_VECTORS, ids=[e["id"] for e in AFFECT_MAPPER_VECTORS])
def test_face_affect_mapper(entry: dict) -> None:
    affect = _make_affect(entry["initial_affect"])
    matrix = _make_matrix(entry["expression"], float(entry["confidence"]))
    face_affect_mapper.apply(matrix, affect)

    exp = entry["expected_affect"]
    tol = float(entry.get("tolerance", EPSILON))

    assert abs(affect.curiosity   - float(exp["curiosity"]))   <= tol, \
        f"[{entry['id']}] curiosity mismatch"
    assert abs(affect.engagement  - float(exp["engagement"]))  <= tol, \
        f"[{entry['id']}] engagement mismatch"
    assert abs(affect.uncertainty - float(exp["uncertainty"])) <= tol, \
        f"[{entry['id']}] uncertainty mismatch"
    assert abs(affect.rapport     - float(exp["rapport"]))     <= tol, \
        f"[{entry['id']}] rapport mismatch"
    assert abs(affect.energy      - float(exp["energy"]))      <= tol, \
        f"[{entry['id']}] energy mismatch"


# ---------------------------------------------------------------------------
# FaceCompanionBridge.observe() — proactive event triggering
# ---------------------------------------------------------------------------

def test_observe_confused_high_uncertainty_triggers_event() -> None:
    """Confused expression with high confidence from uncertain state should produce an event."""
    affect = AffectState()
    affect.uncertainty = 0.75  # above CONFUSION_THRESHOLD

    matrix = _make_matrix("Confused", 0.79)
    # After applying the Confused expression, uncertainty will increase further
    # Reset to a state that will be >= threshold after apply
    affect.uncertainty = 0.70  # exactly at threshold

    event = face_companion_bridge.observe(
        matrix, affect, "sess-1", "id-001", InterfaceKind.MOBILE
    )
    assert event is not None
    assert event.trigger_name == "face_confusion_detected"
    assert event.session_id == "sess-1"
    assert event.identity_id == "id-001"
    assert event.interface is InterfaceKind.MOBILE


def test_observe_stressed_high_uncertainty_triggers_event() -> None:
    """Stressed expression with high confidence from uncertain state should produce an event."""
    affect = AffectState()
    affect.uncertainty = 0.65  # after +0.08 from stressed = 0.73 >= 0.70

    matrix = _make_matrix("Stressed", 0.85)
    event = face_companion_bridge.observe(
        matrix, affect, "sess-2", "id-002", InterfaceKind.DESKTOP
    )
    assert event is not None
    assert event.trigger_name == "face_confusion_detected"


def test_observe_neutral_no_event() -> None:
    """Neutral expression must never produce a proactive event."""
    affect = AffectState()
    affect.uncertainty = 0.9  # very uncertain, but neutral expression

    matrix = _make_matrix("Neutral", 0.95)
    event = face_companion_bridge.observe(
        matrix, affect, "sess-3", "id-003", InterfaceKind.WEB
    )
    assert event is None


def test_observe_happy_no_event() -> None:
    """Happy expression must not produce a proactive event regardless of uncertainty."""
    affect = AffectState()
    affect.uncertainty = 0.9

    matrix = _make_matrix("Happy", 0.92)
    event = face_companion_bridge.observe(
        matrix, affect, "sess-4", "id-004", InterfaceKind.WEARABLE
    )
    assert event is None


def test_observe_low_confidence_no_event() -> None:
    """Low confidence (< 0.5) must produce no event and no affect mutation."""
    affect = AffectState()
    affect.uncertainty = 0.9

    matrix = _make_matrix("Stressed", 0.49)
    event = face_companion_bridge.observe(
        matrix, affect, "sess-5", "id-005", InterfaceKind.MOBILE
    )
    assert event is None
    # Affect should be unchanged (confidence below threshold)
    assert abs(affect.uncertainty - 0.9) <= EPSILON
