// security_aethernet_bindings.test.ts
//
// Verifies the CircleAI.Security.AetherNet port (the AetherNet-specific security
// bindings that glue CircleAI.Aether to the transport-agnostic CircleAI.Security
// engine):
//   AetherMapper                — enum translation + fallbacks
//   MeshDirectiveStore          — sink, lazy expiry, Release lift, block query
//   MeshSecurityGate            — decide / enforce / GateDecision.Allowed
//   AetherSecurityBridge        — telemetry → SecurityLayerService → directives,
//                                 posture mapping (drives a real quarantine)
//   AetherIntelligenceAdapter   — result-type mapping over PeerIntelligenceService
//   MeshGatedCompanionSession   — gates send/stream/agent, passes through the rest

import { describe, it } from "node:test";
import assert from "node:assert/strict";

import {
  AetherSecurityEventKind,
  AetherThreatLevel,
  SecurityDirectiveKind,
  InMemoryAetherTelemetry,
  type SecurityDirective,
  type ISecurityDirectiveConsumer,
  type AetherSecurityEvent,
} from "../src/aether/index";

import {
  SecurityOptions,
  NodeTrustRegistry,
  DirectivePublisher,
  SecurityLayerService,
  PeerIntelligenceService,
  PeerSecurityEventKind,
  PeerThreatLevel,
  PeerDirectiveKind,
} from "../src/security/index";

import {
  AetherMapper,
  MeshDirectiveStore,
  MeshSecurityGate,
  GateDecisionAllowed,
  MeshSecurityBlockedException,
  AetherSecurityBridge,
  AetherIntelligenceAdapter,
  MeshGatedCompanionSession,
} from "../src/security/aethernet/index";

import {
  InterfaceKind,
  type ICompanionSession,
  type CompanionContext,
  type CompanionTurn,
  type ProactiveMessageHandler,
} from "../src/companion/index";

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function dir(
  kind: SecurityDirectiveKind,
  target: string | null,
  issuedAt: Date,
  durationMs: number | null = null,
  reason = "r",
): SecurityDirective {
  return { kind, targetNodeId: target, trustScoreOverride: null, threatLevel: AetherThreatLevel.High, reason, durationMs, issuedAt };
}

function secEvt(kind: AetherSecurityEventKind, level: AetherThreatLevel, node = "n1"): AetherSecurityEvent {
  return { nodeId: node, kind, threatLevel: level, description: `${kind}@${node}`, metadata: {}, occurredAt: new Date() };
}

/** Minimal in-memory ICompanionSession that records the messages it received. */
class FakeSession implements ICompanionSession {
  readonly sessionId = "s1";
  readonly identityId: string;
  readonly interface = InterfaceKind.Headless;
  private readonly _history: CompanionTurn[] = [];
  onProactiveMessageReady: ProactiveMessageHandler | null = null;
  readonly seen: string[] = [];

  constructor(identityId: string) {
    this.identityId = identityId;
  }

  get history(): readonly CompanionTurn[] {
    return this._history;
  }

  sendAsync(message: string): Promise<string> {
    this.seen.push(`send:${message}`);
    return Promise.resolve(`echo:${message}`);
  }

  async *streamAsync(message: string): AsyncGenerator<string> {
    this.seen.push(`stream:${message}`);
    yield "a";
    yield "b";
  }

  agentAsync(instruction: string): Promise<string> {
    this.seen.push(`agent:${instruction}`);
    return Promise.resolve(`done:${instruction}`);
  }

  getContext(): CompanionContext {
    return {
      identityId: this.identityId,
      displayName: "Test",
      preferredLanguage: null,
      interface: this.interface,
      personaHints: "",
      affectSummary: "",
      recentMemorySnippets: [],
      activeGoals: [],
      contextBuiltAt: new Date(),
    };
  }

  refreshContextAsync(): Promise<void> {
    this.seen.push("refresh");
    return Promise.resolve();
  }

