from __future__ import annotations

from dataclasses import dataclass


@dataclass(frozen=True)
class ChatMessage:
    """A single message in a chat history.

    role is one of "system", "user", or "assistant".
    """

    role: str
    content: str


@dataclass(frozen=True)
class DownloadProgress:
    """Progress report for a model or asset download."""

    file_name: str = ""
    bytes_received: int = 0
    total_bytes: int = 0
    bytes_per_second: float = 0.0
    estimated_time_remaining: float = 0.0  # seconds
