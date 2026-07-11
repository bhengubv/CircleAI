# in_memory_operator.py
#
# Port of CircleAI.Operator InMemoryOperator.cs (C# — the EXACT spec).
#
# (3.3.0) Real in-memory IModelOperator + IDeploymentObserver. Applies
# deployments through a lifecycle state machine (Pending -> Downloading ->
# Loading -> Ready) and notifies subscribers on every phase transition.
#
# Concurrency: the C# TransitionAsync snapshots observers under `_obsLock`,
# releases it, then AWAITS each observer in turn (sequential, not
# fire-and-forget), swallowing exceptions. We reproduce that exactly. The
# subscription token's Dispose removes the handler under the same lock.

from __future__ import annotations

import threading
from typing import Dict, List, Optional

from .contracts import (
    IDeploymentObserver,
    IModelOperator,
    ModelDeployment,
    ModelLifecyclePhase,
    ModelStatus,
    StatusHandler,
)


class _ObserverToken:
    def __init__(self, owner: "InMemoryModelOperator", handler: StatusHandler) -> None:
        self._owner = owner
        self._handler = handler
        self._disposed = False
        self._lock = threading.Lock()

    def dispose(self) -> None:
        with self._lock:
            if self._disposed:
                return
            self._disposed = True
        self._owner._remove(self._handler)

    def __enter__(self) -> "_ObserverToken":
        return self

    def __exit__(self, *exc: object) -> None:
        self.dispose()


class InMemoryModelOperator(IModelOperator, IDeploymentObserver):
    """(3.3.0) In-memory model deployment store + lifecycle observers."""

    def __init__(self) -> None:
        self._statuses: Dict[str, ModelStatus] = {}
        self._observers: List[StatusHandler] = []
        self._obs_lock = threading.Lock()
        self._status_lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def apply_async(self, deployment: ModelDeployment, ct: Optional[object] = None) -> None:
        if deployment is None:
            raise ValueError("deployment must not be None")
        if deployment.model_id is None or deployment.model_id.strip() == "":
            raise ValueError("ModelId required")
        if deployment.namespace is None or deployment.namespace.strip() == "":
            raise ValueError("Namespace required")
        if deployment.replicas < 0:
            raise ValueError("Replicas must be non-negative")

        key = self._key(deployment.model_id, deployment.namespace)
        await self._transition_async(key, deployment, ModelLifecyclePhase.Pending, 0, ct)
        await self._transition_async(key, deployment, ModelLifecyclePhase.Downloading, 0, ct)
        await self._transition_async(key, deployment, ModelLifecyclePhase.Loading, 0, ct)
        await self._transition_async(key, deployment, ModelLifecyclePhase.Ready, deployment.replicas, ct)

    async def delete_async(self, model_id: str, namespace: str, ct: Optional[object] = None) -> None:
        if model_id is None or model_id.strip() == "":
            raise ValueError("modelId required")
        if namespace is None or namespace.strip() == "":
            raise ValueError("namespace required")
        with self._status_lock:
            self._statuses.pop(self._key(model_id, namespace), None)

    async def get_status_async(self, model_id: str, namespace: str, ct: Optional[object] = None) -> Optional[ModelStatus]:
        if model_id is None or model_id.strip() == "":
            raise ValueError("modelId required")
        if namespace is None or namespace.strip() == "":
            raise ValueError("namespace required")
        with self._status_lock:
            return self._statuses.get(self._key(model_id, namespace))

    def subscribe(self, handler: StatusHandler) -> _ObserverToken:
        if handler is None:
            raise ValueError("handler must not be None")
        with self._obs_lock:
            self._observers.append(handler)
        return _ObserverToken(self, handler)

    async def _transition_async(
        self, key: str, d: ModelDeployment, phase: ModelLifecyclePhase, ready_replicas: int, ct: Optional[object]
    ) -> None:
        status = ModelStatus(d.model_id, d.namespace, phase, ready_replicas, None)
        with self._status_lock:
            self._statuses[key] = status
        with self._obs_lock:
            snap = list(self._observers)
        for o in snap:
            try:
                await o(status)
            except Exception:
                # A deployment observer that throws must not corrupt the operator.
                pass

    @staticmethod
    def _key(id: str, ns: str) -> str:
        return f"{ns}/{id}"

    def _remove(self, handler: StatusHandler) -> None:
        with self._obs_lock:
            try:
                self._observers.remove(handler)
            except ValueError:
                pass
