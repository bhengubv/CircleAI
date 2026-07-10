// aether_contracts.test.ts
//
// Verifies the CircleAI.Aether port (5 contracts) + deterministic in-memory impls:
//   event records + derived predicates (IsExit/HopCount/ExceedsLoss/IsHighSeverity/…)
//   enum ordinals (wire contract)
//   InMemoryAetherTelemetry (snapshot-safe fan-out, dispose)
//   InMemoryAetherContext (isAvailable / isSufficient / requiresAuth)
//   AuthChallengeResult factories + InMemoryAuthChallenge (minimum enforcement)
//   InMemoryAetherIntelligence (health / assessment / routing / trust stream)
//   SecurityDirective predicates (HasTarget / IsPermanent)

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  // enums
  AetherNodeEventKind,
  AetherTransportKind,
  AetherTransportEventKind,
  AetherRouteEventKind,
  AetherSecurityEventKind,
  AetherThreatLevel,
  AetherNetworkEventKind,
  AetherInstallLevel,
  SecurityDirectiveKind,
  AuthChallengeReason,
  AuthMethod,
  // predicates
  aetherNodeEventIsExit,
  aetherNodeHealthIsValid,
  aetherTransportEventExceedsLoss,
  aetherRouteEventHopCount,
  aetherRouteEventIsFailed,
  aetherSecurityEventIsHighSeverity,
  aetherNetworkEventIsHighCongestion,
  networkHealthReportIsValid,
  threatAssessmentIsValid,
  trustScoreUpdateHasChanged,
  trustScoreUpdateIsDegraded,
  securityDirectiveHasTarget,
  securityDirectiveIsPermanent,
  // versions
  aetherVersion,
  compareAetherVersion,
  // impls
  NullAetherTelemetry,
  InMemoryAetherTelemetry,
  InMemoryAetherContext,
  AuthChallengeResult,
  InMemoryAuthChallenge,
  InMemoryAetherIntelligence,
  // types
  type IAetherTelemetryObserver,
  type AetherNodeEvent,
  type AetherTransportEvent,
  type AetherRouteEvent,
  type AetherSecurityEvent,
  type AetherNetworkEvent,
  type SecurityDirective,
} from "../src/aether/index";

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function nodeEvt(kind: AetherNodeEventKind, trust = 1.0): AetherNodeEvent {
  return {
    nodeId: "n1",
    kind,
    health: { trustScore: trust, isReachable: true, latencyMs: 10, hopCount: 1 },
    occurredAt: new Date(),
  };
}

function secEvt(kind: AetherSecurityEventKind, level: AetherThreatLevel, node = "n1"): AetherSecurityEvent {
  return { nodeId: node, kind, threatLevel: level, description: `${kind}@${node}`, metadata: {}, occurredAt: new Date() };
}

class CollectObserver implements IAetherTelemetryObserver {
  nodes: AetherNodeEvent[] = [];
  security: AetherSecurityEvent[] = [];
  transports: AetherTransportEvent[] = [];
  routes: AetherRouteEvent[] = [];
  networks: AetherNetworkEvent[] = [];
  onNodeEvent(e: AetherNodeEvent): void {
    this.nodes.push(e);
  }
  onSecurityEvent(e: AetherSecurityEvent): void {
    this.security.push(e);
  }
  onTransportEvent(e: AetherTransportEvent): void {
    this.transports.push(e);
  }
  onRouteEvent(e: AetherRouteEvent): void {
    this.routes.push(e);
  }
  onNetworkEvent(e: AetherNetworkEvent): void {
    this.networks.push(e);
  }
}

// ---------------------------------------------------------------------------
// Enum ordinals — wire contract
// ---------------------------------------------------------------------------

