// hosting_mcp_multiplayer.test.ts
//
// Verifies the CircleAI.Hosting.Mcp JSON-RPC 2.0 dispatcher (initialize,
// tools/list, tools/call with tool-error handling, resources/list +
// resources/read) and the CircleAI.Hosting.Multiplayer hub (colour-per-peer,
// join/leave/cursor broadcasts, LWW-by-rev edits, presence).

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  dispatchAsync,
  buildManifest,
  McpToolException,
  type IMcpTool,
  type IMcpResourceProvider,
  type McpRegistry,
  MultiplayerHub,
  GuestPeerIdentity,
  RecordingBroadcaster,
} from "../src/hosting/index";

function rpc(method: string, params?: unknown, id: unknown = 1) {
  return { jsonrpc: "2.0", id, method, params };
}

function registryWith(
  tools: IMcpTool[],
  providers: IMcpResourceProvider[] = [],
): McpRegistry {
  return { tools, resourceProviders: providers };
}

describe("Mcp dispatcher", () => {
  const echoTool: IMcpTool = {
    name: "echo",
    description: "echoes its args",
    inputSchema: { type: "object" },
    async executeAsync(args) {
      return { echoed: args };
    },
  };
  const failingTool: IMcpTool = {
    name: "boom",
    description: "always fails",
    inputSchema: {},
    async executeAsync() {
      throw new McpToolException("kaboom");
    },
  };

  it("initialize returns the protocol + server info", async () => {
    const res = (await dispatchAsync(rpc("initialize"), registryWith([]))) as {
      result: { protocolVersion: string; serverInfo: { name: string } };
    };
    assert.equal(res.result.protocolVersion, "2024-11-05");
    assert.equal(res.result.serverInfo.name, "circleai-mcp");
  });

  it("notifications/initialized returns null (notification, no reply)", async () => {
    const res = await dispatchAsync(rpc("notifications/initialized", undefined, null), registryWith([]));
    assert.equal(res, null);
  });

  it("tools/list enumerates registered tools", async () => {
    const res = (await dispatchAsync(rpc("tools/list"), registryWith([echoTool]))) as {
      result: { tools: { name: string }[] };
    };
    assert.deepEqual(res.result.tools.map((t) => t.name), ["echo"]);
  });

  it("tools/call runs the tool and wraps the result", async () => {
    const res = (await dispatchAsync(
      rpc("tools/call", { name: "echo", arguments: { a: 1 } }),
      registryWith([echoTool]),
    )) as { result: { content: { text: string }[]; isError: boolean } };
    assert.equal(res.result.isError, false);
    assert.match(res.result.content[0].text, /"echoed":\{"a":1\}/);
  });

  it("tools/call maps McpToolException to isError:true", async () => {
    const res = (await dispatchAsync(
      rpc("tools/call", { name: "boom", arguments: {} }),
      registryWith([failingTool]),
    )) as { result: { content: { text: string }[]; isError: boolean } };
    assert.equal(res.result.isError, true);
    assert.equal(res.result.content[0].text, "kaboom");
  });

  it("unknown tool + unknown method produce JSON-RPC errors", async () => {
    const notFound = (await dispatchAsync(
      rpc("tools/call", { name: "ghost" }),
      registryWith([echoTool]),
    )) as { error: { code: number } };
    assert.equal(notFound.error.code, -32602);

    const badMethod = (await dispatchAsync(rpc("nope"), registryWith([]))) as {
      error: { code: number };
    };
    assert.equal(badMethod.error.code, -32601);
  });

  it("resources/list + resources/read work across providers", async () => {
    const provider: IMcpResourceProvider = {
      uriScheme: "vault://",
      async listAsync() {
        return [{ uri: "vault://a", name: "A", description: null, mimeType: "text/plain" }];
      },
      async readAsync(uri) {
        return uri === "vault://a"
          ? { uri, mimeType: "text/plain", text: "hello" }
          : null;
      },
    };
    const reg = registryWith([], [provider]);

    const list = (await dispatchAsync(rpc("resources/list"), reg)) as {
      result: { resources: { uri: string; description: string }[] };
    };
    assert.equal(list.result.resources[0].uri, "vault://a");
    // description falls back to name when null.
    assert.equal(list.result.resources[0].description, "A");

    const read = (await dispatchAsync(rpc("resources/read", { uri: "vault://a" }), reg)) as {
      result: { contents: { text: string }[] };
    };
    assert.equal(read.result.contents[0].text, "hello");

    const missing = (await dispatchAsync(rpc("resources/read", { uri: "vault://x" }), reg)) as {
      error: { code: number };
    };
    assert.equal(missing.error.code, -32602);
  });

  it("buildManifest lists tools and marks itself deprecated", () => {
    const m = buildManifest(registryWith([echoTool])) as {
      deprecated: boolean;
      tools: { name: string }[];
    };
    assert.equal(m.deprecated, true);
    assert.deepEqual(m.tools.map((t) => t.name), ["echo"]);
  });
});

