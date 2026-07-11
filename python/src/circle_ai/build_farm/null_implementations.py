# null_implementations.py
#
# Port of CircleAI.BuildFarm NullImplementations.cs (C# — the EXACT spec).
#
# (2.9.0) Fail-closed build-farm defaults. NullBuildJobRunner.StartAsync returns
# a Failed job stamped with Guid.Empty ("D" format, dashed) and
# DateTimeOffset.MinValue.

from __future__ import annotations

from datetime import datetime, timezone
from typing import List, Optional

from .contracts import (
    BuildAgent,
    BuildAgentKind,
    BuildArtifact,
    BuildJob,
    BuildJobPhase,
    IBuildAgentPool,
    IBuildArtifactStore,
    IBuildJobRunner,
)

_GUID_EMPTY = "00000000-0000-0000-0000-000000000000"
_MIN_UTC = datetime(1, 1, 1, tzinfo=timezone.utc)


class NullBuildAgentPool(IBuildAgentPool):
    Instance: "NullBuildAgentPool"

    @property
    def backend_id(self) -> str:
        return "null"

    async def acquire_async(self, k: BuildAgentKind, ct: Optional[object] = None) -> Optional[BuildAgent]:
        return None

    async def release_async(self, id: str, ct: Optional[object] = None) -> None:
        return None

    async def list_async(self, ct: Optional[object] = None) -> List[BuildAgent]:
        return []


class NullBuildJobRunner(IBuildJobRunner):
    Instance: "NullBuildJobRunner"

    @property
    def backend_id(self) -> str:
        return "null"

    async def start_async(self, a: str, r: str, b: str, ct: Optional[object] = None) -> BuildJob:
        return BuildJob(_GUID_EMPTY, a, r, b, BuildJobPhase.Failed, _MIN_UTC)

    async def get_async(self, j: str, ct: Optional[object] = None) -> Optional[BuildJob]:
        return None


class NullBuildArtifactStore(IBuildArtifactStore):
    Instance: "NullBuildArtifactStore"

    @property
    def backend_id(self) -> str:
        return "null"

    async def save_async(self, a: BuildArtifact, ct: Optional[object] = None) -> None:
        return None

    async def get_async(self, id: str, ct: Optional[object] = None) -> Optional[BuildArtifact]:
        return None


NullBuildAgentPool.Instance = NullBuildAgentPool()
NullBuildJobRunner.Instance = NullBuildJobRunner()
NullBuildArtifactStore.Instance = NullBuildArtifactStore()
