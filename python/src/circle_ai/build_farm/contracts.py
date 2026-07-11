# contracts.py
#
# Port of CircleAI.BuildFarm Contracts.cs (C# — the EXACT spec).
#
# (2.9.0) Build-farm contracts: agents, jobs, artifacts. C# enums map to IntEnum
# with stable ordinals; records map to frozen slotted dataclasses;
# ReadOnlyMemory<byte> maps to bytes.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import List, Optional


class BuildAgentKind(IntEnum):
    """Mirrors ``CircleAI.BuildFarm.BuildAgentKind``."""

    Linux = 0
    Mac = 1
    Windows = 2
    Android = 3
    Ios = 4


class BuildJobPhase(IntEnum):
    """Mirrors ``CircleAI.BuildFarm.BuildJobPhase``."""

    Pending = 0
    Running = 1
    Succeeded = 2
    Failed = 3


@dataclass(frozen=True, slots=True)
class BuildAgent:
    """Mirrors ``CircleAI.BuildFarm.BuildAgent`` — ``record(string AgentId,
    BuildAgentKind Kind, string Os, string? Hardware)``."""

    agent_id: str
    kind: BuildAgentKind
    os: str
    hardware: Optional[str]


@dataclass(frozen=True, slots=True)
class BuildJob:
    """Mirrors ``CircleAI.BuildFarm.BuildJob`` — ``record(string JobId,
    string AgentId, string Repo, string Branch, BuildJobPhase Phase,
    DateTimeOffset StartUtc)``."""

    job_id: str
    agent_id: str
    repo: str
    branch: str
    phase: BuildJobPhase
    start_utc: datetime


@dataclass(frozen=True, slots=True)
class BuildArtifact:
    """Mirrors ``CircleAI.BuildFarm.BuildArtifact`` — ``record(string ArtifactId,
    string JobId, string Name, ReadOnlyMemory<byte> Payload)``."""

    artifact_id: str
    job_id: str
    name: str
    payload: bytes


class IBuildAgentPool(ABC):
    """(2.9.0) Agent pool contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def acquire_async(self, kind: BuildAgentKind, ct: Optional[object] = None) -> Optional[BuildAgent]:
        ...

    @abstractmethod
    async def release_async(self, agent_id: str, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def list_async(self, ct: Optional[object] = None) -> List[BuildAgent]:
        ...


class IBuildJobRunner(ABC):
    """(2.9.0) Job runner contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def start_async(self, agent_id: str, repo: str, branch: str, ct: Optional[object] = None) -> BuildJob:
        ...

    @abstractmethod
    async def get_async(self, job_id: str, ct: Optional[object] = None) -> Optional[BuildJob]:
        ...


class IBuildArtifactStore(ABC):
    """(2.9.0) Artifact store contract."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def save_async(self, artifact: BuildArtifact, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def get_async(self, artifact_id: str, ct: Optional[object] = None) -> Optional[BuildArtifact]:
        ...
