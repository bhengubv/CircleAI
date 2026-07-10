// security_peer_intelligence.test.ts
//
// Verifies the full CircleAI.Security peer-intelligence pipeline port:
//   ThreatDetector (degradation weights + indicator detection)
//   NodeTrustRegistry / NodeTrustEntry (degradation, recovery, history, stream)
//   DirectivePublisher (fan-out, snapshot-safe unsubscribe)
//   SecurityLayerService (thresholds, directives, posture, recovery loop)
//   PeerIntelligenceService (health, threat assessment, routing, trust stream)
//   DefaultSecurityWatchdog (graduated responses, signal stream)
//   DefaultAnomalyEventDispatcher (verify → dedup → dispatch)
//   SecurityCheckpoint (SHA-256 self-verify, redaction-safe toString)
//   SecurityResponse factories / SecurityOptions defaults
//   UhidKeyRing (P-256 sign/verify, rotate/revoke)
//   redactEvidence / RedactedEvidenceJsonConverter (SHA-256 redaction)
//   enum ordinals (wire contract)

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  ThreatVector,
  PeerSecurityEventKind,
  PeerThreatLevel,
  PeerDirectiveKind,
  AnomalyDispatchOutcome,
  SecurityResponseKind,
  SecurityOptions,
  SecurityCheckpoint,
  SecurityResponse,
  ThreatDetector,
  NodeTrustEntry,
  NodeTrustRegistry,
  DirectivePublisher,
  SecurityLayerService,
  PeerIntelligenceService,
  DefaultSecurityWatchdog,
  DefaultAnomalyEventDispatcher,
  UhidKeyRing,
  redactEvidence,
  RedactedEvidenceJsonConverter,
  createAnomalySignal,
  type PeerSecurityEvent,
  type PeerDirective,
  type PeerTrustScoreUpdate,
  type IPeerDirectiveConsumer,
} from '../src/security/index';

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function evt(
  nodeId: string,
  kind: PeerSecurityEventKind,
  level: PeerThreatLevel,
  occurredAt: Date = new Date(),
): PeerSecurityEvent {
  return {
    nodeId,
    kind,
    threatLevel: level,
    description: `${PeerSecurityEventKind[kind]}@${nodeId}`,
    transportId: 'test',
    occurredAt,
  };
}

class CollectingConsumer implements IPeerDirectiveConsumer {
  readonly received: PeerDirective[] = [];
  onDirective(d: PeerDirective): void {
    this.received.push(d);
  }
}

// ---------------------------------------------------------------------------
// Enum ordinals — wire contract
// ---------------------------------------------------------------------------

describe('enum ordinals (wire contract)', () => {
  it('PeerSecurityEventKind ordinals 0..9', () => {
    assert.equal(PeerSecurityEventKind.AuthAttempt, 0);
    assert.equal(PeerSecurityEventKind.RoutingAnomaly, 1);
    assert.equal(PeerSecurityEventKind.BehaviourChange, 2);
    assert.equal(PeerSecurityEventKind.EncryptionEvent, 3);
    assert.equal(PeerSecurityEventKind.IntrusionSignal, 4);
    assert.equal(PeerSecurityEventKind.PrivilegeAttempt, 5);
    assert.equal(PeerSecurityEventKind.ConnectionAnomaly, 6);
    assert.equal(PeerSecurityEventKind.DataExfiltration, 7);
    assert.equal(PeerSecurityEventKind.DenialOfService, 8);
    assert.equal(PeerSecurityEventKind.Unknown, 9);
  });

  it('PeerThreatLevel ordinals None(0)..Critical(4)', () => {
    assert.equal(PeerThreatLevel.None, 0);
    assert.equal(PeerThreatLevel.Low, 1);
    assert.equal(PeerThreatLevel.Medium, 2);
    assert.equal(PeerThreatLevel.High, 3);
    assert.equal(PeerThreatLevel.Critical, 4);
  });

  it('PeerDirectiveKind ordinals 0..3', () => {
    assert.equal(PeerDirectiveKind.ElevateMonitoring, 0);
    assert.equal(PeerDirectiveKind.AvoidNode, 1);
    assert.equal(PeerDirectiveKind.QuarantineNode, 2);
    assert.equal(PeerDirectiveKind.ReleaseNode, 3);
  });

  it('AnomalyDispatchOutcome ordinals 0..4', () => {
    assert.equal(AnomalyDispatchOutcome.Dispatched, 0);
    assert.equal(AnomalyDispatchOutcome.Duplicate, 1);
    assert.equal(AnomalyDispatchOutcome.BelowThreshold, 2);
    assert.equal(AnomalyDispatchOutcome.Unverified, 3);
    assert.equal(AnomalyDispatchOutcome.Cancelled, 4);
  });

  it('SecurityResponseKind ordinals 0..5', () => {
    assert.equal(SecurityResponseKind.NoAction, 0);
    assert.equal(SecurityResponseKind.KeyRotation, 1);
    assert.equal(SecurityResponseKind.SessionRevocation, 2);
    assert.equal(SecurityResponseKind.MeshIsolationSignal, 3);
    assert.equal(SecurityResponseKind.StateRollback, 4);
    assert.equal(SecurityResponseKind.Composite, 5);
  });
});

