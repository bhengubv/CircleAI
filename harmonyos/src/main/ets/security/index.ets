/**
 * security/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// security/index.ts
// Circle AI security layer — full parity port of CircleAI.Security (C#).
//
// This module ports the entire CircleAI.Security peer-intelligence pipeline
// faithfully and idiomatically to TypeScript. C# is the exact spec.
//
// Covers (C# → TS):
//   ThreatVector                     — local runtime anomaly classification (ordinals 0..7)
//   AnomalySignal + createAnomalySignal — immutable detected-anomaly record + factory
//   PeerSecurityEventKind            — transport-neutral peer event category (ordinals 0..9)
//   PeerThreatLevel                  — severity None(0)..Critical(4)
//   PeerDirectiveKind                — recommended action (ordinals 0..3)
//   AnomalyDispatchOutcome           — dispatcher outcome (ordinals 0..4)
//   SecurityResponseKind             — protective-action classification (ordinals 0..5)
//   PeerSecurityEvent / PeerDirective / PeerTrustScoreUpdate / PeerSecurityPosture /
//   PeerNetworkHealthReport / PeerThreatAssessment / PeerRoutingAdvice — records
//   SecurityResponse (+ factories)   — action taken by the watchdog
//   AnomalyDispatchResult            — dispatcher result record
//   SecurityOptions                  — trust thresholds / decay / retention
//   SecurityCheckpoint               — SHA-256 self-verifying state snapshot
//   ThreatDetector                   — pure static threat logic (degradation + indicators)
//   NodeTrustEntry / NodeTrustRegistry — per-peer trust store + live update channel
//   DirectivePublisher               — fan-out of PeerDirective to consumers
//   redactEvidence / RedactedEvidenceJsonConverter — SHA-256-redacted evidence serialisation
//   UhidKeyRing                      — ephemeral ECDSA P-256 session key ring
//   IPeerDirectiveConsumer / IPeerSecurityLayer / IPeerIntelligence /
//   IPeerSecurityEventFeed / ISecurityWatchdog / IAnomalyEventDispatcher — contracts
//   SecurityLayerService             — IPeerSecurityLayer implementation (AISecurityLayerService.cs)
//   PeerIntelligenceService          — IPeerIntelligence implementation (AetherIntelligenceService.cs)
//   DefaultSecurityWatchdog          — in-process ISecurityWatchdog
//   DefaultAnomalyEventDispatcher    — verify → dedup → dispatch composer
//
// CRITICAL: every enum's ordinals are part of the wire/storage contract.
// Entries MUST stay in this exact declaration order so ordinals match the C#
// reference implementation and every other language port.
//
// Concurrency note: C# uses System.Threading.Channels (unbounded) for the
// watchdog signal stream and the trust-score update stream. TypeScript has no
// Channel primitive, so we reuse the AsyncQueue that reproduces the exact
// unbounded producer/consumer contract: TryWrite = enqueue (buffered even with
// no reader attached, so writes before a subscriber attaches are never lost),
// Reader.ReadAllAsync(ct) = drain(signal). DirectivePublisher fans out
// synchronously via a snapshot taken before the callbacks fire, so a consumer
// that unsubscribes inside its own callback cannot corrupt the iteration.

import { KeyObject, createHash, createSign, createVerify, generateKeyPairSync, randomUUID, timingSafeEqual } from "../platform";
import { AsyncQueue } from "../companion/herjarvis/async_queue";

// ─────────────────────────────────────────────────────────────────────────────
// ThreatVector — local runtime anomaly classification
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Classification of a locally-detected runtime threat or anomaly.
 *
 * Differs from {@link PeerSecurityEventKind} which classifies external peer
 * events; ThreatVector classifies threats observed in the LOCAL execution
 * environment. Ordinals are stable across language ports; new values must be
 * appended.
 */
export enum ThreatVector {
  /** Unexpected change in a runtime data structure's layout or content. */
  MemoryAnomaly = 0,
  /** Execution flow deviated from the expected call graph. */
  ControlFlowDrift = 1,
  /** A module or session attempted to acquire unauthorised capabilities. */
  PrivilegeEscalation = 2,
  /** A biometric authentication attempt was detected as spoofing or replay. */
  BiometricSpoofAttempt = 3,
  /** Lateral movement detected from a compromised mesh node. */
  NetworkPivot = 4,
  /** Core state vector mutated outside the trusted companion pipeline. */
  StateCorruption = 5,
  /** An agent-generated patch failed the BugHunter quality gate. */
  AgentPatchRejected = 6,
  /** Catch-all for anomalies that do not map to a specific vector. */
  Unknown = 7,
}

// ─────────────────────────────────────────────────────────────────────────────
// AnomalySignal
// ─────────────────────────────────────────────────────────────────────────────

/**
 * An immutable record describing a locally-detected runtime anomaly.
 * Created at the detection site and consumed by
 * {@link ISecurityWatchdog.onAnomalyDetectedAsync}.
 */
export interface AnomalySignal {
  /** Stable identifier (UUID v4). */
  readonly id: string;
  /** Classification of the detected threat. */
  readonly vector: ThreatVector;
  /** Confidence that this is a genuine threat, in [0.0, 1.0]. */
  readonly confidence: number;
  /** The module or subsystem where the anomaly was detected. */
  readonly affectedModule: string;
  /** Human-readable description of the anomaly. */
  readonly description: string;
  /** Structured evidence attached by the detection site. */
  readonly evidence: Readonly<Record<string, string>>;
  /** UTC timestamp of detection. */
  readonly detectedAt: Date;
}

/**
 * Creates a new {@link AnomalySignal} with a fresh UUID v4 id and a
 * `detectedAt` stamp of the current UTC time. `confidence` is clamped
 * to `[0.0, 1.0]`. `evidence` is defensively copied; when omitted, an
 * empty object is used. Mirrors `AnomalySignal.Create`.
 */
export function createAnomalySignal(
  vector: ThreatVector,
  confidence: number,
  affectedModule: string,
  description: string,
  evidence?: Record<string, string>,
): AnomalySignal {
  return {
    id: randomUUID(),
    vector,
    confidence: clamp(confidence, 0, 1),
    affectedModule,
    description,
    evidence: evidence ? { ...evidence } : {},
    detectedAt: new Date(),
  };
}

// ─────────────────────────────────────────────────────────────────────────────
// Transport-neutral peer security enumerations
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Transport-neutral classification of a peer security event.
 * Ordinals are part of the wire contract.
 */
export enum PeerSecurityEventKind {
  /** Authentication attempt (login, handshake, re-auth). */
  AuthAttempt = 0,
  /** Anomalous routing behaviour detected (loop, black-hole, etc.). */
  RoutingAnomaly = 1,
  /** Peer behaviour changed unexpectedly (rate, pattern, protocol). */
  BehaviourChange = 2,
  /** Encryption negotiation event (downgrade, cipher mismatch). */
  EncryptionEvent = 3,
  /** Active intrusion probe or exploitation attempt. */
  IntrusionSignal = 4,
  /** Privilege escalation or capability violation attempt. */
  PrivilegeAttempt = 5,
  /** Unusual connection pattern (port scan, rapid reconnect). */
  ConnectionAnomaly = 6,
  /** Suspected data exfiltration (volume, destination anomaly). */
  DataExfiltration = 7,
  /** Denial-of-service signal (flooding, resource exhaustion). */
  DenialOfService = 8,
  /** Catch-all for events that do not map to a specific category. */
  Unknown = 9,
}

/**
 * Severity level for a peer security event or threat assessment.
 * Values match the intuitive ordering: None is safest, Critical is worst.
 */
