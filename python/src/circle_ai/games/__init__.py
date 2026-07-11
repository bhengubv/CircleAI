"""circle_ai.games — port of the CircleAI.Games assembly.

(3.x) Game-runtime contracts + deterministic in-memory backends: a timer-driven
game loop that fans ticks out to async subscribers, an in-memory input map, and
an in-memory scene graph, plus the null (no-op) backends. C# is the exact spec.

Public surface:

  * GameTick / InputEvent / SceneNode — runtime records.
  * IGameLoop / IInputMap / ISceneGraph / IDisposable — contracts.
  * TimerGameLoop / InMemoryInputMap / InMemorySceneGraph — in-memory backends.
  * NullGameLoop / NullInputMap / NullSceneGraph — no-op backends.
"""
from __future__ import annotations

from .contracts import (
    GameTick,
    IDisposable,
    IGameLoop,
    IInputMap,
    ISceneGraph,
    InputEvent,
    InputHandler,
    SceneNode,
    TickHandler,
)
from .in_memory_games import (
    InMemoryInputMap,
    InMemorySceneGraph,
    TimerGameLoop,
)
from .null_implementations import (
    NullGameLoop,
    NullInputMap,
    NullSceneGraph,
)

__all__ = [
    "GameTick",
    "InputEvent",
    "SceneNode",
    "TickHandler",
    "InputHandler",
    "IDisposable",
    "IGameLoop",
    "IInputMap",
    "ISceneGraph",
    "TimerGameLoop",
    "InMemoryInputMap",
    "InMemorySceneGraph",
    "NullGameLoop",
    "NullInputMap",
    "NullSceneGraph",
]
