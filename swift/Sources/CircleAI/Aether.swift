// Aether.swift
//
// Port of CircleAI.Aether (the C# reference) — the ONE-WAY contract boundary
// between the Aether mesh protocol and BhenguAI. Collapses the C# folder
// structure (Events/ + the five root contract files) into one Swift file per
// the tree's flat convention.
//
//   Contract 1 — Telemetry      (IAetherTelemetry / IAetherTelemetryObserver)
//   Contract 2 — Presence       (IAetherContext + AetherInstallLevel)
//   Contract 3 — Intelligence   (IAetherIntelligence + report DTOs)
//   Contract 4 — Security Layer (IAISecurityLayer + SecurityDirective)
//   Contract 5 — Auth Challenge (IAuthChallenge + AuthMethod / reason)
//   Events                      (Node / Transport / Route / Security / Network)
//
// The boundary is strictly one-way: Aether PUBLISHES telemetry; BhenguAI
// SUBSCRIBES and produces directives/intelligence. Aether never calls into
// BhenguAI. External Aether adopters can implement IAetherTelemetry with no
// AI dependency at all.
//
// This file ports the interfaces PLUS working in-memory implementations of
// each (no stubs): InMemoryAuthChallenge, InMemoryAetherContext,
// InMemoryAetherTelemetry (a real broadcasting hub), and NullAetherTelemetry.
//
// Concurrency notes (telemetry is fan-out pub/sub):
//   • InMemoryAetherTelemetry snapshots its observer set under an NSLock and
//     fans out AFTER releasing the lock, so an observer callback that
//     subscribes/unsubscribes cannot self-deadlock the non-reentrant lock.
//   • Subscription registration is synchronous — an observer added before a
//     publish sees that publish.

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// Events
// ──────────────────────────────────────────────────────────────────────────

// MARK: Node events

/// Kinds of node lifecycle transitions Aether can emit.
/// Ordinals follow the C# declaration order (cross-language wire).
public enum AetherNodeEventKind: Int, Codable, Sendable, CaseIterable {
    case joined = 0
    case left = 1
    case healthChanged = 2
}

/// Point-in-time health snapshot for a single mesh node.
///
/// - `trustScore`: 0.0 (untrusted) to 1.0 (fully trusted). Maintained by the AI
///   Security Layer when active; defaults to 1.0 for all nodes when the security
///   layer is off.
public struct AetherNodeHealth: Sendable, Equatable, Codable {
    public let trustScore: Double
    public let isReachable: Bool
    public let latency: TimeInterval
    public let hopCount: Int

    public init(trustScore: Double, isReachable: Bool, latency: TimeInterval, hopCount: Int) {
        self.trustScore = trustScore
        self.isReachable = isReachable
        self.latency = latency
        self.hopCount = hopCount
    }

    /// Returns true when `trustScore` is within the valid 0–1 range.
    public var isValid: Bool { trustScore >= 0.0 && trustScore <= 1.0 }
}

/// Emitted by Aether whenever a node joins, leaves, or changes health.
public struct AetherNodeEvent: Sendable, Equatable, Codable {
    public let nodeId: String
    public let kind: AetherNodeEventKind
    public let health: AetherNodeHealth
    public let occurredAt: Date

    public init(nodeId: String, kind: AetherNodeEventKind, health: AetherNodeHealth, occurredAt: Date) {
        self.nodeId = nodeId
        self.kind = kind
        self.health = health
        self.occurredAt = occurredAt
    }

    /// Convenience: true when this is a departure event.
    public var isExit: Bool { kind == .left }
}

// MARK: Transport events

/// Physical or logical transport medium Aether is using.
public enum AetherTransportKind: Int, Codable, Sendable, CaseIterable {
    case wiFi = 0
    case bluetooth = 1
    case loRa = 2
    case nfc = 3
    case cellular = 4
    case ethernet = 5
    case unknown = 6
}

/// Kinds of transport-layer observations Aether can emit.
public enum AetherTransportEventKind: Int, Codable, Sendable, CaseIterable {
    case selected = 0
    case changed = 1
    case latencyMeasured = 2
    case packetLoss = 3
}

