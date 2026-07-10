// security/aethernet/index.ts
// Full-parity port of CircleAI.Security.AetherNet (C#). C# is the exact spec.
//
// These are the AetherNet-specific security bindings — the glue that connects
// the Aether mesh contracts (CircleAI.Aether) to the transport-agnostic
// peer-intelligence engine (CircleAI.Security, already ported at ../index.ts):
//
//   AetherMapper                 — static enum translation (Aether ↔ Peer types)
//   MeshDirectiveStore           — in-memory ISecurityDirectiveConsumer + block query
//   MeshSecurityGate             — read-only "is this id blocked?" query surface
//     GateDecision / MeshSecurityBlockedException
//   AetherSecurityBridge         — IAISecurityLayer over SecurityLayerService
//   AetherIntelligenceAdapter    — IAetherIntelligence over PeerIntelligenceService
//   MeshGatedCompanionSession    — ICompanionSession decorator that consults the gate
//
// The store's expiry is lazy-on-read (no background timer to leak). Block state
// observes Avoid + Quarantine; Release lifts both.
//
// Duration units: C# `SecurityDirective.Duration` / `PeerDirective.Duration` are
// both `TimeSpan?`. The TS ports model both as `durationMs: number | null`, so
// the bridge/store pass the value through unchanged.

import {
  // enums
  AetherSecurityEventKind,
  AetherThreatLevel,
  SecurityDirectiveKind,
  // records / types
  type SecurityDirective,
  type SecurityPosture,
  type NetworkHealthReport,
  type ThreatAssessment,
  type RoutingAdvice,
  type TrustScoreUpdate,
  // contracts
  type ISecurityDirectiveConsumer,
  type IAISecurityLayer,
  type IAetherIntelligence,
  type IAetherTelemetry,
  type IAetherTelemetryObserver,
  type IAetherDisposable,
  type AetherNodeEvent,
  type AetherTransportEvent,
  type AetherRouteEvent,
  type AetherSecurityEvent,
  type AetherNetworkEvent,
  securityDirectiveHasTarget,
  aetherNodeEventIsExit,
} from "../../aether/index.js";

import {
  PeerSecurityEventKind,
  PeerThreatLevel,
  PeerDirectiveKind,
  SecurityLayerService,
  PeerIntelligenceService,
  type PeerSecurityEvent,
  type PeerDirective,
  type IPeerDirectiveConsumer,
  type IDisposableHandle,
} from "../index.js";

import type {
  ICompanionSession,
  CompanionContext,
  CompanionTurn,
  InterfaceKind,
  ProactiveMessageHandler,
} from "../../companion/index.js";

// ═════════════════════════════════════════════════════════════════════════════
// AetherMapper — static translation helpers
// ═════════════════════════════════════════════════════════════════════════════

/**
 * Static helpers that translate between Aether-specific types and the
 * transport-agnostic Peer types defined in `CircleAI.Security`. Every mapping is
 * an explicit switch, defaulting to the same fallbacks the C# `switch`
 * expressions use.
 */
