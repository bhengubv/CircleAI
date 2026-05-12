# companion.py
#
# Python port of Circle.AI.Companion.
#
# Covers:
#   InterfaceKind             — surface the Companion is running on
#   CompanionContext           — context snapshot injected into the system prompt
#   CompanionTurn             — one turn in the in-session conversation log
#   CompanionProactiveEvent   — metadata for proactive Companion messages
#   ICompanionSession         — primary contract for a Companion session

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import Enum
from typing import AsyncGenerator, Callable, Optional


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ---------------------------------------------------------------------------
# Enumerations
# ---------------------------------------------------------------------------

class InterfaceKind(Enum):
    """The surface on which the Companion session is running."""
    Mobile   = "Mobile"    # Phone or tablet (MAUI)
    Wearable = "Wearable"  # Smartwatch or fitness band
    Desktop  = "Desktop"   # Desktop or laptop (MAUI / WPF)
    Web      = "Web"       # Browser-based (Blazor)
    IoT      = "IoT"       # Embedded IoT device — voice in/out
    Ambient  = "Ambient"   # Smart speaker, room display, car
    Headless = "Headless"  # Programmatic / background / testing


# ---------------------------------------------------------------------------
# Data types
# ---------------------------------------------------------------------------

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


# ---------------------------------------------------------------------------
# ICompanionSession ABC
# ---------------------------------------------------------------------------

class ICompanionSession(ABC):
    """A Companion conversation session.

    Combines identity awareness, cross-device memory, language adaptation,
    affect sensing, and proactive reasoning into a single coherent interface.
    """

    # ── Identity ──────────────────────────────────────────────────────────

    @property
    @abstractmethod
    def session_id(self) -> str:
        """Stable unique identifier for this session."""
        ...

    @property
    @abstractmethod
    def identity_id(self) -> str:
        """The authenticated identity driving this session."""
        ...

    @property
    @abstractmethod
    def interface(self) -> InterfaceKind:
        """The surface on which this session is running."""
        ...

    # ── Core conversation ─────────────────────────────────────────────────

    @abstractmethod
    async def send_async(
        self, message: str, *, ct: Optional[object] = None
    ) -> str:
        """Send a message and receive a complete reply."""
        ...

    @abstractmethod
    async def stream_async(
        self, message: str, *, ct: Optional[object] = None
    ) -> AsyncGenerator[str, None]:
        """Stream the Companion's reply token-by-token."""
        ...

    @abstractmethod
    async def agent_async(
        self, instruction: str, *, ct: Optional[object] = None
    ) -> str:
        """Agentic mode: send → detect tool calls → execute → re-prompt loop."""
        ...

    # ── Context ───────────────────────────────────────────────────────────

    @abstractmethod
    def get_context(self) -> CompanionContext:
        """Return the most recent CompanionContext snapshot."""
        ...

    @abstractmethod
    async def refresh_context_async(
        self, *, ct: Optional[object] = None
    ) -> None:
        """Refresh the context from backing stores."""
        ...

    # ── History ───────────────────────────────────────────────────────────

    @property
    @abstractmethod
    def history(self) -> list[CompanionTurn]:
        """In-session conversation history (not persisted)."""
        ...

    # ── Feedback ──────────────────────────────────────────────────────────

    @abstractmethod
    async def signal_feedback_async(
        self,
        positive: bool,
        note: Optional[str] = None,
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Signal satisfaction with the last reply."""
        ...

    # ── Proactive ─────────────────────────────────────────────────────────

    # Event: list of handlers called when the Companion proactively initiates
    # contact.  Register with ``session.proactive_message_ready.append(handler)``.
    # Each handler is ``Callable[[CompanionProactiveEvent], None]``.
    proactive_message_ready: list[Callable[[CompanionProactiveEvent], None]]

    # ── Lifecycle ─────────────────────────────────────────────────────────

    @abstractmethod
    async def aclose(self) -> None:
        """Release resources held by this session (async context manager exit)."""
        ...

    async def __aenter__(self) -> "ICompanionSession":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.aclose()