/// Emitted when Aether selects, changes, or measures quality on a transport channel.
public struct AetherTransportEvent: Sendable, Equatable, Codable {
    public let nodeId: String
    public let kind: AetherTransportEventKind
    public let transport: AetherTransportKind
    public let latency: TimeInterval?
    public let packetLossRate: Double?
    public let occurredAt: Date

    public init(
        nodeId: String,
        kind: AetherTransportEventKind,
        transport: AetherTransportKind,
        latency: TimeInterval?,
        packetLossRate: Double?,
        occurredAt: Date
    ) {
        self.nodeId = nodeId
        self.kind = kind
        self.transport = transport
        self.latency = latency
        self.packetLossRate = packetLossRate
        self.occurredAt = occurredAt
    }

    /// Returns true when `packetLossRate` is set and exceeds `threshold` (0.0–1.0).
    public func exceedsLoss(_ threshold: Double) -> Bool {
        guard let rate = packetLossRate else { return false }
        return rate > threshold
    }
}

// MARK: Route events

/// Kinds of routing changes Aether can emit.
public enum AetherRouteEventKind: Int, Codable, Sendable, CaseIterable {
    case discovered = 0
    case changed = 1
    case failed = 2
}

/// Emitted when Aether discovers, updates, or loses a route between two nodes.
public struct AetherRouteEvent: Sendable, Equatable, Codable {
    public let sourceNodeId: String
    public let destinationNodeId: String
    public let path: [String]
    public let kind: AetherRouteEventKind
    public let failureReason: String?
    public let occurredAt: Date

    public init(
        sourceNodeId: String,
        destinationNodeId: String,
        path: [String],
        kind: AetherRouteEventKind,
        failureReason: String?,
        occurredAt: Date
    ) {
        self.sourceNodeId = sourceNodeId
        self.destinationNodeId = destinationNodeId
        self.path = path
        self.kind = kind
        self.failureReason = failureReason
        self.occurredAt = occurredAt
    }

    /// Number of hops in this route, including source and destination.
    public var hopCount: Int { path.count }

    /// True when this event represents a routing failure.
    public var isFailed: Bool { kind == .failed }
}

// MARK: Security events

/// Categories of security-relevant observations Aether can detect at the
/// protocol layer, without requiring AI.
public enum AetherSecurityEventKind: Int, Codable, Sendable, CaseIterable {
    /// A node attempted to authenticate into the mesh.
    case nodeAuthAttempt = 0
    /// Traffic was observed deviating from expected routing paths.
    case routingAnomaly = 1
    /// A node's behaviour deviated from its established baseline.
    case nodeBehaviourChange = 2
    /// A key exchange or certificate validation event occurred.
    case encryptionEvent = 3
    /// Active attack signature detected (e.g. replay, spoofing).
    case intrusionSignal = 4
    /// A node requested capabilities beyond its granted level.
    case privilegeAttempt = 5
}

/// Protocol-level threat severity as assessed by Aether itself, before any
/// AI reasoning is applied.
public enum AetherThreatLevel: Int, Codable, Sendable, Comparable, CaseIterable {
    case none = 0
    case low = 1
    case medium = 2
    case high = 3
    case critical = 4

    public static func < (lhs: AetherThreatLevel, rhs: AetherThreatLevel) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

/// Emitted by Aether when a security-relevant event occurs at the protocol layer.
/// This is the primary feed for the AI Security Layer.
public struct AetherSecurityEvent: Sendable, Equatable, Codable {
    public let nodeId: String
    public let kind: AetherSecurityEventKind
    public let threatLevel: AetherThreatLevel
    public let description: String
    public let metadata: [String: String]
    public let occurredAt: Date

    public init(
        nodeId: String,
        kind: AetherSecurityEventKind,
        threatLevel: AetherThreatLevel,
        description: String,
        metadata: [String: String],
        occurredAt: Date
    ) {
        self.nodeId = nodeId
        self.kind = kind
        self.threatLevel = threatLevel
        self.description = description
        self.metadata = metadata
        self.occurredAt = occurredAt
    }

