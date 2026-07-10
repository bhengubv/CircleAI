"""Server-sent-events framing.

Port of ``CircleAI.Inference.Server.Streaming.ServerSentEventsWriter``. Since
the Python port exposes handlers behind an interface rather than an HTTP socket,
this writer collects frames into an in-memory list (``frames``) instead of
writing to a live response body. Each frame is ``data: <json>\\n\\n``; the
terminator is ``data: [DONE]\\n\\n``. ``None`` fields are omitted from the JSON
(matching the C# ``WhenWritingNull`` config).
"""
from __future__ import annotations

import json
from typing import Any, List

__all__ = ["ServerSentEventsWriter"]


def _to_serializable(payload: Any) -> Any:
    if hasattr(payload, "to_dict"):
        return payload.to_dict()
    return payload


class ServerSentEventsWriter:
    """Collects SSE-framed JSON chunks in memory. Mirrors the framing of
    ``ServerSentEventsWriter`` (``data: <json>\\n\\n`` + ``[DONE]``).
    """

    __slots__ = ("frames",)

    def __init__(self) -> None:
        self.frames: List[str] = []

    async def write_async(self, payload: Any, ct: object = None) -> None:
        obj = _to_serializable(payload)
        js = json.dumps(obj, separators=(",", ":"))
        self.frames.append(f"data: {js}\n\n")

    async def write_terminator_async(self, ct: object = None) -> None:
        self.frames.append("data: [DONE]\n\n")

    @property
    def body(self) -> str:
        """The full framed body as a single string."""
        return "".join(self.frames)
