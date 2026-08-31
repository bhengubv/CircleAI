import XCTest
@testable import CircleAI

/// The index, the monitor and the escalation floors.
final class SecurityDefenseMonitorTests: XCTestCase {

    private func source(_ list: String) throws -> BlocklistIndicatorSource {
        let s = BlocklistIndicatorSource()
        try s.refresh(from: list, replace: true)
        return s
    }

    private func ip(_ s: String) throws -> IPAddressValue {
        try XCTUnwrap(IPAddressValue(s))
    }

    // MARK: - The index

    func testAnExactAddressMatches() throws {
        let s = try source("203.0.113.5")
        let m = s.match(address: try ip("203.0.113.5"), host: nil)
        XCTAssertEqual(m?.kind, .ipv4)
        XCTAssertEqual(m?.reason, "known-bad-ip")
    }

    func testAnAddressInsideABlockedRangeMatchesTheRange() throws {
        let s = try source("203.0.113.0/24")
        let m = s.match(address: try ip("203.0.113.77"), host: nil)
        XCTAssertEqual(m?.kind, .ipv4Cidr)
        XCTAssertEqual(m?.indicator, "203.0.113.0/24")
    }

    func testAnAddressOutsideEverythingDoesNotMatch() throws {
        let s = try source("203.0.113.0/24")
        XCTAssertNil(s.match(address: try ip("8.8.8.8"), host: nil))
    }

    // Blocking "evil.com" has to block every subdomain, or blocklists are
    // trivially defeated by prefixing a random label.
    func testASubdomainMatchesItsBlockedParent() throws {
        let s = try source("evil.example.com")
        let m = s.match(address: nil, host: "cdn.assets.evil.example.com")
        XCTAssertEqual(m?.indicator, "evil.example.com")
        XCTAssertEqual(m?.reason, "known-bad-parent-domain")
    }

    // And the reverse must NOT hold: blocking the subdomain leaves the parent be.
    func testAParentIsNotBlockedByItsChild() throws {
        let s = try source("bad.example.com")
        XCTAssertNil(s.match(address: nil, host: "example.com"))
    }

    func testASimilarlyNamedSiblingIsNotAMatch() throws {
        let s = try source("evil.com")
        XCTAssertNil(s.match(address: nil, host: "notevil.com"))
    }

    func testHostMatchingIgnoresCaseAndTheRootDot() throws {
        let s = try source("evil.example.com")
        XCTAssertNotNil(s.match(address: nil, host: "EVIL.Example.COM."))
    }

    func testIPv6MatchesOnItsCanonicalForm() throws {
        let s = try source("2001:DB8::99")
        XCTAssertEqual(s.match(address: try ip("2001:db8::99"), host: nil)?.kind, .ipv6)
    }

    func testRefreshReplacesByDefaultAndAppendsWhenAsked() throws {
        let s = BlocklistIndicatorSource()
        try s.refresh(from: "a.example.com", replace: true)
        try s.refresh(from: "b.example.com", replace: true)
        XCTAssertNil(s.match(address: nil, host: "a.example.com"))

        try s.refresh(from: "c.example.com", replace: false)
        XCTAssertNotNil(s.match(address: nil, host: "b.example.com"))
        XCTAssertNotNil(s.match(address: nil, host: "c.example.com"))
    }

    func testTheCountReflectsWhatWasIndexed() throws {
        let s = try source("a.example.com\nb.example.com\n203.0.113.1\n203.0.113.0/24")
        XCTAssertEqual(s.indicatorCount, 4)
    }

    // MARK: - The monitor

    func testAKnownBadHostIsHighSeverity() throws {
        let m = BlocklistThreatMonitor(indicators: try source("evil.example.com"))
        let signal = m.evaluate(.dns(host: "evil.example.com"))
        XCTAssertEqual(signal?.severity, .high)
        XCTAssertEqual(signal?.category, .knownMalwareHost)
        XCTAssertEqual(signal?.confidence, 0.90)
    }

    func testAKnownBadAddressIsAMaliciousEndpointNotAMalwareHost() throws {
        let m = BlocklistThreatMonitor(indicators: try source("203.0.113.5"))
        let signal = m.evaluate(.outbound(address: try ip("203.0.113.5"), port: 443))
        XCTAssertEqual(signal?.category, .maliciousEndpoint)
    }

    // One contact is a mistake; the same one over and over is a program phoning
    // home, and that is a different severity entirely.
    func testRepeatedContactEscalatesToBeaconing() throws {
        let m = BlocklistThreatMonitor(indicators: try source("evil.example.com"))
        _ = m.evaluate(.dns(host: "evil.example.com"))
        _ = m.evaluate(.dns(host: "evil.example.com"))
        let third = m.evaluate(.dns(host: "evil.example.com"))
        XCTAssertEqual(third?.category, .commandAndControl)
        XCTAssertEqual(third?.severity, .critical)
        XCTAssertTrue(third?.tags.contains("beacon-x3") ?? false)
    }

    func testAnAllowedHostIsNeverFlaggedEvenWhenBlocklisted() throws {
        let options = DefenseOptions()
        options.allowedHosts.insert("evil.example.com")
        let m = BlocklistThreatMonitor(indicators: try source("evil.example.com"), options: options)
        XCTAssertNil(m.evaluate(.dns(host: "evil.example.com")))
    }

    func testAnAllowedAddressIsNeverFlagged() throws {
        let options = DefenseOptions()
        options.allowedAddresses.insert("203.0.113.5")
        let m = BlocklistThreatMonitor(indicators: try source("203.0.113.5"), options: options)
        XCTAssertNil(m.evaluate(.outbound(address: try ip("203.0.113.5"), port: 443)))
    }

    func testCleanTrafficProducesNothing() throws {
        let m = BlocklistThreatMonitor(indicators: try source("evil.example.com"))
        XCTAssertNil(m.evaluate(.dns(host: "www.example.org")))
    }

    func testASeverityFloorSuppressesQuieterFindings() throws {
        let options = DefenseOptions()
        options.minReportSeverity = .critical
        let m = BlocklistThreatMonitor(indicators: try source("evil.example.com"), options: options)
        XCTAssertNil(m.evaluate(.dns(host: "evil.example.com")))  // high < critical
    }
}
