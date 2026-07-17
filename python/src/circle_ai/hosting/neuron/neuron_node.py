"""NeuronNode facade — port of CircleAI.Hosting.Neuron.NeuronNode.

A host-neutral IChatRuntime over the on-device brain (IAIService). Streaming
rides the brain's full enrichment pipeline (persona + memory + RAG + concierge
routing + two-slot residency), so a host drives the whole Neuron without seeing
inference types. Exposes ``brain`` so a companion session can sit on top.
"""
from __future__ import annotations

import os
from typing import TYPE_CHECKING, AsyncGenerator, List, Optional, Sequence

from ...models.models import ChatMessage
from ..chat_runtime import ChatTurn

if TYPE_CHECKING:  # pragma: no cover - type-only, avoids an import cycle
    from ..ai_service import IAIService

__all__ = ["NeuronNode"]


class NeuronNode:
    """Host-neutral ``IChatRuntime`` + ``IPersistableChatRuntime`` over a brain."""

    def __init__(
        self,
        brain: "IAIService",
        node_id: str = "circleai-neuron",
        session_snapshot_path: Optional[str] = None,
    ) -> None:
        if brain is None:
            raise ValueError("brain is required")
        self._brain = brain
        self._id = node_id if node_id and node_id.strip() else "circleai-neuron"
        self._snapshot_path = session_snapshot_path or _default_snapshot_path()

    @property
    def brain(self) -> "IAIService":
        """The on-device brain. A companion session consumes this unchanged."""
        return self._brain

    @property
    def id(self) -> str:
        return self._id

    @property
    def engine_label(self) -> str:
        model = getattr(self._brain, "resolved_model_id", None)
        return f"{model} (CircleAI)" if model and str(model).strip() else "CircleAI Neuron"

    @property
    def is_ready(self) -> bool:
        return bool(self._brain.is_ready)

    @property
    def status_message(self) -> str:
        return "ready" if self._brain.is_ready else "loading model…"

    async def stream_async(
        self, messages: Sequence[ChatTurn]
    ) -> AsyncGenerator[str, None]:
        if messages is None:
            raise ValueError("messages is required")
        mapped: List[ChatMessage] = [ChatMessage(t.role, t.content) for t in messages]
        async for chunk in self._brain.stream_async(mapped):
            yield chunk

    # ── IPersistableChatRuntime — generalist floor snapshot (RT-02) ──────────

    @property
    def session_snapshot_path(self) -> Optional[str]:
        return self._snapshot_path

    async def save_session_async(self, path: str) -> bool:
        try:
            return await self._brain.save_session_async(path)
        except Exception:  # noqa: BLE001 - no-throw contract
            return False

    async def load_session_async(self, path: str) -> bool:
        try:
            return await self._brain.load_session_async(path)
        except Exception:  # noqa: BLE001 - no-throw contract
            return False


def _default_snapshot_path() -> str:
    base = os.environ.get("LOCALAPPDATA") or os.path.join(
        os.path.expanduser("~"), ".local", "share"
    )
    return os.path.join(base, "CircleAI", "sessions", "active.session")
