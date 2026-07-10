// SecurityAetherNet.swift
//
// Port of CircleAI.Security.AetherNet (the C# reference) — the AetherNet-specific
// security bindings that sit between the Aether contracts (Aether.swift) and the
// transport-agnostic peer-security layer (PeerSecurity.swift). Collapses the C#
// folder into one Swift file per the tree's flat convention:
//
//   MeshDirectiveStore          — ISecurityDirectiveConsumer sink + block query
//   MeshSecurityGate            — read-only "is this id blocked?" fast path
//   MeshSecurityBlockedError    — thrown by MeshSecurityGate.enforce
//   MeshGatedCompanionSession   — decorator gating the chat path on the mesh
//   AetherMapper                — Aether ↔ Peer enum translation
//   AetherIntelligenceAdapter   — IAetherIntelligence over PeerIntelligenceService
//   AetherSecurityBridge        — IAISecurityLayer over SecurityLayerService
//
// The Peer* types, SecurityLayerService, PeerIntelligenceService, and
// IDirectiveSubscription come from PeerSecurity.swift (CircleAI.Security). The
// Aether contracts come from Aether.swift. This file is pure translation +
// working in-memory glue — no stubs.
//
// Concurrency: MeshDirectiveStore guards per-node directive lists with an
// NSLock and sweeps expired entries lazily on read (no background timer to
// leak), exactly as the C# reference does. All lock use is synchronous.

import Foundation

// MARK: - MeshDirectiveStore

/// Thread-safe in-memory registry of security directives received from the mesh.
/// Acts as both the directive sink (`ISecurityDirectiveConsumer`) and the query
/// surface that other CircleAI components consult before serving a request.
///
/// Two query surfaces:
///   • `isBlocked(_:)`          — fast hot-path check (returns reason)
///   • `getActiveDirectives(_:)` — full audit detail
///
/// Expiry is handled lazily on read. Block state observes Avoid + Quarantine;
/// Release lifts both.
public final class MeshDirectiveStore: ISecurityDirectiveConsumer, @unchecked Sendable {
    private let lock = NSLock()
    private var byNode: [String: [SecurityDirective]] = [:]
    private let clock: @Sendable () -> Date

    /// Constructs a store using `Date()` as the clock.
    public convenience init() {
        self.init(clock: { Date() })
    }

    /// Constructs a store with an explicit clock for testing.
    public init(clock: @escaping @Sendable () -> Date) {
        self.clock = clock
    }

    // ── ISecurityDirectiveConsumer ────────────────────────────────────────────

    public func onDirective(_ directive: SecurityDirective) {
        guard directive.hasTarget, let nodeId = directive.targetNodeId else { return }

        lock.lock()
        defer { lock.unlock() }

        if directive.kind == .releaseNode {
            // Release lifts every Avoid/Quarantine for the node.
            byNode.removeValue(forKey: nodeId)
            return
        }

        byNode[nodeId, default: []].append(directive)
    }

    // ── Queries ───────────────────────────────────────────────────────────────

    /// Returns a block decision for the node: `blocked` is true when an unexpired
    /// Avoid or Quarantine directive is active. `reason` carries the most recent
    /// block's reason text (empty when not blocked).
    ///
    /// Sweeps expired entries for the node as a side effect (matching the C#
    /// lazy-expiry-on-read behaviour).
    @discardableResult
    public func isBlocked(_ nodeId: String) -> (blocked: Bool, reason: String) {
        if nodeId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return (false, "")
        }

        lock.lock()
        defer { lock.unlock() }

        guard var list = byNode[nodeId] else { return (false, "") }

        let now = clock()
        var latestBlock: SecurityDirective?

        // Drop expired entries while we walk the list (iterate a copy; rebuild).
        var kept: [SecurityDirective] = []
        kept.reserveCapacity(list.count)
        for d in list {
            if Self.isExpired(d, now: now) { continue }
            kept.append(d)
            if Self.isBlockKind(d.kind) {
                if latestBlock == nil || d.issuedAt > latestBlock!.issuedAt {
                    latestBlock = d
                }
            }
        }
        list = kept

