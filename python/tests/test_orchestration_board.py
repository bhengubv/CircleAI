"""test_orchestration_board.py — CircleAI.Orchestration port.

Covers the AgentRole / AgentPriority / AgentStatus enums, the AgentTask.create /
AgentSwarmConfig.default / .for_device factories, and the LocalAgentDispatcher
(handler routing, blocked-on-missing-handler, the [CRITICAL]/[HIGH] quality
gate, and disposal). C# is the exact spec.
"""
from __future__ import annotations

from datetime import timedelta

import pytest

from circle_ai.orchestration import (
    AgentPriority,
    AgentRole,
    AgentStatus,
    AgentSwarmConfig,
    AgentTask,
    IAgentDispatcher,
    LocalAgentDispatcher,
    QualityGateResult,
    SwarmResult,
)


def test_enum_ordinals():
    assert AgentRole.Engineering == 0 and AgentRole.Security == 3
    assert AgentPriority.Critical == 0 and AgentPriority.Low == 3
    assert AgentStatus.Pending == 0 and AgentStatus.Blocked == 4


def test_agent_task_create_stamps_id_and_time():
    t = AgentTask.create(AgentRole.Review, "review the diff", AgentPriority.High)
    assert t.role == AgentRole.Review and t.priority == AgentPriority.High
    assert t.description == "review the diff"
    assert t.inputs == {}
    assert t.id is not None and t.created_at is not None
    t2 = AgentTask.create(AgentRole.Engineering, "d", AgentPriority.Normal, {"k": "v"})
    assert t2.inputs == {"k": "v"}
    assert t2.id != t.id  # fresh guid each call


def test_swarm_config_default():
    cfg = AgentSwarmConfig.default()
    assert cfg.max_concurrency == 4
    assert cfg.task_timeout == timedelta(minutes=5)
    assert cfg.require_review_pass_before_deploy is True
    assert cfg.require_security_pass_before_deploy is True


def test_swarm_config_for_device():
    from circle_ai.device import DeviceProbe, DeviceTierDefaults

    # Deterministic desktop-class probe: 16 GiB RAM, 8 cores.
    probe = DeviceProbe(
        ram_available_bytes=16 * 1024 ** 3,
        storage_free_bytes=100 * 1024 ** 3,
        cpu_cores=8,
    )
    cfg = AgentSwarmConfig.for_device(probe)
    expected = DeviceTierDefaults.max_concurrency(probe.classify(), probe.cpu_cores)
    assert cfg.max_concurrency == expected
    assert cfg.task_timeout == timedelta(minutes=5)
    assert cfg.require_review_pass_before_deploy is True


async def test_dispatch_routes_to_handler():
    disp = LocalAgentDispatcher()
    assert isinstance(disp, IAgentDispatcher)

    async def eng_handler(task: AgentTask, ct) -> SwarmResult:
        from datetime import datetime, timezone

        return SwarmResult(task.id, task.role, AgentStatus.Passed, "done", [], datetime.now(timezone.utc))

    disp.register_handler(AgentRole.Engineering, eng_handler)
    task = AgentTask.create(AgentRole.Engineering, "build", AgentPriority.Normal)
    result = await disp.dispatch_async(task)
    assert result.status == AgentStatus.Passed and result.output == "done"


async def test_dispatch_blocked_when_no_handler():
    disp = LocalAgentDispatcher()
    task = AgentTask.create(AgentRole.Security, "scan", AgentPriority.High)
    result = await disp.dispatch_async(task)
    assert result.status == AgentStatus.Blocked
    assert "No handler registered for role Security" in result.output
    assert result.issues == ["Register a handler for AgentRole.Security before dispatching."]


async def test_quality_gate_classifies_blockers_and_warnings():
    disp = LocalAgentDispatcher()
    from datetime import datetime, timezone
    from uuid import uuid4

    result = SwarmResult(
        uuid4(),
        AgentRole.Review,
        AgentStatus.Passed,
        "out",
        ["[CRITICAL] rce", "[high] injection", "[info] style nit", "typo"],
        datetime.now(timezone.utc),
    )
    gate = await disp.run_quality_gate_async(result)
    assert isinstance(gate, QualityGateResult)
    assert gate.passed is False
    assert gate.blockers == ["[CRITICAL] rce", "[high] injection"]  # case-insensitive
    assert gate.warnings == ["[info] style nit", "typo"]


async def test_quality_gate_passes_with_no_blockers():
    disp = LocalAgentDispatcher()
    from datetime import datetime, timezone
    from uuid import uuid4

    result = SwarmResult(uuid4(), AgentRole.Review, AgentStatus.Passed, "o", ["[info] fyi"], datetime.now(timezone.utc))
    gate = await disp.run_quality_gate_async(result)
    assert gate.passed is True and gate.blockers == []


async def test_dispatch_after_dispose_raises():
    disp = LocalAgentDispatcher()
    disp.dispose()
    task = AgentTask.create(AgentRole.Engineering, "d", AgentPriority.Low)
    with pytest.raises(RuntimeError):
        await disp.dispatch_async(task)


def test_register_none_handler_raises():
    with pytest.raises(ValueError):
        LocalAgentDispatcher().register_handler(AgentRole.Review, None)  # type: ignore[arg-type]