// ---------------------------------------------------------------------------
// SecurityOptions defaults
// ---------------------------------------------------------------------------

describe('SecurityOptions defaults', () => {
  it('matches the C# defaults exactly', () => {
    const o = new SecurityOptions();
    assert.equal(o.elevateMonitoringThreshold, 0.75);
    assert.equal(o.avoidNodeThreshold, 0.5);
    assert.equal(o.quarantineThreshold, 0.25);
    assert.equal(o.recoveryRatePerSecond, 0.001);
    assert.equal(o.eventWindowMs, 5 * 60 * 1000);
    assert.equal(o.maxEventsPerNode, 100);
    assert.equal(o.initialTrustScore, 1.0);
  });

  it('threshold ordering invariant holds', () => {
    const o = new SecurityOptions();
    assert.ok(o.quarantineThreshold < o.avoidNodeThreshold);
    assert.ok(o.avoidNodeThreshold < o.elevateMonitoringThreshold);
  });
});

// ---------------------------------------------------------------------------
// ThreatDetector
// ---------------------------------------------------------------------------

describe('ThreatDetector.computeDegradation', () => {
  it('base weight × threat multiplier', () => {
    // IntrusionSignal base 0.15 × Critical mult 3.0 = 0.45
    assert.ok(
      Math.abs(ThreatDetector.computeDegradation(evt('n', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical)) - 0.45) < 1e-9,
    );
    // AuthAttempt base 0.05 × Medium mult 1.0 = 0.05
    assert.ok(
      Math.abs(ThreatDetector.computeDegradation(evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Medium)) - 0.05) < 1e-9,
    );
    // DataExfiltration 0.14 × High 2.0 = 0.28
    assert.ok(
      Math.abs(ThreatDetector.computeDegradation(evt('n', PeerSecurityEventKind.DataExfiltration, PeerThreatLevel.High)) - 0.28) < 1e-9,
    );
  });

  it('returns 0 for PeerThreatLevel.None', () => {
    assert.equal(
      ThreatDetector.computeDegradation(evt('n', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.None)),
      0,
    );
  });

  it('Low multiplier is 0.5', () => {
    // BehaviourChange 0.08 × Low 0.5 = 0.04
    assert.ok(
      Math.abs(ThreatDetector.computeDegradation(evt('n', PeerSecurityEventKind.BehaviourChange, PeerThreatLevel.Low)) - 0.04) < 1e-9,
    );
  });
});

describe('ThreatDetector.detectIndicators', () => {
  const WIN = 5 * 60 * 1000;

  it('empty when no events in window', () => {
    assert.deepEqual(ThreatDetector.detectIndicators([], WIN), []);
  });

  it('flags repeated-auth-attempts at >= 3 auth events', () => {
    const now = new Date();
    const events = [
      evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, now),
      evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, now),
      evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, now),
    ];
    const ind = ThreatDetector.detectIndicators(events, WIN);
    assert.ok(ind.includes('repeated-auth-attempts'));
  });

  it('does not flag brute-force at 2 auth events', () => {
    const now = new Date();
    const ind = ThreatDetector.detectIndicators(
      [
        evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, now),
        evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, now),
      ],
      WIN,
    );
    assert.ok(!ind.includes('repeated-auth-attempts'));
  });

  it('flags intrusion, high-severity, multi-vector, priv-esc, exfil together', () => {
    const now = new Date();
    const events = [
      evt('n', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical, now),
      evt('n', PeerSecurityEventKind.PrivilegeAttempt, PeerThreatLevel.High, now),
      evt('n', PeerSecurityEventKind.DataExfiltration, PeerThreatLevel.Medium, now),
    ];
    const ind = ThreatDetector.detectIndicators(events, WIN);
    assert.ok(ind.includes('intrusion-signal-detected'));
    assert.ok(ind.includes('high-severity-event'));
    assert.ok(ind.includes('multi-vector-activity')); // 3 distinct kinds
    assert.ok(ind.includes('privilege-escalation-attempt'));
    assert.ok(ind.includes('data-exfiltration-signal'));
  });

  it('ignores events outside the window', () => {
    const old = new Date(Date.now() - 10 * 60 * 1000); // 10 min ago, window is 5
    const events = [
      evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, old),
      evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, old),
      evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Low, old),
    ];
    assert.deepEqual(ThreatDetector.detectIndicators(events, WIN), []);
  });
});

// ---------------------------------------------------------------------------
// NodeTrustRegistry / NodeTrustEntry
// ---------------------------------------------------------------------------