describe("Aether enum ordinals", () => {
  it("match the C# declaration order", () => {
    assert.equal(AetherNodeEventKind.Joined, 0);
    assert.equal(AetherNodeEventKind.HealthChanged, 2);
    assert.equal(AetherTransportKind.WiFi, 0);
    assert.equal(AetherTransportKind.Unknown, 6);
    assert.equal(AetherTransportEventKind.PacketLoss, 3);
    assert.equal(AetherRouteEventKind.Failed, 2);
    assert.equal(AetherSecurityEventKind.NodeAuthAttempt, 0);
    assert.equal(AetherSecurityEventKind.PrivilegeAttempt, 5);
    assert.equal(AetherThreatLevel.None, 0);
    assert.equal(AetherThreatLevel.Critical, 4);
    assert.equal(AetherNetworkEventKind.PartitionDetected, 2);
    assert.equal(AetherInstallLevel.None, 0);
    assert.equal(AetherInstallLevel.OS, 2);
    // SecurityDirectiveKind order: UpdateNodeTrust, AvoidNode, QuarantineNode, ReleaseNode, RequestReauth, ElevateMonitoring
    assert.equal(SecurityDirectiveKind.UpdateNodeTrust, 0);
    assert.equal(SecurityDirectiveKind.ReleaseNode, 3);
    assert.equal(SecurityDirectiveKind.ElevateMonitoring, 5);
    assert.equal(AuthChallengeReason.OsLevelToggle, 0);
    assert.equal(AuthChallengeReason.ManualRequest, 4);
    // AuthMethod is strength-ordered starting at 1.
    assert.equal(AuthMethod.Biometric, 1);
    assert.equal(AuthMethod.BiometricAndDeviceAdmin, 3);
    assert.equal(AuthMethod.Custom, 4);
  });
});

// ---------------------------------------------------------------------------
// Event predicates
// ---------------------------------------------------------------------------

describe("Aether event predicates", () => {
  it("node IsExit / health IsValid", () => {
    assert.equal(aetherNodeEventIsExit(nodeEvt(AetherNodeEventKind.Left)), true);
    assert.equal(aetherNodeEventIsExit(nodeEvt(AetherNodeEventKind.Joined)), false);
    assert.equal(aetherNodeHealthIsValid({ trustScore: 0.5, isReachable: true, latencyMs: 0, hopCount: 0 }), true);
    assert.equal(aetherNodeHealthIsValid({ trustScore: 1.5, isReachable: true, latencyMs: 0, hopCount: 0 }), false);
  });

  it("transport ExceedsLoss", () => {
    const e: AetherTransportEvent = {
      nodeId: "n",
      kind: AetherTransportEventKind.PacketLoss,
      transport: AetherTransportKind.WiFi,
      latencyMs: null,
      packetLossRate: 0.4,
      occurredAt: new Date(),
    };
    assert.equal(aetherTransportEventExceedsLoss(e, 0.3), true);
    assert.equal(aetherTransportEventExceedsLoss(e, 0.5), false);
    assert.equal(aetherTransportEventExceedsLoss({ ...e, packetLossRate: null }, 0.1), false);
  });

  it("route HopCount / IsFailed", () => {
    const e: AetherRouteEvent = {
      sourceNodeId: "a",
      destinationNodeId: "c",
      path: ["a", "b", "c"],
      kind: AetherRouteEventKind.Failed,
      failureReason: "loop",
      occurredAt: new Date(),
    };
    assert.equal(aetherRouteEventHopCount(e), 3);
    assert.equal(aetherRouteEventIsFailed(e), true);
    assert.equal(aetherRouteEventIsFailed({ ...e, kind: AetherRouteEventKind.Discovered }), false);
  });

  it("security IsHighSeverity", () => {
    assert.equal(aetherSecurityEventIsHighSeverity(secEvt(AetherSecurityEventKind.IntrusionSignal, AetherThreatLevel.High)), true);
    assert.equal(aetherSecurityEventIsHighSeverity(secEvt(AetherSecurityEventKind.IntrusionSignal, AetherThreatLevel.Critical)), true);
    assert.equal(aetherSecurityEventIsHighSeverity(secEvt(AetherSecurityEventKind.NodeAuthAttempt, AetherThreatLevel.Low)), false);
  });

  it("network IsHighCongestion", () => {
    const base: AetherNetworkEvent = {
      kind: AetherNetworkEventKind.CongestionDetected,
      nodeCount: 5,
      activeRouteCount: 3,
      congestionLevel: 0.8,
      occurredAt: new Date(),
    };
    assert.equal(aetherNetworkEventIsHighCongestion(base), true);
    assert.equal(aetherNetworkEventIsHighCongestion({ ...base, congestionLevel: 0.75 }), false);
  });
});

