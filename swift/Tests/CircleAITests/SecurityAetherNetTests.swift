// SecurityAetherNetTests.swift
//
// Validates the CircleAI.Security.AetherNet port (SecurityAetherNet.swift):
//   • MeshDirectiveStore   — block on Avoid/Quarantine, Release lifts, lazy expiry
//   • MeshSecurityGate     — decide + enforce (+ MeshSecurityBlockedError)
//   • MeshGatedCompanionSession — gates send/stream/agent, passes rest through
//   • AetherMapper         — Aether ↔ Peer enum translation
//   • AetherIntelligenceAdapter — IAetherIntelligence over PeerIntelligenceService
//   • AetherSecurityBridge — telemetry → security layer → directive round-trip

import XCTest
import Foundation
@testable import CircleAI

final class SecurityAetherNetTests: XCTestCase {

    // Mutable clock for deterministic expiry tests.
    private final class MutableClock: @unchecked Sendable {
        private let lock = NSLock()
        private var now: Date
        init(_ start: Date) { self.now = start }
        func advance(_ seconds: TimeInterval) { lock.lock(); now.addTimeInterval(seconds); lock.unlock() }
        func read() -> Date { lock.lock(); defer { lock.unlock() }; return now }
    }

    private func directive(
        _ kind: SecurityDirectiveKind, node: String, reason: String = "r",
        duration: TimeInterval? = nil, at: Date
    ) -> SecurityDirective {
        SecurityDirective(kind: kind, targetNodeId: node, trustScoreOverride: nil,
                          threatLevel: .high, reason: reason, duration: duration, issuedAt: at)
    }

    // ── MeshDirectiveStore ────────────────────────────────────────────────────

    func testStoreBlocksOnAvoidAndReportsReason() {
        let store = MeshDirectiveStore()
        store.onDirective(directive(.avoidNode, node: "n", reason: "sketchy", at: Date()))
        let r = store.isBlocked("n")
        XCTAssertTrue(r.blocked)
        XCTAssertEqual(r.reason, "sketchy")
    }

    func testStoreBlocksOnQuarantine() {
        let store = MeshDirectiveStore()
        store.onDirective(directive(.quarantineNode, node: "n", reason: "compromised", at: Date()))
        XCTAssertTrue(store.isBlocked("n").blocked)
    }

    func testStoreDoesNotBlockOnNonBlockKinds() {
        let store = MeshDirectiveStore()
        store.onDirective(directive(.elevateMonitoring, node: "n", at: Date()))
        store.onDirective(directive(.updateNodeTrust, node: "n", at: Date()))
        store.onDirective(directive(.requestReauth, node: "n", at: Date()))
        XCTAssertFalse(store.isBlocked("n").blocked)
        // But they are tracked and returned by GetActiveDirectives.
        XCTAssertEqual(store.getActiveDirectives("n").count, 3)
    }

    func testReleaseLiftsAllDirectivesForNode() {
        let store = MeshDirectiveStore()
        store.onDirective(directive(.avoidNode, node: "n", at: Date()))
        store.onDirective(directive(.quarantineNode, node: "n", at: Date()))
        XCTAssertTrue(store.isBlocked("n").blocked)
        store.onDirective(directive(.releaseNode, node: "n", at: Date()))
        XCTAssertFalse(store.isBlocked("n").blocked)
        XCTAssertEqual(store.trackedNodeCount, 0)
    }

    func testLatestBlockReasonWins() {
        let store = MeshDirectiveStore()
        let t0 = Date(timeIntervalSince1970: 1000)
        store.onDirective(directive(.avoidNode, node: "n", reason: "first", at: t0))
        store.onDirective(directive(.quarantineNode, node: "n", reason: "latest",
                                    at: t0.addingTimeInterval(10)))
        XCTAssertEqual(store.isBlocked("n").reason, "latest")
    }

    func testExpiryIsLazyOnRead() {
        let clock = MutableClock(Date(timeIntervalSince1970: 5000))
        let store = MeshDirectiveStore(clock: { clock.read() })
        store.onDirective(directive(.avoidNode, node: "n", duration: 60, at: clock.read()))
        XCTAssertTrue(store.isBlocked("n").blocked)
        clock.advance(61) // directive now expired
        XCTAssertFalse(store.isBlocked("n").blocked)
        XCTAssertEqual(store.trackedNodeCount, 0) // swept on read
    }

