// aether/index.ts
// Full-parity port of CircleAI.Aether (C#). C# is the exact spec.
//
// CircleAI.Aether defines the one-way boundary between the Aether mesh runtime
// and BhenguAI. Aether PUBLISHES telemetry; BhenguAI SUBSCRIBES and reasons.
// Aether never calls into BhenguAI. Five contracts, mirrored here 1:1:
//
//   Contract 1 — Telemetry          IAetherTelemetry / IAetherTelemetryObserver
//                                    + the five event records and NullAetherTelemetry
//   Contract 2 — Presence           IAetherContext, AetherInstallLevel
//   Contract 3 — Intelligence output IAetherIntelligence + result records
//   Contract 4 — Security layer      IAISecurityLayer, SecurityDirective(Kind),
//                                    SecurityPosture, ISecurityDirectiveConsumer
//   Contract 5 — Auth challenge      IAuthChallenge, AuthChallengeReason,
//                                    AuthMethod, AuthChallengeResult
//
// Plus deterministic in-memory implementations (NO stubs) so the contracts are
// exercisable without a real mesh:
//   InMemoryAetherTelemetry   — a synchronous fan-out telemetry hub + publisher
//   InMemoryAetherContext     — a configurable IAetherContext
//   InMemoryAuthChallenge     — a scriptable IAuthChallenge with minimum enforcement
//
// The richer IAISecurityLayer / IAetherIntelligence in-memory implementations
// live in src/security/aethernet (AetherSecurityBridge / AetherIntelligenceAdapter)
// which wrap the transport-agnostic CircleAI.Security engine — that is the
// faithful in-memory realisation of these two contracts. A small self-contained
// InMemoryAetherIntelligence is provided here too for callers that want the
// Aether surface without the Security layer.
//
// CONCURRENCY: C# telemetry fan-out snapshots subscribers under a lock and then
// invokes callbacks OUTSIDE the lock, so a subscriber that disposes inside its
// own callback cannot corrupt iteration. The trust-score stream is an unbounded
// channel: writes made before a reader attaches are buffered and never lost.
// Both properties are preserved here (snapshot-before-dispatch; AsyncQueue).

import { AsyncQueue } from "../companion/herjarvis/async_queue.js";

// ─────────────────────────────────────────────────────────────────────────────
// IDisposable analogue
// ─────────────────────────────────────────────────────────────────────────────

/**
 * The TypeScript analogue of C# `IDisposable`. `Subscribe` returns one of these;
 * calling {@link dispose} unsubscribes. Disposal is idempotent.
 */
export interface IAetherDisposable {
  /** Idempotent unsubscribe / cleanup. */
  dispose(): void;
}

// ═════════════════════════════════════════════════════════════════════════════
// Contract 1 — Telemetry event records + enums
// ═════════════════════════════════════════════════════════════════════════════

// ── Node events ──────────────────────────────────────────────────────────────

/** Kinds of node lifecycle transitions Aether can emit. */
export enum AetherNodeEventKind {
  Joined = 0,
  Left = 1,
  HealthChanged = 2,
}

/**
 * Point-in-time health snapshot for a single mesh node.
 * `trustScore` runs 0.0 (untrusted) .. 1.0 (fully trusted); maintained by the
 * AI Security Layer when active, defaulting to 1.0 for all nodes when off.
 */
export interface AetherNodeHealth {
  readonly trustScore: number;
  readonly isReachable: boolean;
  /** Round-trip latency, in milliseconds (C# `TimeSpan`). */
  readonly latencyMs: number;
  readonly hopCount: number;
}

/** Returns true when `health.trustScore` is within the valid 0..1 range. */
export function aetherNodeHealthIsValid(health: AetherNodeHealth): boolean {
  return health.trustScore >= 0.0 && health.trustScore <= 1.0;
}

/**
 * Emitted by Aether whenever a node joins, leaves, or changes health.
 * Consumed by {@link IAetherTelemetry} subscribers — BhenguAI never writes
 * back into Aether directly.
 */
export interface AetherNodeEvent {
  readonly nodeId: string;
  readonly kind: AetherNodeEventKind;
  readonly health: AetherNodeHealth;
  readonly occurredAt: Date;
}

/** Convenience: true when this is a departure event. Mirrors `AetherNodeEvent.IsExit`. */
export function aetherNodeEventIsExit(e: AetherNodeEvent): boolean {
  return e.kind === AetherNodeEventKind.Left;
}

// ── Transport events ─────────────────────────────────────────────────────────

/** Physical or logical transport medium Aether is using. */
export enum AetherTransportKind {
  WiFi = 0,
  Bluetooth = 1,
  LoRa = 2,
  NFC = 3,
  Cellular = 4,
  Ethernet = 5,
  Unknown = 6,
}

/** Kinds of transport-layer observations Aether can emit. */
export enum AetherTransportEventKind {
  Selected = 0,
  Changed = 1,
  LatencyMeasured = 2,
  PacketLoss = 3,
}

