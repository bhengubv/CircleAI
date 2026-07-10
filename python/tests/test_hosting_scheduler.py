"""test_hosting_scheduler.py

Exercises the hosting cron scheduler + triggers + proactive reasoning:
InMemoryScheduledTaskStore (due-job filtering), ScheduledAIService (job
execution + state transitions + next-run recompute + completion event),
ScheduleTrigger / IdleTrigger, and ProactiveReasoningService (first-trigger-
wins, prompt building, message event). Ports of CircleAI.Hosting.{
ScheduledAIService, IScheduledTaskStore, triggers, ProactiveReasoningService}.
"""
from __future__ import annotations

import datetime as _dt
from typing import List, Optional

import pytest

from circle_ai.hosting import (
    CronJob,
    CronJobState,
    DeliveryTarget,
    IdleTrigger,
    InMemoryScheduledTaskStore,
    IProactiveReasoningService,
    ITriggerCondition,
    ProactiveContext,
    ProactiveReasoningService,
    ScheduledAIService,
    ScheduleTrigger,
)

_UTC = _dt.timezone.utc


def _now() -> _dt.datetime:
    return _dt.datetime.now(_UTC)


# ── InMemoryScheduledTaskStore ──────────────────────────────────────────────


async def test_store_upsert_get_list_delete() -> None:
    store = InMemoryScheduledTaskStore()
    job = CronJob("a", "A", "prompt", "* * * * *", DeliveryTarget.LOCAL)
    await store.upsert_async(job)
    assert (await store.get_async("a")).name == "A"
    assert len(await store.list_async()) == 1
    await store.delete_async("a")
    assert await store.get_async("a") is None
    assert len(await store.list_async()) == 0


async def test_store_get_rejects_blank_id() -> None:
    store = InMemoryScheduledTaskStore()
    with pytest.raises(ValueError):
        await store.get_async("")


async def test_store_due_jobs_only_enabled_and_past() -> None:
    store = InMemoryScheduledTaskStore()
    past = _now() - _dt.timedelta(minutes=1)
    future = _now() + _dt.timedelta(hours=1)
    await store.upsert_async(
        CronJob("due", "D", "p", "* * * * *", DeliveryTarget.LOCAL, next_run_utc=past)
    )
    await store.upsert_async(
        CronJob("future", "F", "p", "* * * * *", DeliveryTarget.LOCAL, next_run_utc=future)
    )
    await store.upsert_async(
        CronJob(
            "disabled", "X", "p", "* * * * *", DeliveryTarget.LOCAL,
            next_run_utc=past, is_enabled=False,
        )
    )
    await store.upsert_async(
        CronJob("nonext", "N", "p", "* * * * *", DeliveryTarget.LOCAL, next_run_utc=None)
    )
    due = await store.get_due_jobs_async()
    assert {j.id for j in due} == {"due"}


# ── ScheduledAIService ──────────────────────────────────────────────────────


class _StubButler:
    def __init__(self, reply: str = "job-answer", fail: bool = False) -> None:
        self.reply = reply
        self.fail = fail
        self.asked: List[str] = []

    async def ask_async(self, prompt: str, ct: object = None) -> str:
        self.asked.append(prompt)
        if self.fail:
            raise RuntimeError("butler boom")
        return self.reply


async def test_scheduled_service_runs_due_job_and_marks_succeeded() -> None:
    store = InMemoryScheduledTaskStore()
    past = _now() - _dt.timedelta(minutes=1)
    await store.upsert_async(
        CronJob("j", "J", "the prompt", "* * * * *", DeliveryTarget.LOCAL, next_run_utc=past)
    )
    butler = _StubButler("done")
    svc = ScheduledAIService(butler, store)

    completed: List[tuple] = []
    svc.add_job_completed_handler(lambda e: completed.append((e.job.id, e.response, e.error)))

    await svc.process_due_jobs_async()

    assert butler.asked == ["the prompt"]
    updated = await store.get_async("j")
    assert updated.state == CronJobState.SUCCEEDED
    assert updated.last_run_utc is not None
    assert updated.next_run_utc is not None  # recomputed from "* * * * *"
    assert completed == [("j", "done", None)]