    func testGetActiveDirectivesFiltersExpiredWithoutMutating() {
        let clock = MutableClock(Date(timeIntervalSince1970: 5000))
        let store = MeshDirectiveStore(clock: { clock.read() })
        store.onDirective(directive(.avoidNode, node: "n", duration: 60, at: clock.read()))
        clock.advance(61)
        XCTAssertTrue(store.getActiveDirectives("n").isEmpty)
    }

    func testBlankNodeIdNeverBlocks() {
        let store = MeshDirectiveStore()
        XCTAssertFalse(store.isBlocked("   ").blocked)
        XCTAssertTrue(store.getActiveDirectives("").isEmpty)
    }

    func testDirectiveWithoutTargetIsIgnored() {
        let store = MeshDirectiveStore()
        store.onDirective(SecurityDirective(kind: .avoidNode, targetNodeId: nil,
                                            trustScoreOverride: nil, threatLevel: .high,
                                            reason: "r", duration: nil, issuedAt: Date()))
        XCTAssertEqual(store.trackedNodeCount, 0)
    }

    // ── MeshSecurityGate ──────────────────────────────────────────────────────

    func testGateDecideAllowsUnknown() {
        let gate = MeshSecurityGate(store: MeshDirectiveStore())
        XCTAssertFalse(gate.decide("nobody").isBlocked)
        XCTAssertEqual(MeshSecurityGate.GateDecision.allowed.reason, "")
    }

    func testGateEnforceThrowsWhenBlocked() {
        let store = MeshDirectiveStore()
        store.onDirective(directive(.quarantineNode, node: "bad", reason: "abuse", at: Date()))
        let gate = MeshSecurityGate(store: store)
        XCTAssertThrowsError(try gate.enforce("bad")) { err in
            let blocked = err as? MeshSecurityBlockedError
            XCTAssertEqual(blocked?.blockedId, "bad")
            XCTAssertEqual(blocked?.reason, "abuse")
        }
    }

    func testGateEnforceDoesNotThrowWhenAllowed() {
        let gate = MeshSecurityGate(store: MeshDirectiveStore())
        XCTAssertNoThrow(try gate.enforce("ok"))
    }

    // ── MeshGatedCompanionSession ─────────────────────────────────────────────

    func testGatedSessionBlocksSendAndAgentButPassesContext() async throws {
        let store = MeshDirectiveStore()
        let gate = MeshSecurityGate(store: store)
        let inner = FakeCompanionSession(identityId: "user-1")
        let gated = MeshGatedCompanionSession(inner: inner, gate: gate)

        // Not blocked yet → passes through.
        let ok = try await gated.send("hi")
        XCTAssertEqual(ok, "echo:hi")

        // Block the identity.
        store.onDirective(directive(.quarantineNode, node: "user-1", reason: "blocked", at: Date()))

        await XCTAssertThrowsErrorAsync(try await gated.send("hi again")) { err in
            XCTAssertTrue(err is MeshSecurityBlockedError)
        }
        await XCTAssertThrowsErrorAsync(try await gated.agent("do it")) { err in
            XCTAssertTrue(err is MeshSecurityBlockedError)
        }

        // Context / history / feedback still pass through (unguarded).
        XCTAssertEqual(gated.getContext().identityId, "user-1")
        // Feedback is unguarded and must not throw (test is `throws` → a throw fails it).
        try await gated.signalFeedback(positive: true, note: nil)
        XCTAssertEqual(gated.sessionId, inner.sessionId)
        XCTAssertEqual(gated.identityId, "user-1")
        XCTAssertEqual(gated.interface, .headless)
    }

    func testGatedSessionStreamYieldsNothingWhenBlocked() async {
        let store = MeshDirectiveStore()
        store.onDirective(directive(.avoidNode, node: "user-2", at: Date()))
        let gate = MeshSecurityGate(store: store)
        let inner = FakeCompanionSession(identityId: "user-2")
        let gated = MeshGatedCompanionSession(inner: inner, gate: gate)

        var chunks: [String] = []
        for await c in gated.stream("hello") { chunks.append(c) }
        XCTAssertTrue(chunks.isEmpty) // blocked → no streamed content
    }

