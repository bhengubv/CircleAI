// AetherNetAdaptersTests.swift

import XCTest
@testable import CircleAI

private final class RecordingMeshConsumer: IMeshDirectiveConsumer, @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [SecurityDirective] = []
    func onMeshDirective(_ directive: SecurityDirective) {
        lock.lock(); stored.append(directive); lock.unlock()
    }
    var received: [SecurityDirective] { lock.lock(); defer { lock.unlock() }; return stored }
}

private final class RecordingCircleConsumer: ISecurityDirectiveConsumer, @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [SecurityDirective] = []
    func onDirective(_ directive: SecurityDirective) {
        lock.lock(); stored.append(directive); lock.unlock()
    }
    var received: [SecurityDirective] { lock.lock(); defer { lock.unlock() }; return stored }
}

private final class CountingSubscription: IAetherSubscription, @unchecked Sendable {
    private let lock = NSLock()
    private var disposals = 0
    func dispose() { lock.lock(); disposals += 1; lock.unlock() }
    var count: Int { lock.lock(); defer { lock.unlock() }; return disposals }
}

private final class FakeMeshTelemetry: IMeshTelemetryPublisher, @unchecked Sendable {
    let handles = HandleBox()
    func subscribe(_ observer: IAetherTelemetryObserver) -> IAetherSubscription {
        let h = CountingSubscription()
        handles.add(h)
        return h
    }
}

private final class HandleBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [CountingSubscription] = []
    func add(_ h: CountingSubscription) { lock.lock(); stored.append(h); lock.unlock() }
    var all: [CountingSubscription] { lock.lock(); defer { lock.unlock() }; return stored }
}

private final class SilentObserver: IAetherTelemetryObserver {
    func onNodeEvent(_ e: AetherNodeEvent) {}
    func onTransportEvent(_ e: AetherTransportEvent) {}
    func onRouteEvent(_ e: AetherRouteEvent) {}
    func onSecurityEvent(_ e: AetherSecurityEvent) {}
    func onNetworkEvent(_ e: AetherNetworkEvent) {}
}

final class AetherNetAdaptersTests: XCTestCase {

    private func directive(_ reason: String = "hostile") -> SecurityDirective {
        SecurityDirective(kind: .quarantineNode, targetNodeId: "peer-1",
                          trustScoreOverride: 0.1, threatLevel: .high,
                          reason: reason, duration: 600,
                          issuedAt: Date(timeIntervalSince1970: 1_700_000_000))
    }

    // MARK: - Context

    func testTheInstallLevelIsAppNotOs() {
        // Reporting .os makes requiresAuth true and sends a caller looking for a
        // permission prompt that will never appear.
        let c = AetherNetContextAdapter(protocolVersion: 4)
        XCTAssertEqual(c.installLevel, .app)
        XCTAssertFalse(c.requiresAuth)
        XCTAssertTrue(c.isAvailable)
    }

    func testTheProtocolVersionIsTheMajorVersion() {
        // A mesh speaking protocol 4 and one speaking 5 are not the same
        // runtime, and a caller comparing versions has to see it in the number.
        XCTAssertEqual(AetherNetContextAdapter(protocolVersion: 4).runtimeVersion?.major, 4)
        XCTAssertEqual(AetherNetContextAdapter(protocolVersion: 5).runtimeVersion?.major, 5)
    }

    func testNoMinimumMeansAlwaysSufficient() {
        XCTAssertTrue(AetherNetContextAdapter(protocolVersion: 1).isSufficient)
    }

    func testAnOlderRuntimeThanRequiredIsNotSufficient() {
        let old = AetherNetContextAdapter(protocolVersion: 3,
                                          minimumRequired: SemanticVersion(major: 4))
        XCTAssertFalse(old.isSufficient)

        let same = AetherNetContextAdapter(protocolVersion: 4,
                                           minimumRequired: SemanticVersion(major: 4))
        XCTAssertTrue(same.isSufficient, "equal satisfies the minimum")

        let newer = AetherNetContextAdapter(protocolVersion: 5,
                                            minimumRequired: SemanticVersion(major: 4))
        XCTAssertTrue(newer.isSufficient)
    }

    func testDisabledIsReportedWithoutPretendingItIsAbsent() {
        // Present-but-off and not-installed are different states with different
        // fixes: one is a settings toggle, the other is an install.
        let c = AetherNetContextAdapter(protocolVersion: 4, isEnabled: false)
        XCTAssertFalse(c.isEnabled)
        XCTAssertTrue(c.isAvailable)
        XCTAssertEqual(c.installLevel, .app)
    }

    // MARK: - Directives both ways

    func testAnOutboundDirectiveReachesTheMesh() {
        let mesh = RecordingMeshConsumer()
        AetherNetDirectiveSink(mesh: mesh).onDirective(directive())
        XCTAssertEqual(mesh.received.count, 1)
        XCTAssertEqual(mesh.received[0].reason, "hostile")
    }