    /// True when `threatLevel` is High or Critical.
    public var isHighSeverity: Bool {
        threatLevel == .high || threatLevel == .critical
    }
}

// MARK: Network events

/// Mesh-wide topology and congestion observations.
public enum AetherNetworkEventKind: Int, Codable, Sendable, CaseIterable {
    case topologyChanged = 0
    case congestionDetected = 1
    case partitionDetected = 2
}

/// Emitted when the mesh topology or overall network health changes.
public struct AetherNetworkEvent: Sendable, Equatable, Codable {
    public let kind: AetherNetworkEventKind
    public let nodeCount: Int
    public let activeRouteCount: Int
    public let congestionLevel: Double
    public let occurredAt: Date

    public init(
        kind: AetherNetworkEventKind,
        nodeCount: Int,
        activeRouteCount: Int,
        congestionLevel: Double,
        occurredAt: Date
    ) {
        self.kind = kind
        self.nodeCount = nodeCount
        self.activeRouteCount = activeRouteCount
        self.congestionLevel = congestionLevel
        self.occurredAt = occurredAt
    }

    /// True when `congestionLevel` exceeds 0.75 — a useful default alert threshold.
    public var isHighCongestion: Bool { congestionLevel > 0.75 }
}

// ──────────────────────────────────────────────────────────────────────────
// Contract 1 — Telemetry
// ──────────────────────────────────────────────────────────────────────────

/// Receives events emitted by Aether. Implement this to react to mesh activity —
/// nodes, transports, routes, security signals, and topology.
public protocol IAetherTelemetryObserver: AnyObject {
    func onNodeEvent(_ e: AetherNodeEvent)
    func onTransportEvent(_ e: AetherTransportEvent)
    func onRouteEvent(_ e: AetherRouteEvent)
    func onSecurityEvent(_ e: AetherSecurityEvent)
    func onNetworkEvent(_ e: AetherNetworkEvent)
}

/// A disposable handle. Mirrors C#'s `IDisposable` returned by
/// `IAetherTelemetry.Subscribe`. `dispose()` is idempotent.
public protocol IAetherSubscription: AnyObject, Sendable {
    /// Unsubscribe. Idempotent.
    func dispose()
}

/// The outward-facing telemetry surface of Aether. The AI Security Layer and any
/// other BhenguAI component subscribes here. Aether owns this interface and
/// publishes; consumers subscribe and dispose.
public protocol IAetherTelemetry: AnyObject {
    /// Subscribe to all Aether telemetry events.
    /// Dispose the returned handle to unsubscribe.
    func subscribe(_ observer: IAetherTelemetryObserver) -> IAetherSubscription
}

/// No-op subscription handle — used by `NullAetherTelemetry`.
public final class NullAetherSubscription: IAetherSubscription, @unchecked Sendable {
    public static let shared = NullAetherSubscription()
    public init() {}
    public func dispose() {}
}

/// No-op telemetry — useful for unit tests and environments where Aether is
/// absent. `subscribe` returns a no-op handle; no events are emitted.
public final class NullAetherTelemetry: IAetherTelemetry, @unchecked Sendable {
    public static let shared = NullAetherTelemetry()
    public init() {}

    public func subscribe(_ observer: IAetherTelemetryObserver) -> IAetherSubscription {
        NullAetherSubscription.shared
    }
}

/// Working in-memory telemetry hub. A source of Aether events can `publish*`
/// each event and every subscribed observer is notified synchronously (matching
/// the C# observer-callback semantics). Fan-out is snapshot-then-release so an
/// observer that (un)subscribes from within a callback never self-deadlocks.
///
/// This is the concrete implementation the C# side leaves to the platform
/// adapter; here it is fully functional so the whole telemetry → security →
/// directive pipeline is testable end-to-end with no external mesh.
public final class InMemoryAetherTelemetry: IAetherTelemetry, @unchecked Sendable {
    private let lock = NSLock()
    private var observers: [UUID: IAetherTelemetryObserver] = [:]