/**
 * Emitted when Aether selects, changes, or measures quality on a transport
 * channel. The AI layer uses this to correlate transport behaviour with threat
 * patterns.
 */
export interface AetherTransportEvent {
  readonly nodeId: string;
  readonly kind: AetherTransportEventKind;
  readonly transport: AetherTransportKind;
  /** Latency in milliseconds, or null when unknown (C# `TimeSpan?`). */
  readonly latencyMs: number | null;
  readonly packetLossRate: number | null;
  readonly occurredAt: Date;
}

/**
 * Returns true when `packetLossRate` is set and exceeds `threshold` (0..1).
 * Mirrors `AetherTransportEvent.ExceedsLoss`.
 */
export function aetherTransportEventExceedsLoss(e: AetherTransportEvent, threshold: number): boolean {
  return e.packetLossRate !== null && e.packetLossRate > threshold;
}

// ── Route events ─────────────────────────────────────────────────────────────

/** Kinds of routing changes Aether can emit. */
export enum AetherRouteEventKind {
  Discovered = 0,
  Changed = 1,
  Failed = 2,
}

/**
 * Emitted when Aether discovers, updates, or loses a route between two nodes.
 * `path` describes the sequence of node IDs traversed.
 */
export interface AetherRouteEvent {
  readonly sourceNodeId: string;
  readonly destinationNodeId: string;
  readonly path: readonly string[];
  readonly kind: AetherRouteEventKind;
  readonly failureReason: string | null;
  readonly occurredAt: Date;
}

/** Number of hops in this route, including source and destination. Mirrors `HopCount`. */
export function aetherRouteEventHopCount(e: AetherRouteEvent): number {
  return e.path.length;
}

/** True when this event represents a routing failure. Mirrors `IsFailed`. */
export function aetherRouteEventIsFailed(e: AetherRouteEvent): boolean {
  return e.kind === AetherRouteEventKind.Failed;
}

// ── Security events ──────────────────────────────────────────────────────────

/**
 * Categories of security-relevant observations Aether can detect at the
 * protocol layer, without requiring AI. The AI Security Layer consumes these
 * events to produce threat assessments and directives.
 */
export enum AetherSecurityEventKind {
  /** A node attempted to authenticate into the mesh. */
  NodeAuthAttempt = 0,
  /** Traffic was observed deviating from expected routing paths. */
  RoutingAnomaly = 1,
  /** A node's behaviour deviated from its established baseline. */
  NodeBehaviourChange = 2,
  /** A key exchange or certificate validation event occurred. */
  EncryptionEvent = 3,
  /** Active attack signature detected (e.g. replay, spoofing). */
  IntrusionSignal = 4,
  /** A node requested capabilities beyond its granted level. */
  PrivilegeAttempt = 5,
}

/**
 * Protocol-level threat severity as assessed by Aether itself, before any AI
 * reasoning is applied. Ordinals are part of the wire contract.
 */
export enum AetherThreatLevel {
  None = 0,
  Low = 1,
  Medium = 2,
  High = 3,
  Critical = 4,
}

/**
 * Emitted by Aether when a security-relevant event occurs at the protocol
 * layer. This is the primary feed for the AI Security Layer. Aether never calls
 * into BhenguAI — it only emits; BhenguAI subscribes.
 */
export interface AetherSecurityEvent {
  readonly nodeId: string;
  readonly kind: AetherSecurityEventKind;
  readonly threatLevel: AetherThreatLevel;
  readonly description: string;
  readonly metadata: Readonly<Record<string, string>>;
  readonly occurredAt: Date;
}

/** True when `threatLevel` is High or Critical. Mirrors `IsHighSeverity`. */
export function aetherSecurityEventIsHighSeverity(e: AetherSecurityEvent): boolean {
  return e.threatLevel === AetherThreatLevel.High || e.threatLevel === AetherThreatLevel.Critical;
}

// ── Network events ───────────────────────────────────────────────────────────

/** Mesh-wide topology and congestion observations. */
export enum AetherNetworkEventKind {
  TopologyChanged = 0,
  CongestionDetected = 1,
  PartitionDetected = 2,
}

/**
 * Emitted when the mesh topology or overall network health changes. Provides
 * aggregate context that the AI layer uses alongside individual node events.
 */
export interface AetherNetworkEvent {
  readonly kind: AetherNetworkEventKind;
  readonly nodeCount: number;
  readonly activeRouteCount: number;
  readonly congestionLevel: number;
  readonly occurredAt: Date;
}

/**
 * True when `congestionLevel` exceeds 0.75 — a useful default alert threshold.
 * Mirrors `IsHighCongestion`.
 */
export function aetherNetworkEventIsHighCongestion(e: AetherNetworkEvent): boolean {
  return e.congestionLevel > 0.75;
}

