"""IAIObserver + event records — port of CircleAI.Hosting.IAIObserver.

Neutral observability hook. Hosts implement :class:`IAIObserver` to receive
lifecycle and inference events from the butler service without any
Karma/Qi economics baked in. All methods have default no-op implementations
(via the ABC below) so partial observers are trivial to write. Observer
exceptions are caught by :class:`AIService` and logged — they never
propagate to the caller.

The C# interface uses default-interface-methods; Python's ABC provides the
equivalent by giving every method a concrete no-op body. Subclass and override
only the events you care about.
"""
from __future__ import annotations

import uuid
from abc import ABC
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from enum import IntEnum
from typing import TYPE_CHECKING, List, Sequence

from ..models.models import ChatMessage, UpgradeInfo

if TYPE_CHECKING:  # pragma: no cover
    from ..tools.tool_types import ToolInvocation, ToolResult

__all__ = [
    "AIChatEvent",
    "AIStreamEvent",
    "AIToolEvent",
    "BrownoutReason",
    "IAIObserver",
]


# ── Event records ─────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class AIChatEvent:
    """Payload delivered to :meth:`IAIObserver.on_chat_completed_async`.
    Mirrors ``CircleAI.Hosting.AIChatEvent``.
    """

    correlation_id: uuid.UUID
    messages: Sequence[ChatMessage]
    response: str
    elapsed: timedelta
    timestamp: datetime


@dataclass(frozen=True, slots=True)
class AIStreamEvent:
    """Payload delivered to :meth:`IAIObserver.on_stream_started_async` /
    :meth:`IAIObserver.on_stream_completed_async`. Mirrors ``AIStreamEvent``.

    For on-stream-started: ``elapsed`` is time-to-first-token, ``token_count`` 0.
    For on-stream-completed: ``elapsed`` is total time, ``token_count`` the count.
    """

    correlation_id: uuid.UUID
    messages: Sequence[ChatMessage]
    elapsed: timedelta
    token_count: int
    timestamp: datetime


@dataclass(frozen=True, slots=True)
class AIToolEvent:
    """Payload delivered to :meth:`IAIObserver.on_tool_invoked_async`.
    Mirrors ``CircleAI.Hosting.AIToolEvent``.
    """

    correlation_id: uuid.UUID
    invocation: "ToolInvocation"
    result: "ToolResult"
    elapsed: timedelta
    timestamp: datetime


class BrownoutReason(IntEnum):
    """(RT-04) Why a brownout swap fired. Mirrors ``BrownoutReason`` with the
    same ordinals so numeric comparisons match the C#.
    """

    MEMORY_PRESSURE = 0
    BATTERY_FLOOR = 1
    THERMAL_CRITICAL = 2
    MANUAL = 3


# ── IAIObserver ───────────────────────────────────────────────────────────


class IAIObserver(ABC):
    """Observability hook for :class:`AIService`. Receives lifecycle and
    inference events. Mirrors ``CircleAI.Hosting.IAIObserver``.

    Every method has a default no-op body (the C# default-interface-method
    equivalent), so a partial observer overrides only what it needs. Methods
    are async and must complete quickly — dispatch long work to a background
    task inside the implementation. Implementations must be thread-safe and
    error-isolated; exceptions are caught + logged by the service.
    """

    async def on_started_async(self, ct: object = None) -> None:
        """Called once after the model has loaded and Butler is ready."""
        return None

    async def on_stopped_async(self, ct: object = None) -> None:
        """Called once when Butler is stopping / being disposed."""
        return None

    async def on_chat_completed_async(
        self, event: AIChatEvent, ct: object = None
    ) -> None:
        """Called after a complete (non-streaming) chat response is returned."""
        return None

    async def on_stream_started_async(
        self, event: AIStreamEvent, ct: object = None
    ) -> None:
        """Called when a streaming response emits its first token."""
        return None

    async def on_stream_completed_async(
        self, event: AIStreamEvent, ct: object = None
    ) -> None:
        """Called after a streaming response has finished (or was cancelled)."""
        return None

    async def on_tool_invoked_async(
        self, event: AIToolEvent, ct: object = None
    ) -> None:
        """Called after a tool invocation has completed (success or failure)."""
        return None

    async def on_model_fetching_async(
        self, model_id: str, auto_selected: bool, ct: object = None
    ) -> None:
        """Called once when the SDK has resolved which model to load, before
        the file fetch/load, so observers can surface progress UI.
        """
        return None

    async def on_upgrade_available_async(
        self, upgrade: UpgradeInfo, ct: object = None
    ) -> None:
        """Called once per detected model upgrade (version/SHA drift)."""
        return None

    async def on_brownout_async(
        self,
        from_model: str,
        to_model: str,
        reason: BrownoutReason,
        ct: object = None,
    ) -> None:
        """(RT-04) Called when the runtime hot-swaps to a smaller model in the
        fallback chain under pressure.
        """
        return None
