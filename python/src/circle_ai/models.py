# models.py
#
# Shared primitive types used across multiple Circle AI modules.
# ChatMessage lives here alongside DownloadProgress so that modules that
# only need the message type don't have to import the full inference module.

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Optional


@dataclass
class ChatMessage:
    """A single message in a chat history.

    ``role`` is one of ``"system"``, ``"user"``, or ``"assistant"``.
    """

    role: str
    content: str


@dataclass
class DownloadProgress:
    """Progress report for a model or asset download."""

    bytes_received: int
    total_bytes: Optional[int]  # None when content-length is unknown

    @property
    def fraction(self) -> Optional[float]:
        """0.0–1.0 fraction complete, or None when total is unknown."""
        if self.total_bytes is None or self.total_bytes == 0:
            return None
        return self.bytes_received / self.total_bytes
