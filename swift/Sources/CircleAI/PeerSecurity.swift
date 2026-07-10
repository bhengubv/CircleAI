// PeerSecurity.swift
//
// Port of the transport-agnostic peer-security surface from
// src/CircleAI.Security:
//   • PeerSecurityTypes.cs  — Peer* enums, records, and interfaces
//   • ThreatDetector.cs     — stateless degradation + indicator logic
//   • SecurityOptions.cs     — threshold / decay / retention configuration
//   • NodeTrustRegistry.cs  — per-peer trust store + trust-score update stream
//   • DirectivePublisher.cs — fan-out publisher of PeerDirectives
//   • SecurityLayerService (from AISecurityLayerService.cs) — IPeerSecurityLayer
//   • PeerIntelligenceService (from AetherIntelligenceService.cs) — IPeerIntelligence
//
// These types are deliberately free of any transport dependency (Aether, WiFi,
// BLE, NearLink, HTTP, …). Every transport adapter translates its own event
// vocabulary into these types before feeding the security layer.
//
// Concurrency notes (this subtree is stream/transport-heavy):
//   • NodeTrustRegistry.trustScoreUpdates is an UNBOUNDED, buffered broadcast:
//     updates published before any subscriber attaches are retained and flushed
//     on subscribe (matching the C# unbounded Channel<T> semantics where writes
//     accumulate until read). Fan-out to multiple subscribers is supported.
//   • The recovery loop in SecurityLayerService subscribes to nothing but does
//     mutate the registry; all registry mutation is NSLock-guarded per entry.
//   • Continuations are always finished OUTSIDE the lock (snapshot-then-release)
//     because AsyncStream.finish() can synchronously invoke onTermination, which
//     re-acquires the same non-reentrant NSLock → self-deadlock.

import Foundation

// MARK: - Enumerations

/// Transport-neutral classification of a peer security event.
///
/// Ordinals follow the C# declaration order. Cross-language wire consumers rely
/// on the ordinal, so new values must be appended.
public enum PeerSecurityEventKind: Int, Codable, Sendable, CaseIterable {
    /// Authentication attempt (login, handshake, re-auth).
    case authAttempt = 0
    /// Anomalous routing behaviour detected (loop, black-hole, etc.).
    case routingAnomaly = 1
    /// Peer behaviour changed unexpectedly (rate, pattern, protocol).
    case behaviourChange = 2
    /// Encryption negotiation event (downgrade, cipher mismatch).
    case encryptionEvent = 3
    /// Active intrusion probe or exploitation attempt.
    case intrusionSignal = 4
    /// Privilege escalation or capability violation attempt.
    case privilegeAttempt = 5
    /// Unusual connection pattern (port scan, rapid reconnect).
    case connectionAnomaly = 6
    /// Suspected data exfiltration (volume, destination anomaly).
    case dataExfiltration = 7
    /// Denial-of-service signal (flooding, resource exhaustion).
    case denialOfService = 8
    /// Catch-all for events that do not map to a specific category.
    case unknown = 9
}

/// Severity level for a peer security event or threat assessment.
/// Values match the intuitive ordering: None is safest, Critical is worst.
public enum PeerThreatLevel: Int, Codable, Sendable, Comparable, CaseIterable {
    /// No threat — event carries no security significance.
    case none = 0
    /// Low-level anomaly — monitor but no action required.
    case low = 1
    /// Notable anomaly — elevated monitoring recommended.
    case medium = 2
    /// Significant threat — routing around the peer recommended.
    case high = 3
    /// Active or confirmed attack — quarantine the peer.
    case critical = 4

    public static func < (lhs: PeerThreatLevel, rhs: PeerThreatLevel) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

/// The action recommended by the security layer for a given peer.
public enum PeerDirectiveKind: Int, Codable, Sendable, CaseIterable {
    /// Increase observation cadence; no traffic restriction yet.
    case elevateMonitoring = 0
    /// Exclude the peer from routing; still accept inbound connections.
    case avoidNode = 1
    /// Hard-block the peer — no traffic to or from it.
    case quarantineNode = 2
    /// Lift a previous directive; the peer has recovered sufficient trust.
    /// Not issued automatically — requires explicit operator action.
    case releaseNode = 3
}

// MARK: - Records (DTOs)

/// One security incident observed on any transport.
public struct PeerSecurityEvent: Sendable, Equatable, Codable {
    /// Stable identifier of the peer that generated the event.
    public let nodeId: String
    /// Transport-neutral event category.
    public let kind: PeerSecurityEventKind
    /// Assessed severity at the time of observation.
    public let threatLevel: PeerThreatLevel
    /// Human-readable description of the event.
    public let description: String
    /// Identifier for the transport that produced the event
    /// (e.g. "aether", "wifi", "ble", "nearlink", "http").
    public let transportId: String
    /// UTC timestamp of the event.
    public let occurredAt: Date

