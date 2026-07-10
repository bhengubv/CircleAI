"""test_proactive_scheduler.py

Verifies the proactive scheduling substrate ported from
CircleAI.Companion.Proactive (ProactiveScheduler.cs + NullImplementations.cs):
refresh snapshotting, cron tick with per-minute dedup, event dispatch, manual
run-by-id, per-source-context last-run isolation, and the null/in-memory/delegate
building blocks.
"""
from __future__ import annotations

from datetime import datetime, timezone
from typing import List, Optional

import pytest

from circle_ai.companion.proactive import (
    DelegateProactiveTaskRunner,
    InMemoryProactiveTaskSource,
    IProactiveScheduler,
    IProactiveTaskRunner,
    IProactiveTaskSource,
    NullProactiveTaskRunner,
    NullProactiveTaskSource,
    ProactiveScheduler,
    ProactiveTask,
    ProactiveTaskRunResult,
    ProactiveTrigger,
)


def _dt(y, mo, d, h, mi) -> datetime:
    return datetime(y, mo, d, h, mi, tzinfo=timezone.utc)


class RecordingRunner(IProactiveTaskRunner):
    """Records every task it is asked to run."""

    def __init__(self) -> None:
        self.runs: List[tuple] = []

    @property
    def backend_id(self) -> str:
        return "recording"

    async def run_async(self, task, variables=None, *, ct=None) -> ProactiveTaskRunResult:
        self.runs.append((task.id, dict(variables) if variables else None))
        return ProactiveTaskRunResult(task.id, True, None)


def _cron_task(id: str, cron: str, ctx: Optional[str] = None) -> ProactiveTask:
    return ProactiveTask(id, ProactiveTrigger(cron=cron), payload=object(), source_context=ctx)


def _event_task(id: str, event: str) -> ProactiveTask:
    return ProactiveTask(id, ProactiveTrigger(on_event=event), payload=object())


# ── null building blocks ────────────────────────────────────────────────────


async def test_null_source_is_empty() -> None:
    src = NullProactiveTaskSource()
    assert isinstance(src, IProactiveTaskSource)
    assert src.backend_id == "null"
    assert list(await src.get_tasks_async()) == []
    assert list(await src.get_errors_async()) == []


async def test_null_runner_fails_closed() -> None:
    runner = NullProactiveTaskRunner()
    assert runner.backend_id == "null"
    res = await runner.run_async(_cron_task("t", "* * * * *"))
    assert res.success is False
    assert "NullProactiveTaskRunner" in res.failure_message


async def test_delegate_runner_invokes_handler() -> None:
    seen = {}

    async def handler(task, variables, ct) -> ProactiveTaskRunResult:
        seen["id"] = task.id
        return ProactiveTaskRunResult(task.id, True, None)

    runner = DelegateProactiveTaskRunner(handler)
    assert runner.backend_id == "delegate"
    await runner.run_async(_event_task("e", "x"))
    assert seen["id"] == "e"


# ── in-memory source ────────────────────────────────────────────────────────


async def test_in_memory_source_upsert_remove_clear() -> None:
    src = InMemoryProactiveTaskSource()
    assert src.backend_id == "in-memory"
    src.upsert(_cron_task("a", "* * * * *"))
    src.upsert(_cron_task("b", "* * * * *"))
    assert {t.id for t in await src.get_tasks_async()} == {"a", "b"}
    assert src.remove("a") is True
    assert src.remove("a") is False
    assert {t.id for t in await src.get_tasks_async()} == {"b"}
    src.clear()
    assert list(await src.get_tasks_async()) == []


async def test_in_memory_source_context_isolation() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_cron_task("dup", "* * * * *", ctx="tenant-a"))
    src.upsert(_cron_task("dup", "* * * * *", ctx="tenant-b"))
    # Same id in two contexts -> two distinct entries.
    assert len(await src.get_tasks_async()) == 2


# ── scheduler: refresh + snapshot ───────────────────────────────────────────


async def test_scheduler_refresh_snapshots_tasks() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_cron_task("a", "* * * * *"))
    sched = ProactiveScheduler(src, RecordingRunner())
    assert isinstance(sched, IProactiveScheduler)
    assert sched.backend_id == "default"
    assert sched.tasks == []  # empty before refresh
    await sched.refresh_async()
    assert {t.id for t in sched.tasks} == {"a"}


async def test_scheduler_get_next_run_none_for_non_cron() -> None:
    sched = ProactiveScheduler(InMemoryProactiveTaskSource(), RecordingRunner())
    assert sched.get_next_run(_event_task("e", "x"), _dt(2026, 7, 8, 0, 0)) is None
    nxt = sched.get_next_run(_cron_task("c", "30 6 * * *"), _dt(2026, 7, 8, 6, 0))
    assert nxt == _dt(2026, 7, 8, 6, 30)


# ── scheduler: tick ─────────────────────────────────────────────────────────


