// microagents.test.ts
// Verifies the CircleAI.MicroAgents port: FuncMicroAgent, the InMemoryMicroAgentHost
// registry+router, NullMicroAgent, capability search, and the invocation log.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  FuncMicroAgent,
  NullMicroAgent,
  InMemoryMicroAgentHost,
  MicroAgentSearch,
  MicroAgentInvocationLog,
  microAgentResponse,
  microAgentDescriptor,
  microAgentInvocation,
} from "../src/microagents/index";

describe("FuncMicroAgent", () => {
  it("wraps a delegate and exposes a descriptor", async () => {
    const a = new FuncMicroAgent("echo", "echoes", ["text"], async (input) =>
      microAgentResponse("echo", input.toUpperCase()),
    );
    assert.equal(a.backendId, "func");
    assert.deepEqual([...a.descriptor.capabilities], ["text"]);
    const r = await a.invokeAsync("hi");
    assert.equal(r.output, "HI");
  });

  it("requires an agentId and impl", () => {
    assert.throws(() => new FuncMicroAgent("", "d", [], async () => microAgentResponse("x", "")));
  });
});

describe("InMemoryMicroAgentHost", () => {
  it("registers, lists, and routes invocations", async () => {
    const host = new InMemoryMicroAgentHost();
    host.register(new FuncMicroAgent("a", "A", ["x"], async () => microAgentResponse("a", "ra")));
    host.register(new FuncMicroAgent("b", "B", ["y"], async () => microAgentResponse("b", "rb")));
    assert.equal(host.list().length, 2);
    assert.equal((await host.invokeAsync("a", "in"))?.output, "ra");
    assert.equal(await host.invokeAsync("missing", "in"), null);
  });
});

describe("NullMicroAgent", () => {
  it("returns an empty response", async () => {
    const r = await new NullMicroAgent().invokeAsync("x");
    assert.equal(r.output, "");
    assert.equal(r.agentId, "null");
  });
});

describe("MicroAgentSearch", () => {
  const all = [
    microAgentDescriptor("zeta", "does search things", ["search", "index"]),
    microAgentDescriptor("alpha", "summariser", ["summary"]),
    microAgentDescriptor("beta", "indexer", ["INDEX"]),
  ];

  it("byCapability matches case-insensitively and sorts by agentId", () => {
    const r = MicroAgentSearch.byCapability(all, "index");
    assert.deepEqual(
      r.map((d) => d.agentId),
      ["beta", "zeta"],
    );
  });

  it("search matches id/description/capabilities and honours topK", () => {
    assert.equal(MicroAgentSearch.search(all, "search").length, 1);
    assert.equal(MicroAgentSearch.search(all, "e", 1).length, 1);
  });
});

describe("MicroAgentInvocationLog", () => {
  it("keeps a most-recent-first per-agent log", () => {
    const log = new MicroAgentInvocationLog();
    log.append(microAgentInvocation("a", "1", "r1", new Date("2026-01-01T00:00:00Z")));
    log.append(microAgentInvocation("a", "2", "r2", new Date("2026-02-01T00:00:00Z")));
    log.append(microAgentInvocation("b", "3", "r3", new Date("2026-01-15T00:00:00Z")));
    assert.equal(log.totalInvocations, 3);
    const forA = log.forAgent("a");
    assert.deepEqual(
      forA.map((i) => i.input),
      ["2", "1"],
    );
  });
});
