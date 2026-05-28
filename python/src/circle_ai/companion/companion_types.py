from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime
from enum import Enum
from typing import Optional


class InterfaceKind(Enum):
    """The surface on which the Companion session is running."""
    MOBILE   = "Mobile"
    WEARABLE = "Wearable"
    DESKTOP  = "Desktop"
    WEB      = "Web"
    IOT      = "IoT"
    AMBIENT  = "Ambient"
    HEADLESS = "Headless"


@dataclass(frozen=True)
class CompanionContext:
    """Snapshot of all context injected into the Companion's system prompt.

    Rebuilt at the start of each session and refreshed on request.
    """

    identity_id: str
    display_name: str
    preferred_language: Optional[str]
    interface: InterfaceKind
    persona_hints: str
    affect_summary: str
    recent_memory_snippets: list[str]
    active_goals: list[str]
    context_built_at: datetime


@dataclass(frozen=True)
class CompanionTurn:
    """A single turn in the in-session Companion conversation log."""

    role: str           # "user" | "assistant"
    content: str
    timestamp: datetime


@dataclass(frozen=True)
class CompanionProactiveEvent:
    """Metadata emitted when the Companion proactively initiates contact."""

    session_id: str
    identity_id: str
    interface: InterfaceKind
    message: str
    trigger_name: str
    generated_at: datetime
