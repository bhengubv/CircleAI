"""Host-neutral chat runtime seam — port of CircleAI.Hosting.Chat.IChatRuntime.

Zero deps beyond the stdlib so a UI / harness can drive the on-device engine
without importing inference types. ``NeuronNode`` implements these protocols over
an ``IAIService`` brain.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import AsyncGenerator, Optional, Protocol, Sequence, runtime_checkable

__all__ = ["ChatTurn", "IChatRuntime", "IPersistableChatRuntime", "NullChatRuntime"]


@dataclass(frozen=True)
class ChatTurn:
    """Host-neutral chat turn. Mirrors ``ChatTurn`` (role / content)."""

    role: str
    content: str


@runtime_checkable
class IChatRuntime(Protocol):
    """Host-neutral chat surface. Mirrors ``IChatRuntime``."""

    @property
    def id(self) -> str: ...

    @property
    def engine_label(self) -> str: ...

    @property
    def is_ready(self) -> bool: ...

    @property
    def status_message(self) -> str: ...

    def stream_async(self, messages: Sequence[ChatTurn]) -> AsyncGenerator[str, None]: ...


@runtime_checkable
class IPersistableChatRuntime(Protocol):
    """Optional KV-snapshot capability. Mirrors ``IPersistableChatRuntime``."""

    @property
    def session_snapshot_path(self) -> Optional[str]: ...

    async def save_session_async(self, path: str) -> bool: ...

    async def load_session_async(self, path: str) -> bool: ...


class NullChatRuntime:
    """Honest 'engine offline' runtime. Mirrors ``NullChatRuntime``."""

    id = "null"
    engine_label = "No engine wired"
    is_ready = False
    status_message = (
        "No chat engine is wired. Add a NeuronNode (or another IChatRuntime "
        "adapter) to enable conversations."
    )

    async def stream_async(
        self, messages: Sequence[ChatTurn]
    ) -> AsyncGenerator[str, None]:
        yield self.status_message
