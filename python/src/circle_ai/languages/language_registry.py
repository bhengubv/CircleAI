# language_registry.py
#
# Port of CircleAI.Languages ILanguageRegistry.cs (C# — the EXACT spec).
#
# Registry of all BCP-47 language tags that Circle AI understands. The concrete
# DefaultLanguageRegistry (known_languages.py) already satisfies this contract
# structurally; this ABC makes the interface explicit for callers that want to
# depend on the abstraction.

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import List, Optional

from .language_types import LanguageTag


class ILanguageRegistry(ABC):
    """Registry of all BCP-47 language tags that Circle AI understands."""

    @abstractmethod
    def get_by_bcp_tag(self, bcp_tag: str) -> Optional[LanguageTag]:
        ...

    @abstractmethod
    def get_all(self) -> List[LanguageTag]:
        ...

    @abstractmethod
    def get_for_region(self, iso_region: str) -> List[LanguageTag]:
        ...

    @abstractmethod
    def is_supported(self, bcp_tag: str) -> bool:
        ...