    public init(
        nodeId: String,
        kind: PeerSecurityEventKind,
        threatLevel: PeerThreatLevel,
        description: String,
        transportId: String,
        occurredAt: Date
    ) {
        self.nodeId = nodeId
        self.kind = kind
        self.threatLevel = threatLevel
        self.description = description
        self.transportId = transportId
        self.occurredAt = occurredAt
    }
}

/// A security directive issued to all registered `IPeerDirectiveConsumer`
/// subscribers when a peer's trust crosses a threshold.
public struct PeerDirective: Sendable, Equatable, Codable {
    /// The recommended action.
    public let kind: PeerDirectiveKind
    /// The peer to which the directive applies.
    public let targetNodeId: String
    /// Current trust score of the peer at time of issue.
    public let trustScore: Double
    /// Threat level at time of issue.
    public let threatLevel: PeerThreatLevel
    /// Human-readable explanation for the directive.
    public let reason: String
    /// Optional duration after which the directive should be re-evaluated.
    /// `nil` means permanent until an explicit `releaseNode` directive is issued.
    public let duration: TimeInterval?
    /// UTC timestamp of issue.
    public let issuedAt: Date

    public init(
        kind: PeerDirectiveKind,
        targetNodeId: String,
        trustScore: Double,
        threatLevel: PeerThreatLevel,
        reason: String,
        duration: TimeInterval?,
        issuedAt: Date
    ) {
        self.kind = kind
        self.targetNodeId = targetNodeId
        self.trustScore = trustScore
        self.threatLevel = threatLevel
        self.reason = reason
        self.duration = duration
        self.issuedAt = issuedAt
    }
}

/// Notification emitted by `NodeTrustRegistry` whenever a node's trust score
/// changes.
public struct PeerTrustScoreUpdate: Sendable, Equatable, Codable {
    /// The peer whose score changed.
    public let nodeId: String
    /// Score before this change.
    public let previousScore: Double
    /// Score after this change.
    public let newScore: Double
    /// Short description of the cause (event description or "passive-recovery").
    public let reason: String
    /// UTC timestamp of the change.
    public let changedAt: Date

    public init(
        nodeId: String,
        previousScore: Double,
        newScore: Double,
        reason: String,
        changedAt: Date
    ) {
        self.nodeId = nodeId
        self.previousScore = previousScore
        self.newScore = newScore
        self.reason = reason
        self.changedAt = changedAt
    }
}

/// Snapshot of the overall security posture across all observed peers.
public struct PeerSecurityPosture: Sendable, Equatable, Codable {
    /// Worst-case threat level in the current peer set.
    public let overallThreatLevel: PeerThreatLevel
    /// Number of peers at or below `SecurityOptions.quarantineThreshold`.
    public let quarantinedPeerCount: Int
    /// Number of peers elevated beyond monitoring threshold but not yet quarantined.
    public let monitoredPeerCount: Int
    /// Whether the security layer is currently running.
    public let isActive: Bool
    /// UTC timestamp of this snapshot.
    public let generatedAt: Date

    public init(
        overallThreatLevel: PeerThreatLevel,
        quarantinedPeerCount: Int,
        monitoredPeerCount: Int,
        isActive: Bool,
        generatedAt: Date
    ) {
        self.overallThreatLevel = overallThreatLevel
        self.quarantinedPeerCount = quarantinedPeerCount
        self.monitoredPeerCount = monitoredPeerCount
        self.isActive = isActive
        self.generatedAt = generatedAt
    }
}

/// Aggregate network health across all observed peers.
public struct PeerNetworkHealthReport: Sendable, Equatable, Codable {
    /// Average trust score [0.0, 1.0] across all peers.
    public let overallScore: Double
    /// Peers above `SecurityOptions.avoidNodeThreshold`.
    public let trustedPeerCount: Int
    /// Peers at or below `SecurityOptions.elevateMonitoringThreshold`.
    public let suspiciousPeerCount: Int
    /// Human-readable health summary.
    public let summary: String
    /// UTC timestamp of this report.
    public let generatedAt: Date

    public init(
        overallScore: Double,
        trustedPeerCount: Int,
        suspiciousPeerCount: Int,
        summary: String,
        generatedAt: Date
    ) {
        self.overallScore = overallScore
        self.trustedPeerCount = trustedPeerCount
        self.suspiciousPeerCount = suspiciousPeerCount
        self.summary = summary
        self.generatedAt = generatedAt
    }
}

/// Per-peer threat assessment: confidence score, threat level, and detected
/// indicators.
public struct PeerThreatAssessment: Sendable, Equatable, Codable {
    /// The assessed peer.
    public let nodeId: String
    /// Likelihood that the peer is a genuine threat [0.0, 1.0].
    /// Derived from trust deficit + indicator count.
    public let confidence: Double
    /// Classified severity.
    public let threatLevel: PeerThreatLevel
    /// Human-readable indicator tags (e.g. "brute-force-auth", "intrusion-signal").
    public let indicators: [String]
    /// UTC timestamp of this assessment.
    public let assessedAt: Date

