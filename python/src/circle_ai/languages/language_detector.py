# language_detector.py
#
# Port of CircleAI.Languages ILanguageDetector.cs + NullLanguageDetector.cs
# (C# — the EXACT spec).
#
# Detects the BCP-47 language of a piece of text. The C# assembly ships only a
# NullLanguageDetector (no ML model), so this port mirrors that: the interface
# plus the fail-safe no-op detector that always reports Unknown/0-confidence.

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import List, Optional

from .language_types import DetectionResult, LanguageTag


class ILanguageDetector(ABC):
    """Detects the BCP-47 language of a piece of text."""

    @abstractmethod
    async def detect_async(
        self, text: str, *, ct: Optional[object] = None
    ) -> DetectionResult:
        """Detect the most likely language.

        Returns :data:`LanguageTag.UNKNOWN` with confidence 0 when detection
        fails.
        """
        ...

    @abstractmethod
    async def detect_multiple_async(
        self, text: str, max_results: int = 3, *, ct: Optional[object] = None
    ) -> List[DetectionResult]:
        """Return up to ``max_results`` candidates ranked by confidence."""
        ...


class NullLanguageDetector(ILanguageDetector):
    """No-op :class:`ILanguageDetector`. Used when no ML model is available.

    Always returns Unknown / 0-confidence — callers must treat this as
    "undetected".
    """

    Instance: "NullLanguageDetector"

    async def detect_async(
        self, text: str, *, ct: Optional[object] = None
    ) -> DetectionResult:
        return DetectionResult(
            language=LanguageTag.UNKNOWN, confidence=0.0, is_reliable=False
        )

    async def detect_multiple_async(
        self, text: str, max_results: int = 3, *, ct: Optional[object] = None
    ) -> List[DetectionResult]:
        return [
            DetectionResult(
                language=LanguageTag.UNKNOWN, confidence=0.0, is_reliable=False
            )
        ]


# Singleton mirror of the C# `NullLanguageDetector.Instance`.
NullLanguageDetector.Instance = NullLanguageDetector()  # type: ignore[attr-defined]