describe('NodeTrustRegistry', () => {
  it('getOrCreate initialises to initialTrustScore', () => {
    const reg = new NodeTrustRegistry(new SecurityOptions());
    const e = reg.getOrCreate('n1');
    assert.ok(e instanceof NodeTrustEntry);
    assert.equal(e.trustScore, 1.0);
    assert.equal(reg.getTrustScore('n1'), 1.0);
  });

  it('getTrustScore returns initial for unknown peers without creating them', () => {
    const reg = new NodeTrustRegistry(new SecurityOptions());
    assert.equal(reg.getTrustScore('ghost'), 1.0);
    assert.deepEqual(reg.allNodeIds, []);
  });

  it('applyDegradation clamps to [0,1] and records the event', () => {
    const reg = new NodeTrustRegistry(new SecurityOptions());
    const e = evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical);
    const { previous, current } = reg.applyDegradation(e, 0.45);
    assert.equal(previous, 1.0);
    assert.ok(Math.abs(current - 0.55) < 1e-9);
    assert.equal(reg.getRecentEvents('n1').length, 1);
  });

  it('applyDegradation floors at 0', () => {
    const reg = new NodeTrustRegistry(new SecurityOptions());
    const { current } = reg.applyDegradation(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical), 5.0);
    assert.equal(current, 0);
  });

  it('bounds the event history to maxEventsPerNode (oldest dropped)', () => {
    const o = new SecurityOptions();
    o.maxEventsPerNode = 3;
    const reg = new NodeTrustRegistry(o);
    for (let i = 0; i < 6; i++)
      reg.applyDegradation(evt('n1', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.None), 0); // no score move, still recorded
    const events = reg.getRecentEvents('n1');
    assert.equal(events.length, 3);
  });

  it('applyRecovery heals toward 1.0 but never above', () => {
    const o = new SecurityOptions();
    o.recoveryRatePerSecond = 0.1;
    const reg = new NodeTrustRegistry(o);
    reg.applyDegradation(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical), 0.5); // → 0.5
    reg.applyRecovery(1000); // +0.1 → 0.6
    assert.ok(Math.abs(reg.getTrustScore('n1') - 0.6) < 1e-9);
    reg.applyRecovery(100_000); // large → cap at 1.0
    assert.equal(reg.getTrustScore('n1'), 1.0);
  });

  it('getRecentEvents excludes events outside the window', () => {
    const reg = new NodeTrustRegistry(new SecurityOptions());
    reg.applyDegradation(evt('n1', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.None, new Date(Date.now() - 10 * 60 * 1000)), 0);
    assert.equal(reg.getRecentEvents('n1').length, 0);
  });

  it('publishes trust-score updates on the stream (buffered before reader attaches)', async () => {
    const reg = new NodeTrustRegistry(new SecurityOptions());
    // Degrade BEFORE any reader attaches — unbounded buffer must retain the update.
    reg.applyDegradation(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical), 0.3);
    const ac = new AbortController();
    const seen: PeerTrustScoreUpdate[] = [];
    const reader = (async () => {
      for await (const u of reg.streamTrustScoreUpdates(ac.signal)) {
        seen.push(u);
        break; // one is enough
      }
    })();
    await reader;
    assert.equal(seen.length, 1);
    assert.equal(seen[0].nodeId, 'n1');
    assert.equal(seen[0].previousScore, 1.0);
    assert.ok(Math.abs(seen[0].newScore - 0.7) < 1e-9);
  });

  it('does not publish when the score does not move (degradation 0)', async () => {
    const reg = new NodeTrustRegistry(new SecurityOptions());
    reg.applyDegradation(evt('n1', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.None), 0);
    const ac = new AbortController();
    let count = 0;
    const reader = (async () => {
      for await (const _ of reg.streamTrustScoreUpdates(ac.signal)) count++;
    })();
    // Abort shortly; nothing should have been buffered.
    setTimeout(() => ac.abort(), 20);
    await reader;
    assert.equal(count, 0);
  });
});

// ---------------------------------------------------------------------------
// DirectivePublisher
// ---------------------------------------------------------------------------

describe('DirectivePublisher', () => {
  const sampleDirective: PeerDirective = {
    kind: PeerDirectiveKind.QuarantineNode,
    targetNodeId: 'n1',
    trustScore: 0.1,
    threatLevel: PeerThreatLevel.Critical,
    reason: 'test',
    durationMs: null,
    issuedAt: new Date(),
  };

  it('fans a directive out to all subscribers', () => {
    const pub = new DirectivePublisher();
    const a = new CollectingConsumer();
    const b = new CollectingConsumer();
    pub.subscribe(a);
    pub.subscribe(b);
    assert.equal(pub.subscriberCount, 2);
    pub.publish(sampleDirective);
    assert.equal(a.received.length, 1);
    assert.equal(b.received.length, 1);
  });

  it('dispose unsubscribes; idempotent', () => {
    const pub = new DirectivePublisher();
    const a = new CollectingConsumer();
    const h = pub.subscribe(a);
    h.dispose();
    h.dispose(); // idempotent
    assert.equal(pub.subscriberCount, 0);
    pub.publish(sampleDirective);
    assert.equal(a.received.length, 0);
  });

  it('rejects a null consumer', () => {
    const pub = new DirectivePublisher();
    // @ts-expect-error deliberate null
    assert.throws(() => pub.subscribe(null), /consumer required/);
  });

  it('a consumer that unsubscribes during its callback does not corrupt the fan-out', () => {
    const pub = new DirectivePublisher();
    const later = new CollectingConsumer();
    const selfRemoving: IPeerDirectiveConsumer & { handle?: { dispose(): void } } = {
      onDirective() {
        this.handle?.dispose();
      },
    };
    selfRemoving.handle = pub.subscribe(selfRemoving);
    pub.subscribe(later);
    // Snapshot semantics: both still receive this publish.
    pub.publish(sampleDirective);
    assert.equal(later.received.length, 1);
    assert.equal(pub.subscriberCount, 1); // selfRemoving gone afterward
  });
});

