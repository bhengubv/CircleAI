# pack.py
#
# Port of CircleAI.Languages.Language ILanguagePack.cs, ILanguagePackRegistry.cs,
# DefaultLanguagePackRegistry.cs and LanguagePackHelpers.cs (C# — the EXACT spec).
#
# A language pack is a language-specific knowledge module: idiomatic
# expressions, cultural context, and prompt tuning for the on-device LLM to
# reason correctly in that language. Packs are mostly data; the two registries
# and the locale-hint merge helper are the shared machinery.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Dict, List, Mapping, Optional, Sequence


# ─── Records ─────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class LanguagePackMetadata:
    """Metadata for a language pack.

    Mirrors ``CircleAI.Languages.Language.LanguagePackMetadata`` — ``record(
    string BcpTag, string DisplayName, string NativeName, string PrimaryRegion,
    string[] SpokenInRegions, Version PackVersion)``.

    ``pack_version`` is a ``(major, minor)`` tuple, mirroring C# ``Version``.
    """

    bcp_tag: str
    display_name: str
    native_name: str
    primary_region: str
    spoken_in_regions: Sequence[str]
    pack_version: tuple[int, int]


@dataclass(frozen=True, slots=True)
class CulturalNote:
    """Cultural / contextual note for a specific topic.

    Mirrors ``CircleAI.Languages.Language.CulturalNote`` — ``record(string
    Context, string Guidance, string[] Examples)``.
    """

    context: str
    guidance: str
    examples: Sequence[str]


# ─── Pack contract ───────────────────────────────────────────────────────


class ILanguagePack(ABC):
    """A language-specific knowledge pack.

    Provides idiomatic expressions, cultural context, and prompt tuning for the
    on-device LLM to reason correctly in this language.
    """

    @property
    @abstractmethod
    def metadata(self) -> LanguagePackMetadata:
        ...

    @abstractmethod
    def get_idiomatic_expression(self, phrase: str) -> Optional[str]:
        """Return the idiomatic translation of a common phrase, or None."""
        ...

    @abstractmethod
    def adapt_system_prompt(self, base_prompt: str) -> str:
        """Adapt a base system prompt for this language and culture."""
        ...

    @abstractmethod
    def get_cultural_notes(self, context: str) -> List[CulturalNote]:
        """Cultural notes for a given context (e.g. "greeting", "business")."""
        ...

    @abstractmethod
    def get_greeting(self, time_of_day: str) -> str:
        """Return a locale-appropriate greeting for the given time of day."""
        ...

    @abstractmethod
    def get_locale_hints(self) -> Mapping[str, str]:
        """Return locale-specific number/date/currency formatting hints."""
        ...


# ─── Registries ──────────────────────────────────────────────────────────


class ILanguagePackRegistry(ABC):
    """Registry of all installed language packs."""

    @abstractmethod
    def register(self, pack: ILanguagePack) -> None:
        ...

    @abstractmethod
    def get_by_bcp_tag(self, bcp_tag: str) -> Optional[ILanguagePack]:
        ...

    @abstractmethod
    def get_available_packs(self) -> List[LanguagePackMetadata]:
        ...

    @abstractmethod
    def has_pack(self, bcp_tag: str) -> bool:
        ...


class DefaultLanguagePackRegistry(ILanguagePackRegistry):
    """Thread-safe in-memory :class:`ILanguagePackRegistry`."""

    def __init__(self) -> None:
        self._packs: Dict[str, ILanguagePack] = {}
        self._lock = threading.Lock()

    def register(self, pack: ILanguagePack) -> None:
        if pack is None:
            raise ValueError("pack must not be None")
        with self._lock:
            self._packs[pack.metadata.bcp_tag] = pack

    def get_by_bcp_tag(self, bcp_tag: str) -> Optional[ILanguagePack]:
        with self._lock:
            return self._packs.get(bcp_tag)

    def get_available_packs(self) -> List[LanguagePackMetadata]:
        with self._lock:
            return [p.metadata for p in self._packs.values()]

    def has_pack(self, bcp_tag: str) -> bool:
        with self._lock:
            return bcp_tag in self._packs


class LanguagePackRegistry:
    """Case-insensitive pack registry with BCP-47 matching helpers.

    Mirrors ``CircleAI.Languages.Language.LanguagePackRegistry`` — a
    ``ConcurrentDictionary`` keyed case-insensitively on the BCP tag, with
    exact / language-prefix / region lookups. Kept distinct from
    :class:`DefaultLanguagePackRegistry`, exactly as in the C# source.
    """

    def __init__(self) -> None:
        self._by_tag: Dict[str, ILanguagePack] = {}
        self._lock = threading.Lock()

    def register(self, pack: ILanguagePack) -> None:
        if pack is None:
            raise ValueError("pack must not be None")
        with self._lock:
            self._by_tag[pack.metadata.bcp_tag.lower()] = pack

    def get_by_exact_tag(self, bcp_tag: str) -> Optional[ILanguagePack]:
        if bcp_tag is None or not bcp_tag.strip():
            return None
        with self._lock:
            return self._by_tag.get(bcp_tag.lower())

    def get_by_language(self, lang_prefix: str) -> Optional[ILanguagePack]:
        if lang_prefix is None or not lang_prefix.strip():
            return None
        prefix = lang_prefix.split("-")[0].lower()
        with self._lock:
            for pack in self._by_tag.values():
                if pack.metadata.bcp_tag.lower().startswith(prefix):
                    return pack
        return None

    def for_region(self, region: str) -> List[ILanguagePack]:
        if region is None or not region.strip():
            raise ValueError("region required")
        region_lower = region.lower()
        with self._lock:
            return [
                pack
                for pack in self._by_tag.values()
                if any(r.lower() == region_lower for r in pack.metadata.spoken_in_regions)
            ]

    def all_tags(self) -> List[str]:
        with self._lock:
            return sorted(p.metadata.bcp_tag for p in self._by_tag.values())


# ─── Locale-hint merge ───────────────────────────────────────────────────


class LocaleHintMerge:
    """Merge two locale-hint maps. Mirrors
    ``CircleAI.Languages.Language.LocaleHintMerge`` — ``primary`` overrides
    ``secondary`` on key collision; comparison is case-insensitive on keys.
    """

    @staticmethod
    def merge(
        primary: Mapping[str, str], secondary: Mapping[str, str]
    ) -> Mapping[str, str]:
        if primary is None:
            raise ValueError("primary must not be None")
        if secondary is None:
            raise ValueError("secondary must not be None")
        # Case-insensitive merge: start from secondary, primary wins. Track keys
        # by their lower-cased form so a differing-case collision overwrites
        # rather than duplicating, mirroring the C# OrdinalIgnoreCase dictionary.
        merged: Dict[str, str] = {}
        seen: Dict[str, str] = {}  # lower-key -> stored-key
        for src in (secondary, primary):
            for key, value in src.items():
                lower = key.lower()
                stored = seen.get(lower)
                if stored is not None:
                    merged[stored] = value
                else:
                    seen[lower] = key
                    merged[key] = value
        return merged