async def test_tick_runs_due_cron_task_once_per_minute() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_cron_task("a", "* * * * *"))  # every minute
    runner = RecordingRunner()
    sched = ProactiveScheduler(src, runner)
    await sched.refresh_async()

    now = _dt(2026, 7, 8, 6, 30)
    await sched.tick_async(now)
    assert len(runner.runs) == 1
    # A second tick in the SAME minute must not re-run (last-run dedup).
    await sched.tick_async(now)
    assert len(runner.runs) == 1
    # A tick a minute later fires again.
    await sched.tick_async(_dt(2026, 7, 8, 6, 31))
    assert len(runner.runs) == 2


async def test_tick_skips_not_yet_due() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_cron_task("a", "30 6 * * *"))  # only 06:30
    runner = RecordingRunner()
    sched = ProactiveScheduler(src, runner)
    await sched.refresh_async()
    await sched.tick_async(_dt(2026, 7, 8, 6, 0))  # too early
    assert runner.runs == []
    await sched.tick_async(_dt(2026, 7, 8, 6, 30))
    assert len(runner.runs) == 1


async def test_tick_ignores_event_tasks() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_event_task("e", "note-saved"))
    runner = RecordingRunner()
    sched = ProactiveScheduler(src, runner)
    await sched.refresh_async()
    await sched.tick_async(_dt(2026, 7, 8, 6, 30))
    assert runner.runs == []


async def test_tick_bad_cron_does_not_crash() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_cron_task("bad", "not a cron"))
    runner = RecordingRunner()
    sched = ProactiveScheduler(src, runner)
    await sched.refresh_async()
    await sched.tick_async(_dt(2026, 7, 8, 6, 30))  # should swallow the parse error
    assert runner.runs == []


# ── scheduler: event dispatch ───────────────────────────────────────────────


async def test_dispatch_event_fires_matching_tasks() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_event_task("e1", "note-saved"))
    src.upsert(_event_task("e2", "task-created"))
    runner = RecordingRunner()
    sched = ProactiveScheduler(src, runner)
    await sched.refresh_async()
    await sched.dispatch_event_async("note-saved", {"path": "/n"})
    assert [r[0] for r in runner.runs] == ["e1"]
    assert runner.runs[0][1] == {"path": "/n"}


async def test_dispatch_event_is_case_insensitive() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_event_task("e1", "Note-Saved"))
    runner = RecordingRunner()
    sched = ProactiveScheduler(src, runner)
    await sched.refresh_async()
    await sched.dispatch_event_async("note-saved")
    assert [r[0] for r in runner.runs] == ["e1"]


async def test_dispatch_event_rejects_blank() -> None:
    sched = ProactiveScheduler(InMemoryProactiveTaskSource(), RecordingRunner())
    with pytest.raises(ValueError):
        await sched.dispatch_event_async("  ")


# ── scheduler: manual run-by-id ─────────────────────────────────────────────


async def test_run_by_id_runs_and_returns_result() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_event_task("only", "x"))
    runner = RecordingRunner()
    sched = ProactiveScheduler(src, runner)
    await sched.refresh_async()
    res = await sched.run_by_id_async("only")
    assert res.success is True
    assert [r[0] for r in runner.runs] == ["only"]


async def test_run_by_id_unknown_returns_failure() -> None:
    sched = ProactiveScheduler(InMemoryProactiveTaskSource(), RecordingRunner())
    await sched.refresh_async()
    res = await sched.run_by_id_async("ghost")
    assert res.success is False
    assert "No task with id" in res.failure_message


async def test_run_by_id_rejects_blank() -> None:
    sched = ProactiveScheduler(InMemoryProactiveTaskSource(), RecordingRunner())
    with pytest.raises(ValueError):
        await sched.run_by_id_async("")


# ── scheduler: per-context last-run isolation ───────────────────────────────


async def test_tick_isolates_last_run_by_context() -> None:
    src = InMemoryProactiveTaskSource()
    # Same task id in two contexts, both fire every minute.
    src.upsert(_cron_task("dup", "* * * * *", ctx="a"))
    src.upsert(_cron_task("dup", "* * * * *", ctx="b"))
    runner = RecordingRunner()
    sched = ProactiveScheduler(src, runner)
    await sched.refresh_async()
    await sched.tick_async(_dt(2026, 7, 8, 6, 30))
    # Both contexts run independently in the same minute.
    assert len(runner.runs) == 2


async def test_refresh_drops_stale_last_run_state() -> None:
    src = InMemoryProactiveTaskSource()
    src.upsert(_cron_task("a", "* * * * *"))
    runner = RecordingRunner()
    sched = ProactiveScheduler(src, runner)
    await sched.refresh_async()
    await sched.tick_async(_dt(2026, 7, 8, 6, 30))
    assert len(runner.runs) == 1
    # Remove the task, refresh (drops its last-run), re-add, and a same-minute
    # tick fires again because the last-run state was pruned.
    src.remove("a")
    await sched.refresh_async()
    src.upsert(_cron_task("a", "* * * * *"))
    await sched.refresh_async()
    await sched.tick_async(_dt(2026, 7, 8, 6, 30))
    assert len(runner.runs) == 2


def test_scheduler_rejects_none_deps() -> None:
    with pytest.raises(ValueError):
        ProactiveScheduler(None, RecordingRunner())  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        ProactiveScheduler(InMemoryProactiveTaskSource(), None)  # type: ignore[arg-type]
