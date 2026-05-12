# languages.py
#
# Python port of Circle.AI.Languages.
#
# Covers:
#   WritingSystem      — Latin | Arabic | Ethiopic | Geez | Devanagari |
#                        Han | Cyrillic | Hebrew | Greek | Other
#   LanguageTag        — BCP-47 tag enriched with display metadata
#   DetectionResult    — result of language detection
#   KnownLanguages     — static registry of all 20 shipped languages
#   ILanguageDetector  — ABC for text language detection
#   ILanguageRegistry  — ABC for BCP-47 tag lookup

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import Enum
from typing import Optional


# ---------------------------------------------------------------------------
# Enumerations
# ---------------------------------------------------------------------------

class WritingSystem(Enum):
    """Script / writing system of a language."""
    Latin      = "Latin"
    Arabic     = "Arabic"
    Ethiopic   = "Ethiopic"
    Geez       = "Geez"
    Devanagari = "Devanagari"
    Han        = "Han"
    Cyrillic   = "Cyrillic"
    Hebrew     = "Hebrew"
    Greek      = "Greek"
    Other      = "Other"


# ---------------------------------------------------------------------------
# Data types
# ---------------------------------------------------------------------------

@dataclass(frozen=True)
class LanguageTag:
    """A BCP-47 language tag enriched with display metadata."""

    bcp_tag: str
    display_name: str      # English name
    native_name: str       # Name in the language itself
    script: WritingSystem
    is_rtl: bool
    iso_region: str        # ISO 3166-1 alpha-2 primary region


@dataclass(frozen=True)
class DetectionResult:
    """Result of language detection."""

    language: LanguageTag
    confidence: float
    is_reliable: bool


# Sentinel for unknown / failed detection.  Accessible as ``LanguageTag.UNKNOWN``
# or directly as the module-level ``LANGUAGE_TAG_UNKNOWN``.
LANGUAGE_TAG_UNKNOWN = LanguageTag(
    bcp_tag="und",
    display_name="Unknown",
    native_name="Unknown",
    script=WritingSystem.Latin,
    is_rtl=False,
    iso_region="",
)
# Attach as a class attribute so callers can use ``LanguageTag.UNKNOWN``.
# We bypass frozen-instance protection by setting on the class, not an instance.
LanguageTag.UNKNOWN = LANGUAGE_TAG_UNKNOWN  # type: ignore[attr-defined]


# ---------------------------------------------------------------------------
# KnownLanguages
# ---------------------------------------------------------------------------

class KnownLanguages:
    """Static registry of every language Circle AI ships support for."""

    # ── Africa ────────────────────────────────────────────────────────────────
    IsiZulu  = LanguageTag("zu",  "isiZulu",    "isiZulu",        WritingSystem.Latin,      False, "ZA")
    Sesotho  = LanguageTag("st",  "Sesotho",    "Sesotho",        WritingSystem.Latin,      False, "ZA")
    Afrikaans= LanguageTag("af",  "Afrikaans",  "Afrikaans",      WritingSystem.Latin,      False, "ZA")
    Swahili  = LanguageTag("sw",  "Swahili",    "Kiswahili",      WritingSystem.Latin,      False, "KE")
    Hausa    = LanguageTag("ha",  "Hausa",      "Hausa",          WritingSystem.Latin,      False, "NG")
    Amharic  = LanguageTag("am",  "Amharic",    "አማርኛ",  WritingSystem.Ethiopic,   False, "ET")
    Yoruba   = LanguageTag("yo",  "Yoruba",     "Yorùbá", WritingSystem.Latin,    False, "NG")
    Igbo     = LanguageTag("ig",  "Igbo",       "Igbo",           WritingSystem.Latin,      False, "NG")
    Xhosa    = LanguageTag("xh",  "isiXhosa",   "isiXhosa",       WritingSystem.Latin,      False, "ZA")
    Sepedi   = LanguageTag("nso", "Sepedi",     "Sepedi",         WritingSystem.Latin,      False, "ZA")
    Setswana = LanguageTag("tn",  "Setswana",   "Setswana",       WritingSystem.Latin,      False, "ZA")
    Somali   = LanguageTag("so",  "Somali",     "Soomaali",       WritingSystem.Latin,      False, "SO")
    Oromo    = LanguageTag("om",  "Oromo",      "Afaan Oromoo",   WritingSystem.Latin,      False, "ET")

    # ── Middle East & North Africa ────────────────────────────────────────────
    Arabic   = LanguageTag("ar",  "Arabic",     "العربية", WritingSystem.Arabic, True, "SA")

    # ── Europe & Americas ─────────────────────────────────────────────────────
    English    = LanguageTag("en", "English",    "English",        WritingSystem.Latin,      False, "GB")
    Portuguese = LanguageTag("pt", "Portuguese", "Português", WritingSystem.Latin,      False, "PT")
    French     = LanguageTag("fr", "French",     "Français",  WritingSystem.Latin,      False, "FR")
    Spanish    = LanguageTag("es", "Spanish",    "Español",   WritingSystem.Latin,      False, "ES")

    # ── Asia ──────────────────────────────────────────────────────────────────
    Mandarin = LanguageTag("zh",  "Mandarin",   "中文",   WritingSystem.Han,        False, "CN")
    Hindi    = LanguageTag("hi",  "Hindi",      "हिन्दी", WritingSystem.Devanagari, False, "IN")

    ALL: list[LanguageTag] = []  # populated after class body