        if list.isEmpty {
            byNode.removeValue(forKey: nodeId)
        } else {
            byNode[nodeId] = list
        }

        guard let block = latestBlock else { return (false, "") }
        return (true, block.reason)
    }

    /// Lists every unexpired directive for the node — useful for audit/diagnostics.
    /// Does not mutate the store (a pure read; expired entries are filtered from
    /// the returned copy only).
    public func getActiveDirectives(_ nodeId: String) -> [SecurityDirective] {
        if nodeId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return [] }

        lock.lock()
        defer { lock.unlock() }

        guard let list = byNode[nodeId] else { return [] }
        let now = clock()
        return list.filter { !Self.isExpired($0, now: now) }
    }

    /// Number of nodes with at least one tracked directive.
    public var trackedNodeCount: Int {
        lock.lock(); defer { lock.unlock() }
        return byNode.count
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static func isBlockKind(_ k: SecurityDirectiveKind) -> Bool {
        k == .avoidNode || k == .quarantineNode
    }

    private static func isExpired(_ d: SecurityDirective, now: Date) -> Bool {
        guard let duration = d.duration else { return false }
        return d.issuedAt.addingTimeInterval(duration) <= now
    }
}

// MARK: - MeshSecurityGate

/// Query surface for asking "is this user/node currently blocked by the mesh?"
/// Backed by a `MeshDirectiveStore`.
///
/// Separating the gate from the store lets callers depend on the read-only query
/// view without seeing the directive-write surface (the store).
public final class MeshSecurityGate: @unchecked Sendable {
    private let store: MeshDirectiveStore

    public init(store: MeshDirectiveStore) {
        self.store = store
    }

    /// Decision returned from `decide(_:)`.
    public struct GateDecision: Sendable, Equatable {
        public let isBlocked: Bool
        public let reason: String

        public init(isBlocked: Bool, reason: String) {
            self.isBlocked = isBlocked
            self.reason = reason
        }

        /// Convenience: allow with no reason text.
        public static let allowed = GateDecision(isBlocked: false, reason: "")
    }

    /// Returns a single-shot decision for the given user/node id. The reason text
    /// comes from the most recent active block directive.
    public func decide(_ userOrNodeId: String) -> GateDecision {
        if userOrNodeId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return .allowed
        }
        let result = store.isBlocked(userOrNodeId)
        return result.blocked ? GateDecision(isBlocked: true, reason: result.reason) : .allowed
    }

    /// Convenience wrapper that throws when a request from a blocked id would
    /// proceed. Use as a one-line guard at the top of a method.
    public func enforce(_ userOrNodeId: String) throws {
        let decision = decide(userOrNodeId)
        if decision.isBlocked {
            throw MeshSecurityBlockedError(blockedId: userOrNodeId, reason: decision.reason)
        }
    }
}

/// Thrown by `MeshSecurityGate.enforce(_:)` when the mesh has issued a block
/// directive against the requesting id.
public struct MeshSecurityBlockedError: Error, Equatable, CustomStringConvertible {
    public let blockedId: String
    public let reason: String

    public init(blockedId: String, reason: String) {
        self.blockedId = blockedId
        self.reason = reason
    }

    public var description: String { "Mesh has blocked '\(blockedId)': \(reason)" }
}

// MARK: - MeshGatedCompanionSession

