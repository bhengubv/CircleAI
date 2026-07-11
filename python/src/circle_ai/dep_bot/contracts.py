# contracts.py
#
# Port of CircleAI.DepBot Contracts.cs (C# — the EXACT spec).
#
# (2.9.0) DepBot contracts: dependency + update records and the analyzer /
# updater interfaces.
#
# C# ValueTask/ValueTask<T> -> async def -> None/T. C# records -> frozen slotted
# dataclasses.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Optional


@dataclass(frozen=True, slots=True)
class Dependency:
    """Mirrors ``CircleAI.DepBot.Dependency`` — ``record(string Ecosystem,
    string Name, string CurrentVersion, string? LatestVersion)``.
    """

    ecosystem: str
    name: str
    current_version: str
    latest_version: Optional[str]


@dataclass(frozen=True, slots=True)
class DependencyUpdate:
    """Mirrors ``CircleAI.DepBot.DependencyUpdate`` — ``record(string Ecosystem,
    string Name, string FromVersion, string ToVersion, bool IsBreaking)``.
    """

    ecosystem: str
    name: str
    from_version: str
    to_version: str
    is_breaking: bool


class IDependencyAnalyzer(ABC):
    """(2.9.0) Dependency analyzer."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def scan_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> List[Dependency]:
        ...


class IDependencyUpdater(ABC):
    """(2.9.0) Dependency updater."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def propose_updates_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> List[DependencyUpdate]:
        ...

    @abstractmethod
    async def apply_update_async(
        self, repo_path: str, update: DependencyUpdate, ct: Optional[object] = None
    ) -> None:
        ...