async def test_scheduled_service_marks_failed_on_butler_error() -> None:
    store = InMemoryScheduledTaskStore()
    past = _now() - _dt.timedelta(minutes=1)
    await store.upsert_async(
        CronJob("j", "J", "p", "* * * * *", DeliveryTarget.LOCAL, next_run_utc=past)
    )
    butler = _StubButler(fail=True)
    svc = ScheduledAIService(butler, store)

    events: List = []
    svc.add_job_completed_handler(lambda e: events.append(e))

    await svc.process_due_jobs_async()

    updated = await store.get_async("j")
    assert updated.state == CronJobState.FAILED
    assert events[0].error is not None
    assert events[0].response == ""


async def test_scheduled_service_skips_when_no_due_jobs() -> None:
    store = InMemoryScheduledTaskStore()
    butler = _StubButler()
    svc = ScheduledAIService(butler, store)
    await svc.process_due_jobs_async()
    assert butler.asked == []


async def test_scheduled_service_rejects_none_deps() -> None:
    with pytest.raises(ValueError):
        ScheduledAIService(None, InMemoryScheduledTaskStore())
    with pytest.raises(ValueError):
        ScheduledAIService(_StubButler(), None)


async def test_scheduled_service_handler_error_does_not_crash() -> None:
    store = InMemoryScheduledTaskStore()
    past = _now() - _dt.timedelta(minutes=1)
    await store.upsert_async(
        CronJob("j", "J", "p", "* * * * *", DeliveryTarget.LOCAL, next_run_utc=past)
    )
    svc = ScheduledAIService(_StubButler(), store)

    def _boom(e):
        raise RuntimeError("subscriber boom")

    svc.add_job_completed_handler(_boom)
    # Must not raise despite the throwing subscriber.
    await svc.process_due_jobs_async()
    assert (await store.get_async("j")).state == CronJobState.SUCCEEDED


# ── ScheduleTrigger ─────────────────────────────────────────────────────────


def _ctx(now: _dt.datetime, idle: _dt.timedelta = _dt.timedelta(0)) -> ProactiveContext:
    return ProactiveContext(
        user_id="u",
        now_utc=now,
        time_since_last_interaction=idle,
        affect_state=None,
        active_goals=[],
    )


async def test_schedule_trigger_fires_in_window_once_per_day() -> None:
    trig = ScheduleTrigger(_dt.time(9, 0))
    # 09:02 is inside the 09:00–09:05 window.
    assert await trig.is_met_async(_ctx(_dt.datetime(2026, 7, 8, 9, 2, tzinfo=_UTC))) is True
    # Same day again → already fired, no re-fire.
    assert await trig.is_met_async(_ctx(_dt.datetime(2026, 7, 8, 9, 3, tzinfo=_UTC))) is False
    # Next day, back in window → fires again.
    assert await trig.is_met_async(_ctx(_dt.datetime(2026, 7, 9, 9, 1, tzinfo=_UTC))) is True


async def test_schedule_trigger_outside_window_does_not_fire() -> None:
    trig = ScheduleTrigger(_dt.time(9, 0))
    assert await trig.is_met_async(_ctx(_dt.datetime(2026, 7, 8, 9, 6, tzinfo=_UTC))) is False
    assert await trig.is_met_async(_ctx(_dt.datetime(2026, 7, 8, 8, 59, tzinfo=_UTC))) is False


async def test_schedule_trigger_wraps_midnight() -> None:
    trig = ScheduleTrigger(_dt.time(23, 58))  # window 23:58 .. 00:03
    assert await trig.is_met_async(_ctx(_dt.datetime(2026, 7, 8, 23, 59, tzinfo=_UTC))) is True


def test_schedule_trigger_name_default_and_custom() -> None:
    assert ScheduleTrigger(_dt.time(1, 0)).name == "schedule"
    assert ScheduleTrigger(_dt.time(1, 0), name="morning").name == "morning"


# ── IdleTrigger ─────────────────────────────────────────────────────────────


async def test_idle_trigger_fires_past_threshold() -> None:
    trig = IdleTrigger(_dt.timedelta(hours=1))
    assert await trig.is_met_async(_ctx(_now(), idle=_dt.timedelta(hours=2))) is True
    assert await trig.is_met_async(_ctx(_now(), idle=_dt.timedelta(minutes=30))) is False


