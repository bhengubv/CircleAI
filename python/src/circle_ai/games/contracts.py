# contracts.py
#
# Port of CircleAI.Games Contracts.cs (C# — the EXACT spec).
#
# (3.0.0) Game-runtime contracts: the tick/input/scene records plus the three
# backend interfaces (game loop, input map, scene graph).
#
# C# ValueTask / ValueTask<T> map to ``async def`` -> None / T. C# ``IDisposable``
# (returned by Subscribe) maps to :class:`IDisposable` with a ``dispose()`` that
# also works as a context manager. ``IAsyncDisposable`` maps to an async
# ``dispose_async`` (also usable via ``async with``). C# records map to frozen
# slotted dataclasses.

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import timedelta
from typing import Awaitable, Callable, List, Mapping, Optional


@dataclass(frozen=True, slots=True)
class GameTick:
    """Mirrors ``CircleAI.Games.GameTick`` — ``record(int Frame, TimeSpan Elapsed)``."""

    frame: int
    elapsed: timedelta


@dataclass(frozen=True, slots=True)
class InputEvent:
    """Mirrors ``CircleAI.Games.InputEvent`` — ``record(string Action,
    IReadOnlyDictionary<string,string>? Payload = null)``.
    """

    action: str
    payload: Optional[Mapping[str, str]] = None


@dataclass(frozen=True, slots=True)
class SceneNode:
    """Mirrors ``CircleAI.Games.SceneNode`` — ``record(string NodeId, string
    Kind, double X, double Y, double Z)``.
    """

    node_id: str
    kind: str
    x: float
    y: float
    z: float


#: handler(tick) -> Awaitable[None] (C# Func<GameTick, ValueTask>)
TickHandler = Callable[[GameTick], Awaitable[None]]
#: handler(event) -> Awaitable[None] (C# Func<InputEvent, ValueTask>)
InputHandler = Callable[[InputEvent], Awaitable[None]]


class IDisposable(ABC):
    """Subscription handle mirroring C# ``IDisposable``. Also usable as a
    context manager (``with loop.subscribe(h): ...``).
    """

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class IGameLoop(ABC):
    """Game loop backend. Mirrors ``CircleAI.Games.IGameLoop`` (which is
    ``IAsyncDisposable``).
    """

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def start_async(
        self, target_fps: float = 60, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    async def stop_async(self, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    def subscribe(self, handler: TickHandler) -> IDisposable:
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        ...

    async def __aenter__(self) -> "IGameLoop":
        return self

    async def __aexit__(self, *exc_info: object) -> None:
        await self.dispose_async()


class IInputMap(ABC):
    """Input backend. Mirrors ``CircleAI.Games.IInputMap``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    def subscribe(self, handler: InputHandler) -> IDisposable:
        ...


class ISceneGraph(ABC):
    """Scene-graph backend. Mirrors ``CircleAI.Games.ISceneGraph``."""

    @property
    @abstractmethod
    def backend_id(self) -> str:
        ...

    @abstractmethod
    async def add_async(self, node: SceneNode, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def remove_async(self, node_id: str, ct: Optional[object] = None) -> None:
        ...

    @abstractmethod
    async def snapshot_async(
        self, ct: Optional[object] = None
    ) -> List[SceneNode]:
        ...
