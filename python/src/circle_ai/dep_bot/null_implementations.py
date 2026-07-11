# null_implementations.py
#
# Port of CircleAI.DepBot NullImplementations.cs (C# — the EXACT spec).
#
# (2.9.0) Fail-safe defaults. Each exposes a singleton `INSTANCE` mirroring the
# C# `static readonly ... Instance`.

from __future__ import annotations

from typing import List, Optional

from .contracts import Dependency, DependencyUpdate, IDependencyAnalyzer, IDependencyUpdater


class NullDependencyAnalyzer(IDependencyAnalyzer):
    INSTANCE: "NullDependencyAnalyzer"

    @property
    def backend_id(self) -> str:
        return "null"

    async def scan_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> List[Dependency]:
        return []


class NullDependencyUpdater(IDependencyUpdater):
    INSTANCE: "NullDependencyUpdater"

    @property
    def backend_id(self) -> str:
        return "null"

    async def propose_updates_async(
        self, repo_path: str, ct: Optional[object] = None
    ) -> List[DependencyUpdate]:
        return []

    async def apply_update_async(
        self, repo_path: str, update: DependencyUpdate, ct: Optional[object] = None
    ) -> None:
        return None


NullDependencyAnalyzer.INSTANCE = NullDependencyAnalyzer()
NullDependencyUpdater.INSTANCE = NullDependencyUpdater()
