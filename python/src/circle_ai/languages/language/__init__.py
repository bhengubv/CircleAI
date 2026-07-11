"""circle_ai.languages.language — port of CircleAI.Languages.Language +
the 8 per-language packs.

Language packs are language-specific knowledge modules (idiomatic expressions,
cultural context, prompt tuning) for the on-device LLM. Mostly data; the two
registries and the locale-hint merge helper are the shared machinery. C# is the
exact spec.

Public surface:

  * LanguagePackMetadata / CulturalNote — pack records.
  * ILanguagePack / ILanguagePackRegistry — pack + registry contracts.
  * DefaultLanguagePackRegistry — thread-safe in-memory registry.
  * LanguagePackRegistry — case-insensitive registry with BCP-47 matching.
  * LocaleHintMerge — locale-hint map merge helper.
  * The 8 packs: isiZuluLanguagePack, SwahiliLanguagePack, AmharicLanguagePack,
    HausaLanguagePack, AfrikaansLanguagePack, ArabicLanguagePack,
    PortugueseLanguagePack, SesothoLanguagePack (each exposes an ``Instance``).
"""
from __future__ import annotations

from .pack import (
    CulturalNote,
    DefaultLanguagePackRegistry,
    ILanguagePack,
    ILanguagePackRegistry,
    LanguagePackMetadata,
    LanguagePackRegistry,
    LocaleHintMerge,
)
from .afrikaans import AfrikaansLanguagePack
from .amharic import AmharicLanguagePack
from .arabic import ArabicLanguagePack
from .hausa import HausaLanguagePack
from .isizulu import isiZuluLanguagePack
from .portuguese import PortugueseLanguagePack
from .sesotho import SesothoLanguagePack
from .swahili import SwahiliLanguagePack

__all__ = [
    "LanguagePackMetadata",
    "CulturalNote",
    "ILanguagePack",
    "ILanguagePackRegistry",
    "DefaultLanguagePackRegistry",
    "LanguagePackRegistry",
    "LocaleHintMerge",
    "isiZuluLanguagePack",
    "SwahiliLanguagePack",
    "AmharicLanguagePack",
    "HausaLanguagePack",
    "AfrikaansLanguagePack",
    "ArabicLanguagePack",
    "PortugueseLanguagePack",
    "SesothoLanguagePack",
]