  signalFeedbackAsync(positive: boolean, _note?: string): Promise<void> {
    this.seen.push(`feedback:${positive}`);
    return Promise.resolve();
  }
}

class CollectingConsumer implements ISecurityDirectiveConsumer {
  readonly received: SecurityDirective[] = [];
  onDirective(d: SecurityDirective): void {
    this.received.push(d);
  }
}

// ---------------------------------------------------------------------------
// AetherMapper
// ---------------------------------------------------------------------------

describe("AetherMapper", () => {
  it("maps event kinds with Unknown fallback", () => {
    assert.equal(AetherMapper.toPeerEventKind(AetherSecurityEventKind.NodeAuthAttempt), PeerSecurityEventKind.AuthAttempt);
    assert.equal(AetherMapper.toPeerEventKind(AetherSecurityEventKind.PrivilegeAttempt), PeerSecurityEventKind.PrivilegeAttempt);
    assert.equal(AetherMapper.toPeerEventKind(999 as AetherSecurityEventKind), PeerSecurityEventKind.Unknown);
  });

  it("maps threat levels round-trip", () => {
    for (const lvl of [AetherThreatLevel.None, AetherThreatLevel.Low, AetherThreatLevel.Medium, AetherThreatLevel.High, AetherThreatLevel.Critical]) {
      assert.equal(AetherMapper.toAetherThreatLevel(AetherMapper.toPeerThreatLevel(lvl)), lvl);
    }
    assert.equal(AetherMapper.toPeerThreatLevel(999 as AetherThreatLevel), PeerThreatLevel.None);
    assert.equal(AetherMapper.toAetherThreatLevel(999 as PeerThreatLevel), AetherThreatLevel.None);
  });

  it("maps directive kinds with ElevateMonitoring fallback", () => {
    assert.equal(AetherMapper.toSecurityDirectiveKind(PeerDirectiveKind.AvoidNode), SecurityDirectiveKind.AvoidNode);
    assert.equal(AetherMapper.toSecurityDirectiveKind(PeerDirectiveKind.QuarantineNode), SecurityDirectiveKind.QuarantineNode);
    assert.equal(AetherMapper.toSecurityDirectiveKind(PeerDirectiveKind.ReleaseNode), SecurityDirectiveKind.ReleaseNode);
    assert.equal(AetherMapper.toSecurityDirectiveKind(999 as PeerDirectiveKind), SecurityDirectiveKind.ElevateMonitoring);
  });
});

// ---------------------------------------------------------------------------
// MeshDirectiveStore
// ---------------------------------------------------------------------------

describe("MeshDirectiveStore", () => {
  it("records block directives and reports the latest reason", () => {
    const store = new MeshDirectiveStore();
    store.onDirective(dir(SecurityDirectiveKind.AvoidNode, "n1", new Date(1000), null, "avoid-old"));
    store.onDirective(dir(SecurityDirectiveKind.QuarantineNode, "n1", new Date(2000), null, "quarantine-new"));
    const { blocked, reason } = store.isBlocked("n1");
    assert.equal(blocked, true);
    assert.equal(reason, "quarantine-new"); // most recent issuedAt wins
    assert.equal(store.trackedNodeCount, 1);
  });

  it("ignores directives without a target", () => {
    const store = new MeshDirectiveStore();
    store.onDirective(dir(SecurityDirectiveKind.AvoidNode, null, new Date()));
    assert.equal(store.trackedNodeCount, 0);
  });

  it("non-block directives do not block; ElevateMonitoring is tracked but not a block", () => {
    const store = new MeshDirectiveStore();
    store.onDirective(dir(SecurityDirectiveKind.ElevateMonitoring, "n1", new Date()));
    assert.equal(store.isBlocked("n1").blocked, false);
    assert.equal(store.getActiveDirectives("n1").length, 1);
  });

  it("ReleaseNode lifts every directive for the node", () => {
    const store = new MeshDirectiveStore();
    store.onDirective(dir(SecurityDirectiveKind.QuarantineNode, "n1", new Date()));
    store.onDirective(dir(SecurityDirectiveKind.ReleaseNode, "n1", new Date()));
    assert.equal(store.isBlocked("n1").blocked, false);
    assert.equal(store.trackedNodeCount, 0);
  });

  it("expires directives lazily on read using the injected clock", () => {
    let now = new Date(0);
    const store = new MeshDirectiveStore(() => now);
    // 1s duration, issued at t=0.
    store.onDirective(dir(SecurityDirectiveKind.QuarantineNode, "n1", new Date(0), 1000));
    now = new Date(500);
    assert.equal(store.isBlocked("n1").blocked, true); // still within duration
    now = new Date(2000);
    assert.equal(store.isBlocked("n1").blocked, false); // expired
    assert.equal(store.trackedNodeCount, 0); // swept on read
  });

  it("blank node id is never blocked", () => {
    const store = new MeshDirectiveStore();
    assert.equal(store.isBlocked("  ").blocked, false);
    assert.equal(store.getActiveDirectives("").length, 0);
  });
});

