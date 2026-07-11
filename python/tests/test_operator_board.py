"""test_operator_board.py — CircleAI.Operator port.

Covers the ModelLifecyclePhase enum, InMemoryModelOperator applying a deployment
through the Pending -> Downloading -> Loading -> Ready state machine while
notifying subscribers on every transition, delete/get-status, argument guards,
observer unsubscribe, and the fail-closed null defaults. C# is the exact spec.
"""
from __future__ import annotations

import pytest

from circle_ai.operator import (
    IDeploymentObserver,
    IModelOperator,
    InMemoryModelOperator,
    ModelDeployment,
    ModelLifecyclePhase,
    ModelStatus,
    NullDeploymentObserver,
    NullModelOperator,
)


def test_phase_ordinals():
    assert ModelLifecyclePhase.Pending == 0
    assert ModelLifecyclePhase.Ready == 3
    assert ModelLifecyclePhase.Failed == 6


async def test_apply_drives_full_lifecycle_and_notifies():
    op = InMemoryModelOperator()
    assert isinstance(op, IModelOperator) and isinstance(op, IDeploymentObserver)
    assert op.backend_id == "in-memory"

    phases: list[ModelLifecyclePhase] = []

    async def observer(s: ModelStatus) -> None:
        phases.append(s.phase)

    op.subscribe(observer)
    await op.apply_async(ModelDeployment("qwen", "prod", 3, "tier2"))

    assert phases == [
        ModelLifecyclePhase.Pending,
        ModelLifecyclePhase.Downloading,
        ModelLifecyclePhase.Loading,
        ModelLifecyclePhase.Ready,
    ]
    status = await op.get_status_async("qwen", "prod")
    assert status is not None
    assert status.phase == ModelLifecyclePhase.Ready
    assert status.ready_replicas == 3
    assert status.last_error is None


async def test_delete_removes_status():
    op = InMemoryModelOperator()
    await op.apply_async(ModelDeployment("m", "ns", 1, "t"))
    assert await op.get_status_async("m", "ns") is not None
    await op.delete_async("m", "ns")
    assert await op.get_status_async("m", "ns") is None


async def test_apply_guards():
    op = InMemoryModelOperator()
    with pytest.raises(ValueError):
        await op.apply_async(ModelDeployment("  ", "ns", 1, "t"))
    with pytest.raises(ValueError):
        await op.apply_async(ModelDeployment("m", " ", 1, "t"))
    with pytest.raises(ValueError):
        await op.apply_async(ModelDeployment("m", "ns", -1, "t"))


async def test_observer_unsubscribe():
    op = InMemoryModelOperator()
    seen: list[ModelLifecyclePhase] = []

    async def observer(s: ModelStatus) -> None:
        seen.append(s.phase)

    token = op.subscribe(observer)
    token.dispose()
    await op.apply_async(ModelDeployment("m", "ns", 1, "t"))
    assert seen == []  # no notifications after dispose


async def test_observer_exception_is_swallowed():
    op = InMemoryModelOperator()

    async def bad(s: ModelStatus) -> None:
        raise RuntimeError("boom")

    op.subscribe(bad)
    # Must complete despite the throwing observer.
    await op.apply_async(ModelDeployment("m", "ns", 1, "t"))
    assert (await op.get_status_async("m", "ns")).phase == ModelLifecyclePhase.Ready


async def test_null_implementations_fail_closed():
    op = NullModelOperator.Instance
    obs = NullDeploymentObserver.Instance
    assert op.backend_id == "null" and obs.backend_id == "null"
    await op.apply_async(ModelDeployment("m", "ns", 1, "t"))  # no-op
    assert await op.get_status_async("m", "ns") is None
    tok = obs.subscribe(lambda s: None)  # type: ignore[arg-type]
    tok.dispose()