    func testGatedSessionStreamPassesThroughWhenAllowed() async {
        let gate = MeshSecurityGate(store: MeshDirectiveStore())
        let inner = FakeCompanionSession(identityId: "user-3")
        let gated = MeshGatedCompanionSession(inner: inner, gate: gate)

        var chunks: [String] = []
        for await c in gated.stream("hello") { chunks.append(c) }
        XCTAssertEqual(chunks, ["ec", "ho", ":hello"])
    }

    // ── AetherMapper ──────────────────────────────────────────────────────────

    func testMapperEventKind() {
        XCTAssertEqual(AetherMapper.toPeerEventKind(.nodeAuthAttempt), .authAttempt)
        XCTAssertEqual(AetherMapper.toPeerEventKind(.routingAnomaly), .routingAnomaly)
        XCTAssertEqual(AetherMapper.toPeerEventKind(.nodeBehaviourChange), .behaviourChange)
        XCTAssertEqual(AetherMapper.toPeerEventKind(.encryptionEvent), .encryptionEvent)
        XCTAssertEqual(AetherMapper.toPeerEventKind(.intrusionSignal), .intrusionSignal)
        XCTAssertEqual(AetherMapper.toPeerEventKind(.privilegeAttempt), .privilegeAttempt)
    }

    func testMapperThreatLevelRoundTrip() {
        for lvl in AetherThreatLevel.allCases {
            let peer = AetherMapper.toPeerThreatLevel(lvl)
            XCTAssertEqual(AetherMapper.toAetherThreatLevel(peer), lvl)
        }
    }

    func testMapperDirectiveKind() {
        XCTAssertEqual(AetherMapper.toSecurityDirectiveKind(.elevateMonitoring), .elevateMonitoring)
        XCTAssertEqual(AetherMapper.toSecurityDirectiveKind(.avoidNode), .avoidNode)
        XCTAssertEqual(AetherMapper.toSecurityDirectiveKind(.quarantineNode), .quarantineNode)
        XCTAssertEqual(AetherMapper.toSecurityDirectiveKind(.releaseNode), .releaseNode)
    }

    // ── AetherIntelligenceAdapter ─────────────────────────────────────────────

    private func makeIntelligence() -> (PeerIntelligenceService, NodeTrustRegistry, SecurityOptions) {
        let opts = SecurityOptions()
        let reg = NodeTrustRegistry(options: opts)
        let intel = PeerIntelligenceService(registry: reg, options: opts)
        return (intel, reg, opts)
    }

    func testIntelligenceAdapterMapsNetworkHealth() async throws {
        let (intel, reg, _) = makeIntelligence()
        _ = reg.getOrCreate("a") // one fully-trusted peer
        let adapter = AetherIntelligenceAdapter(inner: intel)
        let report = try await adapter.getNetworkHealth()
        XCTAssertEqual(report.overallScore, 1.0, accuracy: 1e-9)
        XCTAssertEqual(report.trustedNodeCount, 1)
        XCTAssertTrue(report.isValid)
    }

    func testIntelligenceAdapterMapsThreatAssessment() async throws {
        let (intel, reg, _) = makeIntelligence()
        // Drive peer to quarantine band.
        _ = reg.applyDegradation(
            PeerSecurityEvent(nodeId: "bad", kind: .intrusionSignal, threatLevel: .critical,
                              description: "probe", transportId: "aether", occurredAt: Date()),
            degradationAmount: 0.85) // score 0.15 → critical
        let adapter = AetherIntelligenceAdapter(inner: intel)
        let a = try await adapter.assessThreat(nodeId: "bad")
        XCTAssertEqual(a.nodeId, "bad")
        XCTAssertEqual(a.level, .critical)
        XCTAssertGreaterThan(a.threatConfidence, 0.0)
    }