    public init(
        nodeId: String,
        confidence: Double,
        threatLevel: PeerThreatLevel,
        indicators: [String],
        assessedAt: Date
    ) {
        self.nodeId = nodeId
        self.confidence = confidence
        self.threatLevel = threatLevel
        self.indicators = indicators
        self.assessedAt = assessedAt
    }
}

/// Trust-aware routing recommendation for reaching a destination peer.
public struct PeerRoutingAdvice: Sendable, Equatable, Codable {
    /// The target peer.
    public let destinationNodeId: String
    /// Ordered list of peer IDs forming the recommended path.
    /// Empty when no safe path is available.
    public let recommendedPath: [String]
    /// Peers that should be excluded from routing.
    public let avoidNodeIds: [String]
    /// Confidence in the recommendation [0.0, 1.0].
    public let confidence: Double
    /// Human-readable explanation.
    public let reasoning: String
    /// UTC timestamp of this advice.
    public let generatedAt: Date

    public init(
        destinationNodeId: String,
        recommendedPath: [String],
        avoidNodeIds: [String],
        confidence: Double,
        reasoning: String,
        generatedAt: Date
    ) {
        self.destinationNodeId = destinationNodeId
        self.recommendedPath = recommendedPath
        self.avoidNodeIds = avoidNodeIds
        self.confidence = confidence
        self.reasoning = reasoning
        self.generatedAt = generatedAt
    }
}

// MARK: - Interfaces

/// Receives security directives from any `IPeerSecurityLayer` implementation.
public protocol IPeerDirectiveConsumer: AnyObject, Sendable {
    /// Called when the security layer issues a directive for a peer.
    func onDirective(_ directive: PeerDirective)
}

/// Transport-agnostic security layer lifecycle and posture surface.
public protocol IPeerSecurityLayer: AnyObject, Sendable {
    /// Starts the background trust-recovery loop.
    func start() async throws

    /// Stops the recovery loop and releases resources.
    func stop() async throws

    /// Feed a security event from any transport into the security layer.
    /// The layer will degrade the peer's trust score and issue directives as needed.
    func handlePeerEvent(_ e: PeerSecurityEvent)

    /// Subscribe to receive directives. Dispose the returned handle to unsubscribe.
    func subscribeToDirectives(_ consumer: IPeerDirectiveConsumer) -> IDirectiveSubscription

    /// Returns a snapshot of the current security posture.
    func getPosture() async throws -> PeerSecurityPosture
}

/// Transport-agnostic intelligence queries over accumulated trust data.
public protocol IPeerIntelligence: AnyObject, Sendable {
    /// Returns aggregate network health across all observed peers.
    func getNetworkHealth() async throws -> PeerNetworkHealthReport

    /// Returns a threat assessment for a specific peer.
    func assessThreat(nodeId: String) async throws -> PeerThreatAssessment

    /// Returns trust-aware routing advice toward a destination peer.
    func getRoutingAdvice(destinationNodeId: String) async throws -> PeerRoutingAdvice

    /// Streams every trust score change as they occur.
    /// Completes when the caller cancels iteration.
    func streamTrustScores() -> AsyncStream<PeerTrustScoreUpdate>
}

/// Implemented by transport adapters to register an event source with the
/// security layer. The security layer calls `start` once to begin pumping events.
public protocol IPeerSecurityEventFeed: AnyObject, Sendable {
    /// Human-readable identifier for this transport (e.g. "wifi", "ble", "aether").
    var transportId: String { get }

    /// Begins feeding events into `handler` until the task is cancelled.
    func start(handler: @escaping @Sendable (PeerSecurityEvent) -> Void) async throws
}

/// A disposable handle for a directive subscription. Mirrors C#'s `IDisposable`
/// returned by `IPeerSecurityLayer.SubscribeToDirectives`. Dispose is idempotent.
public protocol IDirectiveSubscription: AnyObject, Sendable {
    /// Unsubscribe. Idempotent.
    func dispose()
}

// MARK: - ThreatDetector

/// Stateless threat analysis helpers used by `SecurityLayerService` and
/// `PeerIntelligenceService`.
///
/// Pure static threat logic — no state, no DI, fully testable in isolation.
public enum ThreatDetector {

    // ─── Degradation weights by event kind ───────────────────────────────────

    static func baseWeight(_ kind: PeerSecurityEventKind) -> Double {
        switch kind {
        case .authAttempt:       return 0.05
        case .routingAnomaly:    return 0.10
        case .behaviourChange:   return 0.08
        case .encryptionEvent:   return 0.06
        case .intrusionSignal:   return 0.15
        case .privilegeAttempt:  return 0.12
        case .connectionAnomaly: return 0.07
        case .dataExfiltration:  return 0.14
        case .denialOfService:   return 0.13
        case .unknown:           return 0.05
        }
    }

    // ─── Multipliers by threat level ─────────────────────────────────────────