export enum PeerThreatLevel {
  /** No threat — event carries no security significance. */
  None = 0,
  /** Low-level anomaly — monitor but no action required. */
  Low = 1,
  /** Notable anomaly — elevated monitoring recommended. */
  Medium = 2,
  /** Significant threat — routing around the peer recommended. */
  High = 3,
  /** Active or confirmed attack — quarantine the peer. */
  Critical = 4,
}

/**
 * The action recommended by the security layer for a given peer.
 * Ordinals are part of the wire contract.
 */
export enum PeerDirectiveKind {
  /** Increase observation cadence; no traffic restriction yet. */
  ElevateMonitoring = 0,
  /** Exclude the peer from routing; still accept inbound connections. */
  AvoidNode = 1,
  /** Hard-block the peer — no traffic to or from it. */
  QuarantineNode = 2,
  /**
   * Lift a previous directive; the peer has recovered sufficient trust.
   * Not issued automatically — requires explicit operator action.
   */
  ReleaseNode = 3,
}

/** Outcome of a {@link IAnomalyEventDispatcher.verifyAndDispatchAsync} call. */
export enum AnomalyDispatchOutcome {
  /** Signal accepted; watchdog was invoked. */
  Dispatched = 0,
  /** Signal id was already seen — deduped silently. */
  Duplicate = 1,
  /** Confidence was below the configured threshold — ignored. */
  BelowThreshold = 2,
  /** Signal failed the origin/signature verification step. */
  Unverified = 3,
  /** Cancellation token tripped before dispatch. */
  Cancelled = 4,
}

/** The type of protective action taken in response to an {@link AnomalySignal}. */
export enum SecurityResponseKind {
  /** No action — confidence below threshold or vector is informational. */
  NoAction = 0,
  /** The session's ephemeral UHID key ring was regenerated; prior session keys revoked. */
  KeyRotation = 1,
  /** The affected session or execution sandbox was marked untrusted and isolated. */
  SessionRevocation = 2,
  /** A {@link PeerDirective} was issued to surrounding mesh nodes to isolate the origin. */
  MeshIsolationSignal = 3,
  /** State was rolled back to the most recent verified {@link SecurityCheckpoint}. */
  StateRollback = 4,
  /** A combination of responses was applied. See {@link SecurityResponse.appliedActions}. */
  Composite = 5,
}

// ─────────────────────────────────────────────────────────────────────────────
// Transport-agnostic peer security records
// ─────────────────────────────────────────────────────────────────────────────

/** One security incident observed on any transport. */
export interface PeerSecurityEvent {
  /** Stable identifier of the peer that generated the event. */
  readonly nodeId: string;
  /** Transport-neutral event category. */
  readonly kind: PeerSecurityEventKind;
  /** Assessed severity at the time of observation. */
  readonly threatLevel: PeerThreatLevel;
  /** Human-readable description of the event. */
  readonly description: string;
  /** Identifier for the transport that produced the event (e.g. "aether", "wifi"). */
  readonly transportId: string;
  /** UTC timestamp of the event. */
  readonly occurredAt: Date;
}

/**
 * A security directive issued to all registered {@link IPeerDirectiveConsumer}
 * subscribers when a peer's trust crosses a threshold.
 */
export interface PeerDirective {
  /** The recommended action. */
  readonly kind: PeerDirectiveKind;
  /** The peer to which the directive applies. */
  readonly targetNodeId: string;
  /** Current trust score of the peer at time of issue. */
  readonly trustScore: number;
  /** Threat level at time of issue. */
  readonly threatLevel: PeerThreatLevel;
  /** Human-readable explanation for the directive. */
  readonly reason: string;
  /**
   * Optional duration (milliseconds) after which the directive should be
   * re-evaluated. `null` means permanent until an explicit ReleaseNode directive.
   */
  readonly durationMs: number | null;
  /** UTC timestamp of issue. */
  readonly issuedAt: Date;
}

/**
 * Notification emitted by {@link NodeTrustRegistry} whenever a node's
 * trust score changes.
 */
export interface PeerTrustScoreUpdate {
  /** The peer whose score changed. */
  readonly nodeId: string;
  /** Score before this change. */
  readonly previousScore: number;
  /** Score after this change. */
  readonly newScore: number;
  /** Short description of the cause (event description or "passive-recovery"). */
  readonly reason: string;
  /** UTC timestamp of the change. */
  readonly changedAt: Date;
}

/** Snapshot of the overall security posture across all observed peers. */
export interface PeerSecurityPosture {
  /** Worst-case threat level in the current peer set. */
  readonly overallThreatLevel: PeerThreatLevel;
  /** Number of peers at or below {@link SecurityOptions.quarantineThreshold}. */
  readonly quarantinedPeerCount: number;
  /** Number of peers elevated beyond monitoring threshold but not yet quarantined. */
  readonly monitoredPeerCount: number;
  /** Whether the security layer is currently running. */
  readonly isActive: boolean;
  /** UTC timestamp of this snapshot. */
  readonly generatedAt: Date;
}

/** Aggregate network health across all observed peers. */
export interface PeerNetworkHealthReport {
  /** Average trust score [0.0, 1.0] across all peers. */
  readonly overallScore: number;
  /** Peers above {@link SecurityOptions.avoidNodeThreshold}. */
  readonly trustedPeerCount: number;
  /** Peers at or below {@link SecurityOptions.elevateMonitoringThreshold}. */
  readonly suspiciousPeerCount: number;
  /** Human-readable health summary. */
  readonly summary: string;
  /** UTC timestamp of this report. */
  readonly generatedAt: Date;
}

/** Per-peer threat assessment: confidence score, threat level, and detected indicators. */
export interface PeerThreatAssessment {
  /** The assessed peer. */
  readonly nodeId: string;
  /** Likelihood that the peer is a genuine threat [0.0, 1.0]. */
  readonly confidence: number;
  /** Classified severity. */
  readonly threatLevel: PeerThreatLevel;
  /** Human-readable indicator tags. */
  readonly indicators: readonly string[];
  /** UTC timestamp of this assessment. */
  readonly assessedAt: Date;
}

/** Trust-aware routing recommendation for reaching a destination peer. */
export interface PeerRoutingAdvice {
  /** The target peer. */
  readonly destinationNodeId: string;
  /** Ordered list of peer IDs forming the recommended path. Empty when no safe path. */
  readonly recommendedPath: readonly string[];
  /** Peers that should be excluded from routing. */
  readonly avoidNodeIds: readonly string[];
  /** Confidence in the recommendation [0.0, 1.0]. */
  readonly confidence: number;
  /** Human-readable explanation. */
  readonly reasoning: string;
  /** UTC timestamp of this advice. */
  readonly generatedAt: Date;
}

// ─────────────────────────────────────────────────────────────────────────────
// SecurityOptions
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Configures thresholds, decay rates, and event retention for the AI Security
 * Layer. All threshold values are trust scores in the [0, 1] range; lower means
 * more compromised. Thresholds must satisfy
 * `quarantineThreshold < avoidNodeThreshold < elevateMonitoringThreshold`.
 *
 * Field names mirror the C# `SecurityOptions` (camelCased). Defaults match the
 * C# defaults exactly.
 */
export class SecurityOptions {
  /** Trust score below which monitoring is elevated for the node. Default 0.75. */
  elevateMonitoringThreshold = 0.75;
  /** Trust score below which the node is excluded from routing. Default 0.50. */
  avoidNodeThreshold = 0.5;
  /** Trust score at or below which the node is hard-blocked (quarantined). Default 0.25. */
  quarantineThreshold = 0.25;
  /** Passive trust recovery per second when no adverse events occur. Default 0.001. */
  recoveryRatePerSecond = 0.001;
  /**
   * Sliding window (milliseconds) used for pattern-based indicator detection.
   * Events outside this window are ignored for pattern analysis. Default 5 minutes.
   */
  eventWindowMs = 5 * 60 * 1000;
  /** Maximum security events retained per node. Oldest are dropped first. Default 100. */
  maxEventsPerNode = 100;
  /** Trust score assigned to nodes on first observation. Default 1.0. */
  initialTrustScore = 1.0;
}