// ---------------------------------------------------------------------------
// Intelligence-output record predicates
// ---------------------------------------------------------------------------

describe("Intelligence record predicates", () => {
  it("report/assessment IsValid", () => {
    assert.equal(
      networkHealthReportIsValid({ overallScore: 0.9, trustedNodeCount: 1, suspiciousNodeCount: 0, summary: "", generatedAt: new Date() }),
      true,
    );
    assert.equal(
      networkHealthReportIsValid({ overallScore: 1.1, trustedNodeCount: 0, suspiciousNodeCount: 0, summary: "", generatedAt: new Date() }),
      false,
    );
    assert.equal(
      threatAssessmentIsValid({ nodeId: "n", threatConfidence: 0.2, level: AetherThreatLevel.Low, indicators: [], assessedAt: new Date() }),
      true,
    );
  });

  it("TrustScoreUpdate HasChanged / IsDegraded", () => {
    const u = { nodeId: "n", previousScore: 0.9, currentScore: 0.6, reason: "x", updatedAt: new Date() };
    assert.equal(trustScoreUpdateHasChanged(u), true);
    assert.equal(trustScoreUpdateIsDegraded(u), true);
    const same = { ...u, currentScore: 0.9 };
    assert.equal(trustScoreUpdateHasChanged(same), false);
    assert.equal(trustScoreUpdateIsDegraded(same), false);
    // Below the 0.001 movement floor.
    assert.equal(trustScoreUpdateHasChanged({ ...u, currentScore: 0.9005 }), false);
  });
});

// ---------------------------------------------------------------------------
// SecurityDirective predicates
// ---------------------------------------------------------------------------

describe("SecurityDirective predicates", () => {
  const dir = (target: string | null, durationMs: number | null): SecurityDirective => ({
    kind: SecurityDirectiveKind.AvoidNode,
    targetNodeId: target,
    trustScoreOverride: null,
    threatLevel: AetherThreatLevel.High,
    reason: "r",
    durationMs,
    issuedAt: new Date(),
  });

  it("HasTarget / IsPermanent", () => {
    assert.equal(securityDirectiveHasTarget(dir("n1", null)), true);
    assert.equal(securityDirectiveHasTarget(dir(null, null)), false);
    assert.equal(securityDirectiveHasTarget(dir("   ", null)), false);
    assert.equal(securityDirectiveIsPermanent(dir("n1", null)), true);
    assert.equal(securityDirectiveIsPermanent(dir("n1", 1000)), false);
  });
});

// ---------------------------------------------------------------------------
// Telemetry
// ---------------------------------------------------------------------------

describe("NullAetherTelemetry", () => {
  it("returns a no-op disposable and never throws", () => {
    const obs = new CollectObserver();
    const handle = NullAetherTelemetry.instance.subscribe(obs);
    handle.dispose();
    handle.dispose(); // idempotent
    assert.equal(obs.nodes.length, 0);
  });
});

describe("InMemoryAetherTelemetry", () => {
  it("fans out to all subscribers", () => {
    const hub = new InMemoryAetherTelemetry();
    const a = new CollectObserver();
    const b = new CollectObserver();
    hub.subscribe(a);
    hub.subscribe(b);
    assert.equal(hub.subscriberCount, 2);

    hub.publishNode(nodeEvt(AetherNodeEventKind.Joined));
    hub.publishSecurity(secEvt(AetherSecurityEventKind.IntrusionSignal, AetherThreatLevel.Critical));
    assert.equal(a.nodes.length, 1);
    assert.equal(b.nodes.length, 1);
    assert.equal(a.security.length, 1);
    assert.equal(b.security.length, 1);
  });

  it("dispose unsubscribes exactly one observer", () => {
    const hub = new InMemoryAetherTelemetry();
    const a = new CollectObserver();
    const b = new CollectObserver();
    const ha = hub.subscribe(a);
    hub.subscribe(b);
    ha.dispose();
    assert.equal(hub.subscriberCount, 1);
    hub.publishNode(nodeEvt(AetherNodeEventKind.Joined));
    assert.equal(a.nodes.length, 0);
    assert.equal(b.nodes.length, 1);
  });

  it("survives a subscriber disposing inside its own callback (snapshot-safe)", () => {
    const hub = new InMemoryAetherTelemetry();
    let handleRef: { dispose(): void } | null = null;
    const selfRemover: IAetherTelemetryObserver = {
      onNodeEvent() {
        handleRef?.dispose();
      },
      onSecurityEvent() {},
      onTransportEvent() {},
      onRouteEvent() {},
      onNetworkEvent() {},
    };
    const tail = new CollectObserver();
    handleRef = hub.subscribe(selfRemover);
    hub.subscribe(tail);
    // Must not throw; tail must still receive the event.
    hub.publishNode(nodeEvt(AetherNodeEventKind.Joined));
    assert.equal(tail.nodes.length, 1);
    assert.equal(hub.subscriberCount, 1);
  });
});

