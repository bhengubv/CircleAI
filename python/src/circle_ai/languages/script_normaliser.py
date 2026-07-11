# script_normaliser.py
#
# Port of CircleAI.Languages IScriptNormaliser.cs (C# — the EXACT spec) plus a
# real deterministic implementation.
#
# The C# assembly ships the IScriptNormaliser interface; there is no concrete
# type in the C# tree, so this port supplies a real, fully deterministic
# normaliser built on the Python stdlib ``unicodedata`` (no external deps):
#
#   * Normalise      — NFC-composes, strips zero-width / BiDi control marks, and
#                      collapses redundant whitespace; the DetectedLanguage is
#                      the caller-supplied target (or LanguageTag.UNKNOWN).
#   * ToAsciiApproximation — NFKD-decomposes and drops combining marks so
#                      accented Latin text degrades to plain ASCII.
#   * ContainsRtl    — true when any character carries a right-to-left BiDi
#                      class (R, AL, or the RTL embedding/override controls).

from __future__ import annotations

import unicodedata
from abc import ABC, abstractmethod
from typing import Optional

from .language_types import LanguageTag, ScriptNormalisationResult


class IScriptNormaliser(ABC):
    """Normalises text for a given writing system —
    NFC/NFD, RTL markers, zero-width characters, etc.
    """

    @abstractmethod
    def normalise(
        self, text: str, target_language: Optional[LanguageTag] = None
    ) -> ScriptNormalisationResult:
        ...

    @abstractmethod
    def to_ascii_approximation(self, text: str) -> str:
        ...

    @abstractmethod
    def contains_rtl(self, text: str) -> bool:
        ...


# Zero-width and BiDi-control code points removed during normalisation.
#   U+200B ZERO WIDTH SPACE .. U+200F (incl. ZWNJ/ZWJ, LRM/RLM)
#   U+202A .. U+202E  (BiDi embeddings/overrides + PDF)
#   U+2060 WORD JOINER, U+FEFF ZERO WIDTH NO-BREAK SPACE (BOM)
#   U+2066 .. U+2069  (BiDi isolates)
_ZERO_WIDTH_AND_BIDI = frozenset(
    [
        "​",
        "‌",
        "‍",
        "‎",
        "‏",
        "‪",
        "‫",
        "‬",
        "‭",
        "‮",
        "⁠",
        "﻿",
        "⁦",
        "⁧",
        "⁨",
        "⁩",
    ]
)

# BiDi classes that mark a right-to-left character.
_RTL_BIDI_CLASSES = frozenset(["R", "AL", "RLE", "RLO", "RLI"])


class DefaultScriptNormaliser(IScriptNormaliser):
    """Real, deterministic :class:`IScriptNormaliser` built on ``unicodedata``."""

    def normalise(
        self, text: str, target_language: Optional[LanguageTag] = None
    ) -> ScriptNormalisationResult:
        if text is None:
            raise ValueError("text must not be None")

        # Strip zero-width / BiDi-control marks, NFC-compose, collapse runs of
        # whitespace to single spaces, and trim the ends.
        stripped = "".join(c for c in text if c not in _ZERO_WIDTH_AND_BIDI)
        composed = unicodedata.normalize("NFC", stripped)
        collapsed = " ".join(composed.split())

        detected = target_language if target_language is not None else LanguageTag.UNKNOWN
        return ScriptNormalisationResult(
            input=text, normalised=collapsed, detected_language=detected
        )

    def to_ascii_approximation(self, text: str) -> str:
        if text is None:
            raise ValueError("text must not be None")
        # NFKD split so accents become combining marks, then drop the marks and
        # anything outside the ASCII range.
        decomposed = unicodedata.normalize("NFKD", text)
        return "".join(
            c
            for c in decomposed
            if not unicodedata.combining(c) and ord(c) < 128
        )

    def contains_rtl(self, text: str) -> bool:
        if text is None:
            raise ValueError("text must not be None")
        return any(unicodedata.bidirectional(c) in _RTL_BIDI_CLASSES for c in text)
