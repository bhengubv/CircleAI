"""Feedback training queue (Phase D2).

Ports ``CircleAI.Inference.TrainingSample``, ``IFeedbackTrainingQueue`` and
``FileBackedFeedbackTrainingQueue`` — an append-only, disk-backed queue of user
feedback signals that the nightly adapter trainer drains into LoRA training
batches. Each line of the backing file is one JSON-encoded sample; the queue
survives process restarts without a database.
"""
from __future__ import annotations

import json
import os
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import List

__all__ = [
    "TrainingSample",
    "IFeedbackTrainingQueue",
    "FileBackedFeedbackTrainingQueue",
]


@dataclass(frozen=True, slots=True)
class TrainingSample:
    """One feedback-tagged turn that will inform fine-tuning. Mirrors
    ``CircleAI.Inference.TrainingSample``.

    * ``user_text`` — what the user said.
    * ``assistant_text`` — what we replied (the "current" answer).
    * ``preferred_text`` — user's correction / accepted form (falls back to
      ``assistant_text`` for thumbs-up).
    * ``polarity`` — +1 positive / -1 negative / 0 correction.
    * ``at_utc`` — ISO-8601 timestamp of when the feedback was given.
    """

    user_text: str
    assistant_text: str
    preferred_text: str
    polarity: int
    at_utc: str

    @staticmethod
    def now(
        user_text: str, assistant_text: str, preferred_text: str, polarity: int
    ) -> "TrainingSample":
        """Convenience factory stamping the current UTC time."""
        return TrainingSample(
            user_text=user_text,
            assistant_text=assistant_text,
            preferred_text=preferred_text,
            polarity=polarity,
            at_utc=datetime.now(timezone.utc).isoformat(),
        )

    def to_json(self) -> str:
        return json.dumps(
            {
                "UserText": self.user_text,
                "AssistantText": self.assistant_text,
                "PreferredText": self.preferred_text,
                "Polarity": self.polarity,
                "AtUtc": self.at_utc,
            },
            separators=(",", ":"),
        )

    @staticmethod
    def from_json(line: str) -> "TrainingSample":
        d = json.loads(line)
        return TrainingSample(
            user_text=d.get("UserText", ""),
            assistant_text=d.get("AssistantText", ""),
            preferred_text=d.get("PreferredText", ""),
            polarity=int(d.get("Polarity", 0)),
            at_utc=d.get("AtUtc", ""),
        )


class IFeedbackTrainingQueue(ABC):
    """Append-only feedback queue. Mirrors ``CircleAI.Inference.IFeedbackTrainingQueue``."""

    @abstractmethod
    async def enqueue_async(self, sample: TrainingSample, ct: object = None) -> None: ...

    @abstractmethod
    async def drain_async(self, max_samples: int, ct: object = None) -> List[TrainingSample]: ...

    @property
    @abstractmethod
    def pending(self) -> int: ...


class FileBackedFeedbackTrainingQueue(IFeedbackTrainingQueue):
    """Append-only line-delimited JSON file queue. Mirrors
    ``CircleAI.Inference.FileBackedFeedbackTrainingQueue``.

    :meth:`drain_async` takes the first ``max_samples`` lines (FIFO), rewrites
    the file with the remainder, and returns the parsed samples. Malformed
    lines are skipped (they are still consumed, matching the C# behaviour).
    """

    __slots__ = ("_path",)

    def __init__(self, path: str) -> None:
        if not path or not path.strip():
            raise ValueError("path required")
        self._path = path
        d = os.path.dirname(path)
        if d:
            os.makedirs(d, exist_ok=True)
        if not os.path.isfile(self._path):
            with open(self._path, "w", encoding="utf-8"):
                pass

    @property
    def pending(self) -> int:
        if not os.path.isfile(self._path):
            return 0
        # Enqueue appends "<json>\n" per sample, so the file is a run of
        # newline-terminated lines. Count them the way C#'s ReadLine loop does:
        # one per terminated line, ignoring a trailing empty segment.
        with open(self._path, "r", encoding="utf-8") as fh:
            content = fh.read()
        if not content:
            return 0
        lines = content.split("\n")
        if lines and lines[-1] == "":
            lines.pop()
        return len(lines)

    async def enqueue_async(self, sample: TrainingSample, ct: object = None) -> None:
        if sample is None:
            raise ValueError("sample is required")
        line = sample.to_json()
        with open(self._path, "a", encoding="utf-8") as fh:
            fh.write(line + "\n")

    async def drain_async(self, max_samples: int, ct: object = None) -> List[TrainingSample]:
        if max_samples <= 0:
            raise ValueError("max_samples must be > 0")
        if not os.path.isfile(self._path):
            return []

        with open(self._path, "r", encoding="utf-8") as fh:
            all_lines = [ln.rstrip("\n") for ln in fh.read().split("\n")]
        # split on "\n": a file ending in "\n" yields a trailing "" — drop it,
        # mirroring File.ReadAllLines which does not emit a trailing empty line.
        if all_lines and all_lines[-1] == "":
            all_lines.pop()

        take_count = min(max_samples, len(all_lines))
        taken: List[TrainingSample] = []
        for i in range(take_count):
            try:
                taken.append(TrainingSample.from_json(all_lines[i]))
            except Exception:
                # malformed line skipped (still consumed)
                pass

        remaining = all_lines[take_count:]
        with open(self._path, "w", encoding="utf-8") as fh:
            if remaining:
                fh.write("\n".join(remaining) + "\n")
        return taken