// ---------------------------------------------------------------------------
// MeshSecurityGate
// ---------------------------------------------------------------------------

describe("MeshSecurityGate", () => {
  it("decide returns Allowed for unknown ids, blocked with reason otherwise", () => {
    const store = new MeshDirectiveStore();
    const gate = new MeshSecurityGate(store);
    assert.equal(gate.decide("nobody"), GateDecisionAllowed);

    store.onDirective(dir(SecurityDirectiveKind.QuarantineNode, "bad", new Date(), null, "malware"));
    const d = gate.decide("bad");
    assert.equal(d.isBlocked, true);
    assert.equal(d.reason, "malware");
  });

  it("enforce throws MeshSecurityBlockedException for a blocked id", () => {
    const store = new MeshDirectiveStore();
    const gate = new MeshSecurityGate(store);
    store.onDirective(dir(SecurityDirectiveKind.AvoidNode, "bad", new Date(), null, "spam"));
    assert.throws(
      () => gate.enforce("bad"),
      (e: unknown) => e instanceof MeshSecurityBlockedException && e.blockedId === "bad" && /spam/.test(e.message),
    );
    // Allowed id does not throw.
    assert.doesNotThrow(() => gate.enforce("good"));
  });
});

// ---------------------------------------------------------------------------
// AetherSecurityBridge
// ---------------------------------------------------------------------------