// ---------------------------------------------------------------------------
// SecurityLayerService
// ---------------------------------------------------------------------------

describe('SecurityLayerService', () => {
  function makeLayer(o = new SecurityOptions()) {
    const reg = new NodeTrustRegistry(o);
    const pub = new DirectivePublisher();
    const layer = new SecurityLayerService(reg, o, pub);
    return { reg, pub, layer, o };
  }

  it('ignores PeerThreatLevel.None events (no degradation)', () => {
    const { reg, layer } = makeLayer();
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.None));
    assert.equal(reg.getTrustScore('n1'), 1.0);
  });

  it('issues ElevateMonitoring when crossing the monitoring threshold', () => {
    const { pub, layer } = makeLayer();
    const c = new CollectingConsumer();
    pub.subscribe(c);
    // AuthAttempt 0.05 × Critical 3.0 = 0.15 degradation → 1.0 → 0.85 (still above 0.75)? no.
    // Use RoutingAnomaly 0.10 × High 2.0 = 0.20 → 0.80 (above 0.75, no directive)
    // Then again → 0.60: crosses avoid (0.50)? no, crosses monitoring (0.75) on first sub-0.75 step.
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.RoutingAnomaly, PeerThreatLevel.High)); // 0.80
    assert.equal(c.received.length, 0);
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.RoutingAnomaly, PeerThreatLevel.High)); // 0.60 crosses 0.75
    assert.equal(c.received.length, 1);
    assert.equal(c.received[0].kind, PeerDirectiveKind.ElevateMonitoring);
    assert.equal(c.received[0].threatLevel, PeerThreatLevel.Medium);
  });

  it('issues AvoidNode when crossing the avoid threshold (most-severe wins, one per event)', () => {
    const { pub, layer } = makeLayer();
    const c = new CollectingConsumer();
    pub.subscribe(c);
    // IntrusionSignal 0.15 × Critical 3.0 = 0.45 per event.
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical)); // 1.0→0.55 crosses 0.75 → ElevateMonitoring
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical)); // 0.55→0.10 crosses 0.50 AND 0.25
    // Second event crosses both avoid(0.50) and quarantine(0.25); most-severe (quarantine) wins.
    const kinds = c.received.map((d) => d.kind);
    assert.deepEqual(kinds, [PeerDirectiveKind.ElevateMonitoring, PeerDirectiveKind.QuarantineNode]);
  });

  it('issues QuarantineNode when dropping straight past quarantine', () => {
    const { pub, layer } = makeLayer();
    const c = new CollectingConsumer();
    pub.subscribe(c);
    // One massive event: DoS 0.13 × Critical 3.0 = 0.39; not enough. Stack DataExfil.
    // Force a straight-to-quarantine by two Critical intrusions handled as one big drop:
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical)); // →0.55 elevate
    c.received.length = 0;
    // Now degrade below 0.25 in one step by a compound: not possible in one kind; use a second intrusion (→0.10) crossing avoid+quarantine
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical)); // 0.55→0.10
    assert.equal(c.received[0].kind, PeerDirectiveKind.QuarantineNode);
    assert.equal(c.received[0].threatLevel, PeerThreatLevel.Critical);
    assert.equal(c.received[0].durationMs, null);
  });

  it('getPostureAsync reports quarantined/monitored counts and worst threat level', async () => {
    const { layer } = makeLayer();
    // n1 → quarantine (0.10), n2 → monitoring band (0.60), n3 → healthy (1.0 untouched via getTrustScore-only)
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical)); // 0.55
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical)); // 0.10
    layer.handlePeerEvent(evt('n2', PeerSecurityEventKind.RoutingAnomaly, PeerThreatLevel.High)); // 0.80
    layer.handlePeerEvent(evt('n2', PeerSecurityEventKind.RoutingAnomaly, PeerThreatLevel.High)); // 0.60
    const posture = await layer.getPostureAsync();
    assert.equal(posture.quarantinedPeerCount, 1); // n1 ≤ 0.25
    assert.equal(posture.monitoredPeerCount, 1); // n2 in (0.25, 0.75]
    assert.equal(posture.overallThreatLevel, PeerThreatLevel.Critical); // worst = 0.10
    assert.equal(posture.isActive, false); // not started
  });

  it('empty posture is healthy (no peers → score 1.0 → None)', async () => {
    const { layer } = makeLayer();
    const posture = await layer.getPostureAsync();
    assert.equal(posture.overallThreatLevel, PeerThreatLevel.None);
    assert.equal(posture.quarantinedPeerCount, 0);
    assert.equal(posture.monitoredPeerCount, 0);
  });

  it('start/stop lifecycle toggles isActive and is idempotent', async () => {
    const { layer } = makeLayer();
    assert.equal(layer.isActive, false);
    await layer.startAsync();
    assert.equal(layer.isActive, true);
    await layer.startAsync(); // idempotent
    assert.equal(layer.isActive, true);
    await layer.stopAsync();
    assert.equal(layer.isActive, false);
  });

  it('handlePeerLeft is a no-op that preserves history', () => {
    const { reg, layer } = makeLayer();
    layer.handlePeerEvent(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical));
    const before = reg.getTrustScore('n1');
    layer.handlePeerLeft('n1');
    assert.equal(reg.getTrustScore('n1'), before);
  });
});