// ─────────────────────────────────────────────────────────────────────────────
// SecurityCheckpoint
// ─────────────────────────────────────────────────────────────────────────────

/**
 * An immutable, self-verifying snapshot of trusted local state.
 * Created before a risky operation; used for rollback if an
 * {@link AnomalySignal} is confirmed. The payload is opaque bytes so any
 * module can checkpoint its own serialised state.
 */
export class SecurityCheckpoint {
  /** Unique checkpoint identifier. */
  readonly id: string;
  /** The UHID of the local user whose state is captured. Binds to an identity. */
  readonly uhidIdentityId: string;
  /** Label for the module or subsystem that created this checkpoint. */
  readonly moduleLabel: string;
  /** Opaque serialised state payload. */
  readonly payload: Uint8Array;
  /** SHA-256 hash of {@link payload}, computed at creation time. */
  readonly payloadHash: Uint8Array;
  /** UTC timestamp of checkpoint creation. */
  readonly createdAt: Date;

  constructor(
    id: string,
    uhidIdentityId: string,
    moduleLabel: string,
    payload: Uint8Array,
    payloadHash: Uint8Array,
    createdAt: Date,
  ) {
    this.id = id;
    this.uhidIdentityId = uhidIdentityId;
    this.moduleLabel = moduleLabel;
    this.payload = payload;
    this.payloadHash = payloadHash;
    this.createdAt = createdAt;
  }

  /** Creates a new checkpoint, computing {@link payloadHash} automatically. */
  static create(uhidIdentityId: string, moduleLabel: string, payload: Uint8Array): SecurityCheckpoint {
    if (!uhidIdentityId || uhidIdentityId.trim().length === 0) throw new Error("uhidIdentityId required");
    if (!moduleLabel || moduleLabel.trim().length === 0) throw new Error("moduleLabel required");
    if (payload == null) throw new Error("payload required");
    const hash = sha256(payload);
    return new SecurityCheckpoint(randomUUID(), uhidIdentityId, moduleLabel, payload, hash, new Date());
  }

  /**
   * Verifies that {@link payload} has not been tampered with since the
   * checkpoint was created, using a constant-time comparison.
   */
  verify(): boolean {
    const current = sha256(this.payload);
    return fixedTimeEquals(current, this.payloadHash);
  }