    static func threatMultiplier(_ level: PeerThreatLevel) -> Double {
        switch level {
        case .none:     return 0.0
        case .low:      return 0.5
        case .medium:   return 1.0
        case .high:     return 2.0
        case .critical: return 3.0
        }
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    /// Returns the trust-score degradation amount for a security event,
    /// calculated as `baseWeight(kind) × threatMultiplier(level)`.
    /// Returns 0 when `PeerThreatLevel.none`.
    public static func computeDegradation(_ e: PeerSecurityEvent) -> Double {
        baseWeight(e.kind) * threatMultiplier(e.threatLevel)
    }

    /// Derives human-readable threat indicator tags from a set of recent events
    /// within the given `window`. Returns an empty list when no patterns are
    /// detected.
    public static func detectIndicators(
        _ recentEvents: [PeerSecurityEvent],
        window: TimeInterval
    ) -> [String] {
        let cutoff = Date().addingTimeInterval(-window)
        let windowed = recentEvents.filter { $0.occurredAt >= cutoff }

        if windowed.isEmpty { return [] }

        var indicators: [String] = []

        // ≥ 3 auth attempts within the window → brute-force signal
        if windowed.filter({ $0.kind == .authAttempt }).count >= 3 {
            indicators.append("repeated-auth-attempts")
        }

        // Any intrusion signal → explicit probe or exploit
        if windowed.contains(where: { $0.kind == .intrusionSignal }) {
            indicators.append("intrusion-signal-detected")
        }

        // High or Critical event → severity flag
        if windowed.contains(where: { $0.threatLevel == .high || $0.threatLevel == .critical }) {
            indicators.append("high-severity-event")
        }

        // ≥ 3 distinct event kinds → multi-vector activity
        if Set(windowed.map { $0.kind }).count >= 3 {
            indicators.append("multi-vector-activity")
        }

        // Privilege escalation attempt
        if windowed.contains(where: { $0.kind == .privilegeAttempt }) {
            indicators.append("privilege-escalation-attempt")
        }

        // Data exfiltration signal
        if windowed.contains(where: { $0.kind == .dataExfiltration }) {
            indicators.append("data-exfiltration-signal")
        }

        return indicators
    }
}

// MARK: - SecurityOptions

/// Configures thresholds, decay rates, and event retention for the AI Security
/// Layer. Pass to `NodeTrustRegistry` and `SecurityLayerService`.
///
/// All threshold values are trust scores in the [0, 1] range. Lower score =
/// more compromised. Thresholds must satisfy:
///   quarantineThreshold < avoidNodeThreshold < elevateMonitoringThreshold
///
/// This is a mutable settable configuration object (a reference type), matching
/// the C# `class SecurityOptions` with settable properties.
public final class SecurityOptions: @unchecked Sendable {
    /// Trust score below which monitoring is elevated for the node.
    /// Default: 0.75 — a 25 % trust loss triggers closer observation.
    public var elevateMonitoringThreshold: Double = 0.75

    /// Trust score below which the node is excluded from routing.
    /// Default: 0.50 — half trust lost → route around the node.
    public var avoidNodeThreshold: Double = 0.50

    /// Trust score at or below which the node is hard-blocked (quarantined).
    /// Default: 0.25 — severe compromise → no traffic to or from the node.
    public var quarantineThreshold: Double = 0.25

    /// Passive trust recovery per second when no adverse events occur.
    /// Default: 0.001 ≈ full recovery from zero in ~16 minutes of clean behaviour.
    public var recoveryRatePerSecond: Double = 0.001

    /// Sliding window used for pattern-based indicator detection (e.g. repeated
    /// auth attempts). Events outside this window are ignored for pattern
    /// analysis. Default: 5 minutes.
    public var eventWindow: TimeInterval = 5 * 60

    /// Maximum security events retained per node. Oldest are dropped first.
    /// Default: 100.
    public var maxEventsPerNode: Int = 100

    /// Trust score assigned to nodes on first observation.
    /// Default: 1.0 (full trust until evidence says otherwise).
    public var initialTrustScore: Double = 1.0

    public init() {}
}

// MARK: - NodeTrustEntry

/// Per-peer mutable trust state. Exposed for diagnostics and tests.
///
/// Mutation is externally serialised by `NodeTrustRegistry` under its per-entry
/// lock, so the mutable stored properties are safe. Marked `@unchecked Sendable`
/// because it is only ever mutated while the registry holds the entry lock.
public final class NodeTrustEntry: @unchecked Sendable {
    public let nodeId: String
    public internal(set) var trustScore: Double
    public internal(set) var lastUpdated: Date = Date()

    /// Bounded history of security events (oldest-first).
    public internal(set) var recentEvents: [PeerSecurityEvent] = []

    init(nodeId: String, trustScore: Double) {
        self.nodeId = nodeId
        self.trustScore = trustScore
    }
}

// MARK: - NodeTrustRegistry

/// Maintains per-peer trust scores, event history, and a live broadcast of trust
/// score changes consumed by `PeerIntelligenceService`.
///
/// - Each peer gets a score in [0, 1]. 1.0 = fully trusted; 0.0 = fully lost.
/// - `applyDegradation` drops the score and records the triggering event.
/// - `applyRecovery` heals all peers passively (called by a background timer).
/// - `trustScoreUpdates()` yields every change; updates emitted before a
///   subscriber attaches are buffered and flushed on subscribe (matching the
///   unbounded Channel<T> semantics of the C# reference).
public final class NodeTrustRegistry: @unchecked Sendable {
    private let options: SecurityOptions

