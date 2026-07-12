# security_orchestration_bridge.py
#
# Port of CircleAI.Orchestration SecurityOrchestrationBridge.cs (C# — the EXACT
# spec).
#
# Bridges CircleAI.Security's ISecurityWatchdog to a LokiOrchestrator.
#
# When the local immune system detects a confirmed anomaly, this wrapper:
#   1. Delegates the runtime response to the inner watchdog (key rotation, mesh
#      isolation, state rollback) — fast path, in-process.
#   2. IN PARALLEL, dispatches an ops-security AgentTask to the orchestrator so a
#      background agent swarm can perform deeper diagnostics, generate a patch,
#      and pass it through quality gates.
#
# The two paths are independent — the immediate watchdog response is never
# blocked by agent orchestration, and agent failures never break the runtime
# response.
#
# ─ Porting notes ────────────────────────────────────────────────────────────
#   * Implements ISecurityWatchdog (circle_ai.security.security_watchdog) so the
#     bridge is a drop-in replacement for the inner watchdog.
#   * The C# awaits the watchdog task and fires the agent dispatch
#     fire-and-forget (observing only faults, which it swallows). The port does
#     the same: it schedules `_dispatch_agent_async` as a background task via
#     asyncio.ensure_future and attaches a done-callback that swallows
#     exceptions, then awaits the inner watchdog and returns its response.
#   * `_dispatch_agent_async` maps the signal via IncidentTrigger and drains the
#     orchestrator's swarm enumerator (results are observed on the host side).

from __future__ import annotations

import asyncio
from typing import AsyncIterator, Optional

from ..security.security_watchdog import ISecurityWatchdog
from .incident_trigger import IncidentTrigger
from .loki_orchestrator import LokiOrchestrator


class SecurityOrchestrationBridge(ISecurityWatchdog):
    """Wraps an :class:`ISecurityWatchdog` so that every anomaly signal also
    dispatches an ops-security :class:`AgentTask` to a :class:`LokiOrchestrator`.
    Runtime response and agent dispatch proceed in parallel; neither blocks the
    other. Mirrors ``CircleAI.Orchestration.SecurityOrchestrationBridge``.
    """

    def __init__(
        self,
        inner: ISecurityWatchdog,
        orchestrator: LokiOrchestrator,
        dispatch_threshold: float = 0.30,
    ) -> None:
        if inner is None:
            raise ValueError("inner must not be None")
        if orchestrator is None:
            raise ValueError("orchestrator must not be None")
        self._inner = inner
        self._orchestrator = orchestrator
        self._dispatch_threshold = dispatch_threshold

    async def on_anomaly_detected_async(
        self,
        signal: object,
        checkpoint: Optional[object] = None,
        ct: Optional[object] = None,
    ) -> object:
        if signal is None:
            raise ValueError("signal must not be None")

        # Kick off the agent path fire-and-forget; the runtime response (key
        # rotation, rollback) MUST NOT wait on the agent swarm, which may take
        # minutes. Faults are observed and swallowed — agent failures must not
        # crash the runtime.
        agent_task = asyncio.ensure_future(self._dispatch_agent_async(signal, ct))
        agent_task.add_done_callback(_swallow_agent_fault)

        # Await the watchdog so the caller gets the runtime response immediately.
        return await self._inner.on_anomaly_detected_async(signal, checkpoint, ct)

    def stream_signals_async(self, ct: Optional[object] = None) -> AsyncIterator[object]:
        return self._inner.stream_signals_async(ct)

    async def _dispatch_agent_async(self, signal: object, ct: Optional[object]) -> None:
        task = IncidentTrigger.from_anomaly_signal(signal, self._dispatch_threshold)
        if task is None:
            return

        # Drain the swarm enumerator — typically a single task -> single result.
        # Results are observable through orchestrator subscriptions on the host
        # side.
        async for _ in self._orchestrator.run_swarm_async([task], ct):
            pass


def _swallow_agent_fault(task: "asyncio.Task") -> None:
    """Done-callback that observes and swallows any exception raised by the
    fire-and-forget agent dispatch (mirrors the C# ContinueWith that flattens
    and drops the fault)."""
    if task.cancelled():
        return
    # Retrieving the exception marks it as handled; we intentionally drop it.
    _ = task.exception()