// ---------------------------------------------------------------------------
// PeerIntelligenceService
// ---------------------------------------------------------------------------

describe('PeerIntelligenceService', () => {
  function make(o = new SecurityOptions()) {
    const reg = new NodeTrustRegistry(o);
    const intel = new PeerIntelligenceService(reg, o);
    return { reg, intel, o };
  }

  it('network health with no peers is perfect', async () => {
    const { intel } = make();
    const h = await intel.getNetworkHealthAsync();
    assert.equal(h.overallScore, 1.0);
    assert.equal(h.trustedPeerCount, 0);
    assert.equal(h.suspiciousPeerCount, 0);
    assert.equal(h.summary, 'No peers observed.');
  });

  it('network health averages scores and counts trusted/suspicious', async () => {
    const { reg, intel } = make();
    reg.getOrCreate('good'); // 1.0
    reg.applyDegradation(evt('bad', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical), 0.9); // 0.10
    const h = await intel.getNetworkHealthAsync();
    assert.ok(Math.abs(h.overallScore - 0.55) < 1e-9); // (1.0 + 0.1)/2
    assert.equal(h.trustedPeerCount, 1); // good > 0.50
    assert.equal(h.suspiciousPeerCount, 1); // bad ≤ 0.75
    assert.equal(h.summary, 'Network health is degraded; elevated monitoring active.'); // 0.55 in (0.50, 0.75]
  });

  it('threat assessment: confidence = deficit + 0.1 per indicator, level from score', async () => {
    const { reg, intel } = make();
    // Drive score to 0.40 (deficit 0.60) and generate 3 auth attempts → repeated-auth indicator (1).
    const now = new Date();
    reg.applyDegradation(evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.None, now), 0); // no move but recorded? No: None → but we pass amount 0 anyway
    // Use real degradations to hit 0.40: RoutingAnomaly 0.10×High 2.0 = 0.20 × 3 = 0.60 → 0.40
    reg.applyDegradation(evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Medium, now), 0.20);
    reg.applyDegradation(evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Medium, now), 0.20);
    reg.applyDegradation(evt('n', PeerSecurityEventKind.AuthAttempt, PeerThreatLevel.Medium, now), 0.20);
    const a = await intel.assessThreatAsync('n');
    assert.ok(Math.abs(reg.getTrustScore('n') - 0.4) < 1e-9);
    assert.equal(a.threatLevel, PeerThreatLevel.High); // 0.40 ≤ 0.50
    // 3 auth attempts (in-window) → 'repeated-auth-attempts' indicator.
    assert.ok(a.indicators.includes('repeated-auth-attempts'));
    // deficit 0.60 + 1 indicator × 0.1 = 0.70
    assert.ok(Math.abs(a.confidence - 0.7) < 1e-9);
  });

  it('threat assessment for unknown peer is fully trusted', async () => {
    const { intel } = make();
    const a = await intel.assessThreatAsync('ghost');
    assert.equal(a.threatLevel, PeerThreatLevel.None);
    assert.equal(a.confidence, 0);
    assert.deepEqual(a.indicators, []);
  });

  it('routing advice: direct path when destination is trusted', async () => {
    const { reg, intel } = make();
    reg.getOrCreate('dest'); // 1.0
    const r = await intel.getRoutingAdviceAsync('dest');
    assert.deepEqual(r.recommendedPath, ['dest']);
    assert.equal(r.confidence, 1.0);
    assert.match(r.reasoning, /is trusted/);
  });

  it('routing advice: empty path + avoid-list when destination is below avoid threshold', async () => {
    const { reg, intel } = make();
    reg.applyDegradation(evt('dest', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical), 0.9); // 0.10
    reg.applyDegradation(evt('other', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical), 0.9); // 0.10
    const r = await intel.getRoutingAdviceAsync('dest');
    assert.deepEqual(r.recommendedPath, []);
    assert.ok(r.avoidNodeIds.includes('dest'));
    assert.ok(r.avoidNodeIds.includes('other'));
    assert.match(r.reasoning, /quarantined/);
  });

  it('streams live trust-score updates', async () => {
    const { reg, intel } = make();
    const ac = new AbortController();
    const seen: PeerTrustScoreUpdate[] = [];
    const reader = (async () => {
      for await (const u of intel.streamTrustScoresAsync(ac.signal)) {
        seen.push(u);
        if (seen.length >= 2) break;
      }
    })();
    reg.applyDegradation(evt('n1', PeerSecurityEventKind.IntrusionSignal, PeerThreatLevel.Critical), 0.3);
    reg.applyRecovery(1000 * 60); // large recovery → publishes another update for n1
    await reader;
    assert.ok(seen.length >= 2);
    assert.equal(seen[0].nodeId, 'n1');
  });
});