    // Registry lock guards `nodes`; each entry additionally has value updates
    // performed only while this lock is held, so no separate per-entry lock is
    // needed (C# used per-entry `lock (entry)`; a single registry lock is
    // equivalent and avoids lock-ordering hazards).
    private let lock = NSLock()
    private var nodes: [String: NodeTrustEntry] = [:]

    // Broadcast fan-out for trust-score updates.
    // `pending` buffers updates published before any subscriber attaches; each
    // new subscriber drains the shared pending buffer once, then receives live
    // updates. This matches the C# unbounded channel (writes retained until read)
    // while supporting multiple readers.
    private var continuations: [UUID: AsyncStream<PeerTrustScoreUpdate>.Continuation] = [:]
    private var pending: [PeerTrustScoreUpdate] = []

    public init(options: SecurityOptions) {
        self.options = options
    }

    // ─── Trust-score update stream ────────────────────────────────────────────

    /// Stream of trust score changes; never completes during normal operation.
    /// Callers break out by cancelling iteration (the enclosing Task).
    public func trustScoreUpdates() -> AsyncStream<PeerTrustScoreUpdate> {
        AsyncStream { continuation in
            let id = UUID()
            lock.lock()
            // Flush anything buffered before this subscription, then register.
            for u in pending { continuation.yield(u) }
            pending.removeAll()
            continuations[id] = continuation
            lock.unlock()
            continuation.onTermination = { [weak self] _ in
                guard let self else { return }
                self.lock.lock()
                self.continuations[id] = nil
                self.lock.unlock()
            }
        }
    }

    // ─── Peer access ──────────────────────────────────────────────────────────

    /// Returns the existing entry for `nodeId`, or creates a new one initialised
    /// to `SecurityOptions.initialTrustScore`.
    @discardableResult
    public func getOrCreate(_ nodeId: String) -> NodeTrustEntry {
        lock.lock(); defer { lock.unlock() }
        return getOrCreateLocked(nodeId)
    }

    /// All peer IDs currently tracked.
    public var allNodeIds: [String] {
        lock.lock(); defer { lock.unlock() }
        return Array(nodes.keys)
    }

    /// Returns the current trust score for `nodeId`, or
    /// `SecurityOptions.initialTrustScore` for unknown peers.
    public func getTrustScore(_ nodeId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        if let entry = nodes[nodeId] { return entry.trustScore }
        return options.initialTrustScore
    }

    // ─── Mutations ────────────────────────────────────────────────────────────

    /// Applies trust degradation for a security event. Score is clamped to
    /// [0, 1]; the event is appended to the per-peer history; a
    /// `PeerTrustScoreUpdate` is published on the stream. Returns
    /// `(previousScore, newScore)`.
    @discardableResult
    public func applyDegradation(
        _ securityEvent: PeerSecurityEvent,
        degradationAmount: Double
    ) -> (previous: Double, current: Double) {
        // Snapshot the continuations to yield to AFTER releasing the lock: a
        // yield never re-enters this lock, but we keep the discipline uniform
        // and avoid holding the lock across fan-out.
        var toPublish: PeerTrustScoreUpdate?
        var result: (previous: Double, current: Double)

        lock.lock()
        let entry = getOrCreateLocked(securityEvent.nodeId)
        let previous = entry.trustScore
        entry.trustScore = securityClamp(previous - degradationAmount, 0.0, 1.0)
        entry.lastUpdated = securityEvent.occurredAt

        // Maintain bounded event list (oldest dropped first).
        entry.recentEvents.append(securityEvent)
        while entry.recentEvents.count > options.maxEventsPerNode {
            entry.recentEvents.removeFirst()
        }

        let current = entry.trustScore
        result = (previous, current)

        if abs(current - previous) > 0.0001 {
            toPublish = PeerTrustScoreUpdate(
                nodeId: entry.nodeId,
                previousScore: previous,
                newScore: current,
                reason: securityEvent.description,
                changedAt: securityEvent.occurredAt)
        }
        lock.unlock()

        if let u = toPublish { publish(u) }
        return result
    }

    /// Passively heals all tracked peers by `recoveryRatePerSecond × elapsed`.
    /// Peers already at 1.0 are skipped. Called by the background recovery timer.
    public func applyRecovery(_ elapsed: TimeInterval) {
        let amount = options.recoveryRatePerSecond * elapsed
        if amount <= 0 { return }

        var updates: [PeerTrustScoreUpdate] = []

        lock.lock()
        let now = Date()
        for entry in nodes.values {
            if entry.trustScore >= 1.0 { continue }
            let previous = entry.trustScore
            entry.trustScore = min(1.0, previous + amount)
            entry.lastUpdated = now
            updates.append(PeerTrustScoreUpdate(
                nodeId: entry.nodeId,
                previousScore: previous,
                newScore: entry.trustScore,
                reason: "passive-recovery",
                changedAt: now))
        }
        lock.unlock()

        for u in updates { publish(u) }
    }