  /**
   * Non-sensitive textual representation — the payload bytes are NEVER
   * included in clear. Only the first 16 hex chars (8 bytes) of
   * {@link payloadHash} are emitted, sufficient for correlation without leaking
   * content.
   */
  toString(): string {
    const hashPrefix =
      this.payloadHash.length >= 8 ? toHex(this.payloadHash.subarray(0, 8)).toUpperCase() : "(empty)";
    return (
      `SecurityCheckpoint(Id=${this.id}, Module=${this.moduleLabel}, ` +
      `Uhid=${this.uhidIdentityId}, PayloadSha256=${hashPrefix}…, ` +
      `PayloadBytes=${this.payload?.length ?? 0}, CreatedAt=${toRoundTripUtc(this.createdAt)})`
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// SecurityResponse
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Describes the protective action taken by {@link ISecurityWatchdog} in
 * response to an {@link AnomalySignal}.
 */
export class SecurityResponse {
  /** Identifier of the {@link AnomalySignal} that triggered this response. */
  readonly signalId: string;
  /** Primary response kind. */
  readonly kind: SecurityResponseKind;
  /** When {@link kind} is Composite, lists each individual action applied. Empty otherwise. */
  readonly appliedActions: readonly SecurityResponseKind[];
  /** Human-readable description of what was done and why. */
  readonly description: string;
  /** The {@link SecurityCheckpoint} that was restored, if any; `null` otherwise. */
  readonly restoredCheckpoint: SecurityCheckpoint | null;
  /** UTC timestamp of the response. */
  readonly respondedAt: Date;

  constructor(
    signalId: string,
    kind: SecurityResponseKind,
    appliedActions: readonly SecurityResponseKind[],
    description: string,
    restoredCheckpoint: SecurityCheckpoint | null,
    respondedAt: Date,
  ) {
    this.signalId = signalId;
    this.kind = kind;
    this.appliedActions = appliedActions;
    this.description = description;
    this.restoredCheckpoint = restoredCheckpoint;
    this.respondedAt = respondedAt;
  }

  /** Creates a no-action response for low-confidence or informational signals. */
  static noAction(signalId: string, reason: string): SecurityResponse {
    return new SecurityResponse(signalId, SecurityResponseKind.NoAction, [], reason, null, new Date());
  }

  /** Creates a key-rotation response. */
  static forKeyRotation(signalId: string, description: string): SecurityResponse {
    return new SecurityResponse(signalId, SecurityResponseKind.KeyRotation, [], description, null, new Date());
  }

  /** Creates a state-rollback response, recording the restored checkpoint. */
  static forRollback(signalId: string, restored: SecurityCheckpoint): SecurityResponse {
    return new SecurityResponse(
      signalId,
      SecurityResponseKind.StateRollback,
      [],
      `State rolled back to checkpoint ${restored.id} (${restored.moduleLabel}).`,
      restored,
      new Date(),
    );
  }

  /** Creates a composite response from multiple individual actions. */
  static composite(
    signalId: string,
    actions: readonly SecurityResponseKind[],
    description: string,
    restoredCheckpoint: SecurityCheckpoint | null = null,
  ): SecurityResponse {
    return new SecurityResponse(
      signalId,
      SecurityResponseKind.Composite,
      actions,
      description,
      restoredCheckpoint,
      new Date(),
    );
  }
}

/** Result of a dispatch attempt. */
export interface AnomalyDispatchResult {
  /** What the dispatcher did with the signal. */
  readonly outcome: AnomalyDispatchOutcome;
  /** The watchdog response, when {@link outcome} is Dispatched; `null` otherwise. */
  readonly response: SecurityResponse | null;
}

// ─────────────────────────────────────────────────────────────────────────────
// ThreatDetector — pure static threat logic
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Stateless threat analysis helpers used by {@link SecurityLayerService} and
 * {@link PeerIntelligenceService}. No state, no DI — fully testable in isolation.
 */
export class ThreatDetector {
  private constructor() {
    /* static-only */
  }

  /** Degradation weights by event kind. */
  private static baseWeight(kind: PeerSecurityEventKind): number {
    switch (kind) {
      case PeerSecurityEventKind.AuthAttempt:
        return 0.05;
      case PeerSecurityEventKind.RoutingAnomaly:
        return 0.1;
      case PeerSecurityEventKind.BehaviourChange:
        return 0.08;
      case PeerSecurityEventKind.EncryptionEvent:
        return 0.06;
      case PeerSecurityEventKind.IntrusionSignal:
        return 0.15;
      case PeerSecurityEventKind.PrivilegeAttempt:
        return 0.12;
      case PeerSecurityEventKind.ConnectionAnomaly:
        return 0.07;
      case PeerSecurityEventKind.DataExfiltration:
        return 0.14;
      case PeerSecurityEventKind.DenialOfService:
        return 0.13;
      default:
        return 0.05;
    }
  }

  /** Multipliers by threat level. */
  private static threatMultiplier(level: PeerThreatLevel): number {
    switch (level) {
      case PeerThreatLevel.None:
        return 0.0;
      case PeerThreatLevel.Low:
        return 0.5;
      case PeerThreatLevel.Medium:
        return 1.0;
      case PeerThreatLevel.High:
        return 2.0;
      case PeerThreatLevel.Critical:
        return 3.0;
      default:
        return 1.0;
    }
  }

  /**
   * Returns the trust-score degradation amount for a security event, calculated
   * as `baseWeight(kind) × threatMultiplier(level)`. Returns 0 for
   * {@link PeerThreatLevel.None}.
   */
  static computeDegradation(e: PeerSecurityEvent): number {
    return ThreatDetector.baseWeight(e.kind) * ThreatDetector.threatMultiplier(e.threatLevel);
  }

  /**
   * Derives human-readable threat indicator tags from a set of recent events
   * within the given `windowMs`. Returns an empty list when no patterns are
   * detected.
   */
  static detectIndicators(recentEvents: Iterable<PeerSecurityEvent>, windowMs: number): string[] {
    const cutoff = Date.now() - windowMs;
    const windowed = [...recentEvents].filter((e) => e.occurredAt.getTime() >= cutoff);

    if (windowed.length === 0) return [];

    const indicators: string[] = [];

    // ≥ 3 auth attempts within the window → brute-force signal
    if (windowed.filter((e) => e.kind === PeerSecurityEventKind.AuthAttempt).length >= 3)
      indicators.push("repeated-auth-attempts");

    // Any intrusion signal → explicit probe or exploit
    if (windowed.some((e) => e.kind === PeerSecurityEventKind.IntrusionSignal))
      indicators.push("intrusion-signal-detected");

    // High or Critical event → severity flag
    if (windowed.some((e) => e.threatLevel === PeerThreatLevel.High || e.threatLevel === PeerThreatLevel.Critical))
      indicators.push("high-severity-event");

    // ≥ 3 distinct event kinds → multi-vector activity
    if (new Set(windowed.map((e) => e.kind)).size >= 3) indicators.push("multi-vector-activity");

    // Privilege escalation attempt
    if (windowed.some((e) => e.kind === PeerSecurityEventKind.PrivilegeAttempt))
      indicators.push("privilege-escalation-attempt");

    // Data exfiltration signal
    if (windowed.some((e) => e.kind === PeerSecurityEventKind.DataExfiltration))
      indicators.push("data-exfiltration-signal");

    return indicators;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// NodeTrustEntry / NodeTrustRegistry
// ─────────────────────────────────────────────────────────────────────────────

/** Per-peer mutable trust state. Exposed for diagnostics and tests. */
export class NodeTrustEntry {
  readonly nodeId: string;
  trustScore: number;
  lastUpdated: Date = new Date();
  /** Bounded history of security events (oldest-first). */
  readonly recentEvents: PeerSecurityEvent[] = [];

  constructor(nodeId: string, trustScore: number) {
    this.nodeId = nodeId;
    this.trustScore = trustScore;
  }
}

/**
 * Maintains per-peer trust scores, event history, and a live channel of trust
 * score changes consumed by {@link PeerIntelligenceService}.
 *
 * Each peer gets a score in [0, 1]: 1.0 = fully trusted, 0.0 = fully lost.
 * `applyDegradation` drops the score and records the triggering event;
 * `applyRecovery` heals all peers passively. `trustScoreUpdates` is an unbounded
 * queue mirroring the C# `Channel.CreateUnbounded<PeerTrustScoreUpdate>` — every
 * change is buffered until read, so a reader that attaches after writes have
 * occurred still receives them.
 */
export class NodeTrustRegistry {
  private readonly options: SecurityOptions;
  private readonly nodes = new Map<string, NodeTrustEntry>();
  private readonly channel = new AsyncQueue<PeerTrustScoreUpdate>();

  constructor(options: SecurityOptions) {
    this.options = options;
  }

  /**
   * Stream of trust score changes; never completes during normal operation.
   * Callers should pass an AbortSignal to break out. Mirrors the C#
   * `ChannelReader<PeerTrustScoreUpdate>` exposed via `ReadAllAsync`.
   */
  streamTrustScoreUpdates(signal?: AbortSignal): AsyncGenerator<PeerTrustScoreUpdate> {
    return this.channel.drain(signal);
  }

  /**
   * Returns the existing entry for `nodeId`, or creates a new one initialised
   * to {@link SecurityOptions.initialTrustScore}.
   */
  getOrCreate(nodeId: string): NodeTrustEntry {
    let entry = this.nodes.get(nodeId);
    if (entry === undefined) {
      entry = new NodeTrustEntry(nodeId, this.options.initialTrustScore);
      this.nodes.set(nodeId, entry);
    }
    return entry;
  }

  /** All peer IDs currently tracked. */
  get allNodeIds(): string[] {
    return [...this.nodes.keys()];
  }

  /**
   * Returns the current trust score for `nodeId`, or
   * {@link SecurityOptions.initialTrustScore} for unknown peers.
   */
  getTrustScore(nodeId: string): number {
    const entry = this.nodes.get(nodeId);
    if (entry !== undefined) return entry.trustScore;
    return this.options.initialTrustScore;
  }

  /**
   * Applies trust degradation for a security event. Score is clamped to [0, 1];
   * the event is appended to the per-peer history; a {@link PeerTrustScoreUpdate}
   * is published on the channel when the score actually moved. Returns
   * `{ previous, current }`.
   */
  applyDegradation(
    securityEvent: PeerSecurityEvent,
    degradationAmount: number,
  ): { previous: number; current: number } {
    const entry = this.getOrCreate(securityEvent.nodeId);

    const previous = entry.trustScore;
    entry.trustScore = clamp(previous - degradationAmount, 0, 1);
    entry.lastUpdated = securityEvent.occurredAt;

    // Maintain bounded event list (oldest dropped first).
    entry.recentEvents.push(securityEvent);
    while (entry.recentEvents.length > this.options.maxEventsPerNode) entry.recentEvents.shift();

    const current = entry.trustScore;

    if (Math.abs(current - previous) > 0.0001)
      this.publish(entry.nodeId, previous, current, securityEvent.description, securityEvent.occurredAt);

    return { previous, current };
  }

  /**
   * Passively heals all tracked peers by `recoveryRatePerSecond × elapsed`.
   * Peers already at 1.0 are skipped. Called by the background recovery loop.
   */
  applyRecovery(elapsedMs: number): void {
    const amount = this.options.recoveryRatePerSecond * (elapsedMs / 1000);
    if (amount <= 0) return;

    for (const entry of this.nodes.values()) {
      if (entry.trustScore >= 1.0) continue;

      const previous = entry.trustScore;
      entry.trustScore = Math.min(1.0, previous + amount);
      entry.lastUpdated = new Date();

      this.publish(entry.nodeId, previous, entry.trustScore, "passive-recovery", new Date());
    }
  }

  /**
   * Returns events for `nodeId` that fall within
   * {@link SecurityOptions.eventWindowMs} of now. Empty for unknown peers.
   */
  getRecentEvents(nodeId: string): PeerSecurityEvent[] {
    const entry = this.nodes.get(nodeId);
    if (entry === undefined) return [];
    const cutoff = Date.now() - this.options.eventWindowMs;
    return entry.recentEvents.filter((e) => e.occurredAt.getTime() >= cutoff);
  }

  private publish(nodeId: string, previous: number, current: number, reason: string, at: Date): void {
    this.channel.enqueue({
      nodeId,
      previousScore: previous,
      newScore: current,
      reason,
      changedAt: at,
    });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Peer contracts
// ─────────────────────────────────────────────────────────────────────────────

/** Receives security directives from any {@link IPeerSecurityLayer} implementation. */
export interface IPeerDirectiveConsumer {
  /** Called when the security layer issues a directive for a peer. */
  onDirective(directive: PeerDirective): void;
}

/** Transport-agnostic security layer lifecycle and posture surface. */
export interface IPeerSecurityLayer {
  /** Starts the background trust-recovery loop. */
  startAsync(signal?: AbortSignal): Promise<void>;
  /** Stops the recovery loop and releases resources. */
  stopAsync(signal?: AbortSignal): Promise<void>;
  /**
   * Feed a security event from any transport into the security layer. The layer
   * will degrade the peer's trust score and issue directives as needed.
   */
  handlePeerEvent(e: PeerSecurityEvent): void;
  /** Subscribe to receive directives. Dispose the returned handle to unsubscribe. */
  subscribeToDirectives(consumer: IPeerDirectiveConsumer): IDisposableHandle;
  /** Returns a snapshot of the current security posture. */
  getPostureAsync(signal?: AbortSignal): Promise<PeerSecurityPosture>;
}

/** Transport-agnostic intelligence queries over accumulated trust data. */
export interface IPeerIntelligence {
  /** Returns aggregate network health across all observed peers. */
  getNetworkHealthAsync(signal?: AbortSignal): Promise<PeerNetworkHealthReport>;
  /** Returns a threat assessment for a specific peer. */
  assessThreatAsync(nodeId: string, signal?: AbortSignal): Promise<PeerThreatAssessment>;
  /** Returns trust-aware routing advice toward a destination peer. */
  getRoutingAdviceAsync(destinationNodeId: string, signal?: AbortSignal): Promise<PeerRoutingAdvice>;
  /**
   * Streams every trust score change as they occur. Completes when the
   * AbortSignal fires.
   */
  streamTrustScoresAsync(signal?: AbortSignal): AsyncGenerator<PeerTrustScoreUpdate>;
}

/**
 * Implemented by transport adapters to register an event source with the
 * security layer. The security layer calls `startAsync` once to begin pumping
 * events.
 */
export interface IPeerSecurityEventFeed {
  /** Human-readable identifier for this transport (e.g. "wifi", "ble", "aether"). */
  readonly transportId: string;
  /**
   * Begins feeding events into `handler` until the AbortSignal is fired.
   */
  startAsync(handler: (e: PeerSecurityEvent) => void, signal: AbortSignal): Promise<void>;
}

/** A disposable subscription handle (the TS analogue of `IDisposable`). */
export interface IDisposableHandle {
  /** Idempotent unsubscribe/cleanup. */
  dispose(): void;
}

// ─────────────────────────────────────────────────────────────────────────────
// DirectivePublisher — fan-out
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Manages {@link IPeerDirectiveConsumer} subscriptions and fans published
 * {@link PeerDirective} instances out to all subscribers. A snapshot of the
 * subscriber list is taken before callbacks fire, so a consumer that
 * unsubscribes inside its own callback cannot corrupt the iteration (mirrors the
 * C# `snapshot = [.. _consumers]` under lock, callbacks outside the lock).
 */
export class DirectivePublisher {
  private readonly consumers: IPeerDirectiveConsumer[] = [];

  /**
   * Subscribes `consumer` to receive directives. Dispose the returned handle to
   * unsubscribe. Idempotent disposal.
   */
  subscribe(consumer: IPeerDirectiveConsumer): IDisposableHandle {
    if (consumer == null) throw new Error("consumer required");
    this.consumers.push(consumer);
    return new SubscriptionHandle(this, consumer);
  }

  /** Publishes `directive` to all current subscribers. */
  publish(directive: PeerDirective): void {
    const snapshot = [...this.consumers];
    for (const c of snapshot) c.onDirective(directive);
  }

  /** Number of currently active subscribers. Useful in tests. */
  get subscriberCount(): number {
    return this.consumers.length;
  }

  /** @internal */
  unsubscribe(consumer: IPeerDirectiveConsumer): void {
    const i = this.consumers.indexOf(consumer);
    if (i >= 0) this.consumers.splice(i, 1);
  }
}

class SubscriptionHandle implements IDisposableHandle {
  private readonly publisher: DirectivePublisher;
  private readonly consumer: IPeerDirectiveConsumer;
  private disposed = false;

  constructor(publisher: DirectivePublisher, consumer: IPeerDirectiveConsumer) {
    this.publisher = publisher;
    this.consumer = consumer;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.publisher.unsubscribe(this.consumer);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// SecurityLayerService — IPeerSecurityLayer (AISecurityLayerService.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Transport-agnostic AI Security Layer. Degrades per-peer trust scores via
 * {@link ThreatDetector} and issues {@link PeerDirective} recommendations to all
 * registered {@link IPeerDirectiveConsumer} subscribers.
 *
 * Directives issued (most-severe wins per event):
 *   QuarantineNode     trust ≤ quarantineThreshold
 *   AvoidNode          trust ≤ avoidNodeThreshold
 *   ElevateMonitoring  trust ≤ elevateMonitoringThreshold
 *   ReleaseNode        not issued automatically — requires explicit operator action
 */
export class SecurityLayerService implements IPeerSecurityLayer {
  private readonly registry: NodeTrustRegistry;
  private readonly options: SecurityOptions;
  private readonly publisher: DirectivePublisher;

  private cts: AbortController | null = null;
  private recoveryLoop: Promise<void> | null = null;
  private active = false;
  private static readonly recoveryIntervalMs = 30_000;

  constructor(registry: NodeTrustRegistry, options: SecurityOptions, publisher: DirectivePublisher) {
    this.registry = registry;
    this.options = options;
    this.publisher = publisher;
  }

  get isActive(): boolean {
    return this.active;
  }

  startAsync(signal?: AbortSignal): Promise<void> {
    if (this.active) return Promise.resolve();
    this.cts = linkedController(signal);
    this.recoveryLoop = this.runRecoveryLoop(this.cts.signal);
    this.active = true;
    return Promise.resolve();
  }

  async stopAsync(signal?: AbortSignal): Promise<void> {
    this.active = false;

    if (this.cts !== null) {
      this.cts.abort();
      if (this.recoveryLoop !== null) {
        try {
          await withSignal(this.recoveryLoop, signal);
        } catch {
          /* OperationCanceledException equivalent — swallow */
        }
      }
      this.cts = null;
      this.recoveryLoop = null;
    }
  }

  /**
   * Call this from any transport adapter after translating its native event
   * type to {@link PeerSecurityEvent}.
   */
  handlePeerEvent(e: PeerSecurityEvent): void {
    const degradation = ThreatDetector.computeDegradation(e);
    if (degradation <= 0) return; // PeerThreatLevel.None — no trust impact

    const { previous, current } = this.registry.applyDegradation(e, degradation);
    this.evaluateThresholds(e.nodeId, previous, current, e.description);
  }

  /**
   * Notify the security layer that a peer has left. Trust entry is preserved for
   * historical queries; no directive is issued.
   */
  handlePeerLeft(_nodeId: string): void {
    // Trust entry retained for forensic queries; no action required on departure.
  }

  subscribeToDirectives(consumer: IPeerDirectiveConsumer): IDisposableHandle {
    return this.publisher.subscribe(consumer);
  }

  getPostureAsync(_signal?: AbortSignal): Promise<PeerSecurityPosture> {
    const nodeIds = this.registry.allNodeIds;
    const quarantined = nodeIds.filter((id) => this.registry.getTrustScore(id) <= this.options.quarantineThreshold)
      .length;
    const monitored = nodeIds.filter((id) => {
      const s = this.registry.getTrustScore(id);
      return s <= this.options.elevateMonitoringThreshold && s > this.options.quarantineThreshold;
    }).length;

    const worstScore =
      nodeIds.length === 0 ? 1.0 : Math.min(...nodeIds.map((id) => this.registry.getTrustScore(id)));
    const overallThreat = scoreToThreatLevel(worstScore);

    return Promise.resolve({
      overallThreatLevel: overallThreat,
      quarantinedPeerCount: quarantined,
      monitoredPeerCount: monitored,
      isActive: this.active,
      generatedAt: new Date(),
    });
  }

  // ─── Threshold evaluation ─────────────────────────────────────────────────

  private evaluateThresholds(nodeId: string, previous: number, current: number, reason: string): void {
    // Evaluate from most-severe to least; issue at most one directive per event.
    if (previous > this.options.quarantineThreshold && current <= this.options.quarantineThreshold) {
      this.issueDirective(PeerDirectiveKind.QuarantineNode, nodeId, current, reason, PeerThreatLevel.Critical);
      return;
    }

    if (previous > this.options.avoidNodeThreshold && current <= this.options.avoidNodeThreshold) {
      this.issueDirective(PeerDirectiveKind.AvoidNode, nodeId, current, reason, PeerThreatLevel.High);
      return;
    }

    if (previous > this.options.elevateMonitoringThreshold && current <= this.options.elevateMonitoringThreshold) {
      this.issueDirective(PeerDirectiveKind.ElevateMonitoring, nodeId, current, reason, PeerThreatLevel.Medium);
    }
  }

  private issueDirective(
    kind: PeerDirectiveKind,
    nodeId: string,
    trustScore: number,
    reason: string,
    threatLevel: PeerThreatLevel,
  ): void {
    this.publisher.publish({
      kind,
      targetNodeId: nodeId,
      trustScore,
      threatLevel,
      reason,
      durationMs: null, // permanent until ReleaseNode
      issuedAt: new Date(),
    });
  }

  // ─── Background recovery loop ─────────────────────────────────────────────

  private async runRecoveryLoop(signal: AbortSignal): Promise<void> {
    const interval = SecurityLayerService.recoveryIntervalMs;
    while (!signal.aborted) {
      try {
        await delay(interval, signal);
        this.registry.applyRecovery(interval);
      } catch {
        break; // OperationCanceledException
      }
    }
  }
}

/** Score → threat-level classification, shared by posture and assessment. */
function scoreToThreatLevel(score: number): PeerThreatLevel {
  if (score <= 0.25) return PeerThreatLevel.Critical;
  if (score <= 0.5) return PeerThreatLevel.High;
  if (score <= 0.75) return PeerThreatLevel.Medium;
  if (score <= 0.9) return PeerThreatLevel.Low;
  return PeerThreatLevel.None;
}

// ─────────────────────────────────────────────────────────────────────────────
// PeerIntelligenceService — IPeerIntelligence (AetherIntelligenceService.cs)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Reads {@link NodeTrustRegistry} state to produce transport-agnostic
 * intelligence outputs. Wires directly to the registry's trust-score update
 * stream for the streaming API.
 */
export class PeerIntelligenceService implements IPeerIntelligence {
  private readonly registry: NodeTrustRegistry;
  private readonly options: SecurityOptions;

  constructor(registry: NodeTrustRegistry, options: SecurityOptions) {
    this.registry = registry;
    this.options = options;
  }

  getNetworkHealthAsync(_signal?: AbortSignal): Promise<PeerNetworkHealthReport> {
    const nodeIds = this.registry.allNodeIds;

    if (nodeIds.length === 0) {
      return Promise.resolve({
        overallScore: 1.0,
        trustedPeerCount: 0,
        suspiciousPeerCount: 0,
        summary: "No peers observed.",
        generatedAt: new Date(),
      });
    }

    const scores = nodeIds.map((id) => this.registry.getTrustScore(id));
    const overall = scores.reduce((a, b) => a + b, 0) / scores.length;
    const trusted = scores.filter((s) => s > this.options.avoidNodeThreshold).length;
    const suspicious = scores.filter((s) => s <= this.options.elevateMonitoringThreshold).length;

    let summary: string;
    if (overall > 0.9) summary = "Network health is excellent.";
    else if (overall > 0.75) summary = "Network health is good; minor anomalies detected.";
    else if (overall > 0.5) summary = "Network health is degraded; elevated monitoring active.";
    else if (overall > 0.25) summary = "Network health is poor; routing around compromised peers.";
    else summary = "Network health is critical; quarantine directives in effect.";

    return Promise.resolve({
      overallScore: overall,
      trustedPeerCount: trusted,
      suspiciousPeerCount: suspicious,
      summary,
      generatedAt: new Date(),
    });
  }

  assessThreatAsync(nodeId: string, _signal?: AbortSignal): Promise<PeerThreatAssessment> {
    const score = this.registry.getTrustScore(nodeId);
    const deficit = 1.0 - score; // 0 = fully trusted, 1 = fully lost

    const indicators = ThreatDetector.detectIndicators(
      this.registry.getRecentEvents(nodeId),
      this.options.eventWindowMs,
    );

    const level = scoreToThreatLevel(score);

    // Confidence is proportional to trust deficit, boosted by each indicator.
    const confidence = Math.min(1.0, deficit + indicators.length * 0.1);

    return Promise.resolve({
      nodeId,
      confidence,
      threatLevel: level,
      indicators,
      assessedAt: new Date(),
    });
  }

  getRoutingAdviceAsync(destinationNodeId: string, _signal?: AbortSignal): Promise<PeerRoutingAdvice> {
    const allNodes = this.registry.allNodeIds;
    const avoidNodes = allNodes.filter((id) => this.registry.getTrustScore(id) <= this.options.avoidNodeThreshold);

    const destScore = this.registry.getTrustScore(destinationNodeId);

    // Recommended path is direct only when destination is above avoid-threshold.
    const recommended: string[] = destScore > this.options.avoidNodeThreshold ? [destinationNodeId] : [];

    let reasoning: string;
    if (destScore > 0.75) reasoning = `Direct path to ${destinationNodeId} is trusted (score ${destScore.toFixed(2)}).`;
    else if (destScore > 0.5)
      reasoning = `Destination ${destinationNodeId} is under monitoring; routing with caution.`;
    else if (destScore > 0.25)
      reasoning = `Destination ${destinationNodeId} has degraded trust; avoid recommended.`;
    else reasoning = `Destination ${destinationNodeId} is quarantined; no safe path available.`;

    return Promise.resolve({
      destinationNodeId,
      recommendedPath: recommended,
      avoidNodeIds: avoidNodes,
      confidence: destScore,
      reasoning,
      generatedAt: new Date(),
    });
  }

  async *streamTrustScoresAsync(signal?: AbortSignal): AsyncGenerator<PeerTrustScoreUpdate> {
    yield* this.registry.streamTrustScoreUpdates(signal);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// ISecurityWatchdog + DefaultSecurityWatchdog
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Central contract for the CircleAI local runtime immune system. Receives
 * {@link AnomalySignal} instances from detection sites and returns the
 * {@link SecurityResponse} describing protective action taken.
 */
export interface ISecurityWatchdog {
  /**
   * Called by any detection site when a local runtime anomaly is observed. The
   * watchdog evaluates `signal` and applies the appropriate protective response.
   */
  onAnomalyDetectedAsync(
    signal: AnomalySignal,
    checkpoint?: SecurityCheckpoint | null,
    signal2?: AbortSignal,
  ): Promise<SecurityResponse>;

  /**
   * Returns a live stream of every {@link AnomalySignal} observed since the
   * watchdog started. Completes when the AbortSignal fires.
   */
  streamSignalsAsync(signal?: AbortSignal): AsyncGenerator<AnomalySignal>;
}

const ROTATION_THRESHOLD = 0.3;
const COMPOSITE_THRESHOLD = 0.6;

/**
 * Default in-process watchdog. Applies graduated responses based on
 * {@link ThreatVector} and confidence level:
 *   - confidence < 0.30 → NoAction
 *   - confidence 0.30–0.60 → KeyRotation
 *   - confidence > 0.60 + high-severity vector → Composite (rotation + mesh signal
 *     [+ rollback when a verifying checkpoint is available])
 *
 * Uses an unbounded queue for the signal stream (in-process; single-process
 * correct, not multi-replica safe — signals emitted on replica A do not reach
 * stream subscribers on replica B).
 */
export class DefaultSecurityWatchdog implements ISecurityWatchdog {
  private readonly signals = new AsyncQueue<AnomalySignal>();

  readonly componentName = "DefaultSecurityWatchdog";

  async onAnomalyDetectedAsync(
    signal: AnomalySignal,
    checkpoint: SecurityCheckpoint | null = null,
    _ct?: AbortSignal,
  ): Promise<SecurityResponse> {
    if (signal == null) throw new Error("signal required");

    // Broadcast to any stream subscribers (unbounded — never blocks).
    this.signals.enqueue(signal);

    // ── Graduated response policy ────────────────────────────────────────────

    if (signal.confidence < ROTATION_THRESHOLD)
      return SecurityResponse.noAction(
        signal.id,
        `Confidence ${formatPercent(signal.confidence)} below rotation threshold — monitoring only.`,
      );

    // High-severity vectors always warrant rollback if we have a checkpoint.
    const isHighSeverity =
      signal.vector === ThreatVector.ControlFlowDrift ||
      signal.vector === ThreatVector.PrivilegeEscalation ||
      signal.vector === ThreatVector.NetworkPivot ||
      signal.vector === ThreatVector.StateCorruption;

    if (signal.confidence > COMPOSITE_THRESHOLD) {
      const actions: SecurityResponseKind[] = [
        SecurityResponseKind.KeyRotation,
        SecurityResponseKind.MeshIsolationSignal,
      ];

      let restored: SecurityCheckpoint | null = null;
      if (checkpoint != null && isHighSeverity && checkpoint.verify()) {
        actions.push(SecurityResponseKind.StateRollback);
        restored = checkpoint;
      }

      return SecurityResponse.composite(
        signal.id,
        actions,
        `Composite response for ${ThreatVector[signal.vector]} ` +
          `(confidence ${formatPercent(signal.confidence)}) in ${signal.affectedModule}.`,
        restored,
      );
    }

    // Mid-range confidence: rotate keys only.
    return SecurityResponse.forKeyRotation(
      signal.id,
      `Key rotation triggered for ${ThreatVector[signal.vector]} ` +
        `(confidence ${formatPercent(signal.confidence)}) in ${signal.affectedModule}.`,
    );
  }

  streamSignalsAsync(signal?: AbortSignal): AsyncGenerator<AnomalySignal> {
    return this.signals.drain(signal);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// IAnomalyEventDispatcher + DefaultAnomalyEventDispatcher
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Verify, dedup, and dispatch an {@link AnomalySignal} in a single call.
 * Returns an {@link AnomalyDispatchResult} describing what happened — no
 * exception is thrown on rejection so the caller can branch on the outcome.
 */
export interface IAnomalyEventDispatcher {
  /**
   * Runs the verification pipeline (origin trust, optional signature check,
   * confidence threshold) and, when all gates pass, hands the signal to the
   * wrapped {@link ISecurityWatchdog}.
   */
  verifyAndDispatchAsync(
    signal: AnomalySignal,
    checkpoint?: SecurityCheckpoint | null,
    ct?: AbortSignal,
  ): Promise<AnomalyDispatchResult>;
}

/**
 * Default in-process dispatcher. Threshold-gated, id-deduped, no signature
 * verification (compose with a signature-verifying wrapper when running over an
 * untrusted transport).
 */
export class DefaultAnomalyEventDispatcher implements IAnomalyEventDispatcher {
  private readonly watchdog: ISecurityWatchdog;
  private readonly minimumConfidence: number;
  private readonly seen = new Set<string>();

  /**
   * @param watchdog The watchdog to forward verified signals to.
   * @param minimumConfidence Drop signals whose confidence is below this value.
   *   Default 0.30 — matches the default watchdog rotation threshold so signals
   *   that would have been no-ops aren't even dispatched.
   */
  constructor(watchdog: ISecurityWatchdog, minimumConfidence = 0.3) {
    if (watchdog == null) throw new Error("watchdog required");
    this.watchdog = watchdog;
    this.minimumConfidence = clamp(minimumConfidence, 0, 1);
  }

  async verifyAndDispatchAsync(
    signal: AnomalySignal,
    checkpoint: SecurityCheckpoint | null = null,
    ct?: AbortSignal,
  ): Promise<AnomalyDispatchResult> {
    if (signal == null) throw new Error("signal required");

    if (ct?.aborted ?? false) return { outcome: AnomalyDispatchOutcome.Cancelled, response: null };

    if (signal.confidence < this.minimumConfidence)
      return { outcome: AnomalyDispatchOutcome.BelowThreshold, response: null };

    if (this.seen.has(signal.id)) return { outcome: AnomalyDispatchOutcome.Duplicate, response: null };
    this.seen.add(signal.id);

    const response = await this.watchdog.onAnomalyDetectedAsync(signal, checkpoint, ct);
    return { outcome: AnomalyDispatchOutcome.Dispatched, response };
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// UhidKeyRing — ephemeral ECDSA P-256 session key ring
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Ephemeral ECDSA (P-256) session key ring bound to a UHID identity. Generate a
 * fresh ring at session start or on anomaly confirmation. Once revoked, the ring
 * cannot sign; generate a new one. Verify continues to work after revocation so
 * prior signatures can still be validated.
 *
 * C# uses `ECDsa.SignData(data, SHA256)`, whose default output encoding is DER;
 * Node's `createSign("SHA256").sign(key)` also defaults to DER, so the signature
 * shape matches. Sign+verify stay within the instance, so the encoding choice is
 * about faithfulness rather than cross-language byte-matching.
 */
export class UhidKeyRing {
  private privateKey: KeyObject | null = null;
  private publicKey: KeyObject | null = null;
  private revoked = false;
  private disposed = false;

  /** Unique ring identifier. Changes on every regenerate. */
  ringId = "";
  /** The UHID identity this ring is bound to. */
  readonly uhidIdentityId: string;
  /** UTC timestamp when this ring was generated. */
  generatedAt: Date = new Date();
  /** UTC timestamp when this ring was revoked, or `null` if still active. */
  revokedAt: Date | null = null;
  /** The DER-encoded public key (SubjectPublicKeyInfo) for this ring. Safe to share. */
  publicKeyDer: Uint8Array = new Uint8Array(0);

  private constructor(uhidIdentityId: string) {
    if (!uhidIdentityId || uhidIdentityId.trim().length === 0) throw new Error("uhidIdentityId required");
    this.uhidIdentityId = uhidIdentityId;
    this.regenerateKey();
  }

  /** `true` if this ring has been explicitly revoked. */
  get isRevoked(): boolean {
    return this.revoked;
  }

  /**
   * Creates a new {@link UhidKeyRing} for `uhidIdentityId` with a freshly
   * generated P-256 key pair.
   */
  static generateFresh(uhidIdentityId: string): UhidKeyRing {
    return new UhidKeyRing(uhidIdentityId);
  }

  /**
   * Rotates the ring: revokes the current key and generates a replacement.
   * Returns a NEW {@link UhidKeyRing} — this instance remains revoked.
   */
  rotate(): UhidKeyRing {
    this.revoke();
    return UhidKeyRing.generateFresh(this.uhidIdentityId);
  }

  /**
   * Signs `data` with the current private key using ECDSA-SHA256. Throws if the
   * ring is disposed or revoked.
   */
  sign(data: Uint8Array): Uint8Array {
    if (data == null) throw new Error("data required");
    if (this.disposed || this.privateKey === null) throw new Error("UhidKeyRing disposed");
    if (this.revoked)
      throw new Error(`UhidKeyRing ${this.ringId} has been revoked — call rotate() to get a fresh ring.`);
    const signer = createSign("SHA256");
    signer.update(Buffer.from(data));
    signer.end();
    return new Uint8Array(signer.sign(this.privateKey));
  }

  /**
   * Verifies an ECDSA-SHA256 `signature` against `data` using this ring's public
   * key. Works even after revocation.
   */
  verify(data: Uint8Array, signature: Uint8Array): boolean {
    if (data == null) throw new Error("data required");
    if (signature == null) throw new Error("signature required");
    if (this.publicKey === null) return false;
    try {
      const verifier = createVerify("SHA256");
      verifier.update(Buffer.from(data));
      verifier.end();
      return verifier.verify(this.publicKey, Buffer.from(signature));
    } catch {
      return false;
    }
  }

  /**
   * Revokes this ring. After revocation {@link sign} throws; {@link verify}
   * continues to work for historical validation.
   */
  revoke(): void {
    if (this.revoked) return;
    this.revoked = true;
    this.revokedAt = new Date();
  }

  /** Disposes the ring's key material. */
  dispose(): void {
    this.disposed = true;
    this.privateKey = null;
    this.publicKey = null;
  }

  private regenerateKey(): void {
    const { privateKey, publicKey } = generateKeyPairSync("ec", { namedCurve: "prime256v1" });
    this.privateKey = privateKey;
    this.publicKey = publicKey;
    this.ringId = randomUUID();
    this.generatedAt = new Date();
    this.revokedAt = null;
    this.revoked = false;
    this.disposed = false;
    this.publicKeyDer = new Uint8Array(publicKey.export({ type: "spki", format: "der" }));
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Redacted evidence serialisation
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Serialises an evidence dictionary with every value replaced by the
 * `sha256:<hex>` of its UTF-8 bytes. The keys (evidence labels) are preserved so
 * structured log sinks can still join entries by evidence shape, but the raw
 * values — which may carry session tokens, payload fragments, or PII — never
 * leave the process in clear text.
 *
 * Mirrors the C# `RedactedEvidenceJsonConverter.Write`: an empty/absent value
 * hashes to the literal `"sha256:"`, and non-empty values to
 * `"sha256:" + lowercase-hex(SHA256(utf8(value)))`.
 */
export function redactEvidence(
  value: Readonly<Record<string, string>> | null | undefined,
): Record<string, string> | null {
  if (value == null) return null;
  const out: Record<string, string> = {};
  for (const key of Object.keys(value)) out[key] = hashRedacted(value[key]);
  return out;
}

/** `"sha256:"` for empty input; `"sha256:" + lowercase hex` otherwise. */
function hashRedacted(raw: string | null | undefined): string {
  if (raw == null || raw.length === 0) return "sha256:";
  return "sha256:" + toHex(sha256(Buffer.from(raw, "utf8"))).toLowerCase();
}

/**
 * A System.Text.Json-style converter façade for evidence dictionaries.
 *
 * The C# `RedactedEvidenceJsonConverter` plugs into System.Text.Json; TypeScript
 * has no equivalent converter registry, so this exposes the same read/write
 * contract as static methods:
 *
 *   - {@link write} redacts every value to its SHA-256 (see {@link redactEvidence}).
 *   - {@link read} intentionally returns an empty object: incoming JSON cannot be
 *     trusted to carry the original cleartext, and round-tripping hashes back
 *     would mask whether the source-of-record is the in-process signal or a
 *     serialised copy (matches the C# `Read` returning `new Dictionary`).
 */
export class RedactedEvidenceJsonConverter {
  private constructor() {
    /* static-only façade */
  }

  /** Read side — never trusts inbound values; returns an empty object (or null for JSON null). */
  static read(json: unknown): Record<string, string> | null {
    if (json === null) return null;
    return {};
  }

  /** Write side — redacts every value to its SHA-256 hex. Returns null for a null input. */
  static write(value: Readonly<Record<string, string>> | null | undefined): Record<string, string> | null {
    return redactEvidence(value);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared helpers (private to the module)
// ─────────────────────────────────────────────────────────────────────────────

/** Math.Clamp(value, min, max). */
function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

/** SHA-256 of the given bytes, returned as a Uint8Array. */
function sha256(data: Uint8Array): Uint8Array {
  return new Uint8Array(createHash("sha256").update(data).digest());
}

/** Lowercase hex encoding of the given bytes. */
function toHex(data: Uint8Array): string {
  return Buffer.from(data).toString("hex");
}

/** Constant-time byte comparison; false on length mismatch (mirrors CryptographicOperations.FixedTimeEquals). */
function fixedTimeEquals(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  return timingSafeEqual(Buffer.from(a), Buffer.from(b));
}

/**
 * .NET `"P0"` percentage format for a fraction: multiply by 100, round to a
 * whole number, and append `"%"` (e.g. 0.92 → "92%"). .NET inserts a
 * non-breaking space before the sign in some cultures; the invariant/en form
 * used here is `NN%` with no space, matching how the C# strings read in tests.
 */
function formatPercent(fraction: number): string {
  return `${Math.round(fraction * 100)}%`;
}

/**
 * Render a Date as .NET's DateTimeOffset "O" format for a zero offset:
 * `yyyy-MM-ddTHH:mm:ss.fffffffZ` (7 fractional digits). JS millisecond precision
 * means the last four fractional digits are always 0.
 */
function toRoundTripUtc(d: Date): string {
  const pad = (n: number, w = 2) => String(n).padStart(w, "0");
  const yyyy = pad(d.getUTCFullYear(), 4);
  const mo = pad(d.getUTCMonth() + 1);
  const da = pad(d.getUTCDate());
  const hh = pad(d.getUTCHours());
  const mi = pad(d.getUTCMinutes());
  const ss = pad(d.getUTCSeconds());
  const frac = pad(d.getUTCMilliseconds(), 3) + "0000";
  return `${yyyy}-${mo}-${da}T${hh}:${mi}:${ss}.${frac}Z`;
}

/** An AbortController that also aborts when the linked parent signal fires. */
function linkedController(parent?: AbortSignal): AbortController {
  const ctrl = new AbortController();
  if (parent) {
    if (parent.aborted) ctrl.abort();
    else parent.addEventListener("abort", () => ctrl.abort(), { once: true });
  }
  return ctrl;
}

/**
 * Promise-based delay that rejects when the AbortSignal fires (mirrors
 * Task.Delay(ms, ct) throwing OperationCanceledException on cancel).
 */
function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    if (signal?.aborted) {
      reject(new Error("aborted"));
      return;
    }
    const t = setTimeout(() => {
      cleanup();
      resolve();
    }, ms);
    if (typeof t.unref === "function") t.unref();
    const onAbort = () => {
      clearTimeout(t);
      cleanup();
      reject(new Error("aborted"));
    };
    const cleanup = () => signal?.removeEventListener("abort", onAbort);
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

/** Await a promise, but reject early if the AbortSignal fires (mirrors Task.WaitAsync(ct)). */
function withSignal<T>(promise: Promise<T>, signal?: AbortSignal): Promise<T> {
  if (signal === undefined) return promise;
  return new Promise<T>((resolve, reject) => {
    if (signal.aborted) {
      reject(new Error("aborted"));
      return;
    }
    const onAbort = () => reject(new Error("aborted"));
    signal.addEventListener("abort", onAbort, { once: true });
    promise.then(
      (v) => {
        signal.removeEventListener("abort", onAbort);
        resolve(v);
      },
      (e) => {
        signal.removeEventListener("abort", onAbort);
        reject(e);
      },
    );
  });
}
