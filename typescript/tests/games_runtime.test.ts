// games_runtime.test.ts
// Verifies the CircleAI.Games port: timer game loop start/stop + fan-out +
// unsubscribe, in-memory input map, in-memory scene graph, and null impls.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  TimerGameLoop,
  InMemoryInputMap,
  InMemorySceneGraph,
  NullGameLoop,
  NullInputMap,
  NullSceneGraph,
  sceneNode,
  inputEvent,
  type GameTick,
} from "../src/games/index";

const sleep = (ms: number): Promise<void> => new Promise((r) => setTimeout(r, ms));

describe("TimerGameLoop", () => {
  it("fans ticks out to subscribers with monotonically increasing frames", async () => {
    const loop = new TimerGameLoop();
    assert.equal(loop.backendId, "timer");
    const frames: number[] = [];
    loop.subscribe((t: GameTick) => {
      frames.push(t.frame);
    });
    await loop.startAsync(200); // ~5ms period
    await sleep(60);
    await loop.stopAsync();
    assert.ok(frames.length >= 2, `expected several ticks, got ${frames.length}`);
    assert.equal(frames[0], 1);
    for (let i = 1; i < frames.length; i++) assert.equal(frames[i], frames[i - 1] + 1);
    await loop.disposeAsync();
  });

  it("rejects a second start and non-positive fps", async () => {
    const loop = new TimerGameLoop();
    await loop.startAsync(100);
    assert.throws(() => void loop.startAsync(100), /already started/);
    await loop.stopAsync();
    assert.throws(() => void loop.startAsync(0), /targetFps/);
  });

  it("stops delivering after unsubscribe", async () => {
    const loop = new TimerGameLoop();
    let count = 0;
    const sub = loop.subscribe(() => {
      count++;
    });
    await loop.startAsync(200);
    await sleep(40);
    sub.dispose();
    sub.dispose(); // idempotent
    const seen = count;
    await sleep(40);
    await loop.stopAsync();
    assert.equal(count, seen, "no ticks should arrive after dispose");
  });
});

describe("InMemoryInputMap", () => {
  it("raises events to all subscribers and stops after dispose", () => {
    const map = new InMemoryInputMap();
    assert.equal(map.backendId, "in-memory");
    const seen: string[] = [];
    const sub = map.subscribe((ev) => {
      seen.push(ev.action);
    });
    map.raise(inputEvent("jump"));
    map.raise(inputEvent("crouch", new Map([["key", "ctrl"]])));
    sub.dispose();
    map.raise(inputEvent("shoot"));
    assert.deepEqual(seen, ["jump", "crouch"]);
  });
});

describe("InMemorySceneGraph", () => {
  it("adds, snapshots, and removes nodes", async () => {
    const g = new InMemorySceneGraph();
    assert.equal(g.backendId, "in-memory");
    await g.addAsync(sceneNode("n1", "sprite", 1, 2, 3));
    await g.addAsync(sceneNode("n2", "light", 4, 5, 6));
    let snap = await g.snapshotAsync();
    assert.deepEqual(snap.map((n) => n.nodeId).sort(), ["n1", "n2"]);
    await g.removeAsync("n1");
    snap = await g.snapshotAsync();
    assert.deepEqual(snap.map((n) => n.nodeId), ["n2"]);
  });

  it("rejects blank node ids", async () => {
    const g = new InMemorySceneGraph();
    await assert.rejects(() => g.addAsync(sceneNode("  ", "x", 0, 0, 0)), /NodeId required/);
    await assert.rejects(() => g.removeAsync(""), /nodeId required/);
  });
});

describe("Null implementations", () => {
  it("are inert and report backend 'null'", async () => {
    const loop = new NullGameLoop();
    assert.equal(loop.backendId, "null");
    await loop.startAsync();
    await loop.stopAsync();
    const d = loop.subscribe(() => {});
    d.dispose();
    await loop.disposeAsync();

    assert.equal(NullInputMap.instance.backendId, "null");
    NullInputMap.instance.subscribe(() => {}).dispose();

    assert.equal(NullSceneGraph.instance.backendId, "null");
    await NullSceneGraph.instance.addAsync(sceneNode("n1", "x", 0, 0, 0));
    await NullSceneGraph.instance.removeAsync("n1");
    assert.deepEqual(await NullSceneGraph.instance.snapshotAsync(), []);
  });
});