/// Wraps an inner `ICompanionSession` and enforces the mesh's "block this user"
/// directives via `MeshSecurityGate` on every message-producing call
/// (`send`, `stream`, `agent`).
///
/// When the gate says the session's `identityId` is blocked by an active mesh
/// directive:
///   • `send` / `agent` throw `MeshSecurityBlockedError` (they are `throws`).
///   • `stream` — whose Swift signature is a non-throwing `AsyncStream` — yields
///     nothing and finishes immediately (blocked → no streamed content). This
///     preserves the C# "stop the chat" intent within the async-stream contract.
///
/// The decorator never modifies or impersonates the inner session; it strictly
/// adds the gate check. Context / history / feedback pass through unguarded —
/// gating them would stop a blocked user seeing their own state, which is
/// beyond the "stop the chat" intent.
public final class MeshGatedCompanionSession: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession
    private let gate: MeshSecurityGate

    public init(inner: ICompanionSession, gate: MeshSecurityGate) {
        self.inner = inner
        self.gate = gate
    }

    // ── Pass-through identity / properties ────────────────────────────────────

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    // ── Guarded entry points ──────────────────────────────────────────────────

    public func send(_ message: String) async throws -> String {
        try gate.enforce(identityId)
        return try await inner.send(message)
    }

    public func stream(_ message: String) -> AsyncStream<String> {
        // Non-throwing signature: on block, return an immediately-finished stream.
        if gate.decide(identityId).isBlocked {
            return AsyncStream { $0.finish() }
        }
        return inner.stream(message)
    }

    public func agent(_ instruction: String) async throws -> String {
        try gate.enforce(identityId)
        return try await inner.agent(instruction)
    }

    // ── Unguarded pass-through ────────────────────────────────────────────────

    public func getContext() -> CompanionContext { inner.getContext() }

    public func refreshContext() async throws { try await inner.refreshContext() }

    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }
}

// MARK: - AetherMapper

/// Static helpers that translate between Aether-specific types and the
/// transport-agnostic Peer types defined in `PeerSecurity.swift`.
///
/// All mappings are explicit switch expressions so a new enum value on either
/// side is caught at the mapping site.
enum AetherMapper {

    // ── AetherSecurityEventKind → PeerSecurityEventKind ───────────────────────

    static func toPeerEventKind(_ kind: AetherSecurityEventKind) -> PeerSecurityEventKind {
        switch kind {
        case .nodeAuthAttempt:     return .authAttempt
        case .routingAnomaly:      return .routingAnomaly
        case .nodeBehaviourChange: return .behaviourChange
        case .encryptionEvent:     return .encryptionEvent
        case .intrusionSignal:     return .intrusionSignal
        case .privilegeAttempt:    return .privilegeAttempt
        }
    }

    // ── AetherThreatLevel ↔ PeerThreatLevel ───────────────────────────────────

    static func toPeerThreatLevel(_ level: AetherThreatLevel) -> PeerThreatLevel {
        switch level {
        case .none:     return .none
        case .low:      return .low
        case .medium:   return .medium
        case .high:     return .high
        case .critical: return .critical
        }
    }

    static func toAetherThreatLevel(_ level: PeerThreatLevel) -> AetherThreatLevel {
        switch level {
        case .none:     return .none
        case .low:      return .low
        case .medium:   return .medium
        case .high:     return .high
        case .critical: return .critical
        }
    }

    // ── PeerDirectiveKind → SecurityDirectiveKind ─────────────────────────────

    static func toSecurityDirectiveKind(_ kind: PeerDirectiveKind) -> SecurityDirectiveKind {
        switch kind {
        case .elevateMonitoring: return .elevateMonitoring
        case .avoidNode:         return .avoidNode
        case .quarantineNode:    return .quarantineNode
        case .releaseNode:       return .releaseNode
        }
    }
}

// MARK: - AetherIntelligenceAdapter

/// Implements `IAetherIntelligence` by wrapping `PeerIntelligenceService` and
/// mapping transport-agnostic result types to their Aether equivalents:
///
///   PeerNetworkHealthReport → NetworkHealthReport
///   PeerThreatAssessment    → ThreatAssessment
///   PeerRoutingAdvice       → RoutingAdvice
///   PeerTrustScoreUpdate    → TrustScoreUpdate (streaming)
///
/// Callers that only need transport-agnostic intelligence should use
/// `PeerIntelligenceService` directly.
public final class AetherIntelligenceAdapter: IAetherIntelligence, @unchecked Sendable {
    private let inner: PeerIntelligenceService

    public init(inner: PeerIntelligenceService) {
        self.inner = inner
    }

    public func getNetworkHealth() async throws -> NetworkHealthReport {
        let r = try await inner.getNetworkHealth()
        return NetworkHealthReport(
            overallScore: r.overallScore,
            trustedNodeCount: r.trustedPeerCount,
            suspiciousNodeCount: r.suspiciousPeerCount,
            summary: r.summary,
            generatedAt: r.generatedAt)
    }