    func testIntelligenceAdapterMapsRoutingAdvice() async throws {
        let (intel, reg, _) = makeIntelligence()
        _ = reg.getOrCreate("dest") // trusted
        let adapter = AetherIntelligenceAdapter(inner: intel)
        let advice = try await adapter.getRoutingAdvice(destinationNodeId: "dest")
        XCTAssertEqual(advice.destinationNodeId, "dest")
        XCTAssertEqual(advice.recommendedPath, ["dest"]) // trusted → direct
    }

    func testIntelligenceAdapterStreamsMappedTrustUpdates() async throws {
        let (intel, reg, _) = makeIntelligence()
        let adapter = AetherIntelligenceAdapter(inner: intel)
        // Subscribe synchronously, THEN publish, so the buffered update is seen.
        let stream = adapter.streamTrustScores()
        _ = reg.applyDegradation(
            PeerSecurityEvent(nodeId: "n", kind: .authAttempt, threatLevel: .high,
                              description: "auth-fail", transportId: "aether", occurredAt: Date()),
            degradationAmount: 0.1)

        var received: TrustScoreUpdate?
        for await u in stream { received = u; break }
        XCTAssertEqual(received?.nodeId, "n")
        XCTAssertEqual(received?.reason, "auth-fail")
        XCTAssertEqual(received?.previousScore ?? 0, 1.0, accuracy: 1e-9)
        XCTAssertEqual(received?.currentScore ?? 0, 0.9, accuracy: 1e-9)
    }

    // ── AetherSecurityBridge (end-to-end) ─────────────────────────────────────

    func testBridgeTranslatesTelemetryIntoDirectives() async throws {
        // Wire: InMemory telemetry → bridge → SecurityLayerService → consumer.
        let opts = SecurityOptions()
        let reg = NodeTrustRegistry(options: opts)
        let pub = DirectivePublisher()
        let layer = SecurityLayerService(registry: reg, options: opts, publisher: pub)
        let bridge = AetherSecurityBridge(layer: layer)

        let consumer = CapturingAetherConsumer()
        let dirSub = bridge.subscribeToDirectives(consumer)

        let hub = InMemoryAetherTelemetry()
        try await bridge.start(telemetry: hub)

        // Publish enough critical intrusion events to cross the quarantine floor.
        // intrusion critical degradation = 0.15 × 3 = 0.45 per event.
        func fire() {
            hub.publishSecurityEvent(AetherSecurityEvent(
                nodeId: "attacker", kind: .intrusionSignal, threatLevel: .critical,
                description: "intrusion", metadata: [:], occurredAt: Date()))
        }
        fire() // 1.0 → 0.55 (elevate)
        fire() // 0.55 → 0.10 (quarantine)

        let issued = consumer.snapshot()
        XCTAssertTrue(issued.contains { $0.kind == .quarantineNode && $0.targetNodeId == "attacker" })
        // Reason text and threat level flow through the translation.
        let quarantine = issued.first { $0.kind == .quarantineNode }
        XCTAssertEqual(quarantine?.reason, "intrusion")
        XCTAssertEqual(quarantine?.threatLevel, .critical)

        dirSub.dispose()
        try await bridge.stop()
    }

    func testBridgePostureMapsFromLayer() async throws {
        let opts = SecurityOptions()
        let reg = NodeTrustRegistry(options: opts)
        let pub = DirectivePublisher()
        let layer = SecurityLayerService(registry: reg, options: opts, publisher: pub)
        let bridge = AetherSecurityBridge(layer: layer)

        // Drive one peer into quarantine directly on the registry.
        _ = reg.applyDegradation(
            PeerSecurityEvent(nodeId: "q", kind: .authAttempt, threatLevel: .medium,
                              description: "x", transportId: "aether", occurredAt: Date()),
            degradationAmount: 0.85) // 0.15

        let hub = InMemoryAetherTelemetry()
        try await bridge.start(telemetry: hub)
        let posture = try await bridge.getPosture()
        XCTAssertEqual(posture.quarantinedNodeCount, 1)
        XCTAssertEqual(posture.overallThreatLevel, .critical)
        XCTAssertTrue(posture.isActive)
        try await bridge.stop()
    }