describe("MultiplayerHub", () => {
  it("colourFor is stable and deterministic per peer id", () => {
    const a = MultiplayerHub.colourFor("peer-1");
    const b = MultiplayerHub.colourFor("peer-1");
    assert.equal(a, b);
    assert.notEqual(a, MultiplayerHub.colourFor("peer-2"));
    assert.match(a, /^hsl\(\d+, 70%, 55%\)$/);
    assert.equal(MultiplayerHub.colourFor(""), "#5a4fcf");
  });

  it("join broadcasts PeerJoined and presence reflects membership", async () => {
    const bc = new RecordingBroadcaster();
    const hub = new MultiplayerHub(bc);
    await hub.onConnected("c1", new GuestPeerIdentity("p1", "Alice"));
    await hub.joinDocument("c1", "doc-1");

    assert.equal(hub.peers("doc-1").length, 1);
    assert.equal(hub.peers("doc-1")[0].displayName, "Alice");
    const joined = bc.events.find((e) => e.event === "PeerJoined");
    assert.ok(joined);
    assert.equal(joined!.group, "doc:doc-1");
  });

  it("SendEdit applies a higher rev and rejects a stale one", async () => {
    const bc = new RecordingBroadcaster();
    const hub = new MultiplayerHub(bc);
    await hub.onConnected("c1", new GuestPeerIdentity("p1", "Alice"));
    await hub.joinDocument("c1", "d");

    const r1 = await hub.sendEdit("c1", "d", "v1", 1);
    assert.equal(r1, 1);
    assert.equal(hub.currentRev("d"), 1);

    const r2 = await hub.sendEdit("c1", "d", "v2", 5);
    assert.equal(r2, 5);
    assert.equal(hub.currentRev("d"), 5);

    // Stale rev (3 <= 5) → rejected, returns current server rev, no broadcast.
    const before = bc.events.filter((e) => e.event === "EditApplied").length;
    const r3 = await hub.sendEdit("c1", "d", "stale", 3);
    assert.equal(r3, 5);
    const after = bc.events.filter((e) => e.event === "EditApplied").length;
    assert.equal(after, before);
  });

  it("disconnect removes presence and notifies the doc group", async () => {
    const bc = new RecordingBroadcaster();
    const hub = new MultiplayerHub(bc);
    await hub.onConnected("c1", new GuestPeerIdentity("p1", "Alice"));
    await hub.joinDocument("c1", "d");
    await hub.onDisconnected("c1");
    assert.equal(hub.peers("d").length, 0);
    assert.ok(bc.events.some((e) => e.event === "PeerLeft"));
  });

  it("cursor broadcasts to others in the group", async () => {
    const bc = new RecordingBroadcaster();
    const hub = new MultiplayerHub(bc);
    await hub.onConnected("c1", new GuestPeerIdentity("p1", "Alice"));
    await hub.joinDocument("c1", "d");
    await hub.sendCursor("c1", "d", 3, 7);
    const cursor = bc.events.find((e) => e.event === "CursorChanged");
    assert.ok(cursor);
    assert.deepEqual(cursor!.args.slice(3), [3, 7]);
  });
});