    public func assessThreat(nodeId: String) async throws -> ThreatAssessment {
        let a = try await inner.assessThreat(nodeId: nodeId)
        return ThreatAssessment(
            nodeId: a.nodeId,
            threatConfidence: a.confidence,
            level: AetherMapper.toAetherThreatLevel(a.threatLevel),
            indicators: a.indicators,
            assessedAt: a.assessedAt)
    }

    public func getRoutingAdvice(destinationNodeId: String) async throws -> RoutingAdvice {
        let r = try await inner.getRoutingAdvice(destinationNodeId: destinationNodeId)
        return RoutingAdvice(
            destinationNodeId: r.destinationNodeId,
            recommendedPath: r.recommendedPath,
            avoidNodes: r.avoidNodeIds,
            confidence: r.confidence,
            reasoning: r.reasoning,
            generatedAt: r.generatedAt)
    }

    public func streamTrustScores() -> AsyncStream<TrustScoreUpdate> {
        // Subscribe to the inner stream SYNCHRONOUSLY here, then re-project each
        // element on a forwarding task. The inner registry buffers updates
        // published before a subscriber attaches (unbounded), so nothing sent
        // right after this call is lost.
        let source = inner.streamTrustScores()
        return AsyncStream { continuation in
            let task = Task {
                for await u in source {
                    continuation.yield(TrustScoreUpdate(
                        nodeId: u.nodeId,
                        previousScore: u.previousScore,
                        currentScore: u.newScore,
                        reason: u.reason,
                        updatedAt: u.changedAt))
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}

// MARK: - AetherSecurityBridge

/// Connects an Aether mesh telemetry feed to the transport-agnostic
/// `SecurityLayerService`. Implements `IAISecurityLayer` so it can be used as a
/// drop-in replacement for an Aether-coupled layer.
///
/// Responsibilities:
///   1. Implements `IAISecurityLayer`.
///   2. On `start`, subscribes to `IAetherTelemetry`, translates each
///      `AetherSecurityEvent` into a `PeerSecurityEvent` and calls
///      `SecurityLayerService.handlePeerEvent`.
///   3. Adapts `ISecurityDirectiveConsumer` (Aether) ↔ `IPeerDirectiveConsumer`.
///   4. Maps `SecurityPosture` ↔ `PeerSecurityPosture`.
///
/// The `SecurityLayerService` does all the reasoning; this class is pure
/// translation.
public final class AetherSecurityBridge: IAISecurityLayer, @unchecked Sendable {
    private let layer: SecurityLayerService

    private let lock = NSLock()
    private var telemetrySubscription: IAetherSubscription?
    // Directive adapters are retained here so the underlying publisher's
    // (weakly-held) subscription handles stay alive for the bridge's lifetime.
    private var directiveAdapters: [DirectiveAdapter] = []

    /// Initialises the bridge using an existing transport-agnostic security layer.
    ///
    /// - Parameter layer: the `SecurityLayerService` that receives translated
    ///   events. Must be constructed but need not be started yet.
    public init(layer: SecurityLayerService) {
        self.layer = layer
    }

    // ── IAISecurityLayer ──────────────────────────────────────────────────────

    public func start(telemetry: IAetherTelemetry) async throws {
        let observer = Observer(bridge: self)
        let sub = telemetry.subscribe(observer)
        lock.lock()
        telemetrySubscription = sub
        // Retain the observer alongside the subscription: telemetry hubs may hold
        // observers weakly, so the bridge keeps a strong reference here.
        retainedObserver = observer
        lock.unlock()
        try await layer.start()
    }

    public func stop() async throws {
        lock.lock()
        let sub = telemetrySubscription
        telemetrySubscription = nil
        retainedObserver = nil
        lock.unlock()
        sub?.dispose()
        try await layer.stop()
    }

    public func subscribeToDirectives(_ consumer: ISecurityDirectiveConsumer) -> IAetherSubscription {
        let adapter = DirectiveAdapter(consumer: consumer)
        lock.lock()
        directiveAdapters.append(adapter)
        lock.unlock()
        let inner = layer.subscribeToDirectives(adapter)
        return DirectiveSubscriptionBox(bridge: self, adapter: adapter, inner: inner)
    }

    public func getPosture() async throws -> SecurityPosture {
        let posture = try await layer.getPosture()
        return SecurityPosture(
            overallThreatLevel: AetherMapper.toAetherThreatLevel(posture.overallThreatLevel),
            quarantinedNodeCount: posture.quarantinedPeerCount,
            monitoredNodeCount: posture.monitoredPeerCount,
            isActive: posture.isActive,
            assessedAt: posture.generatedAt)
    }

    // Strong hold for the telemetry observer (see start()).
    private var retainedObserver: Observer?

    fileprivate func forwardSecurityEvent(_ e: AetherSecurityEvent) {
        let peer = PeerSecurityEvent(
            nodeId: e.nodeId,
            kind: AetherMapper.toPeerEventKind(e.kind),
            threatLevel: AetherMapper.toPeerThreatLevel(e.threatLevel),
            description: e.description,
            transportId: "aether",
            occurredAt: e.occurredAt)
        layer.handlePeerEvent(peer)
    }

    fileprivate func forwardPeerLeft(_ nodeId: String) {
        layer.handlePeerLeft(nodeId)
    }

    fileprivate func releaseDirectiveAdapter(_ adapter: DirectiveAdapter) {
        lock.lock()
        directiveAdapters.removeAll { $0 === adapter }
        lock.unlock()
    }

    // ── Telemetry observer ────────────────────────────────────────────────────

    private final class Observer: IAetherTelemetryObserver {
        private weak var bridge: AetherSecurityBridge?
        init(bridge: AetherSecurityBridge) { self.bridge = bridge }

        func onSecurityEvent(_ e: AetherSecurityEvent) {
            bridge?.forwardSecurityEvent(e)
        }

        func onNodeEvent(_ e: AetherNodeEvent) {
            if e.isExit { bridge?.forwardPeerLeft(e.nodeId) }
        }

        // Not relevant to security scoring — ignore.
        func onTransportEvent(_ e: AetherTransportEvent) {}
        func onRouteEvent(_ e: AetherRouteEvent) {}
        func onNetworkEvent(_ e: AetherNetworkEvent) {}
    }

    // ── Directive adapter ─────────────────────────────────────────────────────

    /// Adapts an Aether `ISecurityDirectiveConsumer` so it can receive
    /// `PeerDirective` instances from the transport-agnostic layer, translating
    /// them back to `SecurityDirective` before delivery.
    fileprivate final class DirectiveAdapter: IPeerDirectiveConsumer, @unchecked Sendable {
        private let consumer: ISecurityDirectiveConsumer
        init(consumer: ISecurityDirectiveConsumer) { self.consumer = consumer }

        func onDirective(_ directive: PeerDirective) {
            let aether = SecurityDirective(
                kind: AetherMapper.toSecurityDirectiveKind(directive.kind),
                targetNodeId: directive.targetNodeId,
                trustScoreOverride: directive.trustScore,
                threatLevel: AetherMapper.toAetherThreatLevel(directive.threatLevel),
                reason: directive.reason,
                duration: directive.duration,
                issuedAt: directive.issuedAt)
            consumer.onDirective(aether)
        }
    }

    /// Wraps the inner `IDirectiveSubscription` so disposal also drops the
    /// retained adapter from the bridge. Idempotent.
    private final class DirectiveSubscriptionBox: IAetherSubscription, @unchecked Sendable {
        private weak var bridge: AetherSecurityBridge?
        private let adapter: DirectiveAdapter
        private let inner: IDirectiveSubscription
        private let disposeLock = NSLock()
        private var disposed = false

        init(bridge: AetherSecurityBridge, adapter: DirectiveAdapter, inner: IDirectiveSubscription) {
            self.bridge = bridge
            self.adapter = adapter
            self.inner = inner
        }

        func dispose() {
            disposeLock.lock()
            if disposed { disposeLock.unlock(); return }
            disposed = true
            disposeLock.unlock()
            inner.dispose()
            bridge?.releaseDirectiveAdapter(adapter)
        }
    }
}
