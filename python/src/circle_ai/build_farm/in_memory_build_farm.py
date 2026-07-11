# in_memory_build_farm.py
#
# Port of CircleAI.BuildFarm InMemoryBuildFarm.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory build-farm primitives: agent pool, job runner (state
# machine: Pending -> Running -> Succeeded/Failed), artifact store.
#
# The C# pool uses a ConcurrentDictionary `_busy` with TryAdd for atomic
# acquisition; we use a lock + set. Iteration order over registered agents
# follows insertion order (dict preserves it), matching the C# `_all.Values`
# enumeration for a single-writer registration path. The C# job seq uses
# Interlocked.Increment; we guard a counter with a lock.

from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Dict, List, Optional, Set

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


def _require(value: str, name: str) -> None:
    if value is None or value.strip() == "":
        raise ValueError(f"{name} required")


class InMemoryBuildAgentPool(IBuildAgentPool):
    def __init__(self) -> None:
        self._all: Dict[str, BuildAgent] = {}
        self._busy: Set[str] = set()
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def register(self, a: BuildAgent) -> None:
        if a is None:
            raise ValueError("a must not be None")
        with self._lock:
            self._all[a.agent_id] = a

    async def acquire_async(self, kind: BuildAgentKind, ct: Optional[object] = None) -> Optional[BuildAgent]:
        with self._lock:
            for a in self._all.values():
                if a.kind == kind and a.agent_id not in self._busy:
                    self._busy.add(a.agent_id)
                    return a
        return None

    async def release_async(self, agent_id: str, ct: Optional[object] = None) -> None:
        _require(agent_id, "agentId")
        with self._lock:
            self._busy.discard(agent_id)

    async def list_async(self, ct: Optional[object] = None) -> List[BuildAgent]:
        with self._lock:
            return list(self._all.values())


class InMemoryBuildJobRunner(IBuildJobRunner):
    def __init__(self) -> None:
        self._jobs: Dict[str, BuildJob] = {}
        self._seq = 0
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def start_async(self, agent_id: str, repo: str, branch: str, ct: Optional[object] = None) -> BuildJob:
        _require(agent_id, "agentId")
        _require(repo, "repo")
        _require(branch, "branch")
        with self._lock:
            self._seq += 1
            job_id = f"job-{self._seq}"
            job = BuildJob(job_id, agent_id, repo, branch, BuildJobPhase.Running, datetime.now(timezone.utc))
            self._jobs[job_id] = job
            return job

    async def get_async(self, job_id: str, ct: Optional[object] = None) -> Optional[BuildJob]:
        _require(job_id, "jobId")
        with self._lock:
            return self._jobs.get(job_id)

    def complete(self, job_id: str, success: bool) -> None:
        with self._lock:
            j = self._jobs.get(job_id)
            if j is None:
                raise RuntimeError(f"Unknown job {job_id}")
            phase = BuildJobPhase.Succeeded if success else BuildJobPhase.Failed
            self._jobs[job_id] = BuildJob(j.job_id, j.agent_id, j.repo, j.branch, phase, j.start_utc)


class InMemoryBuildArtifactStore(IBuildArtifactStore):
    def __init__(self) -> None:
        self._items: Dict[str, BuildArtifact] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def save_async(self, artifact: BuildArtifact, ct: Optional[object] = None) -> None:
        if artifact is None:
            raise ValueError("artifact must not be None")
        _require(artifact.artifact_id, "ArtifactId")
        with self._lock:
            self._items[artifact.artifact_id] = artifact

    async def get_async(self, artifact_id: str, ct: Optional[object] = None) -> Optional[BuildArtifact]:
        _require(artifact_id, "artifactId")
        with self._lock:
            return self._items.get(artifact_id)
