// plugins.test.ts
// Verifies the scoped CircleAI.Plugins port: the PluginEvents bus (subscribe /
// raise / unsubscribe, throwing-handler isolation), the default PluginContext,
// and the permission-gated PermissionedPluginContext (workspace + events gating).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  PluginEvents,
  PluginEventNames,
  PluginContext,
  PermissionedPluginContext,
  NullLogger,
  type IPlugin,
  type IPluginContext,
} from "../src/plugins/index";

describe("PluginEvents", () => {
  it("delivers raised events to subscribers and stops after dispose", () => {
    const bus = new PluginEvents();
    const got: unknown[] = [];
    const sub = bus.subscribe("x", (p) => got.push(p));
    bus.raise("x", 1);
    bus.raise("x", 2);
    sub.dispose();
    bus.raise("x", 3);
    assert.deepEqual(got, [1, 2]);
  });

  it("does not deliver to a different event name", () => {
    const bus = new PluginEvents();
    let count = 0;
    bus.subscribe("a", () => count++);
    bus.raise("b", null);
    assert.equal(count, 0);
  });

  it("isolates a throwing handler from the others", () => {
    const bus = new PluginEvents();
    let reached = false;
    bus.subscribe("e", () => {
      throw new Error("bad");
    });
    bus.subscribe("e", () => {
      reached = true;
    });
    assert.doesNotThrow(() => bus.raise("e", null));
    assert.equal(reached, true);
  });

  it("validates subscribe arguments", () => {
    const bus = new PluginEvents();
    assert.throws(() => bus.subscribe("", () => {}));
  });
});

describe("PluginContext", () => {
  it("exposes the workspace accessor, events, and logger", () => {
    let path: string | null = "/ws";
    const ctx = new PluginContext(() => path, new PluginEvents(), NullLogger);
    assert.equal(ctx.workspacePath, "/ws");
    path = null;
    assert.equal(ctx.workspacePath, null);
    assert.equal(ctx.events instanceof PluginEvents, true);
  });
});

describe("PermissionedPluginContext", () => {
  const base = (): IPluginContext => new PluginContext(() => "/ws", new PluginEvents(), NullLogger);

  it("hides the workspace path without a workspace permission", () => {
    const ctx = new PermissionedPluginContext(base(), []);
    assert.equal(ctx.workspacePath, null);
  });

  it("exposes the workspace path with workspace.read", () => {
    const ctx = new PermissionedPluginContext(base(), [
      PermissionedPluginContext.Permissions.WorkspaceRead,
    ]);
    assert.equal(ctx.workspacePath, "/ws");
  });

  it("silences events without events.subscribe", () => {
    const inner = base();
    let delivered = 0;
    inner.events.subscribe("e", () => delivered++);
    const ctx = new PermissionedPluginContext(inner, []);
    ctx.events.raise("e", null); // silent bus — no delivery to inner
    assert.equal(delivered, 0);
  });

  it("passes events through with events.subscribe", () => {
    const inner = base();
    let delivered = 0;
    inner.events.subscribe("e", () => delivered++);
    const ctx = new PermissionedPluginContext(inner, [
      PermissionedPluginContext.Permissions.EventsSubscribe,
    ]);
    ctx.events.raise("e", null);
    assert.equal(delivered, 1);
  });
});

describe("IPlugin lifecycle contract", () => {
  it("a minimal plugin can initialise against a context", async () => {
    const events: string[] = [];
    const plugin: IPlugin = {
      id: "p",
      displayName: "P",
      version: "1.0.0",
      async initializeAsync(ctx) {
        ctx.events.subscribe(PluginEventNames.ChatMessage, () => events.push("msg"));
      },
      async shutdownAsync() {},
    };
    const bus = new PluginEvents();
    await plugin.initializeAsync(new PluginContext(null, bus, NullLogger));
    bus.raise(PluginEventNames.ChatMessage, {});
    assert.deepEqual(events, ["msg"]);
  });
});