describe("AetherSecurityBridge", () => {
  function buildLayer(): { layer: SecurityLayerService; options: SecurityOptions } {
    const options = new SecurityOptions();
    const registry = new NodeTrustRegistry(options);
    const publisher = new DirectivePublisher();
    const layer = new SecurityLayerService(registry, options, publisher);
    return { layer, options };
  }

  it("translates telemetry security events into peer directives (drives a quarantine)", async () => {
    const { layer } = buildLayer();
    const bridge = new AetherSecurityBridge(layer);
    const telemetry = new InMemoryAetherTelemetry();

    const consumer = new CollectingConsumer();
    bridge.subscribeToDirectives(consumer);
    await bridge.startAsync(telemetry);

    // Critical intrusion: degradation 0.15 * 3 = 0.45. Three events push
    // trust 1.0 → 0.55 → 0.10 → 0.0. The layer issues at most ONE directive per
    // event (most-severe crossing wins): event 1 crosses ElevateMonitoring (0.75),
    // event 2 jumps straight past AvoidNode to Quarantine (≤0.25). So the observed
    // sequence is [ElevateMonitoring, QuarantineNode] — AvoidNode is skipped
    // because the second drop overshot it. All translated to the Aether shape.
    for (let i = 0; i < 3; i++) {
      telemetry.publishSecurity(secEvt(AetherSecurityEventKind.IntrusionSignal, AetherThreatLevel.Critical, "attacker"));
    }

    const kinds = consumer.received.map((d) => d.kind);
    assert.ok(kinds.includes(SecurityDirectiveKind.ElevateMonitoring), `kinds=${kinds}`);
    assert.ok(kinds.includes(SecurityDirectiveKind.QuarantineNode), `kinds=${kinds}`);
    // QuarantineNode is the terminal directive (most severe reached).
    assert.equal(kinds[kinds.length - 1], SecurityDirectiveKind.QuarantineNode, `kinds=${kinds}`);
    for (const d of consumer.received) assert.equal(d.targetNodeId, "attacker");

    await bridge.stopAsync();
  });

  it("maps posture from the underlying layer", async () => {
    const { layer } = buildLayer();
    const bridge = new AetherSecurityBridge(layer);
    const telemetry = new InMemoryAetherTelemetry();
    await bridge.startAsync(telemetry);

    telemetry.publishSecurity(secEvt(AetherSecurityEventKind.IntrusionSignal, AetherThreatLevel.Critical, "attacker"));
    telemetry.publishSecurity(secEvt(AetherSecurityEventKind.IntrusionSignal, AetherThreatLevel.Critical, "attacker"));

    const posture = await bridge.getPostureAsync();
    assert.equal(posture.isActive, true);
    assert.ok(posture.overallThreatLevel >= AetherThreatLevel.High);

    await bridge.stopAsync();
  });

  it("stops cleanly and unsubscribes telemetry (no events after stop reach the layer)", async () => {
    const { layer } = buildLayer();
    const bridge = new AetherSecurityBridge(layer);
    const telemetry = new InMemoryAetherTelemetry();
    await bridge.startAsync(telemetry);
    assert.equal(telemetry.subscriberCount, 1);
    await bridge.stopAsync();
    assert.equal(telemetry.subscriberCount, 0);
  });
});

// ---------------------------------------------------------------------------
// AetherIntelligenceAdapter
// ---------------------------------------------------------------------------

describe("AetherIntelligenceAdapter", () => {
  it("maps PeerIntelligenceService outputs to Aether shapes", async () => {
    const options = new SecurityOptions();
    const registry = new NodeTrustRegistry(options);
    const intel = new PeerIntelligenceService(registry, options);
    const adapter = new AetherIntelligenceAdapter(intel);

    // Degrade a node so health/assessment are non-trivial.
    registry.applyDegradation(
      { nodeId: "n1", kind: PeerSecurityEventKind.IntrusionSignal, threatLevel: PeerThreatLevel.Critical, description: "hit", transportId: "aether", occurredAt: new Date() },
      0.8,
    );

    const health = await adapter.getNetworkHealthAsync();
    assert.equal(health.trustedNodeCount + health.suspiciousNodeCount >= 1, true);
    assert.ok(networkScoreInRange(health.overallScore));

    const assess = await adapter.assessThreatAsync("n1");
    assert.equal(assess.nodeId, "n1");
    assert.ok(assess.level >= AetherThreatLevel.High); // score 0.2 → Critical/High
    assert.ok(assess.threatConfidence > 0);

    const advice = await adapter.getRoutingAdviceAsync("n1");
    assert.equal(advice.destinationNodeId, "n1");
    assert.ok(advice.avoidNodes.includes("n1")); // 0.2 ≤ avoid threshold
  });

  it("streams mapped trust-score updates (PreviousScore/NewScore → previousScore/currentScore)", async () => {
    const options = new SecurityOptions();
    const registry = new NodeTrustRegistry(options);
    const intel = new PeerIntelligenceService(registry, options);
    const adapter = new AetherIntelligenceAdapter(intel);

    // Publish a change BEFORE the reader attaches — unbounded channel retains it.
    registry.applyDegradation(
      { nodeId: "n1", kind: PeerSecurityEventKind.IntrusionSignal, threatLevel: PeerThreatLevel.Critical, description: "drop", transportId: "aether", occurredAt: new Date() },
      0.3,
    );

    const controller = new AbortController();
    const got: Array<{ prev: number; cur: number }> = [];
    for await (const u of adapter.streamTrustScoresAsync(controller.signal)) {
      got.push({ prev: u.previousScore, cur: u.currentScore });
      controller.abort();
      break;
    }
    assert.equal(got.length, 1);
    assert.ok(Math.abs(got[0].prev - 1.0) < 1e-9);
    assert.ok(Math.abs(got[0].cur - 0.7) < 1e-9);
  });
});