    // ─── History queries ──────────────────────────────────────────────────────

    /// Returns events for `nodeId` that fall within `SecurityOptions.eventWindow`
    /// of now. Returns an empty list for unknown peers.
    public func getRecentEvents(_ nodeId: String) -> [PeerSecurityEvent] {
        lock.lock(); defer { lock.unlock() }
        guard let entry = nodes[nodeId] else { return [] }
        let cutoff = Date().addingTimeInterval(-options.eventWindow)
        return entry.recentEvents.filter { $0.occurredAt >= cutoff }
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    /// MUST be called with `lock` held.
    private func getOrCreateLocked(_ nodeId: String) -> NodeTrustEntry {
        if let existing = nodes[nodeId] { return existing }
        let created = NodeTrustEntry(nodeId: nodeId, trustScore: options.initialTrustScore)
        nodes[nodeId] = created
        return created
    }

    /// Fan-out one update. If no subscriber is attached, buffer it so the next
    /// subscriber receives it (unbounded, matching the C# channel).
    private func publish(_ update: PeerTrustScoreUpdate) {
        lock.lock()
        if continuations.isEmpty {
            pending.append(update)
            lock.unlock()
            return
        }
        let conts = Array(continuations.values)
        lock.unlock()
        for c in conts { c.yield(update) }
    }
}

// MARK: - DirectivePublisher

/// Manages `IPeerDirectiveConsumer` subscriptions and fans published
/// `PeerDirective` instances out to all subscribers.
///
/// Concurrent subscribe, unsubscribe, and publish operations are all thread-safe.
public final class DirectivePublisher: @unchecked Sendable {
    private let lock = NSLock()
    private var consumers: [ObjectIdentifier: IPeerDirectiveConsumer] = [:]

    public init() {}

    // ─── Public API ──────────────────────────────────────────────────────────

    /// Subscribes `consumer` to receive directives. Dispose the returned handle
    /// to unsubscribe. Idempotent disposal.
    public func subscribe(_ consumer: IPeerDirectiveConsumer) -> IDirectiveSubscription {
        let key = ObjectIdentifier(consumer)
        lock.lock(); consumers[key] = consumer; lock.unlock()
        return SubscriptionHandle(publisher: self, key: key)
    }

    /// Publishes `directive` to all current subscribers. A snapshot is taken
    /// under the lock; callbacks fire outside it.
    public func publish(_ directive: PeerDirective) {
        lock.lock()
        let snapshot = Array(consumers.values)
        lock.unlock()
        for c in snapshot { c.onDirective(directive) }
    }

    /// Number of currently active subscribers. Useful in tests.
    public var subscriberCount: Int {
        lock.lock(); defer { lock.unlock() }
        return consumers.count
    }

    // ─── Private ─────────────────────────────────────────────────────────────

    fileprivate func unsubscribe(_ key: ObjectIdentifier) {
        lock.lock(); consumers[key] = nil; lock.unlock()
    }

    private final class SubscriptionHandle: IDirectiveSubscription, @unchecked Sendable {
        private weak var publisher: DirectivePublisher?
        private let key: ObjectIdentifier
        private let disposeLock = NSLock()
        private var disposed = false

        init(publisher: DirectivePublisher, key: ObjectIdentifier) {
            self.publisher = publisher
            self.key = key
        }

        func dispose() {
            disposeLock.lock()
            if disposed { disposeLock.unlock(); return }
            disposed = true
            disposeLock.unlock()
            publisher?.unsubscribe(key)
        }
    }
}

// MARK: - SecurityLayerService (IPeerSecurityLayer)

/// Transport-agnostic AI Security Layer. Degrades per-peer trust scores via
/// `ThreatDetector` and issues `PeerDirective` recommendations to all registered
/// `IPeerDirectiveConsumer` subscribers.
///
/// Lifecycle:
///   start  → launches the background trust-recovery loop.
///   (running)   → security events arrive via `handlePeerEvent`. Each event
///                 degrades the peer's trust score; threshold evaluation decides
///                 which `PeerDirective` (if any) to issue.
///   stop   → cancels the recovery loop, cleans up.
///
/// Directives issued (most-severe wins per event):
///   quarantineNode     trust ≤ quarantineThreshold
///   avoidNode          trust ≤ avoidNodeThreshold
///   elevateMonitoring  trust ≤ elevateMonitoringThreshold
///   releaseNode        not issued automatically — requires operator action
public final class SecurityLayerService: IPeerSecurityLayer, @unchecked Sendable {
    private let registry: NodeTrustRegistry
    private let options: SecurityOptions
    private let publisher: DirectivePublisher

    private let lock = NSLock()
    private var recoveryTask: Task<Void, Never>?
    private var active = false

    /// Recovery loop tick interval. Kept as a stored property (rather than a
    /// literal in the loop) so tests can drive recovery deterministically via
    /// `applyRecoveryTick()` without waiting real seconds.
    private let recoveryInterval: TimeInterval

