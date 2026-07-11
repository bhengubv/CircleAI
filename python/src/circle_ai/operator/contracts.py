# contracts.py
#
# Port of CircleAI.Operator Contracts.cs (C# — the EXACT spec).
#
# (2.7.0) Kubernetes-operator contracts (kagent pattern): model deployments,
# status, and a lifecycle observer. C# enums map to IntEnum; records map to
# frozen slotted dataclasses; the observer handler is Func<ModelStatus,
# ValueTask>.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from enum import IntEnum
from typing import Awaitable, Callable, Optional


class ModelLifecyclePhase(IntEnum):
    """Mirrors ``CircleAI.Operator.ModelLifecyclePhase``."""

    Pending = 0
    Downloading = 1
    Loading = 2
    Ready = 3
    Brownout = 4
    Unloading = 5
    Failed = 6


@dataclass(frozen=True, slots=True)
class ModelDeployment:
    """Mirrors ``CircleAI.Operator.ModelDeployment`` — ``record(string ModelId,
    string Namespace, int Replicas, string TargetTierLabel)``."""

    model_id: str
    namespace: str
    replicas: int
    target_tier_label: str


@dataclass(frozen=True, slots=True)
class ModelStatus:
    """Mirrors ``CircleAI.Operator.ModelStatus`` — ``record(string ModelId,
    string Namespace, ModelLifecyclePhase Phase, int ReadyReplicas,
    string? LastError)``."""

    model_id: str
    namespace: str
    phase: ModelLifecyclePhase
    ready_replicas: int
    last_error: Optional[str]


# C# Func<ModelStatus, ValueTask> handler.
StatusHandler = Callable[[ModelStatus], Awaitable[None]]


class IModelOperator(ABC):
    """(2.7.0) Reconcile model deployments against CRDs."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def apply_async(self, deployment: ModelDeployment, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def delete_async(self, model_id: str, namespace: str, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def get_status_async(self, model_id: str, namespace: str, ct: Optional[object] = None) -> Optional[ModelStatus]:
        ...


class IDeploymentObserver(ABC):
    """(2.7.0) Lifecycle observer — fires when phase changes."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    def subscribe(self, handler: StatusHandler) -> object:
        """Returns an IDisposable-style token (has ``dispose()``; is a context
        manager)."""
        ...
