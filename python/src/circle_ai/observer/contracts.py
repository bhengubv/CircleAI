# contracts.py
#
# Port of CircleAI.Observer Contracts.cs (C# — the EXACT spec).
#
# (2.6.0) Observation-loop contracts (pattern-port of bhengubv/Observer):
# sensor-reading / observation-tool / observation-tick records and the sensor /
# toolbox / loop interfaces.
#
# C# Task/ValueTask -> async def -> None/T. C# records -> frozen slotted
# dataclasses. ReadOnlyMemory<byte>? -> Optional[bytes]. DateTimeOffset ->
# datetime. TimeSpan -> timedelta. C# ``IDisposable`` (returned by Subscribe) maps
# to :class:`IDisposable` with a ``dispose()`` (also a context manager).
# ``IAsyncDisposable`` maps to ``dispose_async`` (also usable via ``async with``).

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta
from typing import Awaitable, Callable, Mapping, Optional, Sequence


@dataclass(frozen=True, slots=True)
class SensorReading:
    """Mirrors ``CircleAI.Observer.SensorReading`` — ``record(string SensorId,
    string Kind, DateTimeOffset CapturedAtUtc,
    IReadOnlyDictionary<string,string> Values, ReadOnlyMemory<byte>? Payload = null)``.
    """

    sensor_id: str
    kind: str
    captured_at_utc: datetime
    values: Mapping[str, str]
    payload: Optional[bytes] = None


#: reading -> Awaitable[None] (C# Func<SensorReading, ValueTask>)
SensorHandler = Callable[[SensorReading], Awaitable[None]]

#: (args, ct) -> Awaitable[str] (C# Func<IReadOnlyDictionary<string,string>, CancellationToken, ValueTask<string>>)
ToolInvoke = Callable[[Mapping[str, str], Optional[object]], Awaitable[str]]


@dataclass(frozen=True, slots=True)
class ObservationTool:
    """Mirrors ``CircleAI.Observer.ObservationTool`` — ``record(string ToolId,
    string Description, IReadOnlyList<string> Tags,
    Func<IReadOnlyDictionary<string,string>, CancellationToken, ValueTask<string>> Invoke)``.
    """

    tool_id: str
    description: str
    tags: Sequence[str]
    invoke: ToolInvoke


@dataclass(frozen=True, slots=True)
class ObservationTick:
    """Mirrors ``CircleAI.Observer.ObservationTick`` — ``record(DateTimeOffset AtUtc,
    IReadOnlyList<SensorReading> Perceived, string Reasoning,
    IReadOnlyList<string> ToolsInvoked)``.
    """

    at_utc: datetime
    perceived: Sequence[SensorReading]
    reasoning: str
    tools_invoked: Sequence[str]


#: tick -> Awaitable[None] (C# Func<ObservationTick, ValueTask>)
TickObserver = Callable[[ObservationTick], Awaitable[None]]


class IDisposable(ABC):
    """Subscription handle mirroring C# ``IDisposable``. Also usable as a
    context manager (``with sensor.subscribe(h): ...``)."""

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class ISensor(ABC):
    """(2.6.0) A single perception source (camera / mic / GPS / phone-state /
    accelerometer). Mirrors ``CircleAI.Observer.ISensor`` (``IAsyncDisposable``)."""

    @property
    @abstractmethod
    def sensor_id(self) -> str:
        ...

    @property
    @abstractmethod
    def kind(self) -> str:
        ...

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def start_async(self, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def stop_async(self, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    def subscribe(self, handler: SensorHandler) -> IDisposable:
        ...

    async def dispose_async(self) -> None:
        return None

    async def __aenter__(self) -> "ISensor":
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.dispose_async()


class IObservationToolbox(ABC):
    """(2.6.0) Registry of tools available to the observation loop."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    def register_tool(self, tool: ObservationTool) -> None:
        ...

    @abstractmethod
    def try_get(self, tool_id: str) -> Optional[ObservationTool]:
        """Return the tool for ``tool_id`` or ``None`` (C# ``bool TryGet(out ...)``
        is exposed here as ``Optional`` since Python has no out-params)."""
        ...

    @abstractmethod
    def list_tools(self) -> Sequence[ObservationTool]:
        ...


class IObservationLoop(ABC):
    """(2.6.0) The perceive-reason-act loop itself. Mirrors
    ``CircleAI.Observer.IObservationLoop`` (``IAsyncDisposable``)."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def start_async(
        self, tick_interval: timedelta, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def stop_async(self, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    def subscribe(self, handler: TickObserver) -> IDisposable:
        ...

    async def dispose_async(self) -> None:
        return None

    async def __aenter__(self) -> "IObservationLoop":
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.dispose_async()
