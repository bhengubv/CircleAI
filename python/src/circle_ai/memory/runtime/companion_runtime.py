# companion_runtime.py
#
# The host orchestrator that ticks the consolidator on a schedule, keeps the
# sync engine running, and exposes a single ingestion entry point for
# multimodal artefacts.
#
# The C# reference implements IHostedService (Generic Host) + IAsyncDisposable.
# Python has no Generic Host, so this port exposes the same lifecycle as
# start_async / stop_async / dispose_async and drives the periodic passes with
# asyncio background tasks. Behaviour matches the spec: an optional catch-up
# pass on start, per-tier periodic consolidation, periodic sync broadcasts, and
# graceful cancellation on stop.
#
# Ported faithfully from CircleAI.Memory.Runtime.CompanionRuntime (C# — spec).

from __future__ import annotations

import asyncio
import logging
from datetime import timedelta
from typing import Optional

from ..consolidation import (
    ConsolidationOutcome,
    IMemoryConsolidator,
    SleepKind,
)
from ..multimodal import (
    IngestionResult,
    MediaModality,
    MultimodalMemoryIngester,
)
from ..sync import ICompanionStateSyncEngine
from .companion_runtime_options import CompanionRuntimeOptions

_LOGGER = logging.getLogger("circle_ai.memory.runtime.CompanionRuntime")

_ZERO = timedelta(0)


def _seconds(interval: timedelta) -> float:
    return interval.total_seconds()