// ---------------------------------------------------------------------------
// Version + Context
// ---------------------------------------------------------------------------

describe("AetherVersion", () => {
  it("compares component-by-component", () => {
    assert.ok(compareAetherVersion(aetherVersion(2, 6), aetherVersion(2, 5)) > 0);
    assert.ok(compareAetherVersion(aetherVersion(2, 5), aetherVersion(2, 5)) === 0);
    assert.ok(compareAetherVersion(aetherVersion(1, 9), aetherVersion(2, 0)) < 0);
  });
});

describe("InMemoryAetherContext", () => {
  it("App level, enabled → available, no auth needed", () => {
    const ctx = new InMemoryAetherContext({ installLevel: AetherInstallLevel.App, runtimeVersion: aetherVersion(2, 6) });
    assert.equal(ctx.isAvailable, true);
    assert.equal(ctx.isEnabled, true);
    assert.equal(ctx.requiresAuth, false);
    assert.equal(ctx.isSufficient, true); // null minimum → always sufficient
  });

  it("None level → not available", () => {
    const ctx = new InMemoryAetherContext({ installLevel: AetherInstallLevel.None });
    assert.equal(ctx.isAvailable, false);
  });

  it("disabled OS instance → not available but requiresAuth", () => {
    const ctx = new InMemoryAetherContext({ installLevel: AetherInstallLevel.OS, isEnabled: false });
    assert.equal(ctx.requiresAuth, true);
    assert.equal(ctx.isAvailable, false);
  });

  it("isSufficient honours the minimum version", () => {
    const ok = new InMemoryAetherContext({ runtimeVersion: aetherVersion(2, 6), minimumRequired: aetherVersion(2, 5) });
    assert.equal(ok.isSufficient, true);
    const low = new InMemoryAetherContext({ runtimeVersion: aetherVersion(2, 4), minimumRequired: aetherVersion(2, 5) });
    assert.equal(low.isSufficient, false);
    const absent = new InMemoryAetherContext({ runtimeVersion: null, minimumRequired: aetherVersion(2, 5) });
    assert.equal(absent.isSufficient, false);
  });
});

// ---------------------------------------------------------------------------
// Auth challenge
// ---------------------------------------------------------------------------

describe("AuthChallengeResult", () => {
  it("success / failure factories", () => {
    const s = AuthChallengeResult.success(AuthMethod.BiometricAndDeviceAdmin);
    assert.equal(s.succeeded, true);
    assert.equal(s.failureReason, null);
    assert.equal(s.methodUsed, AuthMethod.BiometricAndDeviceAdmin);
    const f = AuthChallengeResult.failure(AuthMethod.Biometric, "nope");
    assert.equal(f.succeeded, false);
    assert.equal(f.failureReason, "nope");
  });
});

