"""test_games_runtime.py — CircleAI.Games port.

Covers the runtime records, the in-memory scene graph (add/remove/snapshot),
the in-memory input map (raise -> async subscriber fan-out + unsubscribe), the
timer game loop (ticks fire on the running loop; stop halts them), and the null
backends. asyncio_mode = auto. C# is the exact spec.
"""
from __future__ import annotations

import asyncio

import pytest

from circle_ai import (
    GameTick,
    IGameLoop,
    IInputMap,
    ISceneGraph,
    InMemoryInputMap,
    InMemorySceneGraph,
    InputEvent,
    NullGameLoop,
    NullInputMap,
    NullSceneGraph,
    SceneNode,
    TimerGameLoop,
)


def test_types():
    assert isinstance(TimerGameLoop(), IGameLoop)
    assert isinstance(InMemoryInputMap(), IInputMap)
    assert isinstance(InMemorySceneGraph(), ISceneGraph)
    assert TimerGameLoop().backend_id == "timer"
    assert InMemoryInputMap().backend_id == "in-memory"
    assert InMemorySceneGraph().backend_id == "in-memory"


async def test_scene_graph_add_remove_snapshot():
    sg = InMemorySceneGraph()
    await sg.add_async(SceneNode("n1", "cube", 0, 0, 0))
    await sg.add_async(SceneNode("n2", "sphere", 1, 1, 1))
    snap = await sg.snapshot_async()
    assert {n.node_id for n in snap} == {"n1", "n2"}
    await sg.remove_async("n1")
    snap2 = await sg.snapshot_async()
    assert {n.node_id for n in snap2} == {"n2"}


async def test_scene_graph_blank_node_id_raises():
    sg = InMemorySceneGraph()
    with pytest.raises(ValueError):
        await sg.add_async(SceneNode("  ", "cube", 0, 0, 0))
    with pytest.raises(ValueError):
        await sg.remove_async("")


async def test_input_map_fanout_and_unsubscribe():
    im = InMemoryInputMap()
    seen: list[str] = []

    async def handler(ev: InputEvent) -> None:
        seen.append(ev.action)

    sub = im.subscribe(handler)
    im.raise_event(InputEvent("jump"))
    await asyncio.sleep(0.02)  # let the scheduled coroutine run
    assert seen == ["jump"]

    sub.dispose()
    im.raise_event(InputEvent("crouch"))
    await asyncio.sleep(0.02)
    assert seen == ["jump"]  # no further delivery after dispose


async def test_timer_loop_ticks_then_stops():
    loop = TimerGameLoop()
    ticks: list[GameTick] = []

    async def on_tick(t: GameTick) -> None:
        ticks.append(t)

    loop.subscribe(on_tick)
    await loop.start_async(target_fps=50)  # ~20ms interval
    await asyncio.sleep(0.12)
    await loop.stop_async()
    count_after_stop = len(ticks)
    assert count_after_stop >= 1
    await asyncio.sleep(0.06)
    # No new ticks after stop (allow the frame already in flight).
    assert len(ticks) - count_after_stop <= 1


async def test_timer_loop_start_twice_raises():
    loop = TimerGameLoop()
    await loop.start_async(target_fps=30)
    with pytest.raises(RuntimeError):
        await loop.start_async(target_fps=30)
    await loop.dispose_async()


async def test_timer_loop_bad_fps_raises():
    with pytest.raises(ValueError):
        await TimerGameLoop().start_async(target_fps=0)


async def test_null_backends_are_noops():
    ngl = NullGameLoop()
    assert ngl.backend_id == "null"
    await ngl.start_async()
    await ngl.stop_async()
    ngl.subscribe(lambda t: asyncio.sleep(0)).dispose()
    await ngl.dispose_async()

    assert NullInputMap.Instance.backend_id == "null"
    NullInputMap.Instance.subscribe(lambda e: asyncio.sleep(0)).dispose()

    assert NullSceneGraph.Instance.backend_id == "null"
    await NullSceneGraph.Instance.add_async(SceneNode("n", "k", 0, 0, 0))
    assert await NullSceneGraph.Instance.snapshot_async() == []