    public init(
        registry: NodeTrustRegistry,
        options: SecurityOptions,
        publisher: DirectivePublisher,
        recoveryInterval: TimeInterval = 30
    ) {
        self.registry = registry
        self.options = options
        self.publisher = publisher
        self.recoveryInterval = recoveryInterval
    }

    /// Whether the recovery loop is currently running.
    public var isActive: Bool {
        lock.lock(); defer { lock.unlock() }
        return active
    }

    // ─── IPeerSecurityLayer ───────────────────────────────────────────────────

    public func start() async throws {
        lock.lock()
        if active { lock.unlock(); return }
        active = true
        let interval = recoveryInterval
        let reg = registry // NodeTrustRegistry is @unchecked Sendable — safe to capture
        // Loop termination is driven purely by cancellation (stop() cancels the
        // task). No self-capturing flag closure is needed, keeping the captured
        // set Sendable-clean.
        let task = Task {
            await Self.runRecoveryLoop(interval: interval, registry: reg)
        }
        recoveryTask = task
        lock.unlock()
    }

    public func stop() async throws {
        lock.lock()
        active = false
        let task = recoveryTask
        recoveryTask = nil
        lock.unlock()
        task?.cancel()
    }

    /// Call this from any transport adapter after translating its native event
    /// type to `PeerSecurityEvent`. Thread-safe.
    public func handlePeerEvent(_ e: PeerSecurityEvent) {
        let degradation = ThreatDetector.computeDegradation(e)
        if degradation <= 0 { return } // PeerThreatLevel.none — no trust impact

        let (previous, current) = registry.applyDegradation(e, degradationAmount: degradation)
        evaluateThresholds(nodeId: e.nodeId, previous: previous, current: current, reason: e.description)
    }

    /// Notify the security layer that a peer has left. Trust entry is preserved
    /// for historical queries; no directive is issued.
    public func handlePeerLeft(_ nodeId: String) {
        // Trust entry retained for forensic queries; no action on departure.
        _ = nodeId
    }

    public func subscribeToDirectives(_ consumer: IPeerDirectiveConsumer) -> IDirectiveSubscription {
        publisher.subscribe(consumer)
    }

    public func getPosture() async throws -> PeerSecurityPosture {
        let nodeIds = registry.allNodeIds
        let quarantined = nodeIds.filter { registry.getTrustScore($0) <= options.quarantineThreshold }.count
        let monitored = nodeIds.filter {
            let s = registry.getTrustScore($0)
            return s <= options.elevateMonitoringThreshold && s > options.quarantineThreshold
        }.count

        let worstScore = nodeIds.isEmpty
            ? 1.0
            : nodeIds.map { registry.getTrustScore($0) }.min()!
        let overallThreat = Self.scoreToThreatLevel(worstScore)

        return PeerSecurityPosture(
            overallThreatLevel: overallThreat,
            quarantinedPeerCount: quarantined,
            monitoredPeerCount: monitored,
            isActive: isActive,
            generatedAt: Date())
    }

    /// Deterministic single recovery tick — exposed so hosts/tests can drive
    /// passive recovery without the real-time loop.
    public func applyRecoveryTick(elapsed: TimeInterval) {
        registry.applyRecovery(elapsed)
    }

    // ─── Threshold evaluation ─────────────────────────────────────────────────

    private func evaluateThresholds(
        nodeId: String, previous: Double, current: Double, reason: String
    ) {
        // Evaluate from most-severe to least; issue at most one directive per event.

        if previous > options.quarantineThreshold && current <= options.quarantineThreshold {
            issueDirective(.quarantineNode, nodeId: nodeId, trustScore: current,
                           reason: reason, threatLevel: .critical)
            return
        }

        if previous > options.avoidNodeThreshold && current <= options.avoidNodeThreshold {
            issueDirective(.avoidNode, nodeId: nodeId, trustScore: current,
                           reason: reason, threatLevel: .high)
            return
        }

        if previous > options.elevateMonitoringThreshold && current <= options.elevateMonitoringThreshold {
            issueDirective(.elevateMonitoring, nodeId: nodeId, trustScore: current,
                           reason: reason, threatLevel: .medium)
        }
    }

    private func issueDirective(
        _ kind: PeerDirectiveKind, nodeId: String, trustScore: Double,
        reason: String, threatLevel: PeerThreatLevel
    ) {
        publisher.publish(PeerDirective(
            kind: kind,
            targetNodeId: nodeId,
            trustScore: trustScore,
            threatLevel: threatLevel,
            reason: reason,
            duration: nil, // permanent until releaseNode
            issuedAt: Date()))
    }

    // ─── Background recovery loop ─────────────────────────────────────────────