function networkScoreInRange(s: number): boolean {
  return s >= 0 && s <= 1;
}

// ---------------------------------------------------------------------------
// MeshGatedCompanionSession
// ---------------------------------------------------------------------------

describe("MeshGatedCompanionSession", () => {
  function build(identity: string): { inner: FakeSession; gated: MeshGatedCompanionSession; store: MeshDirectiveStore } {
    const store = new MeshDirectiveStore();
    const gate = new MeshSecurityGate(store);
    const inner = new FakeSession(identity);
    const gated = new MeshGatedCompanionSession(inner, gate);
    return { inner, gated, store };
  }

  it("passes through identity + message calls when not blocked", async () => {
    const { inner, gated } = build("user-ok");
    assert.equal(gated.sessionId, "s1");
    assert.equal(gated.identityId, "user-ok");
    assert.equal(gated.interface, InterfaceKind.Headless);

    assert.equal(await gated.sendAsync("hi"), "echo:hi");
    const chunks: string[] = [];
    for await (const c of gated.streamAsync("go")) chunks.push(c);
    assert.deepEqual(chunks, ["a", "b"]);
    assert.equal(await gated.agentAsync("do"), "done:do");
    assert.deepEqual(inner.seen, ["send:hi", "stream:go", "agent:do"]);
  });

  it("blocks send/stream/agent when the mesh has quarantined the identity", async () => {
    const { inner, gated, store } = build("user-bad");
    store.onDirective(dir(SecurityDirectiveKind.QuarantineNode, "user-bad", new Date(), null, "abuse"));

    // The gate check runs synchronously before the underlying Task (mirrors the
    // C# `_gate.Enforce()` guard), so send/agent THROW synchronously. Wrap in an
    // async IIFE to normalise the sync throw into a rejection for assert.rejects.
    // streamAsync is a generator: the guard fires on first iteration.
    await assert.rejects(async () => gated.sendAsync("hi"), MeshSecurityBlockedException);
    await assert.rejects(
      (async () => {
        for await (const _ of gated.streamAsync("go")) {
          /* drain */
        }
      })(),
      MeshSecurityBlockedException,
    );
    await assert.rejects(async () => gated.agentAsync("do"), MeshSecurityBlockedException);
    // The inner session was never reached.
    assert.deepEqual(inner.seen, []);
  });

  it("does NOT gate context/history/feedback for a blocked identity", async () => {
    const { inner, gated, store } = build("user-bad");
    store.onDirective(dir(SecurityDirectiveKind.QuarantineNode, "user-bad", new Date(), null, "abuse"));

    // These must still work even though the user is blocked.
    assert.equal(gated.getContext().identityId, "user-bad");
    await gated.refreshContextAsync();
    await gated.signalFeedbackAsync(true, "note");
    assert.deepEqual(inner.seen, ["refresh", "feedback:true"]);
  });

  it("forwards the proactive handler to the inner session", () => {
    const { inner, gated } = build("user-ok");
    const handler: ProactiveMessageHandler = () => {};
    gated.onProactiveMessageReady = handler;
    assert.equal(inner.onProactiveMessageReady, handler);
    assert.equal(gated.onProactiveMessageReady, handler);
  });
});