// ═════════════════════════════════════════════════════════════════════════════
// Contract 1 — Telemetry surface
// ═════════════════════════════════════════════════════════════════════════════

/**
 * Receives events emitted by Aether. Implement this to react to mesh activity —
 * nodes, transports, routes, security signals, and topology.
 *
 * Each callback is optional at the value level, but the C# interface requires
 * all five; concrete observers should implement every method they care about.
 */
export interface IAetherTelemetryObserver {
  onNodeEvent(e: AetherNodeEvent): void;
  onTransportEvent(e: AetherTransportEvent): void;
  onRouteEvent(e: AetherRouteEvent): void;
  onSecurityEvent(e: AetherSecurityEvent): void;
  onNetworkEvent(e: AetherNetworkEvent): void;
}

/**
 * The outward-facing telemetry surface of Aether. The AI Security Layer and any
 * other BhenguAI component subscribes here. Aether owns this interface and
 * publishes; consumers subscribe and dispose.
 */
export interface IAetherTelemetry {
  /**
   * Subscribe to all Aether telemetry events. Dispose the returned handle to
   * unsubscribe.
   */
  subscribe(observer: IAetherTelemetryObserver): IAetherDisposable;
}

/**
 * No-op telemetry — useful for unit tests and environments where Aether is
 * absent. `subscribe` returns a no-op disposable; no events are emitted.
 * Mirrors `NullAetherTelemetry`.
 */
export class NullAetherTelemetry implements IAetherTelemetry {
  static readonly instance = new NullAetherTelemetry();

  subscribe(observer: IAetherTelemetryObserver): IAetherDisposable {
    if (observer == null) throw new Error("observer required");
    return { dispose(): void {} };
  }
}

/**
 * A working in-memory telemetry hub: it is both the {@link IAetherTelemetry}
 * subscription surface AND a publisher. Aether adapters (or tests) call the
 * `publish*` methods; every subscribed observer receives the event synchronously.
 *
 * Fan-out takes a snapshot of the subscriber set BEFORE invoking callbacks, so a
 * subscriber that disposes inside its own callback cannot corrupt iteration —
 * mirroring the C# `snapshot = [.. _observers]` under lock, callbacks outside it.
 */
export class InMemoryAetherTelemetry implements IAetherTelemetry {
  private readonly observers: IAetherTelemetryObserver[] = [];

  subscribe(observer: IAetherTelemetryObserver): IAetherDisposable {
    if (observer == null) throw new Error("observer required");
    this.observers.push(observer);
    let disposed = false;
    const self = this;
    return {
      dispose(): void {
        if (disposed) return;
        disposed = true;
        const i = self.observers.indexOf(observer);
        if (i >= 0) self.observers.splice(i, 1);
      },
    };
  }

  /** Number of currently-attached observers. Useful in tests. */
  get subscriberCount(): number {
    return this.observers.length;
  }

  publishNode(e: AetherNodeEvent): void {
    for (const o of [...this.observers]) o.onNodeEvent(e);
  }

  publishTransport(e: AetherTransportEvent): void {
    for (const o of [...this.observers]) o.onTransportEvent(e);
  }

  publishRoute(e: AetherRouteEvent): void {
    for (const o of [...this.observers]) o.onRouteEvent(e);
  }

  publishSecurity(e: AetherSecurityEvent): void {
    for (const o of [...this.observers]) o.onSecurityEvent(e);
  }