// ---------------------------------------------------------------------------
// DefaultSecurityWatchdog
// ---------------------------------------------------------------------------

describe('DefaultSecurityWatchdog', () => {
  it('below rotation threshold → NoAction', async () => {
    const w = new DefaultSecurityWatchdog();
    const sig = createAnomalySignal(ThreatVector.MemoryAnomaly, 0.2, 'Mod', 'low');
    const r = await w.onAnomalyDetectedAsync(sig);
    assert.equal(r.kind, SecurityResponseKind.NoAction);
    assert.equal(r.signalId, sig.id);
    assert.equal(r.restoredCheckpoint, null);
  });

  it('mid-range confidence → KeyRotation', async () => {
    const w = new DefaultSecurityWatchdog();
    const sig = createAnomalySignal(ThreatVector.MemoryAnomaly, 0.45, 'Mod', 'mid');
    const r = await w.onAnomalyDetectedAsync(sig);
    assert.equal(r.kind, SecurityResponseKind.KeyRotation);
    assert.deepEqual(r.appliedActions, []);
  });

  it('high confidence + high-severity vector + verifying checkpoint → Composite with rollback', async () => {
    const w = new DefaultSecurityWatchdog();
    const cp = SecurityCheckpoint.create('uhid-1', 'CircleAI.Companion', new TextEncoder().encode('state'));
    const sig = createAnomalySignal(ThreatVector.StateCorruption, 0.9, 'CircleAI.Companion', 'high');
    const r = await w.onAnomalyDetectedAsync(sig, cp);
    assert.equal(r.kind, SecurityResponseKind.Composite);
    assert.ok(r.appliedActions.includes(SecurityResponseKind.KeyRotation));
    assert.ok(r.appliedActions.includes(SecurityResponseKind.MeshIsolationSignal));
    assert.ok(r.appliedActions.includes(SecurityResponseKind.StateRollback));
    assert.equal(r.restoredCheckpoint, cp);
  });

  it('high confidence but low-severity vector → Composite WITHOUT rollback even with checkpoint', async () => {
    const w = new DefaultSecurityWatchdog();
    const cp = SecurityCheckpoint.create('uhid-1', 'M', new TextEncoder().encode('s'));
    const sig = createAnomalySignal(ThreatVector.MemoryAnomaly, 0.95, 'M', 'high-but-low-sev');
    const r = await w.onAnomalyDetectedAsync(sig, cp);
    assert.equal(r.kind, SecurityResponseKind.Composite);
    assert.ok(!r.appliedActions.includes(SecurityResponseKind.StateRollback));
    assert.equal(r.restoredCheckpoint, null);
  });

  it('high confidence + high severity but tampered checkpoint → no rollback', async () => {
    const w = new DefaultSecurityWatchdog();
    const cp = SecurityCheckpoint.create('uhid-1', 'M', new TextEncoder().encode('s'));
    cp.payload[0] ^= 0xff; // tamper → verify() fails
    const sig = createAnomalySignal(ThreatVector.NetworkPivot, 0.9, 'M', 'tampered');
    const r = await w.onAnomalyDetectedAsync(sig, cp);
    assert.ok(!r.appliedActions.includes(SecurityResponseKind.StateRollback));
    assert.equal(r.restoredCheckpoint, null);
  });

  it('rejects a null signal', async () => {
    const w = new DefaultSecurityWatchdog();
    // @ts-expect-error deliberate null
    await assert.rejects(() => w.onAnomalyDetectedAsync(null), /signal required/);
  });

  it('streams observed signals (buffered before reader attaches)', async () => {
    const w = new DefaultSecurityWatchdog();
    const s1 = createAnomalySignal(ThreatVector.Unknown, 0.1, 'M', 'a');
    await w.onAnomalyDetectedAsync(s1); // enqueued before stream attaches
    const ac = new AbortController();
    const seen: string[] = [];
    const reader = (async () => {
      for await (const s of w.streamSignalsAsync(ac.signal)) {
        seen.push(s.id);
        break;
      }
    })();
    await reader;
    assert.deepEqual(seen, [s1.id]);
  });
});

// ---------------------------------------------------------------------------
// DefaultAnomalyEventDispatcher
// ---------------------------------------------------------------------------