    func testAnInboundDirectiveReachesCircleAi() {
        // Without this direction a device issues security decisions and receives
        // none, so a node the rest of the mesh has agreed is hostile stays
        // trusted here.
        let circle = RecordingCircleConsumer()
        AetherNetInboundDirectiveBridge(circle: circle).onMeshDirective(directive("peer says so"))
        XCTAssertEqual(circle.received.count, 1)
        XCTAssertEqual(circle.received[0].reason, "peer says so")
    }

    func testADirectiveSurvivesTheRoundTripUnchanged() {
        // The pair only works if what goes out is what comes back. A field lost
        // in one direction is a directive that means something different by the
        // time it returns.
        let circle = RecordingCircleConsumer()
        let inbound = AetherNetInboundDirectiveBridge(circle: circle)
        let outbound = AetherNetDirectiveSink(mesh: inbound)

        let original = directive()
        outbound.onDirective(original)

        XCTAssertEqual(circle.received.count, 1)
        XCTAssertEqual(circle.received[0], original)
    }

    func testEveryDirectiveKindMakesTheRoundTrip() {
        let circle = RecordingCircleConsumer()
        let outbound = AetherNetDirectiveSink(
            mesh: AetherNetInboundDirectiveBridge(circle: circle))

        for kind in SecurityDirectiveKind.allCases {
            outbound.onDirective(SecurityDirective(
                kind: kind, targetNodeId: "n", trustScoreOverride: nil,
                threatLevel: .medium, reason: "\(kind)", duration: nil,
                issuedAt: Date(timeIntervalSince1970: 0)))
        }
        XCTAssertEqual(circle.received.map(\.kind), SecurityDirectiveKind.allCases)
    }

    // MARK: - Telemetry

    func testEachSubscriberGetsItsOwnHandle() {
        // A shared handle is how one component shutting down takes the security
        // layer's feed with it, and the symptom is a mesh that silently stops
        // being watched.
        let mesh = FakeMeshTelemetry()
        let adapter = AetherNetTelemetryAdapter(mesh: mesh)

        let a = adapter.subscribe(SilentObserver())
        let b = adapter.subscribe(SilentObserver())
        XCTAssertEqual(mesh.handles.all.count, 2)

        a.dispose()
        XCTAssertEqual(mesh.handles.all[0].count, 1)
        XCTAssertEqual(mesh.handles.all[1].count, 0, "the other subscriber is untouched")
        _ = b
    }

    // MARK: - Companion state

    func testADeviceNeverReceivesItsOwnBroadcast() {
        // Without this a two-device pairing echoes forever, and each device
        // treats its own message as news from the other one.
        let channel = AetherNetCompanionStateChannel(deviceId: "me", send: { _ in })
        var delivered = 0
        channel.observe { _ in delivered += 1 }

        XCTAssertFalse(channel.receive(CompanionStateMessage(
            deviceId: "me", payloadJson: "{}", at: Date(timeIntervalSince1970: 0))))
        XCTAssertEqual(delivered, 0)
    }

    func testAMessageFromAnotherDeviceIsDelivered() {
        let channel = AetherNetCompanionStateChannel(deviceId: "me", send: { _ in })
        var got: [String] = []
        channel.observe { got.append($0.payloadJson) }

        XCTAssertTrue(channel.receive(CompanionStateMessage(
            deviceId: "other", payloadJson: "{\"mood\":1}",
            at: Date(timeIntervalSince1970: 0))))
        XCTAssertEqual(got, ["{\"mood\":1}"])
    }

    func testTheSameMessageArrivingTwiceIsDeliveredOnce() {
        // A mesh FLOODS, so the same message legitimately arrives by more than
        // one route. Delivering it twice applies the same state change twice.
        let channel = AetherNetCompanionStateChannel(deviceId: "me", send: { _ in })
        var delivered = 0
        channel.observe { _ in delivered += 1 }

        let m = CompanionStateMessage(deviceId: "other", payloadJson: "{}",
                                      at: Date(timeIntervalSince1970: 5))
        XCTAssertTrue(channel.receive(m))
        XCTAssertFalse(channel.receive(m))
        XCTAssertEqual(delivered, 1)
    }

    func testTwoDifferentMessagesFromTheSameDeviceBothArrive() {
        let channel = AetherNetCompanionStateChannel(deviceId: "me", send: { _ in })
        var delivered = 0
        channel.observe { _ in delivered += 1 }

        XCTAssertTrue(channel.receive(CompanionStateMessage(
            deviceId: "other", payloadJson: "{\"a\":1}", at: Date(timeIntervalSince1970: 1))))
        XCTAssertTrue(channel.receive(CompanionStateMessage(
            deviceId: "other", payloadJson: "{\"a\":2}", at: Date(timeIntervalSince1970: 1))))
        XCTAssertEqual(delivered, 2)
    }

