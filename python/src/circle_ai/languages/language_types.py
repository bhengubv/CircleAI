from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import ClassVar, Optional


class WritingSystem(Enum):
    """Script / writing system of a language."""
    LATIN = "Latin"
    ARABIC = "Arabic"
    ETHIOPIC = "Ethiopic"
    GEEZ = "Geez"
    DEVANAGARI = "Devanagari"
    HAN = "Han"
    CYRILLIC = "Cyrillic"
    HEBREW = "Hebrew"
    GREEK = "Greek"
    OTHER = "Other"


@dataclass(frozen=True)
class LanguageTag:
    """A BCP-47 language tag enriched with display metadata."""

    bcp_tag: str
    display_name: str      # English name
    native_name: str       # Name in the language itself
    script: WritingSystem
    is_rtl: bool
    iso_region: str        # ISO 3166-1 alpha-2 primary region

    # Sentinel for unknown / failed detection — set after class definition.
    UNKNOWN: ClassVar["LanguageTag"]


@dataclass(frozen=True)
class DetectionResult:
    """Result of language detection."""

    language: LanguageTag
    confidence: float
    is_reliable: bool


@dataclass(frozen=True)
class ScriptNormalisationResult:
    """Result of script normalisation.

    Mirrors ``CircleAI.Languages.ScriptNormalisationResult`` —
    ``record(string Input, string Normalised, LanguageTag DetectedLanguage)``.
    """

    input: str
    normalised: str
    detected_language: LanguageTag


# Attach sentinel after class body to avoid forward-reference issues.
_UNKNOWN = LanguageTag(
    bcp_tag="und",
    display_name="Unknown",
    native_name="Unknown",
    script=WritingSystem.LATIN,
    is_rtl=False,
    iso_region="",
)
LanguageTag.UNKNOWN = _UNKNOWN  # type: ignore[attr-defined]
