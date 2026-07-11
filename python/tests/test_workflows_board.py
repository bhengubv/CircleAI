"""test_workflows_board.py — CircleAI.Workflows port.

Covers the WorkflowPhase enum + null workflow store/runner/state defaults, and
the Paca conversation state machine (queue -> start -> Finished, step history,
Stop -> Stopped, executor failure -> Failed). C# is the exact spec.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone

import pytest

from circle_ai.workflows import (
    AgentConversation,
    CheckpointPayload,
    ConversationCancelToken,
    ConversationPermissions,
    ConversationState,
    ConversationStep,
    IConversationExecutor,
    NullWorkflowDefinitionStore,
    NullWorkflowRunner,
    NullWorkflowState,
    PacaConversationRuntime,
    WorkflowDefinition,
    WorkflowPhase,
)

_PERMS = ConversationPermissions(allow_clone_repos=True, allow_create_pr=False)


def test_workflow_phase_ordinals():
    assert WorkflowPhase.Pending == 0
    assert WorkflowPhase.Running == 1
    assert WorkflowPhase.Failed == 4


async def test_null_workflow_defaults():
    store = NullWorkflowDefinitionStore.Instance
    runner = NullWorkflowRunner.Instance
    state = NullWorkflowState.Instance
    assert store.backend_id == "null" and runner.backend_id == "null" and state.backend_id == "null"
    await store.upsert_async(WorkflowDefinition("d", "n", "1", "desc"))  # no-op
    assert await store.get_async("d") is None
    exec_ = await runner.start_async("d")
    assert exec_.run_id == "00000000-0000-0000-0000-000000000000"
    assert exec_.phase == WorkflowPhase.Failed and exec_.failure_reason == "NullWorkflowRunner"
    assert await runner.get_async("x") is None
    await runner.cancel_async("x")
    await state.checkpoint_async(CheckpointPayload("r", "s", b"\x00"))  # no-op
    assert await state.load_async("r", "s") is None


class _EchoExecutor(IConversationExecutor):
    """Emits two steps then finishes."""

    async def run_async(self, conversation, permissions, on_step, ct):
        on_step(ConversationStep(conversation.id, 0, "user", "{}", datetime.now(timezone.utc)))
        on_step(ConversationStep(conversation.id, 1, "agent", '{"reply":"hi"}', datetime.now(timezone.utc)))


class _BoomExecutor(IConversationExecutor):
    async def run_async(self, conversation, permissions, on_step, ct):
        raise RuntimeError("executor failed")


class _WaitExecutor(IConversationExecutor):
    """Waits for cancellation, then raises CancelledError like a cooperative task."""

    async def run_async(self, conversation, permissions, on_step, ct):
        await ct.wait()
        raise asyncio.CancelledError()


def test_runtime_requires_executor():
    with pytest.raises(ValueError):
        PacaConversationRuntime(None)  # type: ignore[arg-type]


async def test_queue_start_finish_with_steps():
    rt = PacaConversationRuntime(_EchoExecutor())
    c = rt.queue("c1", "proj", "agent-1", "hello", human_member_id="human-1")
    assert isinstance(c, AgentConversation) and c.state == ConversationState.Queued
    await rt.start_async("c1", _PERMS)
    done = rt.get("c1")
    assert done.state == ConversationState.Finished
    assert done.result_json == "{}"
    steps = rt.steps("c1")
    assert [s.speaker for s in steps] == ["user", "agent"]


async def test_queue_duplicate_raises():
    rt = PacaConversationRuntime(_EchoExecutor())
    rt.queue("dup", "p", "a", "x")
    with pytest.raises(RuntimeError):
        rt.queue("dup", "p", "a", "x")


async def test_start_non_queued_raises():
    rt = PacaConversationRuntime(_EchoExecutor())
    with pytest.raises(RuntimeError):
        await rt.start_async("missing", _PERMS)


async def test_executor_failure_marks_failed():
    rt = PacaConversationRuntime(_BoomExecutor())
    rt.queue("c", "p", "a", "x")
    await rt.start_async("c", _PERMS)
    done = rt.get("c")
    assert done.state == ConversationState.Failed
    assert done.failure_reason == "executor failed"


async def test_stop_marks_stopped():
    rt = PacaConversationRuntime(_WaitExecutor())
    rt.queue("c", "p", "a", "x")
    task = asyncio.ensure_future(rt.start_async("c", _PERMS))
    await asyncio.sleep(0)  # let the executor reach the wait
    rt.stop("c")
    await task
    assert rt.get("c").state == ConversationState.Stopped


def test_steps_of_unknown_conversation_empty():
    rt = PacaConversationRuntime(_EchoExecutor())
    assert rt.steps("ghost") == []
    assert rt.get("ghost") is None
