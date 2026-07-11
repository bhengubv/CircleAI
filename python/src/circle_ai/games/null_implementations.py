# null_implementations.py
#
# Port of CircleAI.Games NullImplementations.cs (C# — the EXACT spec).
#
# (3.0.0) No-op game-runtime backends. Every method is a no-op; Subscribe
# returns an already-disposed handle; Snapshot returns empty.

from __future__ import annotations

from typing import List, Optional

from .contracts import (
    IDisposable,
    IGameLoop,
    IInputMap,
    ISceneGraph,
    InputHandler,
    SceneNode,
    TickHandler,
)


class _EmptyDisposable(IDisposable):
    """No-op subscription handle (C# private ``EmptyDisposable``)."""

    def dispose(self) -> None:
        pass


class NullGameLoop(IGameLoop):
    """No-op :class:`IGameLoop`. Mirrors ``CircleAI.Games.NullGameLoop``."""

    @property
    def backend_id(self) -> str:
        return "null"

    async def start_async(
        self, target_fps: float = 60, ct: Optional[object] = None
    ) -> None:
        pass

    async def stop_async(self, ct: Optional[object] = None) -> None:
        pass

    def subscribe(self, handler: TickHandler) -> IDisposable:
        return _EmptyDisposable()

    async def dispose_async(self) -> None:
        pass


class NullInputMap(IInputMap):
    """No-op :class:`IInputMap`. Mirrors ``CircleAI.Games.NullInputMap``."""

    #: C# ``public static readonly NullInputMap Instance = new();``
    Instance: "NullInputMap"

    @property
    def backend_id(self) -> str:
        return "null"

    def subscribe(self, handler: InputHandler) -> IDisposable:
        return _EmptyDisposable()


class NullSceneGraph(ISceneGraph):
    """No-op :class:`ISceneGraph`. Mirrors ``CircleAI.Games.NullSceneGraph``."""

    #: C# ``public static readonly NullSceneGraph Instance = new();``
    Instance: "NullSceneGraph"

    @property
    def backend_id(self) -> str:
        return "null"

    async def add_async(self, node: SceneNode, ct: Optional[object] = None) -> None:
        pass

    async def remove_async(self, node_id: str, ct: Optional[object] = None) -> None:
        pass

    async def snapshot_async(self, ct: Optional[object] = None) -> List[SceneNode]:
        return []


# C# exposes shared singletons `NullInputMap.Instance` / `NullSceneGraph.Instance`.
NullInputMap.Instance = NullInputMap()
NullSceneGraph.Instance = NullSceneGraph()
