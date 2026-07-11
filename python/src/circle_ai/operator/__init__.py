"""circle_ai.operator — port of the CircleAI.Operator assembly.

(2.7.0 contracts / 3.3.0 in-memory impl) Kubernetes-operator (kagent-pattern)
domain: model deployments driven through a lifecycle state machine (Pending ->
Downloading -> Loading -> Ready) with a phase-change observer, plus fail-closed
null defaults. C# is the exact spec.

Public surface:

  * ModelLifecyclePhase                                   — enum.
  * ModelDeployment / ModelStatus                         — domain records.
  * IModelOperator / IDeploymentObserver                  — contracts.
  * InMemoryModelOperator                                 — operator + observer.
  * NullModelOperator / NullDeploymentObserver            — fail-closed defaults.
"""
from __future__ import annotations

from .contracts import (
    IDeploymentObserver,
    IModelOperator,
    ModelDeployment,
    ModelLifecyclePhase,
    ModelStatus,
    StatusHandler,
)
from .in_memory_operator import InMemoryModelOperator
from .null_implementations import NullDeploymentObserver, NullModelOperator

__all__ = [
    "ModelLifecyclePhase",
    "ModelDeployment",
    "ModelStatus",
    "StatusHandler",
    "IModelOperator",
    "IDeploymentObserver",
    "InMemoryModelOperator",
    "NullModelOperator",
    "NullDeploymentObserver",
]
