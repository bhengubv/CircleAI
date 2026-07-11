# null_implementations.py
#
# Port of CircleAI.Operator NullImplementations.cs (C# — the EXACT spec).
#
# (2.7.0) In-proc defaults — no k8s reconciliation. NullDeploymentObserver hands
# back an empty disposable that never fires.

from __future__ import annotations

from typing import Optional

from .contracts import (
    IDeploymentObserver,
    IModelOperator,
    ModelDeployment,
    ModelStatus,
    StatusHandler,
)


class _EmptyDisposable:
    Instance: "_EmptyDisposable"

    def dispose(self) -> None:
        pass

    def __enter__(self) -> "_EmptyDisposable":
        return self

    def __exit__(self, *exc: object) -> None:
        pass


_EmptyDisposable.Instance = _EmptyDisposable()


class NullModelOperator(IModelOperator):
    Instance: "NullModelOperator"

    @property
    def backend_id(self) -> str:
        return "null"

    async def apply_async(self, deployment: ModelDeployment, ct: Optional[object] = None) -> None:
        return None

    async def delete_async(self, model_id: str, ns: str, ct: Optional[object] = None) -> None:
        return None

    async def get_status_async(self, model_id: str, ns: str, ct: Optional[object] = None) -> Optional[ModelStatus]:
        return None


class NullDeploymentObserver(IDeploymentObserver):
    Instance: "NullDeploymentObserver"

    @property
    def backend_id(self) -> str:
        return "null"

    def subscribe(self, handler: StatusHandler) -> object:
        return _EmptyDisposable.Instance


NullModelOperator.Instance = NullModelOperator()
NullDeploymentObserver.Instance = NullDeploymentObserver()