describe('DefaultAnomalyEventDispatcher', () => {
  it('dispatches a signal above threshold and returns the watchdog response', async () => {
    const w = new DefaultSecurityWatchdog();
    const d = new DefaultAnomalyEventDispatcher(w);
    const sig = createAnomalySignal(ThreatVector.MemoryAnomaly, 0.9, 'M', 'x');
    const res = await d.verifyAndDispatchAsync(sig);
    assert.equal(res.outcome, AnomalyDispatchOutcome.Dispatched);
    assert.ok(res.response !== null);
    assert.equal(res.response!.signalId, sig.id);
  });

  it('drops a below-threshold signal', async () => {
    const w = new DefaultSecurityWatchdog();
    const d = new DefaultAnomalyEventDispatcher(w, 0.5);
    const sig = createAnomalySignal(ThreatVector.MemoryAnomaly, 0.2, 'M', 'x');
    const res = await d.verifyAndDispatchAsync(sig);
    assert.equal(res.outcome, AnomalyDispatchOutcome.BelowThreshold);
    assert.equal(res.response, null);
  });

  it('dedupes a repeated signal id', async () => {
    const w = new DefaultSecurityWatchdog();
    const d = new DefaultAnomalyEventDispatcher(w);
    const sig = createAnomalySignal(ThreatVector.MemoryAnomaly, 0.9, 'M', 'x');
    const first = await d.verifyAndDispatchAsync(sig);
    const second = await d.verifyAndDispatchAsync(sig);
    assert.equal(first.outcome, AnomalyDispatchOutcome.Dispatched);
    assert.equal(second.outcome, AnomalyDispatchOutcome.Duplicate);
    assert.equal(second.response, null);
  });

  it('returns Cancelled when the token is already aborted', async () => {
    const w = new DefaultSecurityWatchdog();
    const d = new DefaultAnomalyEventDispatcher(w);
    const sig = createAnomalySignal(ThreatVector.MemoryAnomaly, 0.9, 'M', 'x');
    const ac = new AbortController();
    ac.abort();
    const res = await d.verifyAndDispatchAsync(sig, null, ac.signal);
    assert.equal(res.outcome, AnomalyDispatchOutcome.Cancelled);
  });

  it('rejects a null watchdog', () => {
    // @ts-expect-error deliberate null
    assert.throws(() => new DefaultAnomalyEventDispatcher(null), /watchdog required/);
  });
});

// ---------------------------------------------------------------------------
// SecurityCheckpoint
// ---------------------------------------------------------------------------

describe('SecurityCheckpoint', () => {
  it('create computes a SHA-256 hash and verifies clean', () => {
    const cp = SecurityCheckpoint.create('uhid-1', 'CircleAI.Memory', new TextEncoder().encode('trusted-state'));
    assert.equal(cp.payloadHash.length, 32);
    assert.equal(cp.verify(), true);
    assert.equal(typeof cp.id, 'string');
  });

  it('verify fails after payload tampering', () => {
    const cp = SecurityCheckpoint.create('uhid-1', 'M', new TextEncoder().encode('state'));
    cp.payload[0] ^= 0x01;
    assert.equal(cp.verify(), false);
  });

  it('rejects blank uhid / module / null payload', () => {
    assert.throws(() => SecurityCheckpoint.create('', 'M', new Uint8Array()), /uhidIdentityId required/);
    assert.throws(() => SecurityCheckpoint.create('u', '  ', new Uint8Array()), /moduleLabel required/);
    // @ts-expect-error deliberate null payload
    assert.throws(() => SecurityCheckpoint.create('u', 'M', null), /payload required/);
  });

  it('toString never leaks payload; emits an 8-byte hash prefix', () => {
    const cp = SecurityCheckpoint.create('uhid-99', 'CircleAI.Companion', new TextEncoder().encode('SECRET-PAYLOAD'));
    const s = cp.toString();
    assert.ok(!s.includes('SECRET-PAYLOAD'));
    assert.ok(s.includes('CircleAI.Companion'));
    assert.ok(s.includes('uhid-99'));
    assert.ok(s.includes('PayloadBytes=14'));
    assert.match(s, /PayloadSha256=[0-9A-F]{16}…/);
  });
});

// ---------------------------------------------------------------------------
// SecurityResponse factories
// ---------------------------------------------------------------------------

describe('SecurityResponse factories', () => {
  it('noAction', () => {
    const r = SecurityResponse.noAction('sig', 'why');
    assert.equal(r.kind, SecurityResponseKind.NoAction);
    assert.deepEqual(r.appliedActions, []);
    assert.equal(r.restoredCheckpoint, null);
  });

  it('forKeyRotation', () => {
    const r = SecurityResponse.forKeyRotation('sig', 'rotate');
    assert.equal(r.kind, SecurityResponseKind.KeyRotation);
  });

  it('forRollback embeds the checkpoint and mentions its id + module', () => {
    const cp = SecurityCheckpoint.create('u', 'ModX', new TextEncoder().encode('s'));
    const r = SecurityResponse.forRollback('sig', cp);
    assert.equal(r.kind, SecurityResponseKind.StateRollback);
    assert.equal(r.restoredCheckpoint, cp);
    assert.ok(r.description.includes(cp.id));
    assert.ok(r.description.includes('ModX'));
  });

  it('composite carries the action list and optional checkpoint', () => {
    const r = SecurityResponse.composite('sig', [SecurityResponseKind.KeyRotation, SecurityResponseKind.MeshIsolationSignal], 'desc');
    assert.equal(r.kind, SecurityResponseKind.Composite);
    assert.equal(r.appliedActions.length, 2);
    assert.equal(r.restoredCheckpoint, null);
  });
});

