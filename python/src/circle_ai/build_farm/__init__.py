"""circle_ai.build_farm — port of the CircleAI.BuildFarm assembly.

(2.9.0 contracts / 3.3.0 in-memory impl) Build-farm domain: agent pool, job
runner (Pending -> Running -> Succeeded/Failed state machine), and artifact
store, plus fail-closed null defaults. C# is the exact spec.

Public surface:

  * BuildAgentKind / BuildJobPhase                        — enums.
  * BuildAgent / BuildJob / BuildArtifact                 — domain records.
  * IBuildAgentPool / IBuildJobRunner / IBuildArtifactStore — contracts.
  * InMemoryBuildAgentPool / InMemoryBuildJobRunner / InMemoryBuildArtifactStore.
  * NullBuildAgentPool / NullBuildJobRunner / NullBuildArtifactStore.
"""
from __future__ import annotations

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
from .in_memory_build_farm import (
    InMemoryBuildAgentPool,
    InMemoryBuildArtifactStore,
    InMemoryBuildJobRunner,
)
from .null_implementations import (
    NullBuildAgentPool,
    NullBuildArtifactStore,
    NullBuildJobRunner,
)

__all__ = [
    "BuildAgentKind",
    "BuildJobPhase",
    "BuildAgent",
    "BuildJob",
    "BuildArtifact",
    "IBuildAgentPool",
    "IBuildJobRunner",
    "IBuildArtifactStore",
    "InMemoryBuildAgentPool",
    "InMemoryBuildJobRunner",
    "InMemoryBuildArtifactStore",
    "NullBuildAgentPool",
    "NullBuildJobRunner",
    "NullBuildArtifactStore",
]