# Populate ALL in declaration order (must match C# order exactly)
KnownLanguages.ALL = [
    KnownLanguages.IsiZulu,
    KnownLanguages.Sesotho,
    KnownLanguages.Afrikaans,
    KnownLanguages.Swahili,
    KnownLanguages.Hausa,
    KnownLanguages.Amharic,
    KnownLanguages.Yoruba,
    KnownLanguages.Igbo,
    KnownLanguages.Xhosa,
    KnownLanguages.Sepedi,
    KnownLanguages.Setswana,
    KnownLanguages.Somali,
    KnownLanguages.Oromo,
    KnownLanguages.Arabic,
    KnownLanguages.English,
    KnownLanguages.Portuguese,
    KnownLanguages.French,
    KnownLanguages.Spanish,
    KnownLanguages.Mandarin,
    KnownLanguages.Hindi,
]


# ---------------------------------------------------------------------------
# Default implementation of ILanguageRegistry (backed by KnownLanguages)
# ---------------------------------------------------------------------------

class DefaultLanguageRegistry:
    """Registry backed by KnownLanguages.ALL.

    Implements the same contract as ILanguageRegistry.  Provided as a concrete
    class so tests can instantiate it directly without subclassing.
    """

    def __init__(self) -> None:
        self._by_tag: dict[str, LanguageTag] = {
            lang.bcp_tag: lang for lang in KnownLanguages.ALL
        }

    def get_by_bcp_tag(self, bcp_tag: str) -> Optional[LanguageTag]:
        return self._by_tag.get(bcp_tag)

    def get_all(self) -> list[LanguageTag]:
        return list(KnownLanguages.ALL)

    def get_for_region(self, iso_region: str) -> list[LanguageTag]:
        return [lang for lang in KnownLanguages.ALL if lang.iso_region == iso_region]

    def is_supported(self, bcp_tag: str) -> bool:
        return bcp_tag in self._by_tag


# ---------------------------------------------------------------------------
# ABCs
# ---------------------------------------------------------------------------

class ILanguageDetector(ABC):
    """Detects the BCP-47 language of a piece of text."""

    @abstractmethod
    async def detect_async(
        self, text: str, *, ct: Optional[object] = None
    ) -> DetectionResult:
        """Detect the most likely language.

        Returns ``LanguageTag.UNKNOWN`` with confidence=0 when detection fails.
        """
        ...

    @abstractmethod
    async def detect_multiple_async(
        self,
        text: str,
        max_results: int = 3,
        *,
        ct: Optional[object] = None,
    ) -> list[DetectionResult]:
        """Return up to *max_results* candidates ranked by confidence."""
        ...


class ILanguageRegistry(ABC):
    """Registry of all BCP-47 language tags that Circle AI understands."""

    @abstractmethod
    def get_by_bcp_tag(self, bcp_tag: str) -> Optional[LanguageTag]:
        ...

    @abstractmethod
    def get_all(self) -> list[LanguageTag]:
        ...

    @abstractmethod
    def get_for_region(self, iso_region: str) -> list[LanguageTag]:
        ...

    @abstractmethod
    def is_supported(self, bcp_tag: str) -> bool:
        ...