    private static func runRecoveryLoop(
        interval: TimeInterval,
        registry: NodeTrustRegistry
    ) async {
        let nanos = UInt64(max(0, interval) * 1_000_000_000)
        while !Task.isCancelled {
            do {
                try await Task.sleep(nanoseconds: nanos)
            } catch {
                break // cancellation
            }
            if Task.isCancelled { break }
            registry.applyRecovery(interval)
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    static func scoreToThreatLevel(_ score: Double) -> PeerThreatLevel {
        switch score {
        case ...0.25: return .critical
        case ...0.50: return .high
        case ...0.75: return .medium
        case ...0.90: return .low
        default:      return .none
        }
    }
}

// MARK: - PeerIntelligenceService (IPeerIntelligence)

/// Reads `NodeTrustRegistry` state to produce transport-agnostic intelligence
/// outputs. Wires directly to the registry's trust-score update stream for the
/// streaming API.
public final class PeerIntelligenceService: IPeerIntelligence, @unchecked Sendable {
    private let registry: NodeTrustRegistry
    private let options: SecurityOptions

    public init(registry: NodeTrustRegistry, options: SecurityOptions) {
        self.registry = registry
        self.options = options
    }

    // ─── IPeerIntelligence ────────────────────────────────────────────────────

    public func getNetworkHealth() async throws -> PeerNetworkHealthReport {
        let nodeIds = registry.allNodeIds

        if nodeIds.isEmpty {
            return PeerNetworkHealthReport(
                overallScore: 1.0,
                trustedPeerCount: 0,
                suspiciousPeerCount: 0,
                summary: "No peers observed.",
                generatedAt: Date())
        }

        let scores = nodeIds.map { registry.getTrustScore($0) }
        let overall = scores.reduce(0.0, +) / Double(scores.count)
        let trusted = scores.filter { $0 > options.avoidNodeThreshold }.count
        let suspicious = scores.filter { $0 <= options.elevateMonitoringThreshold }.count

        let summary: String
        switch overall {
        case let x where x > 0.90: summary = "Network health is excellent."
        case let x where x > 0.75: summary = "Network health is good; minor anomalies detected."
        case let x where x > 0.50: summary = "Network health is degraded; elevated monitoring active."
        case let x where x > 0.25: summary = "Network health is poor; routing around compromised peers."
        default:                   summary = "Network health is critical; quarantine directives in effect."
        }

        return PeerNetworkHealthReport(
            overallScore: overall,
            trustedPeerCount: trusted,
            suspiciousPeerCount: suspicious,
            summary: summary,
            generatedAt: Date())
    }

    public func assessThreat(nodeId: String) async throws -> PeerThreatAssessment {
        let score = registry.getTrustScore(nodeId)
        let deficit = 1.0 - score // 0 = fully trusted, 1 = fully lost

        let indicators = ThreatDetector.detectIndicators(
            registry.getRecentEvents(nodeId), window: options.eventWindow)

        let level: PeerThreatLevel
        switch score {
        case ...0.25: level = .critical
        case ...0.50: level = .high
        case ...0.75: level = .medium
        case ...0.90: level = .low
        default:      level = .none
        }

        // Confidence is proportional to trust deficit, boosted by each indicator.
        let confidence = min(1.0, deficit + Double(indicators.count) * 0.1)

        return PeerThreatAssessment(
            nodeId: nodeId,
            confidence: confidence,
            threatLevel: level,
            indicators: indicators,
            assessedAt: Date())
    }

    public func getRoutingAdvice(destinationNodeId: String) async throws -> PeerRoutingAdvice {
        let allNodes = registry.allNodeIds
        let avoidNodes = allNodes.filter { registry.getTrustScore($0) <= options.avoidNodeThreshold }

        let destScore = registry.getTrustScore(destinationNodeId)

        // Recommended path is direct only when destination is above avoid-threshold.
        let recommended: [String] = destScore > options.avoidNodeThreshold ? [destinationNodeId] : []

        let reasoning: String
        switch destScore {
        case let x where x > 0.75:
            reasoning = "Direct path to \(destinationNodeId) is trusted (score \(Self.f2(destScore)))."
        case let x where x > 0.50:
            reasoning = "Destination \(destinationNodeId) is under monitoring; routing with caution."
        case let x where x > 0.25:
            reasoning = "Destination \(destinationNodeId) has degraded trust; avoid recommended."
        default:
            reasoning = "Destination \(destinationNodeId) is quarantined; no safe path available."
        }

        return PeerRoutingAdvice(
            destinationNodeId: destinationNodeId,
            recommendedPath: recommended,
            avoidNodeIds: avoidNodes,
            confidence: destScore,
            reasoning: reasoning,
            generatedAt: Date())
    }

    public func streamTrustScores() -> AsyncStream<PeerTrustScoreUpdate> {
        // Delegate to the registry's buffered broadcast. Subscription happens
        // synchronously inside `trustScoreUpdates()` before any consumer Task is
        // spawned by the caller, so no update published right after this call is
        // lost.
        registry.trustScoreUpdates()
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// Renders a Double with 2 fractional digits ("F2" in .NET), invariant.
    static func f2(_ value: Double) -> String {
        String(format: "%.2f", value)
    }
}

// MARK: - Shared numeric helper

/// Clamp `value` into `[lo, hi]`. Mirrors `Math.Clamp`. Named distinctly to avoid
/// colliding with the type-scoped `clamp` helpers elsewhere in the module.
@inline(__always)
func securityClamp(_ value: Double, _ lo: Double, _ hi: Double) -> Double {
    min(max(value, lo), hi)
}
