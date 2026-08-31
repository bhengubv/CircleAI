import XCTest
@testable import CircleAI

/// Anomaly patterns, the sinks and the always-on loop.
final class SecurityDefenseSentinelTests: XCTestCase {

    private func ip(_ s: String) throws -> IPAddressValue { try XCTUnwrap(IPAddressValue(s)) }

    private func emptySource() -> BlocklistIndicatorSource { BlocklistIndicatorSource() }

    // MARK: - Scan and flood

    func testFanOutToManyDestinationsReadsAsAScan() throws {
        let options = DefenseOptions()
        options.distinctDestinationScanThreshold = 5
        let m = BlocklistThreatMonitor(indicators: emptySource(), options: options)

        var last: ThreatSignal?
        for i in 1...5 {
            last = m.evaluate(.outbound(address: try ip("203.0.113.\(i)"), port: 80))
        }
        XCTAssertEqual(last?.category, .portScan)
        XCTAssertTrue(last?.tags.contains("distinct-5") ?? false)
    }

    // Many connections to ONE destination is a flood, not a scan - the two
    // thresholds count different things and must not be confused.
    func testManyConnectionsToOneDestinationReadsAsAFlood() throws {
        let options = DefenseOptions()
        options.distinctDestinationScanThreshold = 100
        options.connectionFloodThreshold = 6
        let m = BlocklistThreatMonitor(indicators: emptySource(), options: options)

        var last: ThreatSignal?
        for _ in 1...6 {
            last = m.evaluate(.outbound(address: try ip("203.0.113.9"), port: 80))
        }
        XCTAssertEqual(last?.category, .connectionFlood)
        XCTAssertTrue(last?.tags.contains("count-6") ?? false)
    }

    func testTrafficThatFallsOutOfTheWindowStopsCounting() throws {
        let options = DefenseOptions()
        options.anomalyWindow = 10
        options.distinctDestinationScanThreshold = 3
        let d = ConnectionRateAnomalyDetector(options: options)
        let base = Date(timeIntervalSince1970: 1_000_000)

        // Two destinations an hour apart never coexist inside a 10s window.
        _ = d.observe(.outbound(address: try ip("203.0.113.1"), port: 80), now: base)
        _ = d.observe(.outbound(address: try ip("203.0.113.2"), port: 80), now: base.addingTimeInterval(3600))
        let third = d.observe(.outbound(address: try ip("203.0.113.3"), port: 80),
                              now: base.addingTimeInterval(7200))
        XCTAssertNil(third)
    }

    func testAnomalyDetectionCanBeTurnedOff() throws {
        let options = DefenseOptions()
        options.enableAnomalyDetection = false
        options.distinctDestinationScanThreshold = 2
        let m = BlocklistThreatMonitor(indicators: emptySource(), options: options)
        _ = m.evaluate(.outbound(address: try ip("203.0.113.1"), port: 80))
        XCTAssertNil(m.evaluate(.outbound(address: try ip("203.0.113.2"), port: 80)))
    }

    // Inbound traffic is not a fan-out even when it looks like one.
    func testOnlyOutboundTrafficIsScoredForFanOut() throws {
        let options = DefenseOptions()
        options.distinctDestinationScanThreshold = 2
        let m = BlocklistThreatMonitor(indicators: emptySource(), options: options)
        let inbound = { (s: String) in
            NetworkObservation(host: nil, remoteAddress: IPAddressValue(s), remotePort: 80,
                               direction: .inbound, proto: "tcp", appHint: nil, observedAt: Date())
        }
        _ = m.evaluate(inbound("203.0.113.1"))
        XCTAssertNil(m.evaluate(inbound("203.0.113.2")))
    }

    // MARK: - Confidence

    func testConfidenceIsClampedSoNoCallerCanPublishNonsense() {
        let over = ThreatSignal.create(category: .portScan, severity: .low, confidence: 1.4,
                                       indicator: "x", description: "d", direction: .outbound)
        let under = ThreatSignal.create(category: .portScan, severity: .low, confidence: -0.2,
                                        indicator: "x", description: "d", direction: .outbound)
        XCTAssertEqual(over.confidence, 1.0)
        XCTAssertEqual(under.confidence, 0.0)
    }

    func testNetworkCategoriesMapToTheNetworkPivotVector() {
        XCTAssertEqual(WatchdogThreatSink.mapVector(.commandAndControl), .networkPivot)
        XCTAssertEqual(WatchdogThreatSink.mapVector(.dataExfiltration), .networkPivot)
        XCTAssertEqual(WatchdogThreatSink.mapVector(.phishing), .networkPivot)
        XCTAssertEqual(WatchdogThreatSink.mapVector(.portScan), .unknown)
        XCTAssertEqual(WatchdogThreatSink.mapVector(.unclassified), .unknown)
    }
}
