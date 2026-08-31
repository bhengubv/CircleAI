import XCTest
@testable import CircleAI

/// Where findings go, and the loop that sends them there.
final class SecurityDefenseSinkTests: XCTestCase {

    private enum TestError: Error { case boom }

    private actor Box {
        var count = 0
        func hit() { count += 1 }
    }

    /// The loop is asynchronous, so a stop issued immediately after a start can
    /// arrive before the feed has been read at all. Wait for the work rather
    /// than assuming it happened.
    private func waitFor(_ box: Box, count target: Int, timeout: TimeInterval = 2.0) async -> Int {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            let n = await box.count
            if n >= target { return n }
            try? await Task.sleep(nanoseconds: 2_000_000)
        }
        return await box.count
    }

    private struct ScriptedFeed: INetworkObservationFeed {
        let observations: [NetworkObservation]
        var sourceId: String { "scripted" }
        func observe() -> AsyncStream<NetworkObservation> {
            AsyncStream { continuation in
                for o in observations { continuation.yield(o) }
                continuation.finish()
            }
        }
    }

    private func signal(_ severity: ThreatSeverity) -> ThreatSignal {
        .create(category: .portScan, severity: severity, confidence: 0.5,
                indicator: "x", description: "d", direction: .outbound)
    }

    // MARK: - Floors

    func testTheSosSinkIgnoresAnythingBelowItsFloor() async throws {
        let box = Box()
        let sink = SosThreatSink(sos: DelegateSosEscalation { _ in await box.hit() })

        try await sink.handle(signal(.medium))
        let quiet = await box.count
        XCTAssertEqual(quiet, 0)

        try await sink.handle(signal(.critical))
        let loud = await box.count
        XCTAssertEqual(loud, 1)
    }

    func testTheSosFloorIsConfigurable() async throws {
        let box = Box()
        let options = DefenseOptions()
        options.sosSeverityFloor = .low
        let sink = SosThreatSink(sos: DelegateSosEscalation { _ in await box.hit() }, options: options)

        try await sink.handle(signal(.medium))
        let hits = await box.count
        XCTAssertEqual(hits, 1)
    }

    // A logging sink that throws must not be able to suppress an SOS behind it.
    func testAFailingSinkDoesNotStopTheOnesAfterIt() async throws {
        let box = Box()
        let composite = CompositeThreatSink(
            DelegateThreatSink { _ in throw TestError.boom },
            DelegateThreatSink { _ in await box.hit() })

        try await composite.handle(signal(.high))
        let hits = await box.count
        XCTAssertEqual(hits, 1)
    }

    func testEverySinkInACompositeIsCalled() async throws {
        let box = Box()
        let composite = CompositeThreatSink([
            DelegateThreatSink { _ in await box.hit() },
            DelegateThreatSink { _ in await box.hit() },
            DelegateThreatSink { _ in await box.hit() },
        ])
        try await composite.handle(signal(.high))
        let hits = await box.count
        XCTAssertEqual(hits, 3)
    }

    func testTheNullSinkSwallowsEverything() async throws {
        try await NullThreatSink.instance.handle(signal(.critical))
        try await NullSosEscalation.instance.escalate(signal(.critical))
    }

    // MARK: - The loop

    func testTheSentinelEvaluatesTheFeedAndForwardsFindings() async throws {
        let source = BlocklistIndicatorSource()
        try source.refresh(from: "evil.example.com", replace: true)
        let box = Box()

        let sentinel = AlwaysOnDefenseSentinel(
            monitor: BlocklistThreatMonitor(indicators: source),
            feed: ScriptedFeed(observations: [
                .dns(host: "www.example.org"),      // clean
                .dns(host: "evil.example.com"),     // flagged
            ]),
            sink: DelegateThreatSink { _ in await box.hit() })

        await sentinel.start()
        XCTAssertTrue(sentinel.isActive)

        let hits = await waitFor(box, count: 1)
        XCTAssertEqual(hits, 1)

        await sentinel.stop()
        XCTAssertFalse(sentinel.isActive)
    }

    // A sink that throws must not take the loop down with it.
    func testAThrowingSinkDoesNotEndTheLoop() async throws {
        let source = BlocklistIndicatorSource()
        try source.refresh(from: "evil.example.com", replace: true)

        let sentinel = AlwaysOnDefenseSentinel(
            monitor: BlocklistThreatMonitor(indicators: source),
            feed: ScriptedFeed(observations: [.dns(host: "evil.example.com")]),
            sink: DelegateThreatSink { _ in throw TestError.boom })

        await sentinel.start()
        await sentinel.stop()
        XCTAssertFalse(sentinel.isActive)
    }

    func testStartingTwiceIsHarmlessAndStoppingWhenIdleIsToo() async {
        let sentinel = AlwaysOnDefenseSentinel(
            monitor: BlocklistThreatMonitor(indicators: BlocklistIndicatorSource()),
            feed: ScriptedFeed(observations: []))
        await sentinel.stop()
        await sentinel.start()
        await sentinel.start()
        XCTAssertTrue(sentinel.isActive)
        await sentinel.stop()
        XCTAssertFalse(sentinel.isActive)
    }

    func testTheModuleWiresItselfFromAListOfIndicators() throws {
        let module = try DefenseModule.create(
            feed: ScriptedFeed(observations: []),
            blocklist: "evil.example.com\n203.0.113.0/24")
        XCTAssertEqual(module.indicators.indicatorCount, 2)
        XCTAssertNotNil(module.monitor.evaluate(.dns(host: "evil.example.com")))
    }
}