    func testBridgeStopUnsubscribesFromTelemetry() async throws {
        let opts = SecurityOptions()
        let reg = NodeTrustRegistry(options: opts)
        let layer = SecurityLayerService(registry: reg, options: opts, publisher: DirectivePublisher())
        let bridge = AetherSecurityBridge(layer: layer)
        let hub = InMemoryAetherTelemetry()

        try await bridge.start(telemetry: hub)
        XCTAssertEqual(hub.subscriberCount, 1)
        try await bridge.stop()
        XCTAssertEqual(hub.subscriberCount, 0)

        // After stop, further events must not degrade trust.
        hub.publishSecurityEvent(AetherSecurityEvent(
            nodeId: "late", kind: .intrusionSignal, threatLevel: .critical,
            description: "d", metadata: [:], occurredAt: Date()))
        XCTAssertEqual(reg.getTrustScore("late"), 1.0, accuracy: 1e-9)
    }

    func testBridgeHandlesNodeExitWithoutCrash() async throws {
        let opts = SecurityOptions()
        let reg = NodeTrustRegistry(options: opts)
        let layer = SecurityLayerService(registry: reg, options: opts, publisher: DirectivePublisher())
        let bridge = AetherSecurityBridge(layer: layer)
        let hub = InMemoryAetherTelemetry()
        try await bridge.start(telemetry: hub)
        hub.publishNodeEvent(AetherNodeEvent(
            nodeId: "gone", kind: .left,
            health: AetherNodeHealth(trustScore: 1, isReachable: false, latency: 0, hopCount: 0),
            occurredAt: Date()))
        // handlePeerLeft is a no-op that must not throw.
        try await bridge.stop()
    }
}

// ── Test doubles ──────────────────────────────────────────────────────────────

/// Minimal `ICompanionSession` for gate tests. `send`/`agent` echo; `stream`
/// yields three fixed chunks that concatenate to the echo.
private final class FakeCompanionSession: ICompanionSession, @unchecked Sendable {
    let sessionId: String
    let identityId: String
    let interface: InterfaceKind = .headless
    private(set) var history: [CompanionTurn] = []

    init(identityId: String) {
        self.sessionId = "sess-\(identityId)"
        self.identityId = identityId
    }

    func send(_ message: String) async throws -> String { "echo:\(message)" }

    func stream(_ message: String) -> AsyncStream<String> {
        AsyncStream { cont in
            cont.yield("ec")
            cont.yield("ho")
            cont.yield(":\(message)")
            cont.finish()
        }
    }

    func agent(_ instruction: String) async throws -> String { "echo:\(instruction)" }

    func getContext() -> CompanionContext {
        CompanionContext(identityId: identityId, displayName: "Test", interface: interface,
                         personaHints: "", affectSummary: "", recentMemorySnippets: [], activeGoals: [])
    }

    func refreshContext() async throws {}

    func signalFeedback(positive: Bool, note: String?) async throws {}

    var proactiveEvents: AsyncStream<CompanionProactiveEvent> { AsyncStream { $0.finish() } }
}

/// Captures Aether directives delivered through the bridge's directive adapter.
private final class CapturingAetherConsumer: ISecurityDirectiveConsumer, @unchecked Sendable {
    private let lock = NSLock()
    private var directives: [SecurityDirective] = []
    func onDirective(_ directive: SecurityDirective) {
        lock.lock(); directives.append(directive); lock.unlock()
    }
    func snapshot() -> [SecurityDirective] { lock.lock(); defer { lock.unlock() }; return directives }
}

// ── async throwing assertion helper ───────────────────────────────────────────

/// XCTest has no built-in async `XCTAssertThrowsError`; this awaits the
/// autoclosure and asserts it threw, routing the error to `handler`.
func XCTAssertThrowsErrorAsync<T>(
    _ expression: @autoclosure () async throws -> T,
    _ message: @autoclosure () -> String = "",
    file: StaticString = #filePath,
    line: UInt = #line,
    _ handler: (Error) -> Void = { _ in }
) async {
    do {
        _ = try await expression()
        XCTFail(message().isEmpty ? "Expected an error to be thrown" : message(), file: file, line: line)
    } catch {
        handler(error)
    }
}
