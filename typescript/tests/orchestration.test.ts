// orchestration.test.ts
// Verifies the CircleAI.Orchestration port: task creation, dispatcher handler
// routing + blocked default, quality-gate blocker classification, LokiOrchestrator
// swarm streaming + timeout + exception wrapping, IncidentTrigger mapping, and the
// SecurityOrchestrationBridge parallel dispatch.

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  AgentRole,
  AgentPriority,
  AgentStatus,
  AgentSwarmConfig,
  LocalAgentDispatcher,
  LokiOrchestrator,
  IncidentTrigger,
  SecurityOrchestrationBridge,
  createAgentTask,
  swarmResult,
  type IAgentDispatcher,
  type SwarmResult,
  type AgentTask,
} from "../src/orchestration/index";
import {
  ThreatVector,
  DefaultSecurityWatchdog,
  type AnomalySignal,
} from "../src/security/index";
import type { EpisodicMemoryEntry } from "../src/memory/index";

describe("createAgentTask", () => {
  it("stamps a fresh id, empty inputs by default, and a createdAt", () => {
    const t = createAgentTask(AgentRole.Engineering, "fix bug", AgentPriority.Normal);
    assert.ok(t.id.length > 0);
    assert.equal(t.role, AgentRole.Engineering);
    assert.equal(t.inputs.size, 0);
    assert.ok(t.createdAt instanceof Date);
    const t2 = createAgentTask(AgentRole.Engineering, "x", AgentPriority.Low);
    assert.notEqual(t.id, t2.id);
  });
});

describe("LocalAgentDispatcher", () => {
  it("routes to a registered handler", async () => {
    const d = new LocalAgentDispatcher();
    d.registerHandler(AgentRole.Review, async (task) =>
      swarmResult(task.id, task.role, AgentStatus.Passed, "ok", [], new Date()),
    );
    const t = createAgentTask(AgentRole.Review, "review", AgentPriority.Normal);
    const r = await d.dispatchAsync(t);
    assert.equal(r.status, AgentStatus.Passed);
    assert.equal(r.output, "ok");
  });

  it("returns Blocked when no handler is registered", async () => {
    const d = new LocalAgentDispatcher();
    const t = createAgentTask(AgentRole.Security, "scan", AgentPriority.Critical);
    const r = await d.dispatchAsync(t);
    assert.equal(r.status, AgentStatus.Blocked);
    assert.match(r.output, /No handler registered for role Security/);
  });

  it("classifies [CRITICAL]/[HIGH] as blockers, else warnings", async () => {
    const d = new LocalAgentDispatcher();
    const r = swarmResult(
      "id",
      AgentRole.Review,
      AgentStatus.Passed,
      "out",
      ["[HIGH] boom", "[critical] bad", "[info] fyi", "plain"],
      new Date(),
    );
    const gate = await d.runQualityGateAsync(r);
    assert.equal(gate.passed, false);
    assert.deepEqual([...gate.blockers], ["[HIGH] boom", "[critical] bad"]);
    assert.deepEqual([...gate.warnings], ["[info] fyi", "plain"]);
  });

  it("throws after dispose", async () => {
    const d = new LocalAgentDispatcher();
    d.dispose();
    const t = createAgentTask(AgentRole.Review, "x", AgentPriority.Normal);
    await assert.rejects(() => d.dispatchAsync(t));
  });
});

describe("LokiOrchestrator", () => {
  it("streams a result per task and blocks gate failures", async () => {
    const d = new LocalAgentDispatcher();
    d.registerHandler(AgentRole.Engineering, async (task) =>
      swarmResult(
        task.id,
        task.role,
        AgentStatus.Passed,
        "done",
        task.description === "bad" ? ["[HIGH] nope"] : [],
        new Date(),
      ),
    );
    const orch = new LokiOrchestrator(d, AgentSwarmConfig.default());
    const tasks = [
      createAgentTask(AgentRole.Engineering, "good", AgentPriority.Normal),
      createAgentTask(AgentRole.Engineering, "bad", AgentPriority.Normal),
    ];
    const results: SwarmResult[] = [];
    for await (const r of orch.runSwarmAsync(tasks)) results.push(r);
    assert.equal(results.length, 2);
    assert.equal(results[0].status, AgentStatus.Passed);
    assert.equal(results[1].status, AgentStatus.Blocked);
    assert.ok(results[1].issues.includes("[HIGH] nope"));
  });

  it("wraps a dispatcher exception as a Failed result instead of breaking the stream", async () => {
    const throwing: IAgentDispatcher = {
      dispatchAsync: async () => {
        throw new Error("kaboom");
      },
      runQualityGateAsync: async (r) => ({ passed: r.issues.length === 0, blockers: [], warnings: [] }),
    };
    // Disable gate enforcement so the wrapped Failed result is yielded as-is. With
    // the default config (both require-flags true), RunSwarmAsync re-classifies any
    // gate-failing result to Blocked (LokiOrchestrator.cs lines 75-83) — so the C#
    // reference tests that assert a Failed result (e.g.
    // RunSwarmAsync_TaskExceedsTimeout_YieldsFailed) construct the orchestrator with
    // `new AgentSwarmConfig(1, …, false, false)`. We mirror that here.
    const cfg: AgentSwarmConfig = {
      ...AgentSwarmConfig.default(),
      requireReviewPassBeforeDeploy: false,
      requireSecurityPassBeforeDeploy: false,
    };
    const orch = new LokiOrchestrator(throwing, cfg);
    const results: SwarmResult[] = [];
    for await (const r of orch.runSwarmAsync([
      createAgentTask(AgentRole.Operations, "x", AgentPriority.Normal),
    ])) {
      results.push(r);
    }
    assert.equal(results.length, 1);
    assert.equal(results[0].status, AgentStatus.Failed);
    assert.match(results[0].output, /Dispatcher threw/);
  });

  it("times out a slow task", async () => {
    const slow: IAgentDispatcher = {
      dispatchAsync: (_t, signal) =>
        new Promise((resolve, reject) => {
          const timer = setTimeout(
            () => resolve(swarmResult("x", AgentRole.Review, AgentStatus.Passed, "late", [], new Date())),
            10_000,
          );
          signal?.addEventListener("abort", () => {
            clearTimeout(timer);
            reject(new Error("aborted"));
          });
        }),
      runQualityGateAsync: async () => ({ passed: true, blockers: [], warnings: [] }),
    };
    const cfg: AgentSwarmConfig = { ...AgentSwarmConfig.default(), taskTimeoutMs: 20 };
    const orch = new LokiOrchestrator(slow, cfg);
    const results: SwarmResult[] = [];
    for await (const r of orch.runSwarmAsync([
      createAgentTask(AgentRole.Review, "x", AgentPriority.Normal),
    ])) {
      results.push(r);
    }
    assert.equal(results[0].status, AgentStatus.Failed);
    assert.match(results[0].output, /timed out/);
  });
});