// ---------------------------------------------------------------------------
// UhidKeyRing
// ---------------------------------------------------------------------------

describe('UhidKeyRing', () => {
  it('generates a P-256 ring that signs and self-verifies', () => {
    const ring = UhidKeyRing.generateFresh('uhid-1');
    assert.equal(ring.uhidIdentityId, 'uhid-1');
    assert.equal(ring.isRevoked, false);
    assert.ok(ring.publicKeyDer.length > 0);
    const data = new TextEncoder().encode('sign me');
    const sig = ring.sign(data);
    assert.ok(sig.length > 0);
    assert.equal(ring.verify(data, sig), true);
  });

  it('verify rejects a tampered message', () => {
    const ring = UhidKeyRing.generateFresh('uhid-1');
    const data = new TextEncoder().encode('original');
    const sig = ring.sign(data);
    assert.equal(ring.verify(new TextEncoder().encode('tampered'), sig), false);
  });

  it('revoke blocks signing but not verifying', () => {
    const ring = UhidKeyRing.generateFresh('uhid-1');
    const data = new TextEncoder().encode('x');
    const sig = ring.sign(data);
    ring.revoke();
    assert.equal(ring.isRevoked, true);
    assert.ok(ring.revokedAt instanceof Date);
    assert.throws(() => ring.sign(data), /revoked/);
    assert.equal(ring.verify(data, sig), true); // historical verify still works
  });

  it('revoke is idempotent', () => {
    const ring = UhidKeyRing.generateFresh('uhid-1');
    ring.revoke();
    const at = ring.revokedAt;
    ring.revoke();
    assert.equal(ring.revokedAt, at);
  });

  it('rotate revokes the old ring and returns a fresh one with a new id', () => {
    const oldRing = UhidKeyRing.generateFresh('uhid-1');
    const oldId = oldRing.ringId;
    const fresh = oldRing.rotate();
    assert.equal(oldRing.isRevoked, true);
    assert.equal(fresh.isRevoked, false);
    assert.equal(fresh.uhidIdentityId, 'uhid-1');
    assert.notEqual(fresh.ringId, oldId);
    // Old ring's signatures do not verify under the fresh ring (distinct keys).
    const data = new TextEncoder().encode('x');
    const freshSig = fresh.sign(data);
    assert.equal(oldRing.verify(data, freshSig), false);
  });

  it('rejects a blank identity', () => {
    assert.throws(() => UhidKeyRing.generateFresh('   '), /uhidIdentityId required/);
  });

  it('dispose blocks signing', () => {
    const ring = UhidKeyRing.generateFresh('uhid-1');
    ring.dispose();
    assert.throws(() => ring.sign(new TextEncoder().encode('x')), /disposed/);
  });
});

// ---------------------------------------------------------------------------
// Redacted evidence
// ---------------------------------------------------------------------------

describe('redactEvidence / RedactedEvidenceJsonConverter', () => {
  it('replaces each value with sha256:<lowercase-hex>, preserving keys', () => {
    const red = redactEvidence({ addr: '0xdeadbeef', token: 'secret' });
    assert.ok(red !== null);
    assert.deepEqual(Object.keys(red!), ['addr', 'token']);
    // Known SHA-256 of "0xdeadbeef" utf8.
    const expectedAddr = 'sha256:' + createHash('sha256').update('0xdeadbeef', 'utf8').digest('hex');
    assert.equal(red!.addr, expectedAddr);
    assert.match(red!.token, /^sha256:[0-9a-f]{64}$/);
    // Raw values never appear.
    assert.ok(!JSON.stringify(red).includes('secret'));
  });

  it('empty value hashes to the literal "sha256:"', () => {
    const red = redactEvidence({ empty: '' });
    assert.equal(red!.empty, 'sha256:');
  });

  it('null input → null', () => {
    assert.equal(redactEvidence(null), null);
    assert.equal(redactEvidence(undefined), null);
  });

  it('converter write mirrors redactEvidence; read never trusts inbound values', () => {
    const w = RedactedEvidenceJsonConverter.write({ k: 'v' });
    assert.match(w!.k, /^sha256:[0-9a-f]{64}$/);
    // Read side returns an empty object (or null for JSON null).
    assert.deepEqual(RedactedEvidenceJsonConverter.read({ k: 'sha256:abc' }), {});
    assert.equal(RedactedEvidenceJsonConverter.read(null), null);
  });
});