    func testPublishCarriesThisDevicesId() {
        let sent = MessageBox()
        let channel = AetherNetCompanionStateChannel(deviceId: "me", send: { sent.add($0) })
        channel.publish(payloadJson: "{\"x\":1}", at: Date(timeIntervalSince1970: 3))

        XCTAssertEqual(sent.all.count, 1)
        XCTAssertEqual(sent.all[0].deviceId, "me")
        XCTAssertEqual(sent.all[0].payloadJson, "{\"x\":1}")
    }

    func testAnObserverCanBeRemoved() {
        let channel = AetherNetCompanionStateChannel(deviceId: "me", send: { _ in })
        var delivered = 0
        let token = channel.observe { _ in delivered += 1 }
        channel.stopObserving(token)

        // Still ACCEPTED - it was new and not our own echo. What it is not is
        // delivered, because the observer is gone.
        XCTAssertTrue(channel.receive(CompanionStateMessage(
            deviceId: "other", payloadJson: "{}", at: Date(timeIntervalSince1970: 0))))
        XCTAssertEqual(delivered, 0)
    }

    func testForgettingSeenMessagesBoundsTheSet() {
        // A long-lived device must not grow this without limit.
        let channel = AetherNetCompanionStateChannel(deviceId: "me", send: { _ in })
        channel.observe { _ in }
        for i in 0..<50 {
            channel.receive(CompanionStateMessage(deviceId: "other", payloadJson: "{\"n\":\(i)}",
                                                  at: Date(timeIntervalSince1970: 0)))
        }
        XCTAssertEqual(channel.seenCount, 50)
        channel.forgetSeen()
        XCTAssertEqual(channel.seenCount, 0)
    }

    // MARK: - AI over the mesh

    func testAPeerAnswersWhenThisDeviceCannot() async throws {
        // The point of the whole arrangement: a cheap phone with no room for a
        // generalist gets an answer from one on the same mesh, without either
        // device reaching the internet.
        let p = CircleAiAetherNetAiProvider(peers: { ["big-phone"] },
                                            ask: { prompt, _ in "answer to \(prompt)" })
        XCTAssertTrue(p.hasPeer)
        let out = try await p.complete(prompt: "hello")
        XCTAssertEqual(out, "answer to hello")
    }

    func testNoPeerIsANamedFailureNotAHang() async {
        let p = CircleAiAetherNetAiProvider(peers: { [] }, ask: { _, _ in "x" })
        XCTAssertFalse(p.hasPeer)
        do {
            _ = try await p.complete(prompt: "hello")
            XCTFail("must fail")
        } catch {
            XCTAssertTrue("\(error)".contains("No peer"))
        }
    }

    func testPeersAreAskedInTurnAndTheFirstAnswerWins() async throws {
        // IN TURN, not in parallel: every attempt costs the peer's battery and
        // the radio's airtime, and asking four phones a question one will answer
        // wastes three of them.
        let asked = NameBox()
        let p = CircleAiAetherNetAiProvider(peers: { ["a", "b", "c"] }, ask: { _, peer in
            asked.add(peer)
            return peer == "a" ? "from a" : "should not be reached"
        })
        let out = try await p.complete(prompt: "q")
        XCTAssertEqual(out, "from a")
        XCTAssertEqual(asked.all, ["a"], "b and c were never disturbed")
    }

    func testAPeerThatWentOutOfRangeIsSkippedNotFatal() async throws {
        struct Gone: Error {}
        let p = CircleAiAetherNetAiProvider(peers: { ["gone", "here"] }, ask: { _, peer in
            if peer == "gone" { throw Gone() }
            return "from here"
        })
        let hoisted1 = try await p.complete(prompt: "q")
        XCTAssertEqual(hoisted1, "from here")
    }

    func testAPeerThatAnswersWithNothingIsTreatedAsAFailure() async throws {
        let p = CircleAiAetherNetAiProvider(peers: { ["empty", "real"] }, ask: { _, peer in
            peer == "empty" ? "   " : "a real answer"
        })
        let hoisted2 = try await p.complete(prompt: "q")
        XCTAssertEqual(hoisted2, "a real answer")
    }

    func testEveryPeerFailingReportsThatRatherThanReturningNothing() async {
        struct Gone: Error {}
        let p = CircleAiAetherNetAiProvider(peers: { ["a", "b"] }, ask: { _, _ in throw Gone() })
        do {
            _ = try await p.complete(prompt: "q")
            XCTFail("must fail")
        } catch {
            XCTAssertTrue("\(error)".contains("Every peer"))
        }
    }
}

private final class MessageBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [CompanionStateMessage] = []
    func add(_ m: CompanionStateMessage) { lock.lock(); stored.append(m); lock.unlock() }
    var all: [CompanionStateMessage] { lock.lock(); defer { lock.unlock() }; return stored }
}

private final class NameBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [String] = []
    func add(_ n: String) { lock.lock(); stored.append(n); lock.unlock() }
    var all: [String] { lock.lock(); defer { lock.unlock() }; return stored }
}
