# in_memory_observer.py
#
# Port of CircleAI.Observer InMemoryObserver.cs (C# — the EXACT spec).
#
# (3.3.0) Real observation-loop runtime + sensor recorder + decision shape.
#   • SensorRecorder — subscribes to a sensor and holds its latest reading.
#   • ObserverDecision — what the reasoner returns each tick.
#   • InMemoryObservationLoop — perceive→reason→act: collect the latest reading
#     from each recorder, ask the (async) reasoner for a decision, run each named
#     tool, then fan the ObservationTick out to subscribers.
#
# The C# runs the loop on a Task with Task.Delay between ticks. Here the reasoner
# and tool.Invoke are already coroutines, so the loop runs as an asyncio task and
# uses asyncio.sleep for the interval.
#
# Concurrency (mirrors the C#):
#   • the subscriber list is snapshotted under the lock and invoked OUTSIDE it;
#   • per-tool and per-subscriber exceptions are swallowed so one failure never
#     stops the loop;
#   • the reasoner's exception skips just that tick (the loop keeps ticking);
#   • cancellation (stop) breaks the loop cleanly.

from __future__ import annotations

import asyncio
import threading
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from typing import Awaitable, Callable, List, Mapping, Optional, Sequence

from .contracts import (
    IDisposable,
    IObservationLoop,
    IObservationToolbox,
    ISensor,
    ObservationTick,
    SensorReading,
    TickObserver,
)


class SensorRecorder:
    """Captures the latest reading from a sensor. Mirrors
    ``CircleAI.Observer.SensorRecorder``."""

    def __init__(self, sensor: ISensor) -> None:
        if sensor is None:
            raise ValueError("sensor")
        self._latest: Optional[SensorReading] = None

        async def _on(r: SensorReading) -> None:
            self._latest = r

        self._sub = sensor.subscribe(_on)

    @property
    def latest(self) -> Optional[SensorReading]:
        return self._latest

    def dispose(self) -> None:
        self._sub.dispose()


@dataclass(frozen=True, slots=True)
class ObserverDecision:
    """Mirrors ``CircleAI.Observer.ObserverDecision`` — ``record(string Reasoning,
    IReadOnlyList<string> ToolsToInvoke, IReadOnlyDictionary<string,string>? ToolArgs = null)``.
    """

    reasoning: str
    tools_to_invoke: Sequence[str]
    tool_args: Optional[Mapping[str, str]] = None


#: reasoner(readings, ct) -> Awaitable[ObserverDecision]
Reasoner = Callable[[Sequence[SensorReading], Optional[object]], Awaitable[ObserverDecision]]


class _Token(IDisposable):
    """Unsubscribe handle for :meth:`InMemoryObservationLoop.subscribe`."""

    def __init__(self, owner: "InMemoryObservationLoop", handler: TickObserver) -> None:
        self._owner = owner
        self._handler = handler

    def dispose(self) -> None:
        with self._owner._lock:
            try:
                self._owner._subs.remove(self._handler)
            except ValueError:
                pass


class InMemoryObservationLoop(IObservationLoop):
    """The perceive-reason-act loop. Mirrors
    ``CircleAI.Observer.InMemoryObservationLoop``."""

    def __init__(
        self,
        sensors: Sequence[ISensor],
        toolbox: IObservationToolbox,
        reason: Reasoner,
    ) -> None:
        if sensors is None:
            raise ValueError("sensors")
        if toolbox is None:
            raise ValueError("toolbox")
        if reason is None:
            raise ValueError("reason")
        self._recorders: List[SensorRecorder] = [SensorRecorder(s) for s in sensors]
        self._toolbox = toolbox
        self._reason = reason
        self._subs: List[TickObserver] = []
        self._lock = threading.Lock()
        self._task: Optional["asyncio.Task[None]"] = None
        self._stop = asyncio.Event()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def start_async(
        self, tick_interval: timedelta, ct: Optional[object] = None
    ) -> None:
        if self._task is not None:
            raise RuntimeError("already started")
        self._stop.clear()
        self._task = asyncio.create_task(self._run(tick_interval.total_seconds()))

    async def stop_async(self, ct: Optional[object] = None) -> None:
        if self._task is None:
            return
        self._stop.set()
        task = self._task
        try:
            await task
        except asyncio.CancelledError:  # pragma: no cover - expected on cancel
            pass
        self._task = None

    def subscribe(self, handler: TickObserver) -> IDisposable:
        if handler is None:
            raise ValueError("handler")
        with self._lock:
            self._subs.append(handler)
        return _Token(self, handler)

    async def dispose_async(self) -> None:
        await self.stop_async()
        for r in self._recorders:
            r.dispose()

    async def _run(self, interval_seconds: float) -> None:
        while not self._stop.is_set():
            try:
                readings = [r.latest for r in self._recorders if r.latest is not None]
                decision = await self._reason(readings, None)
                invoked: List[str] = []
                for tool_id in decision.tools_to_invoke:
                    tool = self._toolbox.try_get(tool_id)
                    if tool is not None:
                        try:
                            await tool.invoke(
                                decision.tool_args if decision.tool_args is not None else {},
                                None,
                            )
                            invoked.append(tool_id)
                        except Exception:
                            # mirror C# per-tool swallow (Debug.WriteLine)
                            pass
                tick = ObservationTick(
                    datetime.now(timezone.utc), readings, decision.reasoning, invoked
                )
                with self._lock:
                    snap = list(self._subs)
                for s in snap:
                    try:
                        await s(tick)
                    except Exception:
                        # mirror C# per-subscriber swallow
                        pass
            except Exception:
                # mirror C# reasoner swallow — skip this tick, keep looping
                pass
            # Task.Delay(interval, ct) — wake early if stopped.
            try:
                await asyncio.wait_for(self._stop.wait(), timeout=interval_seconds)
            except asyncio.TimeoutError:
                pass