  publishNetwork(e: AetherNetworkEvent): void {
    for (const o of [...this.observers]) o.onNetworkEvent(e);
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// Contract 2 — Presence and capability
// ═════════════════════════════════════════════════════════════════════════════

/** Indicates where Aether is installed and who manages it. */
export enum AetherInstallLevel {
  /** Aether is not present on this device. */
  None = 0,
  /**
   * Aether was installed at app level — bundled with the app or downloaded at
   * first launch. Updated independently by the app.
   */
  App = 1,
  /**
   * Aether is a system service managed by the OS. Always present on TGN devices.
   * Updated with OS updates. Requires biometric + device admin auth to toggle.
   */
  OS = 2,
}

/**
 * A parsed semantic version, sufficient for the `>=` comparison Aether needs.
 * Mirrors just enough of System.Version for the `IsSufficient` check.
 */
export interface AetherVersion {
  readonly major: number;
  readonly minor: number;
  readonly build: number;
  readonly revision: number;
}

/** Builds an {@link AetherVersion}; missing components default to 0 (as System.Version does not, but the -1 sentinel is normalised to 0 here for `>=`). */
export function aetherVersion(major: number, minor = 0, build = 0, revision = 0): AetherVersion {
  return { major, minor, build, revision };
}

/**
 * Compares two versions component-by-component: negative if a < b, 0 if equal,
 * positive if a > b. Mirrors System.Version's ordering.
 */
export function compareAetherVersion(a: AetherVersion, b: AetherVersion): number {
  if (a.major !== b.major) return a.major - b.major;
  if (a.minor !== b.minor) return a.minor - b.minor;
  if (a.build !== b.build) return a.build - b.build;
  return a.revision - b.revision;
}

/**
 * Reports the presence, version, and capability of the Aether runtime on this
 * device. Inject via DI; the platform adapter (MAUI, server) provides the
 * concrete implementation.
 */
export interface IAetherContext {
  /** Where Aether is installed, if at all. */
  readonly installLevel: AetherInstallLevel;
  /** True when Aether is installed and enabled. */
  readonly isAvailable: boolean;
  /** The installed Aether runtime version, or null when Aether is absent. */
  readonly runtimeVersion: AetherVersion | null;
  /** The minimum Aether version declared as required by the consuming app. */
  readonly minimumRequired: AetherVersion | null;
  /**
   * True when {@link runtimeVersion} satisfies {@link minimumRequired}. Always
   * true when minimumRequired is null.
   */
  readonly isSufficient: boolean;
  /**
   * True when the install level is {@link AetherInstallLevel.OS}. OS-managed
   * instances require biometric + device admin auth before they can be toggled.
   */
  readonly requiresAuth: boolean;
  /**
   * True when Aether is installed and currently enabled. An OS-managed instance
   * that has been toggled off returns false here.
   */
  readonly isEnabled: boolean;
}

/** Configuration for {@link InMemoryAetherContext}. */
export interface InMemoryAetherContextOptions {
  installLevel?: AetherInstallLevel;
  runtimeVersion?: AetherVersion | null;
  minimumRequired?: AetherVersion | null;
  isEnabled?: boolean;
}

/**
 * A configurable in-memory {@link IAetherContext}. Computes `isAvailable`,
 * `isSufficient`, and `requiresAuth` from the supplied fields exactly as the
 * C# platform adapters do.
 *
 * `isAvailable` is true when the install level is not None AND the instance is
 * enabled (matches "installed and enabled"). `isSufficient` mirrors
 * `MinimumRequired is null || (RuntimeVersion is not null && RuntimeVersion >= MinimumRequired)`.
 */
export class InMemoryAetherContext implements IAetherContext {
  readonly installLevel: AetherInstallLevel;
  readonly runtimeVersion: AetherVersion | null;
  readonly minimumRequired: AetherVersion | null;
  private readonly enabled: boolean;

  constructor(options: InMemoryAetherContextOptions = {}) {
    this.installLevel = options.installLevel ?? AetherInstallLevel.App;
    this.runtimeVersion = options.runtimeVersion ?? null;
    this.minimumRequired = options.minimumRequired ?? null;
    this.enabled = options.isEnabled ?? true;
  }

  get isEnabled(): boolean {
    return this.enabled;
  }

  get isAvailable(): boolean {
    return this.installLevel !== AetherInstallLevel.None && this.enabled;
  }

  get isSufficient(): boolean {
    if (this.minimumRequired === null) return true;
    return this.runtimeVersion !== null && compareAetherVersion(this.runtimeVersion, this.minimumRequired) >= 0;
  }

  get requiresAuth(): boolean {
    return this.installLevel === AetherInstallLevel.OS;
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// Contract 3 — Intelligence output
// ═════════════════════════════════════════════════════════════════════════════

/** Aggregate health of the mesh as assessed by BhenguAI. */
export interface NetworkHealthReport {
  readonly overallScore: number;
  readonly trustedNodeCount: number;
  readonly suspiciousNodeCount: number;
  readonly summary: string;
  readonly generatedAt: Date;
}

/** True when `overallScore` is within the valid 0..1 range. Mirrors `IsValid`. */
export function networkHealthReportIsValid(r: NetworkHealthReport): boolean {
  return r.overallScore >= 0.0 && r.overallScore <= 1.0;
}

/** BhenguAI's assessment of the threat posed by a specific node. */
export interface ThreatAssessment {
  readonly nodeId: string;
  readonly threatConfidence: number;
  readonly level: AetherThreatLevel;
  readonly indicators: readonly string[];
  readonly assessedAt: Date;
}

/** True when `threatConfidence` is within the valid 0..1 range. Mirrors `IsValid`. */
export function threatAssessmentIsValid(a: ThreatAssessment): boolean {
  return a.threatConfidence >= 0.0 && a.threatConfidence <= 1.0;
}

/**
 * BhenguAI's recommendation for routing to a destination node, taking trust
 * scores and current threat assessments into account.
 */
export interface RoutingAdvice {
  readonly destinationNodeId: string;
  readonly recommendedPath: readonly string[];
  readonly avoidNodes: readonly string[];
  readonly confidence: number;
  readonly reasoning: string;
  readonly generatedAt: Date;
}

/** Emitted when BhenguAI revises the trust score for a node. */
export interface TrustScoreUpdate {
  readonly nodeId: string;
  readonly previousScore: number;
  readonly currentScore: number;
  readonly reason: string;
  readonly updatedAt: Date;
}

/** True when the score moved in either direction (> 0.001). Mirrors `HasChanged`. */
export function trustScoreUpdateHasChanged(u: TrustScoreUpdate): boolean {
  return Math.abs(u.currentScore - u.previousScore) > 0.001;
}

/** True when the score decreased. Mirrors `IsDegraded`. */
export function trustScoreUpdateIsDegraded(u: TrustScoreUpdate): boolean {
  return u.currentScore < u.previousScore;
}

/**
 * The intelligence output surface produced by BhenguAI from Aether telemetry.
 * Consumed by apps and the Security Layer; never by Aether.
 */
export interface IAetherIntelligence {
  /** Returns an aggregate health report for the current mesh state. */
  getNetworkHealthAsync(signal?: AbortSignal): Promise<NetworkHealthReport>;

  /**
   * Assesses the current threat level of a specific node. Returns a
   * zero-confidence assessment when the node is unknown.
   */
  assessThreatAsync(nodeId: string, signal?: AbortSignal): Promise<ThreatAssessment>;

  /**
   * Returns a routing recommendation for reaching the given destination,
   * factoring out nodes with low trust scores.
   */
  getRoutingAdviceAsync(destinationNodeId: string, signal?: AbortSignal): Promise<RoutingAdvice>;

  /**
   * Streams trust score updates as BhenguAI observes new telemetry. Useful for
   * live dashboards and security monitoring UIs.
   */
  streamTrustScoresAsync(signal?: AbortSignal): AsyncGenerator<TrustScoreUpdate>;
}

// ═════════════════════════════════════════════════════════════════════════════
// Contract 4 — Security layer
// ═════════════════════════════════════════════════════════════════════════════

/** The action BhenguAI is recommending to Aether's policy engine. */
export enum SecurityDirectiveKind {
  /** Adjust the recorded trust score for a node. */
  UpdateNodeTrust = 0,
  /** Exclude the node from routing decisions (soft block). */
  AvoidNode = 1,
  /** Hard block — no traffic to or from the node until released. */
  QuarantineNode = 2,
  /** Lift an AvoidNode or QuarantineNode directive. */
  ReleaseNode = 3,
  /** Request that the user re-authenticates before a sensitive operation. */
  RequestReauth = 4,
  /** Increase telemetry verbosity for the target node. */
  ElevateMonitoring = 5,
}

/**
 * An instruction published by the AI Security Layer to Aether's policy engine.
 * Aether is never required to honour a directive — adoption is a policy decision
 * for each deployment.
 */
export interface SecurityDirective {
  readonly kind: SecurityDirectiveKind;
  readonly targetNodeId: string | null;
  readonly trustScoreOverride: number | null;
  readonly threatLevel: AetherThreatLevel;
  readonly reason: string;
  /** Optional expiry, in milliseconds (C# `TimeSpan?`). null = permanent. */
  readonly durationMs: number | null;
  readonly issuedAt: Date;
}

/** True when the directive targets a specific node. Mirrors `HasTarget`. */
export function securityDirectiveHasTarget(d: SecurityDirective): boolean {
  return d.targetNodeId !== null && d.targetNodeId.trim().length > 0;
}

/** True when `durationMs` is null — the directive has no automatic expiry. Mirrors `IsPermanent`. */
export function securityDirectiveIsPermanent(d: SecurityDirective): boolean {
  return d.durationMs === null;
}

/** Point-in-time summary of the AI Security Layer's current posture. */
export interface SecurityPosture {
  readonly overallThreatLevel: AetherThreatLevel;
  readonly quarantinedNodeCount: number;
  readonly monitoredNodeCount: number;
  readonly isActive: boolean;
  readonly assessedAt: Date;
}

/**
 * Receives security directives from the AI Security Layer. Implement this on
 * Aether's policy engine to participate in AI-guided security decisions.
 */
export interface ISecurityDirectiveConsumer {
  /**
   * Called each time BhenguAI issues a security directive. Implementations
   * decide whether and how to honour it.
   */
  onDirective(directive: SecurityDirective): void;
}

/**
 * The AI Security Layer contract. BhenguAI implements this by subscribing to
 * {@link IAetherTelemetry} and producing {@link SecurityDirective} outputs
 * consumed by Aether's policy engine via {@link ISecurityDirectiveConsumer}.
 */
export interface IAISecurityLayer {
  /**
   * Wire the security layer to an Aether telemetry feed and begin processing
   * events.
   */
  startAsync(telemetry: IAetherTelemetry, signal?: AbortSignal): Promise<void>;

  /** Stop processing and release all telemetry subscriptions. */
  stopAsync(signal?: AbortSignal): Promise<void>;

  /**
   * Subscribe a policy engine to receive security directives. Dispose the
   * returned handle to unsubscribe.
   */
  subscribeToDirectives(consumer: ISecurityDirectiveConsumer): IAetherDisposable;

  /** Returns the current security posture snapshot. */
  getPostureAsync(signal?: AbortSignal): Promise<SecurityPosture>;
}

// ═════════════════════════════════════════════════════════════════════════════
// Contract 5 — Auth challenge
// ═════════════════════════════════════════════════════════════════════════════

/** Why an auth challenge is being issued. */
export enum AuthChallengeReason {
  /** The user is enabling or disabling the OS-level Aether service. */
  OsLevelToggle = 0,
  /**
   * The AI Security Layer detected anomaly scores above the configured threshold
   * and requires the user to confirm their identity.
   */
  ThreatThresholdReached = 1,
  /** The operation being attempted requires elevated auth. */
  PrivilegedOperation = 2,
  /** Scheduled trust renewal — periodic re-validation. */
  PeriodicRevalidation = 3,
  /** Explicitly triggered by the developer or admin. */
  ManualRequest = 4,
}

/**
 * The authentication method used or required. Methods are ordered by strength;
 * higher numeric values are stronger. Values are the wire contract.
 */
export enum AuthMethod {
  /** Fingerprint, face, or iris recognition. */
  Biometric = 1,
  /** Device administrator credential (PIN, password, pattern). */
  DeviceAdmin = 2,
  /** Biometric AND device admin — the minimum for any OS-level operation. */
  BiometricAndDeviceAdmin = 3,
  /** Developer-defined method layered on top of BiometricAndDeviceAdmin. */
  Custom = 4,
}

/** The outcome of an auth challenge. */
export class AuthChallengeResult {
  readonly succeeded: boolean;
  readonly methodUsed: AuthMethod;
  readonly failureReason: string | null;
  readonly completedAt: Date;

  constructor(succeeded: boolean, methodUsed: AuthMethod, failureReason: string | null, completedAt: Date) {
    this.succeeded = succeeded;
    this.methodUsed = methodUsed;
    this.failureReason = failureReason;
    this.completedAt = completedAt;
  }

  /** Convenience: a successful result with no failure reason. */
  static success(method: AuthMethod): AuthChallengeResult {
    return new AuthChallengeResult(true, method, null, new Date());
  }

  /** Convenience: a failed result with an explanatory reason. */
  static failure(method: AuthMethod, reason: string): AuthChallengeResult {
    return new AuthChallengeResult(false, method, reason, new Date());
  }
}

/**
 * Issues and resolves authentication challenges for security-sensitive
 * operations. Platform adapters (MAUI, server) implement this using native
 * biometric and device admin APIs.
 */
export interface IAuthChallenge {
  /**
   * Presents an auth challenge to the user for the given reason. The platform
   * adapter enforces the minimum method requirement.
   *
   * @param minimumMethod The weakest method acceptable. Defaults to
   *   {@link AuthMethod.BiometricAndDeviceAdmin} when null.
   */
  challengeAsync(
    reason: AuthChallengeReason,
    minimumMethod: AuthMethod | null,
    prompt: string,
    signal?: AbortSignal,
  ): Promise<AuthChallengeResult>;

  /**
   * Presents the OS-level toggle challenge. Always requires
   * {@link AuthMethod.BiometricAndDeviceAdmin} at minimum.
   */
  requestOsToggleAsync(enable: boolean, signal?: AbortSignal): Promise<AuthChallengeResult>;
}

/**
 * A scriptable in-memory {@link IAuthChallenge} for tests and headless flows.
 *
 * Behaviour:
 *   - The minimum acceptable method defaults to `BiometricAndDeviceAdmin` when
 *     the caller passes null, exactly as the C# doc contract specifies.
 *   - `requestOsToggleAsync` always enforces a floor of `BiometricAndDeviceAdmin`
 *     (a caller cannot lower the OS-toggle bar below the minimum).
 *   - `availableMethod` is the strongest method this device/user can satisfy;
 *     the challenge succeeds iff `availableMethod >= effectiveMinimum` AND the
 *     ring is not scripted to deny.
 *
 * This is deterministic: no real biometrics, no prompts — the outcome is a pure
 * function of the configured `availableMethod` and `denyReason`.
 */
export class InMemoryAuthChallenge implements IAuthChallenge {
  /** The strongest method this simulated device can produce. */
  availableMethod: AuthMethod;
  /** When set, every challenge fails with this reason regardless of method. */
  denyReason: string | null;
  /** Records every challenge issued, for assertions in tests. */
  readonly issued: Array<{ reason: AuthChallengeReason; minimum: AuthMethod; prompt: string }> = [];

  constructor(availableMethod: AuthMethod = AuthMethod.BiometricAndDeviceAdmin, denyReason: string | null = null) {
    this.availableMethod = availableMethod;
    this.denyReason = denyReason;
  }

  challengeAsync(
    reason: AuthChallengeReason,
    minimumMethod: AuthMethod | null,
    prompt: string,
    signal?: AbortSignal,
  ): Promise<AuthChallengeResult> {
    return this.run(reason, minimumMethod ?? AuthMethod.BiometricAndDeviceAdmin, prompt, signal);
  }

  requestOsToggleAsync(enable: boolean, signal?: AbortSignal): Promise<AuthChallengeResult> {
    const prompt = enable ? "Enable the Aether service?" : "Disable the Aether service?";
    // OS toggle can never go below BiometricAndDeviceAdmin.
    return this.run(AuthChallengeReason.OsLevelToggle, AuthMethod.BiometricAndDeviceAdmin, prompt, signal);
  }

  private run(
    reason: AuthChallengeReason,
    minimum: AuthMethod,
    prompt: string,
    signal?: AbortSignal,
  ): Promise<AuthChallengeResult> {
    this.issued.push({ reason, minimum, prompt });

    if (signal?.aborted) {
      return Promise.resolve(AuthChallengeResult.failure(this.availableMethod, "cancelled"));
    }
    if (this.denyReason !== null) {
      return Promise.resolve(AuthChallengeResult.failure(this.availableMethod, this.denyReason));
    }
    if (this.availableMethod < minimum) {
      return Promise.resolve(
        AuthChallengeResult.failure(
          this.availableMethod,
          `available method ${AuthMethod[this.availableMethod]} is weaker than required ${AuthMethod[minimum]}`,
        ),
      );
    }
    // Succeeds at exactly the minimum required strength (what the gate demanded).
    return Promise.resolve(AuthChallengeResult.success(minimum));
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// Self-contained in-memory IAetherIntelligence
// ═════════════════════════════════════════════════════════════════════════════

/**
 * A minimal, dependency-free in-memory {@link IAetherIntelligence}. It maintains
 * per-node trust scores fed from {@link AetherSecurityEvent}s (via
 * {@link recordSecurityEvent}) and answers the four intelligence queries with
 * the same score→threat classification the Security layer uses.
 *
 * This is intentionally lighter than the Security-backed
 * `AetherIntelligenceAdapter` in src/security/aethernet; use that adapter when
 * you want the full trust-decay/recovery engine. Use this when you just need a
 * self-contained implementation of the Aether intelligence surface.
 *
 * The trust-score stream is an unbounded queue: updates recorded before a reader
 * attaches are buffered and delivered, never lost (mirrors the C# unbounded
 * channel).
 */
export class InMemoryAetherIntelligence implements IAetherIntelligence {
  private readonly scores = new Map<string, number>();
  private readonly indicators = new Map<string, string[]>();
  private readonly stream = new AsyncQueue<TrustScoreUpdate>();

  /** Trust threshold at/below which a node is considered suspicious. */
  static readonly suspiciousThreshold = 0.75;
  /** Trust threshold at/below which a node is avoided in routing. */
  static readonly avoidThreshold = 0.5;

  /**
   * Directly sets a node's trust score (0..1). Publishes a
   * {@link TrustScoreUpdate} when the value actually changes.
   */
  setTrust(nodeId: string, score: number, reason = "manual"): void {
    const clamped = Math.min(1, Math.max(0, score));
    const previous = this.scores.get(nodeId) ?? 1.0;
    this.scores.set(nodeId, clamped);
    if (Math.abs(clamped - previous) > 0.0001) {
      this.stream.enqueue({ nodeId, previousScore: previous, currentScore: clamped, reason, updatedAt: new Date() });
    }
  }

  /**
   * Folds an Aether security event into the node's trust score using the same
   * base-weight × threat-multiplier degradation model as
   * `CircleAI.Security.ThreatDetector`. Records any indicator tags too.
   */
  recordSecurityEvent(e: AetherSecurityEvent): void {
    const degradation = baseWeight(e.kind) * threatMultiplier(e.threatLevel);
    const previous = this.scores.get(e.nodeId) ?? 1.0;
    const next = Math.min(1, Math.max(0, previous - degradation));
    this.scores.set(e.nodeId, next);

    if (aetherSecurityEventIsHighSeverity(e)) this.addIndicator(e.nodeId, "high-severity-event");
    if (e.kind === AetherSecurityEventKind.IntrusionSignal) this.addIndicator(e.nodeId, "intrusion-signal-detected");
    if (e.kind === AetherSecurityEventKind.PrivilegeAttempt) this.addIndicator(e.nodeId, "privilege-escalation-attempt");

    if (Math.abs(next - previous) > 0.0001) {
      this.stream.enqueue({
        nodeId: e.nodeId,
        previousScore: previous,
        currentScore: next,
        reason: e.description,
        updatedAt: e.occurredAt,
      });
    }
  }

  getNetworkHealthAsync(_signal?: AbortSignal): Promise<NetworkHealthReport> {
    const ids = [...this.scores.keys()];
    if (ids.length === 0) {
      return Promise.resolve({
        overallScore: 1.0,
        trustedNodeCount: 0,
        suspiciousNodeCount: 0,
        summary: "No nodes observed.",
        generatedAt: new Date(),
      });
    }
    const scores = ids.map((id) => this.scores.get(id)!);
    const overall = scores.reduce((a, b) => a + b, 0) / scores.length;
    const trusted = scores.filter((s) => s > InMemoryAetherIntelligence.avoidThreshold).length;
    const suspicious = scores.filter((s) => s <= InMemoryAetherIntelligence.suspiciousThreshold).length;
    return Promise.resolve({
      overallScore: overall,
      trustedNodeCount: trusted,
      suspiciousNodeCount: suspicious,
      summary: healthSummary(overall),
      generatedAt: new Date(),
    });
  }

  assessThreatAsync(nodeId: string, _signal?: AbortSignal): Promise<ThreatAssessment> {
    const score = this.scores.get(nodeId) ?? 1.0;
    const deficit = 1.0 - score;
    const inds = this.indicators.get(nodeId) ?? [];
    const confidence = Math.min(1.0, deficit + inds.length * 0.1);
    return Promise.resolve({
      nodeId,
      threatConfidence: confidence,
      level: scoreToThreatLevel(score),
      indicators: [...inds],
      assessedAt: new Date(),
    });
  }

  getRoutingAdviceAsync(destinationNodeId: string, _signal?: AbortSignal): Promise<RoutingAdvice> {
    const avoid = [...this.scores.keys()].filter(
      (id) => (this.scores.get(id) ?? 1.0) <= InMemoryAetherIntelligence.avoidThreshold,
    );
    const destScore = this.scores.get(destinationNodeId) ?? 1.0;
    const recommended = destScore > InMemoryAetherIntelligence.avoidThreshold ? [destinationNodeId] : [];
    return Promise.resolve({
      destinationNodeId,
      recommendedPath: recommended,
      avoidNodes: avoid,
      confidence: destScore,
      reasoning: routingReasoning(destinationNodeId, destScore),
      generatedAt: new Date(),
    });
  }

  async *streamTrustScoresAsync(signal?: AbortSignal): AsyncGenerator<TrustScoreUpdate> {
    yield* this.stream.drain(signal);
  }

  private addIndicator(nodeId: string, tag: string): void {
    let list = this.indicators.get(nodeId);
    if (list === undefined) {
      list = [];
      this.indicators.set(nodeId, list);
    }
    if (!list.includes(tag)) list.push(tag);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared classification helpers (private)
// ─────────────────────────────────────────────────────────────────────────────

/** Score → Aether threat-level classification (same thresholds as the Security port). */
function scoreToThreatLevel(score: number): AetherThreatLevel {
  if (score <= 0.25) return AetherThreatLevel.Critical;
  if (score <= 0.5) return AetherThreatLevel.High;
  if (score <= 0.75) return AetherThreatLevel.Medium;
  if (score <= 0.9) return AetherThreatLevel.Low;
  return AetherThreatLevel.None;
}

function healthSummary(overall: number): string {
  if (overall > 0.9) return "Network health is excellent.";
  if (overall > 0.75) return "Network health is good; minor anomalies detected.";
  if (overall > 0.5) return "Network health is degraded; elevated monitoring active.";
  if (overall > 0.25) return "Network health is poor; routing around compromised peers.";
  return "Network health is critical; quarantine directives in effect.";
}

function routingReasoning(dest: string, destScore: number): string {
  if (destScore > 0.75) return `Direct path to ${dest} is trusted (score ${destScore.toFixed(2)}).`;
  if (destScore > 0.5) return `Destination ${dest} is under monitoring; routing with caution.`;
  if (destScore > 0.25) return `Destination ${dest} has degraded trust; avoid recommended.`;
  return `Destination ${dest} is quarantined; no safe path available.`;
}

/** Degradation base weight per Aether security event kind. */
function baseWeight(kind: AetherSecurityEventKind): number {
  switch (kind) {
    case AetherSecurityEventKind.NodeAuthAttempt:
      return 0.05;
    case AetherSecurityEventKind.RoutingAnomaly:
      return 0.1;
    case AetherSecurityEventKind.NodeBehaviourChange:
      return 0.08;
    case AetherSecurityEventKind.EncryptionEvent:
      return 0.06;
    case AetherSecurityEventKind.IntrusionSignal:
      return 0.15;
    case AetherSecurityEventKind.PrivilegeAttempt:
      return 0.12;
    default:
      return 0.05;
  }
}

/** Threat-level multiplier for degradation. */
function threatMultiplier(level: AetherThreatLevel): number {
  switch (level) {
    case AetherThreatLevel.None:
      return 0.0;
    case AetherThreatLevel.Low:
      return 0.5;
    case AetherThreatLevel.Medium:
      return 1.0;
    case AetherThreatLevel.High:
      return 2.0;
    case AetherThreatLevel.Critical:
      return 3.0;
    default:
      return 1.0;
  }
}