    public init() {}

    public func subscribe(_ observer: IAetherTelemetryObserver) -> IAetherSubscription {
        let id = UUID()
        lock.lock()
        observers[id] = observer
        lock.unlock()
        return Handle(owner: self, id: id)
    }

    /// Number of active subscribers. Useful in tests.
    public var subscriberCount: Int {
        lock.lock(); defer { lock.unlock() }
        return observers.count
    }

    // ── Publish surface (the source side of the bus) ──────────────────────────

    public func publishNodeEvent(_ e: AetherNodeEvent) {
        for o in snapshot() { o.onNodeEvent(e) }
    }

    public func publishTransportEvent(_ e: AetherTransportEvent) {
        for o in snapshot() { o.onTransportEvent(e) }
    }

    public func publishRouteEvent(_ e: AetherRouteEvent) {
        for o in snapshot() { o.onRouteEvent(e) }
    }

    public func publishSecurityEvent(_ e: AetherSecurityEvent) {
        for o in snapshot() { o.onSecurityEvent(e) }
    }

    public func publishNetworkEvent(_ e: AetherNetworkEvent) {
        for o in snapshot() { o.onNetworkEvent(e) }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// Snapshot the observer set under the lock; callbacks fire OUTSIDE it.
    private func snapshot() -> [IAetherTelemetryObserver] {
        lock.lock(); defer { lock.unlock() }
        return Array(observers.values)
    }

    private func remove(_ id: UUID) {
        lock.lock(); observers[id] = nil; lock.unlock()
    }

    private final class Handle: IAetherSubscription, @unchecked Sendable {
        private weak var owner: InMemoryAetherTelemetry?
        private let id: UUID
        private let disposeLock = NSLock()
        private var disposed = false

        init(owner: InMemoryAetherTelemetry, id: UUID) {
            self.owner = owner
            self.id = id
        }

        func dispose() {
            disposeLock.lock()
            if disposed { disposeLock.unlock(); return }
            disposed = true
            disposeLock.unlock()
            owner?.remove(id)
        }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// Contract 2 — Presence and Capability
// ──────────────────────────────────────────────────────────────────────────

/// Indicates where Aether is installed and who manages it.
public enum AetherInstallLevel: Int, Codable, Sendable, CaseIterable {
    /// Aether is not present on this device.
    case none = 0
    /// Aether was installed at app level — bundled or downloaded at first launch.
    case app = 1
    /// Aether is a system service managed by the OS.
    case os = 2
}

/// Reports the presence, version, and capability of the Aether runtime on this
/// device. Inject this; the platform adapter provides the concrete implementation.
public protocol IAetherContext: AnyObject {
    /// Where Aether is installed, if at all.
    var installLevel: AetherInstallLevel { get }

    /// True when Aether is installed and enabled.
    var isAvailable: Bool { get }

    /// The installed Aether runtime version, or nil when Aether is absent.
    var runtimeVersion: SemanticVersion? { get }

    /// The minimum Aether version declared as required by the consuming app.
    var minimumRequired: SemanticVersion? { get }

    /// True when `runtimeVersion` satisfies `minimumRequired`.
    /// Always true when `minimumRequired` is nil.
    var isSufficient: Bool { get }

    /// True when the install level is `.os`.
    var requiresAuth: Bool { get }

    /// True when Aether is installed and currently enabled.
    var isEnabled: Bool { get }
}

/// A four-component version (Major.Minor.Build.Revision), the Swift analogue of
/// .NET `System.Version`. Comparison is component-wise; unspecified components
/// compare as if 0. Immutable value type.
public struct SemanticVersion: Sendable, Equatable, Comparable, Codable, CustomStringConvertible {
    public let major: Int
    public let minor: Int
    public let build: Int
    public let revision: Int

    public init(major: Int, minor: Int = 0, build: Int = 0, revision: Int = 0) {
        self.major = major
        self.minor = minor
        self.build = build
        self.revision = revision
    }

    public static func < (lhs: SemanticVersion, rhs: SemanticVersion) -> Bool {
        if lhs.major != rhs.major { return lhs.major < rhs.major }
        if lhs.minor != rhs.minor { return lhs.minor < rhs.minor }
        if lhs.build != rhs.build { return lhs.build < rhs.build }
        return lhs.revision < rhs.revision
    }

    public var description: String { "\(major).\(minor).\(build).\(revision)" }
}

/// Working in-memory `IAetherContext`. Mirrors the C# `AetherNetContextAdapter`
/// shape: install level, runtime version, configured minimum, and enabled state
/// are all supplied at construction. `isSufficient` / `requiresAuth` are derived.
public final class InMemoryAetherContext: IAetherContext, @unchecked Sendable {
    public let installLevel: AetherInstallLevel
    public let runtimeVersion: SemanticVersion?
    public let minimumRequired: SemanticVersion?
    public let isEnabled: Bool

    /// - Parameters:
    ///   - installLevel: where Aether is installed. Defaults to `.app`
    ///     (the in-process library assumption).
    ///   - runtimeVersion: the installed runtime version, or nil when absent.
    ///   - minimumRequired: the app's declared minimum; nil means any version.
    ///   - isEnabled: whether Aether is currently enabled. Default true.
    public init(
        installLevel: AetherInstallLevel = .app,
        runtimeVersion: SemanticVersion?,
        minimumRequired: SemanticVersion? = nil,
        isEnabled: Bool = true
    ) {
        self.installLevel = installLevel
        self.runtimeVersion = runtimeVersion
        self.minimumRequired = minimumRequired
        self.isEnabled = isEnabled
    }

    /// True when Aether is present (install level not `.none`) and enabled.
    public var isAvailable: Bool { installLevel != .none && isEnabled }

    public var isSufficient: Bool {
        guard let minimum = minimumRequired else { return true }
        guard let runtime = runtimeVersion else { return false }
        return runtime >= minimum
    }

    public var requiresAuth: Bool { installLevel == .os }
}

// ──────────────────────────────────────────────────────────────────────────
// Contract 3 — Intelligence Output
// ──────────────────────────────────────────────────────────────────────────

/// Aggregate health of the mesh as assessed by BhenguAI.
public struct NetworkHealthReport: Sendable, Equatable, Codable {
    public let overallScore: Double
    public let trustedNodeCount: Int
    public let suspiciousNodeCount: Int
    public let summary: String
    public let generatedAt: Date

    public init(
        overallScore: Double,
        trustedNodeCount: Int,
        suspiciousNodeCount: Int,
        summary: String,
        generatedAt: Date
    ) {
        self.overallScore = overallScore
        self.trustedNodeCount = trustedNodeCount
        self.suspiciousNodeCount = suspiciousNodeCount
        self.summary = summary
        self.generatedAt = generatedAt
    }

    /// True when `overallScore` is within the valid 0–1 range.
    public var isValid: Bool { overallScore >= 0.0 && overallScore <= 1.0 }
}

/// BhenguAI's assessment of the threat posed by a specific node.
public struct ThreatAssessment: Sendable, Equatable, Codable {
    public let nodeId: String
    public let threatConfidence: Double
    public let level: AetherThreatLevel
    public let indicators: [String]
    public let assessedAt: Date

    public init(
        nodeId: String,
        threatConfidence: Double,
        level: AetherThreatLevel,
        indicators: [String],
        assessedAt: Date
    ) {
        self.nodeId = nodeId
        self.threatConfidence = threatConfidence
        self.level = level
        self.indicators = indicators
        self.assessedAt = assessedAt
    }

    /// True when `threatConfidence` is within the valid 0–1 range.
    public var isValid: Bool { threatConfidence >= 0.0 && threatConfidence <= 1.0 }
}

/// BhenguAI's recommendation for routing to a destination node, taking trust
/// scores and current threat assessments into account.
public struct RoutingAdvice: Sendable, Equatable, Codable {
    public let destinationNodeId: String
    public let recommendedPath: [String]
    public let avoidNodes: [String]
    public let confidence: Double
    public let reasoning: String
    public let generatedAt: Date

    public init(
        destinationNodeId: String,
        recommendedPath: [String],
        avoidNodes: [String],
        confidence: Double,
        reasoning: String,
        generatedAt: Date
    ) {
        self.destinationNodeId = destinationNodeId
        self.recommendedPath = recommendedPath
        self.avoidNodes = avoidNodes
        self.confidence = confidence
        self.reasoning = reasoning
        self.generatedAt = generatedAt
    }
}

/// Emitted when BhenguAI revises the trust score for a node.
public struct TrustScoreUpdate: Sendable, Equatable, Codable {
    public let nodeId: String
    public let previousScore: Double
    public let currentScore: Double
    public let reason: String
    public let updatedAt: Date

    public init(
        nodeId: String,
        previousScore: Double,
        currentScore: Double,
        reason: String,
        updatedAt: Date
    ) {
        self.nodeId = nodeId
        self.previousScore = previousScore
        self.currentScore = currentScore
        self.reason = reason
        self.updatedAt = updatedAt
    }

    /// True when the score moved in either direction.
    public var hasChanged: Bool { abs(currentScore - previousScore) > 0.001 }

    /// True when the score decreased.
    public var isDegraded: Bool { currentScore < previousScore }
}

/// The intelligence output surface produced by BhenguAI from Aether telemetry.
/// Consumed by apps and the Security Layer; never by Aether.
public protocol IAetherIntelligence: AnyObject {
    /// Returns an aggregate health report for the current mesh state.
    func getNetworkHealth() async throws -> NetworkHealthReport

    /// Assesses the current threat level of a specific node.
    /// Returns a zero-confidence assessment when the node is unknown.
    func assessThreat(nodeId: String) async throws -> ThreatAssessment

    /// Returns a routing recommendation for reaching the given destination,
    /// factoring out nodes with low trust scores.
    func getRoutingAdvice(destinationNodeId: String) async throws -> RoutingAdvice

    /// Streams trust score updates as BhenguAI observes new telemetry.
    func streamTrustScores() -> AsyncStream<TrustScoreUpdate>
}

// ──────────────────────────────────────────────────────────────────────────
// Contract 4 — Security Layer
// ──────────────────────────────────────────────────────────────────────────

/// The action BhenguAI is recommending to Aether's policy engine.
public enum SecurityDirectiveKind: Int, Codable, Sendable, CaseIterable {
    /// Adjust the recorded trust score for a node.
    case updateNodeTrust = 0
    /// Exclude the node from routing decisions (soft block).
    case avoidNode = 1
    /// Hard block — no traffic to or from the node until released.
    case quarantineNode = 2
    /// Lift an AvoidNode or QuarantineNode directive.
    case releaseNode = 3
    /// Request that the user re-authenticates before a sensitive operation.
    case requestReauth = 4
    /// Increase telemetry verbosity for the target node.
    case elevateMonitoring = 5
}

/// An instruction published by the AI Security Layer to Aether's policy engine.
/// Aether is never required to honour a directive — adoption is a policy
/// decision for each deployment.
public struct SecurityDirective: Sendable, Equatable, Codable {
    public let kind: SecurityDirectiveKind
    public let targetNodeId: String?
    public let trustScoreOverride: Double?
    public let threatLevel: AetherThreatLevel
    public let reason: String
    public let duration: TimeInterval?
    public let issuedAt: Date

    public init(
        kind: SecurityDirectiveKind,
        targetNodeId: String?,
        trustScoreOverride: Double?,
        threatLevel: AetherThreatLevel,
        reason: String,
        duration: TimeInterval?,
        issuedAt: Date
    ) {
        self.kind = kind
        self.targetNodeId = targetNodeId
        self.trustScoreOverride = trustScoreOverride
        self.threatLevel = threatLevel
        self.reason = reason
        self.duration = duration
        self.issuedAt = issuedAt
    }

    /// True when the directive targets a specific node.
    public var hasTarget: Bool {
        guard let id = targetNodeId else { return false }
        return !id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    /// True when `duration` is nil — the directive has no automatic expiry.
    public var isPermanent: Bool { duration == nil }
}

/// Point-in-time summary of the AI Security Layer's current posture.
public struct SecurityPosture: Sendable, Equatable, Codable {
    public let overallThreatLevel: AetherThreatLevel
    public let quarantinedNodeCount: Int
    public let monitoredNodeCount: Int
    public let isActive: Bool
    public let assessedAt: Date

    public init(
        overallThreatLevel: AetherThreatLevel,
        quarantinedNodeCount: Int,
        monitoredNodeCount: Int,
        isActive: Bool,
        assessedAt: Date
    ) {
        self.overallThreatLevel = overallThreatLevel
        self.quarantinedNodeCount = quarantinedNodeCount
        self.monitoredNodeCount = monitoredNodeCount
        self.isActive = isActive
        self.assessedAt = assessedAt
    }
}

/// Receives security directives from the AI Security Layer. Implement this on
/// Aether's policy engine to participate in AI-guided security decisions.
public protocol ISecurityDirectiveConsumer: AnyObject {
    /// Called each time BhenguAI issues a security directive.
    /// Implementations decide whether and how to honour it.
    func onDirective(_ directive: SecurityDirective)
}

/// The AI Security Layer contract. BhenguAI implements this by subscribing to
/// `IAetherTelemetry` and producing `SecurityDirective` outputs consumed by
/// Aether's policy engine via `ISecurityDirectiveConsumer`.
public protocol IAISecurityLayer: AnyObject {
    /// Wire the security layer to an Aether telemetry feed and begin processing.
    func start(telemetry: IAetherTelemetry) async throws

    /// Stop processing and release all telemetry subscriptions.
    func stop() async throws

    /// Subscribe a policy engine to receive security directives.
    /// Dispose the returned handle to unsubscribe.
    func subscribeToDirectives(_ consumer: ISecurityDirectiveConsumer) -> IAetherSubscription

    /// Returns the current security posture snapshot.
    func getPosture() async throws -> SecurityPosture
}

// ──────────────────────────────────────────────────────────────────────────
// Contract 5 — Auth Challenge
// ──────────────────────────────────────────────────────────────────────────

/// Why an auth challenge is being issued.
public enum AuthChallengeReason: Int, Codable, Sendable, CaseIterable {
    /// The user is enabling or disabling the OS-level Aether service.
    case osLevelToggle = 0
    /// Anomaly scores crossed the configured threshold; confirm identity.
    case threatThresholdReached = 1
    /// The operation being attempted requires elevated auth.
    case privilegedOperation = 2
    /// Scheduled trust renewal — periodic re-validation.
    case periodicRevalidation = 3
    /// Explicitly triggered by the developer or admin.
    case manualRequest = 4
}

/// The authentication method used or required. Methods are ordered by strength;
/// higher raw values are stronger. Raw values mirror the C# explicit values
/// (Biometric = 1 … Custom = 4), NOT declaration index.
public enum AuthMethod: Int, Codable, Sendable, Comparable, CaseIterable {
    /// Fingerprint, face, or iris recognition.
    case biometric = 1
    /// Device administrator credential (PIN, password, pattern).
    case deviceAdmin = 2
    /// Biometric AND device admin — the minimum for any OS-level operation.
    case biometricAndDeviceAdmin = 3
    /// Developer-defined method layered on top of biometricAndDeviceAdmin.
    case custom = 4

    public static func < (lhs: AuthMethod, rhs: AuthMethod) -> Bool {
        lhs.rawValue < rhs.rawValue
    }
}

/// The outcome of an auth challenge.
public struct AuthChallengeResult: Sendable, Equatable, Codable {
    public let succeeded: Bool
    public let methodUsed: AuthMethod
    public let failureReason: String?
    public let completedAt: Date

    public init(succeeded: Bool, methodUsed: AuthMethod, failureReason: String?, completedAt: Date) {
        self.succeeded = succeeded
        self.methodUsed = methodUsed
        self.failureReason = failureReason
        self.completedAt = completedAt
    }

    /// Convenience: a successful result with no failure reason.
    public static func success(_ method: AuthMethod, at now: Date = Date()) -> AuthChallengeResult {
        AuthChallengeResult(succeeded: true, methodUsed: method, failureReason: nil, completedAt: now)
    }

    /// Convenience: a failed result with an explanatory reason.
    public static func failure(_ method: AuthMethod, reason: String, at now: Date = Date()) -> AuthChallengeResult {
        AuthChallengeResult(succeeded: false, methodUsed: method, failureReason: reason, completedAt: now)
    }
}

/// Issues and resolves authentication challenges for security-sensitive
/// operations. Platform adapters implement this using native biometric and
/// device admin APIs.
public protocol IAuthChallenge: AnyObject {
    /// Presents an auth challenge to the user for the given reason. The platform
    /// adapter enforces the minimum method requirement.
    ///
    /// - Parameters:
    ///   - reason: why auth is being requested.
    ///   - minimumMethod: the weakest method acceptable. Defaults to
    ///     `.biometricAndDeviceAdmin` when nil.
    ///   - prompt: human-readable message shown to the user.
    func challenge(
        reason: AuthChallengeReason,
        minimumMethod: AuthMethod?,
        prompt: String
    ) async throws -> AuthChallengeResult

    /// Presents the OS-level toggle challenge. Always requires
    /// `.biometricAndDeviceAdmin` at minimum.
    ///
    /// - Parameter enable: true to enable the service, false to disable.
    func requestOsToggle(enable: Bool) async throws -> AuthChallengeResult
}

/// Working in-memory `IAuthChallenge`. Deterministic: a configurable set of
/// methods the "device" can satisfy decides success. The gate enforces the
/// documented minimum (`.biometricAndDeviceAdmin` for OS-level toggles, and the
/// requested minimum — defaulting to `.biometricAndDeviceAdmin` — otherwise)
/// and never lets a caller drop below `.biometricAndDeviceAdmin`.
///
/// This models the contract fully with no UI: given the available capability,
/// it decides whether the strongest satisfiable method meets the required bar.
public final class InMemoryAuthChallenge: IAuthChallenge, @unchecked Sendable {
    /// The methods this simulated device can satisfy. `challenge` succeeds when
    /// the strongest available method is at least the effective minimum.
    private let available: Set<AuthMethod>
    private let clock: @Sendable () -> Date

    /// The floor below which no operation may be authorised — matches the C#
    /// documented "cannot lower below the minimum" rule.
    public static let absoluteMinimum: AuthMethod = .biometricAndDeviceAdmin

    /// - Parameters:
    ///   - available: methods the device can satisfy. Defaults to the full set
    ///     (a fully-capable device) so the happy path succeeds out of the box.
    ///   - clock: time source for `completedAt`. Defaults to `Date()`.
    public init(
        available: Set<AuthMethod> = [.biometric, .deviceAdmin, .biometricAndDeviceAdmin, .custom],
        clock: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.available = available
        self.clock = clock
    }

    public func challenge(
        reason: AuthChallengeReason,
        minimumMethod: AuthMethod?,
        prompt: String
    ) async throws -> AuthChallengeResult {
        // Effective minimum can never be weaker than the absolute floor.
        let requested = minimumMethod ?? Self.absoluteMinimum
        let effectiveMinimum = max(requested, Self.absoluteMinimum)
        return evaluate(effectiveMinimum)
    }

    public func requestOsToggle(enable: Bool) async throws -> AuthChallengeResult {
        // OS toggle always demands the absolute minimum.
        return evaluate(Self.absoluteMinimum)
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private func evaluate(_ effectiveMinimum: AuthMethod) -> AuthChallengeResult {
        guard let strongest = available.max() else {
            return .failure(effectiveMinimum,
                            reason: "No authentication method is available on this device.",
                            at: clock())
        }
        if strongest >= effectiveMinimum {
            return .success(strongest, at: clock())
        }
        return .failure(strongest,
                        reason: "Available method (\(strongest)) is weaker than the required minimum (\(effectiveMinimum)).",
                        at: clock())
    }
}