export const AetherMapper = {
  /** AetherSecurityEventKind → PeerSecurityEventKind (unknown → Unknown). */
  toPeerEventKind(kind: AetherSecurityEventKind): PeerSecurityEventKind {
    switch (kind) {
      case AetherSecurityEventKind.NodeAuthAttempt:
        return PeerSecurityEventKind.AuthAttempt;
      case AetherSecurityEventKind.RoutingAnomaly:
        return PeerSecurityEventKind.RoutingAnomaly;
      case AetherSecurityEventKind.NodeBehaviourChange:
        return PeerSecurityEventKind.BehaviourChange;
      case AetherSecurityEventKind.EncryptionEvent:
        return PeerSecurityEventKind.EncryptionEvent;
      case AetherSecurityEventKind.IntrusionSignal:
        return PeerSecurityEventKind.IntrusionSignal;
      case AetherSecurityEventKind.PrivilegeAttempt:
        return PeerSecurityEventKind.PrivilegeAttempt;
      default:
        return PeerSecurityEventKind.Unknown;
    }
  },

  /** AetherThreatLevel → PeerThreatLevel (unknown → None). */
  toPeerThreatLevel(level: AetherThreatLevel): PeerThreatLevel {
    switch (level) {
      case AetherThreatLevel.None:
        return PeerThreatLevel.None;
      case AetherThreatLevel.Low:
        return PeerThreatLevel.Low;
      case AetherThreatLevel.Medium:
        return PeerThreatLevel.Medium;
      case AetherThreatLevel.High:
        return PeerThreatLevel.High;
      case AetherThreatLevel.Critical:
        return PeerThreatLevel.Critical;
      default:
        return PeerThreatLevel.None;
    }
  },

  /** PeerThreatLevel → AetherThreatLevel (unknown → None). */
  toAetherThreatLevel(level: PeerThreatLevel): AetherThreatLevel {
    switch (level) {
      case PeerThreatLevel.None:
        return AetherThreatLevel.None;
      case PeerThreatLevel.Low:
        return AetherThreatLevel.Low;
      case PeerThreatLevel.Medium:
        return AetherThreatLevel.Medium;
      case PeerThreatLevel.High:
        return AetherThreatLevel.High;
      case PeerThreatLevel.Critical:
        return AetherThreatLevel.Critical;
      default:
        return AetherThreatLevel.None;
    }
  },

  /** PeerDirectiveKind → SecurityDirectiveKind (unknown → ElevateMonitoring). */
  toSecurityDirectiveKind(kind: PeerDirectiveKind): SecurityDirectiveKind {
    switch (kind) {
      case PeerDirectiveKind.ElevateMonitoring:
        return SecurityDirectiveKind.ElevateMonitoring;
      case PeerDirectiveKind.AvoidNode:
        return SecurityDirectiveKind.AvoidNode;
      case PeerDirectiveKind.QuarantineNode:
        return SecurityDirectiveKind.QuarantineNode;
      case PeerDirectiveKind.ReleaseNode:
        return SecurityDirectiveKind.ReleaseNode;
      default:
        return SecurityDirectiveKind.ElevateMonitoring;
    }
  },
} as const;

// ═════════════════════════════════════════════════════════════════════════════
// MeshDirectiveStore — in-memory ISecurityDirectiveConsumer + block query
// ═════════════════════════════════════════════════════════════════════════════

/**
 * In-memory registry of security directives received from the mesh. Acts as both
 * the directive sink ({@link ISecurityDirectiveConsumer}) and the query surface
 * that other CircleAI components consult before serving a request.
 *
 * Expiry is handled lazily on read — no background timer. Block state observes
 * Avoid + Quarantine; Release lifts every tracked directive for the node.
 */
export class MeshDirectiveStore implements ISecurityDirectiveConsumer {
  private readonly byNode = new Map<string, SecurityDirective[]>();
  private readonly clock: () => Date;

  /** Constructs a store; the optional `clock` (defaults to `() => new Date()`) drives expiry. */
  constructor(clock: () => Date = () => new Date()) {
    if (clock == null) throw new Error("clock required");
    this.clock = clock;
  }

  onDirective(directive: SecurityDirective): void {
    if (directive == null) throw new Error("directive required");
    if (!securityDirectiveHasTarget(directive)) return;
    const nodeId = directive.targetNodeId!;

    if (directive.kind === SecurityDirectiveKind.ReleaseNode) {
      // Release lifts every Avoid/Quarantine for the node.
      this.byNode.delete(nodeId);
      return;
    }

    const list = this.byNode.get(nodeId);
    if (list === undefined) {
      this.byNode.set(nodeId, [directive]);
    } else {
      list.push(directive);
    }
  }

  /**
   * Returns `{ blocked, reason }`. `blocked` is true when an unexpired Avoid or
   * Quarantine directive is active for the node; `reason` carries the most recent
   * block's reason text (empty string when not blocked).
   *
   * C# exposes this as `bool IsBlocked(string, out string reason)`; TS has no
   * `out` parameter so it returns an object. The lazy expiry sweep (drop expired
   * entries while walking, remove the node when its list empties) is preserved.
   */
  isBlocked(nodeId: string): { blocked: boolean; reason: string } {
    if (!nodeId || nodeId.trim().length === 0) return { blocked: false, reason: "" };
    const list = this.byNode.get(nodeId);
    if (list === undefined) return { blocked: false, reason: "" };

    const now = this.clock();
    let latestBlock: SecurityDirective | null = null;

    // Drop expired entries while we walk the list (iterate high→low for safe splice).
    for (let i = list.length - 1; i >= 0; i--) {
      const d = list[i];
      if (isExpired(d, now)) {
        list.splice(i, 1);
        continue;
      }
      if (isBlockKind(d.kind) && (latestBlock === null || d.issuedAt.getTime() > latestBlock.issuedAt.getTime())) {
        latestBlock = d;
      }
    }
    if (list.length === 0) this.byNode.delete(nodeId);

    if (latestBlock === null) return { blocked: false, reason: "" };
    return { blocked: true, reason: latestBlock.reason };
  }