async def test_idle_trigger_default_is_four_hours() -> None:
    trig = IdleTrigger()
    assert trig.idle_threshold == _dt.timedelta(hours=4)
    assert trig.name == "idle"


# ── ProactiveReasoningService ────────────────────────────────────────────────


class _AlwaysTrigger(ITriggerCondition):
    def __init__(self, name: str) -> None:
        self._name = name
        self.checked = 0

    @property
    def name(self) -> str:
        return self._name

    async def is_met_async(self, context: ProactiveContext, ct: object = None) -> bool:
        self.checked += 1
        return True


class _NeverTrigger(ITriggerCondition):
    def __init__(self, name: str) -> None:
        self._name = name
        self.checked = 0

    @property
    def name(self) -> str:
        return self._name

    async def is_met_async(self, context: ProactiveContext, ct: object = None) -> bool:
        self.checked += 1
        return False


async def test_proactive_fires_only_first_matching_trigger() -> None:
    butler = _StubButler("check-in!")
    t1 = _NeverTrigger("first")
    t2 = _AlwaysTrigger("second")
    t3 = _AlwaysTrigger("third")
    svc = ProactiveReasoningService(butler, triggers=[t1, t2, t3])
    assert isinstance(svc, IProactiveReasoningService)

    messages: List = []
    svc.add_proactive_message_handler(lambda a: messages.append(a))

    await svc.check_async("user-1")

    assert t1.checked == 1
    assert t2.checked == 1
    assert t3.checked == 0  # second fired → third never evaluated
    assert len(messages) == 1
    assert messages[0].trigger_name == "second"
    assert messages[0].message == "check-in!"
    assert messages[0].user_id == "user-1"


async def test_proactive_no_message_when_no_trigger_fires() -> None:
    butler = _StubButler()
    svc = ProactiveReasoningService(butler, triggers=[_NeverTrigger("x")])
    messages: List = []
    svc.add_proactive_message_handler(lambda a: messages.append(a))
    await svc.check_async("u")
    assert messages == []
    assert butler.asked == []


async def test_proactive_empty_triggers_is_noop() -> None:
    butler = _StubButler()
    svc = ProactiveReasoningService(butler, triggers=[])
    await svc.check_async("u")
    assert butler.asked == []


async def test_proactive_rejects_blank_user() -> None:
    svc = ProactiveReasoningService(_StubButler(), triggers=[_AlwaysTrigger("t")])
    with pytest.raises(ValueError):
        await svc.check_async("  ")


async def test_proactive_requires_triggers_and_butler() -> None:
    with pytest.raises(ValueError):
        ProactiveReasoningService(None, triggers=[])
    with pytest.raises(ValueError):
        ProactiveReasoningService(_StubButler(), triggers=None)


async def test_proactive_prompt_mentions_goals(monkeypatch) -> None:
    # A goal store returning one active goal should surface the goal title in
    # the generated prompt (byte-for-byte prompt builder).
    from circle_ai.memory.goal import Goal, GoalPriority, GoalStatus

    def _goal(title: str) -> Goal:
        return Goal(
            id="g1",
            user_id="u",
            title=title,
            description="",
            status=GoalStatus.ACTIVE,
            priority=GoalPriority.NORMAL,
            created_utc=_now(),
        )

    class _CaptureButler(_StubButler):
        def __init__(self) -> None:
            super().__init__("ok")
            self.prompt: Optional[str] = None

        async def ask_async(self, prompt: str, ct: object = None) -> str:
            self.prompt = prompt
            return "ok"

    class _OneGoalStore:
        async def get_active_async(self, user_id: str, ct: object = None):
            return [_goal("Learn piano")]

    butler = _CaptureButler()
    svc = ProactiveReasoningService(
        butler, goal_store=_OneGoalStore(), triggers=[_AlwaysTrigger("t")]
    )
    svc.add_proactive_message_handler(lambda a: None)
    await svc.check_async("u")
    assert butler.prompt is not None
    assert "Learn piano" in butler.prompt
    assert "1 active goal" in butler.prompt