describe("InMemoryAuthChallenge", () => {
  it("defaults the minimum to BiometricAndDeviceAdmin when null", async () => {
    const auth = new InMemoryAuthChallenge(AuthMethod.BiometricAndDeviceAdmin);
    const r = await auth.challengeAsync(AuthChallengeReason.PrivilegedOperation, null, "confirm");
    assert.equal(r.succeeded, true);
    assert.equal(r.methodUsed, AuthMethod.BiometricAndDeviceAdmin);
    assert.equal(auth.issued[0].minimum, AuthMethod.BiometricAndDeviceAdmin);
  });

  it("fails when the available method is weaker than the requested minimum", async () => {
    const auth = new InMemoryAuthChallenge(AuthMethod.Biometric); // only biometric
    const r = await auth.challengeAsync(AuthChallengeReason.PrivilegedOperation, AuthMethod.BiometricAndDeviceAdmin, "x");
    assert.equal(r.succeeded, false);
    assert.match(r.failureReason ?? "", /weaker than required/);
  });

  it("OS toggle always enforces BiometricAndDeviceAdmin at minimum", async () => {
    const weak = new InMemoryAuthChallenge(AuthMethod.Biometric);
    const denied = await weak.requestOsToggleAsync(true);
    assert.equal(denied.succeeded, false);
    assert.equal(weak.issued[0].reason, AuthChallengeReason.OsLevelToggle);
    assert.equal(weak.issued[0].minimum, AuthMethod.BiometricAndDeviceAdmin);

    const strong = new InMemoryAuthChallenge(AuthMethod.BiometricAndDeviceAdmin);
    const ok = await strong.requestOsToggleAsync(false);
    assert.equal(ok.succeeded, true);
  });

  it("honours a scripted denyReason", async () => {
    const auth = new InMemoryAuthChallenge(AuthMethod.Custom, "user cancelled");
    const r = await auth.challengeAsync(AuthChallengeReason.ManualRequest, AuthMethod.Biometric, "x");
    assert.equal(r.succeeded, false);
    assert.equal(r.failureReason, "user cancelled");
  });
});

// ---------------------------------------------------------------------------
// InMemoryAetherIntelligence
// ---------------------------------------------------------------------------

describe("InMemoryAetherIntelligence", () => {
  it("empty mesh reports full health", async () => {
    const intel = new InMemoryAetherIntelligence();
    const h = await intel.getNetworkHealthAsync();
    assert.equal(h.overallScore, 1.0);
    assert.equal(h.trustedNodeCount, 0);
    assert.equal(h.summary, "No nodes observed.");
  });

  it("security events degrade trust and drive assessment + routing", async () => {
    const intel = new InMemoryAetherIntelligence();
    // Two Critical intrusion signals on n-bad: 0.15 * 3 = 0.45 each → 1.0 → 0.55 → 0.10
    intel.recordSecurityEvent(secEvt(AetherSecurityEventKind.IntrusionSignal, AetherThreatLevel.Critical, "n-bad"));
    intel.recordSecurityEvent(secEvt(AetherSecurityEventKind.IntrusionSignal, AetherThreatLevel.Critical, "n-bad"));

    const a = await intel.assessThreatAsync("n-bad");
    assert.equal(a.level, AetherThreatLevel.Critical); // score ~0.10 ≤ 0.25
    assert.ok(a.threatConfidence > 0.8);
    assert.ok(a.indicators.includes("intrusion-signal-detected"));
    assert.ok(a.indicators.includes("high-severity-event"));

    const advice = await intel.getRoutingAdviceAsync("n-bad");
    assert.deepEqual(advice.recommendedPath, []); // below avoid threshold
    assert.ok(advice.avoidNodes.includes("n-bad"));

    // Unknown node → full trust, direct path.
    const good = await intel.getRoutingAdviceAsync("n-good");
    assert.deepEqual(good.recommendedPath, ["n-good"]);
  });

  it("streams trust updates buffered before the reader attaches (unbounded)", async () => {
    const intel = new InMemoryAetherIntelligence();
    // Publish BEFORE subscribing — unbounded channel must retain these.
    intel.setTrust("n1", 0.4, "manual-a");
    intel.setTrust("n1", 0.2, "manual-b");

    const received: string[] = [];
    const controller = new AbortController();
    const consumer = (async () => {
      for await (const u of intel.streamTrustScoresAsync(controller.signal)) {
        received.push(u.reason);
        if (received.length === 2) {
          controller.abort();
          break;
        }
      }
    })();
    await consumer;
    assert.deepEqual(received, ["manual-a", "manual-b"]);
  });
});
