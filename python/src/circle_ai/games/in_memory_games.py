# in_memory_games.py
#
# Port of CircleAI.Games InMemoryGames.cs (C# — the EXACT spec).
#
# (3.3.0) Real game-loop primitive — the C# uses a System.Threading.Timer at the
# requested FPS and fans out ticks to subscribers; this port uses a rescheduling
# :class:`threading.Timer` (Python's Timer is single-shot, so each tick arms the
# next), which reproduces the same periodic cadence. Input map + scene graph are
# plain dicts/lists guarded by a lock.
#
# Concurrency: subscriber lists are snapshotted under the lock and invoked
# OUTSIDE it (mirroring the C# ``lock (_lock) snap = _subs.ToArray();`` then
# ``foreach``). Subscribers are ``async`` (return an awaitable, the C# ValueTask):
# the loop was started on an asyncio event loop, which we capture at
# ``start_async`` and dispatch coroutines onto with
# :func:`asyncio.run_coroutine_threadsafe` — the fire-and-forget analogue of the
# C# ``_ = s(tick)``. Any exception raised while *scheduling* a subscriber is
# swallowed (the C# ``catch (Exception ex)`` around the invoke), so one bad
# handler never stops the loop.

from __future__ import annotations

import asyncio
import threading
from datetime import datetime, timedelta, timezone
from typing import Dict, List, Optional

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


def _dispatch(loop: Optional[asyncio.AbstractEventLoop], coro) -> None:
    """Fire-and-forget a subscriber coroutine (C# ``_ = s(x)``).

    Schedules ``coro`` on ``loop`` (the event loop captured when the producer
    started, or — for a synchronously-raised input event — the caller's running
    loop). Exceptions from *scheduling* are swallowed to match the C# per-
    subscriber try/catch. If no loop is available the coroutine is closed to
    avoid a "coroutine was never awaited" warning.
    """
    try:
        if loop is not None and not loop.is_closed():
            asyncio.run_coroutine_threadsafe(coro, loop)
            return
        running = asyncio.get_running_loop()
        running.create_task(coro)
    except RuntimeError:
        # No usable loop — nothing to run the awaitable on.
        coro.close()
    except Exception:  # pragma: no cover - mirrors C# swallow-all
        try:
            coro.close()
        except Exception:
            pass


class _TickToken(IDisposable):
    """Unsubscribe handle for :class:`TimerGameLoop.subscribe`."""

    def __init__(self, owner: "TimerGameLoop", handler: TickHandler) -> None:
        self._owner = owner
        self._handler = handler

    def dispose(self) -> None:
        with self._owner._lock:
            try:
                self._owner._subs.remove(self._handler)
            except ValueError:
                pass


class TimerGameLoop(IGameLoop):
    """Timer-driven :class:`IGameLoop`. Mirrors ``CircleAI.Games.TimerGameLoop``."""

    def __init__(self) -> None:
        self._subs: List[TickHandler] = []
        self._lock = threading.Lock()
        self._timer: Optional[threading.Timer] = None
        self._frame = 0
        self._start = datetime.now(timezone.utc)
        self._interval = 0.0
        self._loop: Optional[asyncio.AbstractEventLoop] = None

    @property
    def backend_id(self) -> str:
        return "timer"

    async def start_async(
        self, target_fps: float = 60, ct: Optional[object] = None
    ) -> None:
        if target_fps <= 0:
            raise ValueError("target_fps must be positive")
        if self._timer is not None:
            raise RuntimeError("already started")
        # C# ms = Max(1, (int)(1000.0 / targetFps)); keep as seconds for Timer.
        ms = max(1, int(1000.0 / target_fps))
        self._interval = ms / 1000.0
        self._start = datetime.now(timezone.utc)
        try:
            self._loop = asyncio.get_running_loop()
        except RuntimeError:
            self._loop = None
        self._arm()

    def _arm(self) -> None:
        timer = threading.Timer(self._interval, self._on_tick)
        timer.daemon = True
        self._timer = timer
        timer.start()

    async def stop_async(self, ct: Optional[object] = None) -> None:
        timer = self._timer
        self._timer = None
        if timer is not None:
            timer.cancel()

    def subscribe(self, handler: TickHandler) -> IDisposable:
        if handler is None:
            raise ValueError("handler must not be None")
        with self._lock:
            self._subs.append(handler)
        return _TickToken(self, handler)

    async def dispose_async(self) -> None:
        await self.stop_async()

    def _on_tick(self) -> None:
        # A single-shot Timer fired; arm the next one first so cadence is kept
        # even if a subscriber is slow to schedule. Only re-arm if not stopped.
        if self._timer is not None:
            self._arm()
        with self._lock:
            self._frame += 1
            frame = self._frame
            snap = list(self._subs)
        tick = GameTick(frame, datetime.now(timezone.utc) - self._start)
        for s in snap:
            try:
                _dispatch(self._loop, s(tick))
            except Exception:  # pragma: no cover - mirrors C# swallow-all
                pass


class _InputToken(IDisposable):
    """Unsubscribe handle for :class:`InMemoryInputMap.subscribe`."""

    def __init__(self, owner: "InMemoryInputMap", handler: InputHandler) -> None:
        self._owner = owner
        self._handler = handler

    def dispose(self) -> None:
        with self._owner._lock:
            try:
                self._owner._subs.remove(self._handler)
            except ValueError:
                pass


class InMemoryInputMap(IInputMap):
    """In-memory :class:`IInputMap`. Mirrors ``CircleAI.Games.InMemoryInputMap``.

    Call :meth:`raise_event` to fan an :class:`InputEvent` out to subscribers.
    """

    def __init__(self) -> None:
        self._subs: List[InputHandler] = []
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    def raise_event(self, ev: InputEvent) -> None:
        """Mirrors ``CircleAI.Games.InMemoryInputMap.Raise``."""
        if ev is None:
            raise ValueError("input event must not be None")
        with self._lock:
            snap = list(self._subs)
        try:
            loop: Optional[asyncio.AbstractEventLoop] = asyncio.get_running_loop()
        except RuntimeError:
            loop = None
        for s in snap:
            try:
                _dispatch(loop, s(ev))
            except Exception:  # pragma: no cover - mirrors C# swallow-all
                pass

    def subscribe(self, handler: InputHandler) -> IDisposable:
        if handler is None:
            raise ValueError("handler must not be None")
        with self._lock:
            self._subs.append(handler)
        return _InputToken(self, handler)


class InMemorySceneGraph(ISceneGraph):
    """In-memory :class:`ISceneGraph`. Mirrors ``CircleAI.Games.InMemorySceneGraph``."""

    def __init__(self) -> None:
        self._nodes: Dict[str, SceneNode] = {}
        self._lock = threading.Lock()

    @property
    def backend_id(self) -> str:
        return "in-memory"

    async def add_async(self, node: SceneNode, ct: Optional[object] = None) -> None:
        if node is None:
            raise ValueError("node must not be None")
        if node.node_id is None or node.node_id.strip() == "":
            raise ValueError("NodeId required")
        with self._lock:
            self._nodes[node.node_id] = node

    async def remove_async(self, node_id: str, ct: Optional[object] = None) -> None:
        if node_id is None or node_id.strip() == "":
            raise ValueError("node_id required")
        with self._lock:
            self._nodes.pop(node_id, None)

    async def snapshot_async(self, ct: Optional[object] = None) -> List[SceneNode]:
        with self._lock:
            return list(self._nodes.values())