  /** Lists every unexpired directive for the node — useful for audit/diagnostics. */
  getActiveDirectives(nodeId: string): readonly SecurityDirective[] {
    if (!nodeId || nodeId.trim().length === 0) return [];
    const list = this.byNode.get(nodeId);
    if (list === undefined) return [];
    const now = this.clock();
    return list.filter((d) => !isExpired(d, now));
  }

  /** Number of nodes with at least one tracked directive. */
  get trackedNodeCount(): number {
    return this.byNode.size;
  }
}

function isBlockKind(k: SecurityDirectiveKind): boolean {
  return k === SecurityDirectiveKind.AvoidNode || k === SecurityDirectiveKind.QuarantineNode;
}

function isExpired(d: SecurityDirective, now: Date): boolean {
  return d.durationMs !== null && d.issuedAt.getTime() + d.durationMs <= now.getTime();
}

// ═════════════════════════════════════════════════════════════════════════════
// MeshSecurityGate — read-only fast-path query surface
// ═════════════════════════════════════════════════════════════════════════════

/** Decision returned from {@link MeshSecurityGate.decide}. Mirrors the C# readonly record struct. */
export interface GateDecision {
  readonly isBlocked: boolean;
  readonly reason: string;
}

/** Convenience: allow with no reason text. Mirrors `GateDecision.Allowed`. */
export const GateDecisionAllowed: GateDecision = { isBlocked: false, reason: "" };

/**
 * Thrown by {@link MeshSecurityGate.enforce} when the mesh has issued a block
 * directive against the requesting id. Mirrors `MeshSecurityBlockedException`.
 */
export class MeshSecurityBlockedException extends Error {
  readonly blockedId: string;

  constructor(blockedId: string, reason: string) {
    super(`Mesh has blocked '${blockedId}': ${reason}`);
    this.name = "MeshSecurityBlockedException";
    this.blockedId = blockedId;
  }
}

/**
 * Query surface for asking "is this user/node currently blocked by the mesh?"
 * Backed by a {@link MeshDirectiveStore}. Separating the gate (read) from the
 * store (write) lets consumers depend on the query view without the write surface.
 */
export class MeshSecurityGate {
  private readonly store: MeshDirectiveStore;

  constructor(store: MeshDirectiveStore) {
    if (store == null) throw new Error("store required");
    this.store = store;
  }

  /**
   * Returns a single-shot decision for the given user/node id. The reason text
   * comes from the most recent active block directive.
   */
  decide(userOrNodeId: string): GateDecision {
    if (!userOrNodeId || userOrNodeId.trim().length === 0) return GateDecisionAllowed;
    const { blocked, reason } = this.store.isBlocked(userOrNodeId);
    return blocked ? { isBlocked: true, reason } : GateDecisionAllowed;
  }