describe("IncidentTrigger.fromMemoryEntry", () => {
  const entry = (tags: Record<string, string>): EpisodicMemoryEntry => ({
    id: "ep1",
    recordedAtUtc: new Date("2026-01-01T00:00:00Z"),
    userText: "u",
    assistantText: "a",
    appContext: "app",
    tags,
  });

  it("returns empty for a non-incident entry", () => {
    assert.equal(IncidentTrigger.fromMemoryEntry(entry({ chat: "1" })).length, 0);
  });

  it("returns one Operations task for a crash", () => {
    const tasks = IncidentTrigger.fromMemoryEntry(entry({ crash: "1" }));
    assert.equal(tasks.length, 1);
    assert.equal(tasks[0].role, AgentRole.Operations);
    assert.equal(tasks[0].priority, AgentPriority.High);
    assert.equal(tasks[0].inputs.get("episode_id"), "ep1");
  });

  it("adds a Security task when a security tag is also present", () => {
    const tasks = IncidentTrigger.fromMemoryEntry(entry({ exception: "1", injection: "1" }));
    assert.equal(tasks.length, 2);
    assert.equal(tasks[1].role, AgentRole.Security);
    assert.equal(tasks[1].priority, AgentPriority.Critical);
  });
});

describe("IncidentTrigger.fromAnomalySignal", () => {
  const sig = (confidence: number, vector: ThreatVector): AnomalySignal => ({
    id: "s1",
    vector,
    confidence,
    affectedModule: "mod",
    description: "d",
    evidence: { k: "v" },
    detectedAt: new Date("2026-01-01T00:00:00Z"),
  });

  it("returns null below threshold", () => {
    assert.equal(IncidentTrigger.fromAnomalySignal(sig(0.2, ThreatVector.Unknown)), null);
  });

  it("maps confidence to priority and bumps high-severity vectors", () => {
    // 0.6 → High; StateCorruption is high-severity → bumped to Critical.
    const t = IncidentTrigger.fromAnomalySignal(sig(0.6, ThreatVector.StateCorruption));
    assert.ok(t);
    assert.equal(t?.role, AgentRole.Security);
    assert.equal(t?.priority, AgentPriority.Critical);
    assert.equal(t?.inputs.get("vector"), "StateCorruption");
    assert.equal(t?.inputs.get("k"), "v");
  });

  it("keeps Normal priority for a low-severity vector at 0.5", () => {
    const t = IncidentTrigger.fromAnomalySignal(sig(0.5, ThreatVector.MemoryAnomaly));
    assert.equal(t?.priority, AgentPriority.Normal);
  });
});

describe("SecurityOrchestrationBridge", () => {
  it("returns the inner watchdog response and dispatches in parallel", async () => {
    const inner = new DefaultSecurityWatchdog();
    const d = new LocalAgentDispatcher();
    let dispatched = 0;
    d.registerHandler(AgentRole.Security, async (task) => {
      dispatched++;
      return swarmResult(task.id, task.role, AgentStatus.Passed, "handled", [], new Date());
    });
    const orch = new LokiOrchestrator(d);
    const bridge = new SecurityOrchestrationBridge(inner, orch, 0.3);
    const signal: AnomalySignal = {
      id: "a1",
      vector: ThreatVector.PrivilegeEscalation,
      confidence: 0.9,
      affectedModule: "mod",
      description: "d",
      evidence: {},
      detectedAt: new Date(),
    };
    const resp = await bridge.onAnomalyDetectedAsync(signal);
    assert.ok(resp);
    // Give the fire-and-forget agent path a tick to run.
    await new Promise((r) => setTimeout(r, 5));
    assert.equal(dispatched, 1);
  });
});
