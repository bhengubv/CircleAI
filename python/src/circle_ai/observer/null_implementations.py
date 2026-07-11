# null_implementations.py
#
# Port of CircleAI.Observer NullImplementations.cs (C# — the EXACT spec).
#
# (2.6.0) Fail-safe defaults for the Observer pack, plus the real in-memory
# toolbox (which lives here in the C# file):
#   • NullSensor — no readings, subscribe returns an inert handle.
#   • InMemoryObservationToolbox — real ConcurrentDictionary-style tool registry.
#   • NullObservationLoop — no-op loop.
#
# NullSensor / NullObservationLoop expose a singleton `INSTANCE`; the C# has no
# static Instance for these two (they are plain sealed classes) but the pattern
# matches the rest of the pack — construct fresh where needed.

from __future__ import annotations

import threading
from datetime import timedelta
from typing import Dict, List, Optional, Sequence

from .contracts import (
    IDisposable,
    IObservationLoop,
    IObservationToolbox,
    ISensor,
    ObservationTool,
    SensorHandler,
    TickObserver,
)


class _EmptyDisposable(IDisposable):
    INSTANCE: "_EmptyDisposable"

    def dispose(self) -> None:
        return None


_EmptyDisposable.INSTANCE = _EmptyDisposable()


class NullSensor(ISensor):
    """Fail-safe :class:`ISensor`. Mirrors ``CircleAI.Observer.NullSensor``."""

    @property
    def sensor_id(self) -> str:
        return "null"

    @property
    def kind(self) -> str:
        return "null"

    @property
    def backend_id(self) -> str:
        return "null"

    async def start_async(self, ct: Optional[object] = None) -> None:
        return None

    async def stop_async(self, ct: Optional[object] = None) -> None:
        return None

    def subscribe(self, handler: SensorHandler) -> IDisposable:
        return _EmptyDisposable.INSTANCE

    async def dispose_async(self) -> None:
        return None


class InMemoryObservationToolbox(IObservationToolbox):
    """Real in-memory :class:`IObservationToolbox`. Mirrors
    ``CircleAI.Observer.InMemoryObservationToolbox``."""

    def __init__(self) -> None:
        self._tools: Dict[str, ObservationTool] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def register_tool(self, tool: ObservationTool) -> None:
        with self._lock:
            self._tools[tool.tool_id] = tool

    def try_get(self, tool_id: str) -> Optional[ObservationTool]:
        with self._lock:
            return self._tools.get(tool_id)

    def list_tools(self) -> Sequence[ObservationTool]:
        with self._lock:
            return list(self._tools.values())


class NullObservationLoop(IObservationLoop):
    """Fail-safe :class:`IObservationLoop`. Mirrors
    ``CircleAI.Observer.NullObservationLoop``."""

    @property
    def backend_id(self) -> str:
        return "null"

    async def start_async(
        self, tick_interval: timedelta, ct: Optional[object] = None
    ) -> None:
        return None

    async def stop_async(self, ct: Optional[object] = None) -> None:
        return None

    def subscribe(self, handler: TickObserver) -> IDisposable:
        return _EmptyDisposable.INSTANCE

    async def dispose_async(self) -> None:
        return None