class CompanionRuntime:
    """Owns the lifecycle of the memory pipeline (consolidator, sync engine,
    multimodal ingester) and ticks the consolidation passes on a configurable
    schedule.
    """

    def __init__(
        self,
        consolidator: IMemoryConsolidator,
        options: Optional[CompanionRuntimeOptions] = None,
        sync_engine: Optional[ICompanionStateSyncEngine] = None,
        ingester: Optional[MultimodalMemoryIngester] = None,
        logger: Optional[logging.Logger] = None,
    ) -> None:
        if consolidator is None:
            raise ValueError("consolidator required")
        self._consolidator = consolidator
        self._sync_engine = sync_engine
        self._ingester = ingester
        self._options = options or CompanionRuntimeOptions()
        self._logger = logger or _LOGGER

        self._stop_event: Optional[asyncio.Event] = None
        self._daily_loop: Optional[asyncio.Task] = None
        self._weekly_loop: Optional[asyncio.Task] = None
        self._monthly_loop: Optional[asyncio.Task] = None
        self._sync_loop: Optional[asyncio.Task] = None

    # ── lifecycle ─────────────────────────────────────────────────────────

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        self._logger.info("CompanionRuntime starting.")
        self._stop_event = asyncio.Event()

        if self._sync_engine is not None:
            await self._sync_engine.start_async(ct=ct)
            self._logger.info("Sync engine started.")

        if self._options.catch_up_on_start:
            try:
                outcome = await self._consolidator.tick_async(
                    SleepKind.OnDemand, ct=ct
                )
                self._logger.info(
                    "Catch-up consolidation: daily=%s weekly=%s monthly=%s core=%s.",
                    outcome.daily_summaries_produced,
                    outcome.semantic_clusters_produced,
                    outcome.persona_deltas_produced,
                    outcome.core_promotions,
                )
            except Exception as ex:  # non-fatal
                self._logger.warning(
                    "Catch-up consolidation failed (non-fatal): %s", ex
                )

        if self._options.daily_tick_interval > _ZERO:
            self._daily_loop = asyncio.ensure_future(
                self._run_periodic(SleepKind.Daily, self._options.daily_tick_interval)
            )
        if self._options.weekly_tick_interval > _ZERO:
            self._weekly_loop = asyncio.ensure_future(
                self._run_periodic(
                    SleepKind.Weekly, self._options.weekly_tick_interval
                )
            )
        if self._options.monthly_tick_interval > _ZERO:
            self._monthly_loop = asyncio.ensure_future(
                self._run_periodic(
                    SleepKind.Monthly, self._options.monthly_tick_interval
                )
            )
        if self._sync_engine is not None and self._options.sync_broadcast_interval > _ZERO:
            self._sync_loop = asyncio.ensure_future(
                self._run_sync_broadcasts(self._options.sync_broadcast_interval)
            )

        self._logger.info("CompanionRuntime started.")

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        self._logger.info("CompanionRuntime stopping.")
        if self._stop_event is not None:
            self._stop_event.set()

        for loop in (
            self._daily_loop,
            self._weekly_loop,
            self._monthly_loop,
            self._sync_loop,
        ):
            await self._safe_await(loop)
        self._daily_loop = None
        self._weekly_loop = None
        self._monthly_loop = None
        self._sync_loop = None

        if self._sync_engine is not None:
            await self._sync_engine.dispose_async()

        self._logger.info("CompanionRuntime stopped.")

    async def dispose_async(self) -> None:
        await self.stop_async()

    async def __aenter__(self) -> "CompanionRuntime":
        await self.start_async()
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.dispose_async()

    # ── public helpers ────────────────────────────────────────────────────

    def consolidate_now_async(self, *, ct: Optional[object] = None):
        """Trigger an OnDemand consolidation pass. Hosts call this after large
        chunks of new activity (e.g. end of a long conversation) when they
        don't want to wait for the timer. Returns the awaitable outcome.
        """
        return self._consolidator.tick_async(SleepKind.OnDemand, ct=ct)

    def ingest_media_async(
        self,
        modality: MediaModality,
        source_bytes: bytes,
        *,
        mime_type: Optional[str] = None,
        source_uri: Optional[str] = None,
        tags: Optional[dict] = None,
        ct: Optional[object] = None,
    ):
        """Forward multimodal ingestion to the registered ingester. Raises
        :class:`RuntimeError` when no ingester was wired (the runtime can be
        wired without one for text-only hosts). Returns the awaitable result.
        """
        if self._ingester is None:
            raise RuntimeError(
                "CompanionRuntime was constructed without a MultimodalMemoryIngester."
            )
        return self._ingester.ingest_async(
            modality,
            source_bytes,
            mime_type=mime_type,
            source_uri=source_uri,
            tags=tags,
            ct=ct,
        )

    async def sync_now_async(self, *, ct: Optional[object] = None) -> None:
        """Force an immediate sync broadcast. No-op when sync isn't wired."""
        if self._sync_engine is not None:
            await self._sync_engine.sync_now_async(ct=ct)

    # ── internals ─────────────────────────────────────────────────────────

    async def _run_periodic(self, kind: SleepKind, interval: timedelta) -> None:
        try:
            await self._delay(self._options.initial_delay)
            while not self._is_stopping():
                try:
                    outcome = await self._consolidator.tick_async(kind)
                    if (
                        outcome.daily_summaries_produced
                        + outcome.semantic_clusters_produced
                        + outcome.persona_deltas_produced
                        + outcome.core_promotions
                    ) > 0:
                        self._logger.info(
                            "Consolidation tick %s: daily=%s weekly=%s monthly=%s core=%s.",
                            kind,
                            outcome.daily_summaries_produced,
                            outcome.semantic_clusters_produced,
                            outcome.persona_deltas_produced,
                            outcome.core_promotions,
                        )
                except asyncio.CancelledError:
                    raise
                except Exception as ex:
                    self._logger.warning("Consolidation tick %s failed: %s", kind, ex)
                await self._delay(interval)
        except asyncio.CancelledError:
            pass  # graceful

    async def _run_sync_broadcasts(self, interval: timedelta) -> None:
        try:
            await self._delay(self._options.initial_delay)
            while not self._is_stopping():
                try:
                    assert self._sync_engine is not None
                    await self._sync_engine.sync_now_async()
                except asyncio.CancelledError:
                    raise
                except Exception as ex:
                    self._logger.warning("Sync broadcast failed: %s", ex)
                await self._delay(interval)
        except asyncio.CancelledError:
            pass  # graceful

    async def _delay(self, interval: timedelta) -> None:
        """Sleep for ``interval``, but wake immediately if stop was requested.

        Mirrors ``Task.Delay(interval, stopToken)`` — cancellation short-circuits
        the wait.
        """
        assert self._stop_event is not None
        seconds = _seconds(interval)
        if seconds <= 0:
            # Still yield + honour an already-set stop.
            if self._stop_event.is_set():
                raise asyncio.CancelledError()
            return
        try:
            await asyncio.wait_for(self._stop_event.wait(), timeout=seconds)
            # Event was set within the window — behave like a cancelled delay.
            raise asyncio.CancelledError()
        except asyncio.TimeoutError:
            return

    def _is_stopping(self) -> bool:
        return self._stop_event is None or self._stop_event.is_set()

    @staticmethod
    async def _safe_await(task: Optional[asyncio.Task]) -> None:
        if task is None:
            return
        try:
            await task
        except asyncio.CancelledError:
            pass
        except Exception:
            pass  # logged earlier
