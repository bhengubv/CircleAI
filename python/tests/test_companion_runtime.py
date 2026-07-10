"""test_companion_runtime.py

Verifies CompanionRuntime lifecycle: catch-up-on-start consolidation, sync
engine start + periodic broadcast, on-demand consolidation, media ingestion
forwarding (and the no-ingester error), and graceful stop/dispose.

Mirrors CircleAI.Memory.Runtime.CompanionRuntime (C# — the spec). The C#
reference drives its loops with Task.Delay; this port uses asyncio, so the test
uses tiny intervals and a zero initial delay to exercise the loops quickly.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.memory.consolidation import ConsolidationOutcome, SleepKind
from circle_ai.memory.multimodal import (
    HeuristicMultimodalCaptioner,
    InMemoryMultimodalMemoryStore,
    MediaModality,
    MultimodalMemoryIngester,
)
from circle_ai.memory.runtime import CompanionRuntime, CompanionRuntimeOptions


class _FakeConsolidator:
    """IMemoryConsolidator double — records the kinds it was ticked with."""

    def __init__(self, produced: int = 0) -> None:
        self.ticks: list[SleepKind] = []
        self._produced = produced

    async def tick_async(self, kind: SleepKind, *, ct=None) -> ConsolidationOutcome:
        self.ticks.append(kind)
        return ConsolidationOutcome(
            kind=kind,
            daily_summaries_produced=self._produced,
            semantic_clusters_produced=0,
            persona_deltas_produced=0,
            core_promotions=0,
            episodes_pruned=0,
            dailies_pruned=0,
            semantics_pruned=0,
            ran_at_utc=datetime.now(timezone.utc),
        )


class _FakeSyncEngine:
    """ICompanionStateSyncEngine double — records lifecycle + broadcasts."""

    def __init__(self) -> None:
        self.started = False
        self.disposed = False
        self.sync_calls = 0

    async def start_async(self, *, ct=None) -> None:
        self.started = True

    async def sync_now_async(self, *, ct=None) -> None:
        self.sync_calls += 1

    async def write_local_async(self, *a, **k):  # pragma: no cover - unused here
        raise AssertionError("not exercised")

    async def dispose_async(self) -> None:
        self.disposed = True


def _no_periodic_opts(**over) -> CompanionRuntimeOptions:
    """Options with every periodic loop disabled unless overridden."""
    base = dict(
        daily_tick_interval=timedelta(0),
        weekly_tick_interval=timedelta(0),
        monthly_tick_interval=timedelta(0),
        sync_broadcast_interval=timedelta(0),
        initial_delay=timedelta(0),
        catch_up_on_start=True,
    )
    base.update(over)
    return CompanionRuntimeOptions(**base)


# ── catch-up on start ─────────────────────────────────────────────────────────


async def test_start_runs_catch_up_consolidation_on_demand() -> None:
    consolidator = _FakeConsolidator()
    runtime = CompanionRuntime(consolidator, options=_no_periodic_opts())
    await runtime.start_async()
    await runtime.stop_async()
    assert consolidator.ticks == [SleepKind.OnDemand]


async def test_start_skips_catch_up_when_disabled() -> None:
    consolidator = _FakeConsolidator()
    runtime = CompanionRuntime(
        consolidator, options=_no_periodic_opts(catch_up_on_start=False)
    )
    await runtime.start_async()
    await runtime.stop_async()
    assert consolidator.ticks == []


async def test_catch_up_failure_is_non_fatal() -> None:
    class _Boom(_FakeConsolidator):
        async def tick_async(self, kind, *, ct=None):
            raise RuntimeError("kaboom")

    runtime = CompanionRuntime(_Boom(), options=_no_periodic_opts())
    await runtime.start_async()  # must not raise
    await runtime.stop_async()


# ── sync engine lifecycle ─────────────────────────────────────────────────────


async def test_start_starts_sync_engine_and_stop_disposes_it() -> None:
    consolidator = _FakeConsolidator()
    engine = _FakeSyncEngine()
    runtime = CompanionRuntime(
        consolidator, options=_no_periodic_opts(), sync_engine=engine
    )
    await runtime.start_async()
    assert engine.started is True
    await runtime.stop_async()
    assert engine.disposed is True


async def test_periodic_sync_broadcasts_fire() -> None:
    engine = _FakeSyncEngine()
    runtime = CompanionRuntime(
        _FakeConsolidator(),
        options=_no_periodic_opts(sync_broadcast_interval=timedelta(milliseconds=10)),
        sync_engine=engine,
    )
    await runtime.start_async()
    await asyncio.sleep(0.05)  # allow a few broadcasts
    await runtime.stop_async()
    assert engine.sync_calls >= 1


async def test_periodic_daily_ticks_fire() -> None:
    consolidator = _FakeConsolidator()
    runtime = CompanionRuntime(
        consolidator,
        options=_no_periodic_opts(
            catch_up_on_start=False,
            daily_tick_interval=timedelta(milliseconds=10),
        ),
    )
    await runtime.start_async()
    await asyncio.sleep(0.05)
    await runtime.stop_async()
    assert SleepKind.Daily in consolidator.ticks


# ── public helpers ────────────────────────────────────────────────────────────


async def test_consolidate_now_ticks_on_demand() -> None:
    consolidator = _FakeConsolidator()
    runtime = CompanionRuntime(
        consolidator, options=_no_periodic_opts(catch_up_on_start=False)
    )
    outcome = await runtime.consolidate_now_async()
    assert outcome.kind == SleepKind.OnDemand
    assert consolidator.ticks == [SleepKind.OnDemand]


async def test_sync_now_is_noop_without_engine() -> None:
    runtime = CompanionRuntime(
        _FakeConsolidator(), options=_no_periodic_opts(catch_up_on_start=False)
    )
    await runtime.sync_now_async()  # must not raise


async def test_ingest_media_without_ingester_raises() -> None:
    runtime = CompanionRuntime(
        _FakeConsolidator(), options=_no_periodic_opts(catch_up_on_start=False)
    )
    with pytest.raises(RuntimeError):
        await runtime.ingest_media_async(MediaModality.Image, b"\x89PNG\r\n")


async def test_ingest_media_forwards_to_ingester() -> None:
    store = InMemoryMultimodalMemoryStore()
    ingester = MultimodalMemoryIngester([HeuristicMultimodalCaptioner()], store)
    runtime = CompanionRuntime(
        _FakeConsolidator(),
        options=_no_periodic_opts(catch_up_on_start=False),
        ingester=ingester,
    )
    result = await runtime.ingest_media_async(
        MediaModality.Image, b"\x89PNG\r\n\x1a\n" + b"x" * 32, mime_type="image/png"
    )
    assert result.entry is not None
    assert result.was_deduplicated is False


# ── async context manager ─────────────────────────────────────────────────────


async def test_runtime_as_async_context_manager_starts_and_disposes() -> None:
    consolidator = _FakeConsolidator()
    engine = _FakeSyncEngine()
    async with CompanionRuntime(
        consolidator, options=_no_periodic_opts(), sync_engine=engine
    ):
        assert engine.started is True
    assert engine.disposed is True