  /**
   * Throws {@link MeshSecurityBlockedException} when a request from a blocked id
   * would proceed. Use as a one-line guard at the top of a method.
   */
  enforce(userOrNodeId: string): void {
    const decision = this.decide(userOrNodeId);
    if (decision.isBlocked) {
      throw new MeshSecurityBlockedException(userOrNodeId, decision.reason);
    }
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// AetherSecurityBridge — IAISecurityLayer over SecurityLayerService
// ═════════════════════════════════════════════════════════════════════════════

/**
 * Connects an Aether mesh telemetry feed to the transport-agnostic
 * {@link SecurityLayerService}. Implements {@link IAISecurityLayer} so it can be
 * used as a drop-in replacement for an Aether-coupled layer.
 *
 * Responsibilities (pure translation — the SecurityLayerService does the reasoning):
 *   1. On `startAsync`, subscribe to the telemetry feed and start the layer.
 *   2. Translate each {@link AetherSecurityEvent} → {@link PeerSecurityEvent} and
 *      hand it to `handlePeerEvent`; on node departure, call `handlePeerLeft`.
 *   3. Adapt {@link ISecurityDirectiveConsumer} (Aether) ↔ {@link IPeerDirectiveConsumer}.
 *   4. Map {@link SecurityPosture} from the layer's PeerSecurityPosture.
 *
 * CONCURRENCY: the telemetry subscription is created synchronously inside
 * `startAsync` (before the layer's recovery loop is spawned), so a security event
 * published immediately after start cannot race the subscription.
 */
export class AetherSecurityBridge implements IAISecurityLayer {
  private readonly layer: SecurityLayerService;
  private telemetrySubscription: IAetherDisposable | null = null;

  constructor(layer: SecurityLayerService) {
    if (layer == null) throw new Error("layer required");
    this.layer = layer;
  }

  async startAsync(telemetry: IAetherTelemetry, signal?: AbortSignal): Promise<void> {
    if (telemetry == null) throw new Error("telemetry required");
    // Subscribe synchronously BEFORE starting the layer, so no event is lost.
    this.telemetrySubscription = telemetry.subscribe(new BridgeObserver(this.layer));
    await this.layer.startAsync(signal);
  }

  async stopAsync(signal?: AbortSignal): Promise<void> {
    this.telemetrySubscription?.dispose();
    this.telemetrySubscription = null;
    await this.layer.stopAsync(signal);
  }

  subscribeToDirectives(consumer: ISecurityDirectiveConsumer): IAetherDisposable {
    if (consumer == null) throw new Error("consumer required");
    const handle: IDisposableHandle = this.layer.subscribeToDirectives(new DirectiveAdapter(consumer));
    return { dispose: () => handle.dispose() };
  }

  async getPostureAsync(signal?: AbortSignal): Promise<SecurityPosture> {
    const posture = await this.layer.getPostureAsync(signal);
    return {
      overallThreatLevel: AetherMapper.toAetherThreatLevel(posture.overallThreatLevel),
      quarantinedNodeCount: posture.quarantinedPeerCount,
      monitoredNodeCount: posture.monitoredPeerCount,
      isActive: posture.isActive,
      assessedAt: posture.generatedAt,
    };
  }
}

/** Telemetry observer: translates Aether events into peer events for the layer. */
class BridgeObserver implements IAetherTelemetryObserver {
  private readonly layer: SecurityLayerService;
  constructor(layer: SecurityLayerService) {
    this.layer = layer;
  }

  onSecurityEvent(e: AetherSecurityEvent): void {
    const peer: PeerSecurityEvent = {
      nodeId: e.nodeId,
      kind: AetherMapper.toPeerEventKind(e.kind),
      threatLevel: AetherMapper.toPeerThreatLevel(e.threatLevel),
      description: e.description,
      transportId: "aether",
      occurredAt: e.occurredAt,
    };
    this.layer.handlePeerEvent(peer);
  }

  onNodeEvent(e: AetherNodeEvent): void {
    if (aetherNodeEventIsExit(e)) this.layer.handlePeerLeft(e.nodeId);
  }

  // Not relevant to security scoring — ignore.
  onTransportEvent(_e: AetherTransportEvent): void {}
  onRouteEvent(_e: AetherRouteEvent): void {}
  onNetworkEvent(_e: AetherNetworkEvent): void {}
}

/**
 * Adapts an Aether {@link ISecurityDirectiveConsumer} so it can receive
 * {@link PeerDirective} instances from the transport-agnostic layer, translating
 * them back to {@link SecurityDirective} before delivery.
 */
class DirectiveAdapter implements IPeerDirectiveConsumer {
  private readonly consumer: ISecurityDirectiveConsumer;
  constructor(consumer: ISecurityDirectiveConsumer) {
    this.consumer = consumer;
  }

  onDirective(directive: PeerDirective): void {
    const aether: SecurityDirective = {
      kind: AetherMapper.toSecurityDirectiveKind(directive.kind),
      targetNodeId: directive.targetNodeId,
      trustScoreOverride: directive.trustScore,
      threatLevel: AetherMapper.toAetherThreatLevel(directive.threatLevel),
      reason: directive.reason,
      durationMs: directive.durationMs,
      issuedAt: directive.issuedAt,
    };
    this.consumer.onDirective(aether);
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// AetherIntelligenceAdapter — IAetherIntelligence over PeerIntelligenceService
// ═════════════════════════════════════════════════════════════════════════════

/**
 * Implements {@link IAetherIntelligence} by wrapping {@link PeerIntelligenceService}
 * and mapping transport-agnostic result types to their Aether equivalents:
 *   PeerNetworkHealthReport → NetworkHealthReport
 *   PeerThreatAssessment    → ThreatAssessment
 *   PeerRoutingAdvice       → RoutingAdvice
 *   PeerTrustScoreUpdate    → TrustScoreUpdate (streaming)
 */
export class AetherIntelligenceAdapter implements IAetherIntelligence {
  private readonly inner: PeerIntelligenceService;

  constructor(inner: PeerIntelligenceService) {
    if (inner == null) throw new Error("inner required");
    this.inner = inner;
  }

  async getNetworkHealthAsync(signal?: AbortSignal): Promise<NetworkHealthReport> {
    const r = await this.inner.getNetworkHealthAsync(signal);
    return {
      overallScore: r.overallScore,
      trustedNodeCount: r.trustedPeerCount,
      suspiciousNodeCount: r.suspiciousPeerCount,
      summary: r.summary,
      generatedAt: r.generatedAt,
    };
  }

  async assessThreatAsync(nodeId: string, signal?: AbortSignal): Promise<ThreatAssessment> {
    const a = await this.inner.assessThreatAsync(nodeId, signal);
    return {
      nodeId: a.nodeId,
      threatConfidence: a.confidence,
      level: AetherMapper.toAetherThreatLevel(a.threatLevel),
      indicators: a.indicators,
      assessedAt: a.assessedAt,
    };
  }

  async getRoutingAdviceAsync(destinationNodeId: string, signal?: AbortSignal): Promise<RoutingAdvice> {
    const r = await this.inner.getRoutingAdviceAsync(destinationNodeId, signal);
    return {
      destinationNodeId: r.destinationNodeId,
      recommendedPath: r.recommendedPath,
      avoidNodes: r.avoidNodeIds,
      confidence: r.confidence,
      reasoning: r.reasoning,
      generatedAt: r.generatedAt,
    };
  }

  async *streamTrustScoresAsync(signal?: AbortSignal): AsyncGenerator<TrustScoreUpdate> {
    for await (const u of this.inner.streamTrustScoresAsync(signal)) {
      yield {
        nodeId: u.nodeId,
        previousScore: u.previousScore,
        currentScore: u.newScore,
        reason: u.reason,
        updatedAt: u.changedAt,
      };
    }
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// MeshGatedCompanionSession — ICompanionSession decorator
// ═════════════════════════════════════════════════════════════════════════════

/**
 * Wraps an inner {@link ICompanionSession} and enforces the mesh's "block this
 * user" directives via {@link MeshSecurityGate} on every message-producing call
 * (send / stream / agent). When the gate says the session's identityId is
 * blocked, the decorator throws {@link MeshSecurityBlockedException} instead of
 * reaching the underlying generator.
 *
 * Context / history / feedback are diagnostic calls and are NOT gated — blocking
 * them would prevent a blocked user from even seeing their own state, which goes
 * beyond "stop the chat" into "punish". The decorator never modifies or
 * impersonates the inner session; it strictly adds the gate check.
 */
export class MeshGatedCompanionSession implements ICompanionSession {
  private readonly inner: ICompanionSession;
  private readonly gate: MeshSecurityGate;

  constructor(inner: ICompanionSession, gate: MeshSecurityGate) {
    if (inner == null) throw new Error("inner required");
    if (gate == null) throw new Error("gate required");
    this.inner = inner;
    this.gate = gate;
  }

  // ── Pass-through identity / properties ────────────────────────────────────
  get sessionId(): string {
    return this.inner.sessionId;
  }
  get identityId(): string {
    return this.inner.identityId;
  }
  get interface(): InterfaceKind {
    return this.inner.interface;
  }
  get history(): readonly CompanionTurn[] {
    return this.inner.history;
  }

  get onProactiveMessageReady(): ProactiveMessageHandler | null {
    return this.inner.onProactiveMessageReady;
  }
  set onProactiveMessageReady(handler: ProactiveMessageHandler | null) {
    this.inner.onProactiveMessageReady = handler;
  }

  // ── Guarded entry points ──────────────────────────────────────────────────
  sendAsync(message: string): Promise<string> {
    this.gate.enforce(this.identityId);
    return this.inner.sendAsync(message);
  }

  async *streamAsync(message: string): AsyncGenerator<string> {
    this.gate.enforce(this.identityId);
    yield* this.inner.streamAsync(message);
  }

  agentAsync(instruction: string): Promise<string> {
    this.gate.enforce(this.identityId);
    return this.inner.agentAsync(instruction);
  }

  // ── Unguarded pass-through ────────────────────────────────────────────────
  getContext(): CompanionContext {
    return this.inner.getContext();
  }

  refreshContextAsync(): Promise<void> {
    return this.inner.refreshContextAsync();
  }

  signalFeedbackAsync(positive: boolean, note?: string): Promise<void> {
    return this.inner.signalFeedbackAsync(positive, note);
  }
}
