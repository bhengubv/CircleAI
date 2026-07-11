# contracts.py
#
# Port of CircleAI.Skills SkillSource.cs / SkillSummary.cs / SkillDetail.cs /
# SkillDraft.cs / ISkillStore.cs (C# — the EXACT spec).
#
# The SkillSource enum, the summary / detail / draft records, and the ISkillStore
# contract. C# enums map to IntEnum (declaration order == ordinal); records map
# to frozen slotted dataclasses.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import List, Optional


class SkillSource(IntEnum):
    """Mirrors ``CircleAI.Skills.SkillSource`` (declaration order)."""

    File = 0
    InMemory = 1
    Remote = 2


@dataclass(frozen=True, slots=True)
class SkillSummary:
    """Mirrors ``CircleAI.Skills.SkillSummary`` — ``record(string Id,
    string Name, string Description, IReadOnlyList<string> Tags,
    SkillSource Source)``."""

    id: str
    name: str
    description: str
    tags: List[str]
    source: SkillSource


@dataclass(frozen=True, slots=True)
class SkillDetail:
    """Mirrors ``CircleAI.Skills.SkillDetail`` — ``record(string Id, string Name,
    string Description, string Instructions, IReadOnlyList<string> Tags,
    SkillSource Source, DateTimeOffset LastModified)``."""

    id: str
    name: str
    description: str
    instructions: str
    tags: List[str]
    source: SkillSource
    last_modified: datetime


@dataclass(frozen=True, slots=True)
class SkillDraft:
    """Mirrors ``CircleAI.Skills.SkillDraft`` — ``record(string Name,
    string Description, string Instructions, IReadOnlyList<string> Tags)``."""

    name: str
    description: str
    instructions: str
    tags: List[str]


class ISkillStore(ABC):
    """Persistent store for B! skills."""

    @abstractmethod
    async def list_async(self, cancellation_token: Optional[object] = None) -> List[SkillSummary]:
        ...

    @abstractmethod
    async def get_async(self, id: str, cancellation_token: Optional[object] = None) -> Optional[SkillDetail]:
        ...

    @abstractmethod
    async def search_async(self, query: str, cancellation_token: Optional[object] = None) -> List[SkillSummary]:
        ...

    @abstractmethod
    async def upsert_async(
        self, id: Optional[str], draft: SkillDraft, cancellation_token: Optional[object] = None
    ) -> SkillDetail:
        ...

    @abstractmethod
    async def delete_async(self, id: str, cancellation_token: Optional[object] = None) -> None:
        ...
