# test_companion_types.py
#
# Validates CompanionContext, CompanionTurn, CompanionProactiveEvent,
# and InterfaceKind enum.

from __future__ import annotations

import sys
import pathlib
from datetime import datetime, timezone

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).parent.parent / "src"))

from circle_ai.companion import (
    InterfaceKind,
    CompanionContext,
    CompanionTurn,
    CompanionProactiveEvent,
)


def _utc() -> datetime:
    return datetime.now(timezone.utc)


# ---------------------------------------------------------------------------
# InterfaceKind — must have exactly 7 values
# ---------------------------------------------------------------------------

def test_interface_kind_count() -> None:
    assert len(InterfaceKind) == 7


def test_interface_kind_values() -> None:
    expected = {"Mobile", "Wearable", "Desktop", "Web", "IoT", "Ambient", "Headless"}
    actual   = {member.value for member in InterfaceKind}
    assert actual == expected


def test_interface_kind_by_value() -> None:
    assert InterfaceKind("Mobile")   is InterfaceKind.Mobile
    assert InterfaceKind("Wearable") is InterfaceKind.Wearable
    assert InterfaceKind("Desktop")  is InterfaceKind.Desktop
    assert InterfaceKind("Web")      is InterfaceKind.Web
    assert InterfaceKind("IoT")      is InterfaceKind.IoT
    assert InterfaceKind("Ambient")  is InterfaceKind.Ambient
    assert InterfaceKind("Headless") is InterfaceKind.Headless


# ---------------------------------------------------------------------------
# CompanionContext round-trip
# ---------------------------------------------------------------------------

def test_companion_context_fields() -> None:
    now = _utc()
    ctx = CompanionContext(
        identity_id="id-001",
        display_name="Sipho",
        preferred_language="zu",
        interface=InterfaceKind.Mobile,
        persona_hints="[User preferences]\nKeep responses brief.\n",
        affect_summary="[Affect state]\nYou are fully engaged.\n",
        recent_memory_snippets=["snippet-1", "snippet-2"],
        active_goals=["Finish project", "Read 10 pages"],
        context_built_at=now,
    )

    assert ctx.identity_id          == "id-001"
    assert ctx.display_name         == "Sipho"
    assert ctx.preferred_language   == "zu"
    assert ctx.interface            is InterfaceKind.Mobile
    assert ctx.persona_hints        == "[User preferences]\nKeep responses brief.\n"
    assert ctx.affect_summary       == "[Affect state]\nYou are fully engaged.\n"
    assert ctx.recent_memory_snippets == ["snippet-1", "snippet-2"]
    assert ctx.active_goals         == ["Finish project", "Read 10 pages"]
    assert ctx.context_built_at     == now


def test_companion_context_optional_language() -> None:
    ctx = CompanionContext(
        identity_id="anon",
        display_name="Guest",
        preferred_language=None,
        interface=InterfaceKind.IoT,
        persona_hints="",
        affect_summary="",
        recent_memory_snippets=[],
        active_goals=[],
        context_built_at=_utc(),
    )
    assert ctx.preferred_language is None


# ---------------------------------------------------------------------------
# CompanionTurn round-trip
# ---------------------------------------------------------------------------

def test_companion_turn_user() -> None:
    now = _utc()
    turn = CompanionTurn(role="user", content="Hello, B!", timestamp=now)
    assert turn.role      == "user"
    assert turn.content   == "Hello, B!"
    assert turn.timestamp == now


def test_companion_turn_assistant() -> None:
    now = _utc()
    turn = CompanionTurn(role="assistant", content="Hey there!", timestamp=now)
    assert turn.role      == "assistant"
    assert turn.content   == "Hey there!"
    assert turn.timestamp == now


# ---------------------------------------------------------------------------
# CompanionProactiveEvent round-trip
# ---------------------------------------------------------------------------

def test_companion_proactive_event() -> None:
    now = _utc()
    event = CompanionProactiveEvent(
        session_id="sess-42",
        identity_id="id-001",
        interface=InterfaceKind.Wearable,
        message="Time to stretch — you've been sitting for 45 minutes.",
        trigger_name="posture_reminder",
        generated_at=now,
    )

    assert event.session_id   == "sess-42"
    assert event.identity_id  == "id-001"
    assert event.interface    is InterfaceKind.Wearable
    assert "stretch" in event.message
    assert event.trigger_name == "posture_reminder"
    assert event.generated_at == now


# ---------------------------------------------------------------------------
# Immutability (frozen dataclasses)
# ---------------------------------------------------------------------------

def test_companion_turn_is_frozen() -> None:
    turn = CompanionTurn(role="user", content="hi", timestamp=_utc())
    with pytest.raises((AttributeError, TypeError)):
        turn.content = "changed"  # type: ignore[misc]


def test_companion_context_is_frozen() -> None:
    ctx = CompanionContext(
        identity_id="x",
        display_name="X",
        preferred_language=None,
        interface=InterfaceKind.Headless,
        persona_hints="",
        affect_summary="",
        recent_memory_snippets=[],
        active_goals=[],
        context_built_at=_utc(),
    )
    with pytest.raises((AttributeError, TypeError)):
        ctx.display_name = "Y"  # type: ignore[misc]
