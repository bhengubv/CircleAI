import XCTest
@testable import CircleAI

/// Normalisation and the three assessors.
final class SecurityAntibodiesAwarenessTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func corpus() -> InMemoryIndicatorCorpus { InMemoryIndicatorCorpus() }

    // MARK: - Normalisation

    func testDomainsLoseTheirWwwAndTheirCase() {
        XCTAssertEqual(IndicatorNormalizer.normalizeNetwork(.domainName, "WWW.Evil.COM"), "evil.com")
        XCTAssertEqual(IndicatorNormalizer.normalizeNetwork(.domainName, "evil.com"), "evil.com")
    }

    // Only DOMAINS lose the prefix - a URL that starts with www is a different
    // string and must stay one.
    func testAUrlKeepsItsWww() {
        XCTAssertEqual(IndicatorNormalizer.normalizeNetwork(.url, "HTTPS://WWW.Evil.com/a"),
                       "https://www.evil.com/a")
    }

    func testAnEmptyNetworkValueNormalisesToNothing() {
        XCTAssertNil(IndicatorNormalizer.normalizeNetwork(.domainName, "   "))
    }

    // An identity is HASHED before it is looked up, so the corpus never holds
    // the address itself.
    func testAnIdentityIsHashedNotStoredInTheClear() {
        let h = IndicatorNormalizer.normalizeIdentityToHash(.emailAddress, "Nandi@Example.com")
        XCTAssertEqual(h?.count, 64)
        XCTAssertFalse(h!.contains("nandi"))
        // Case-insensitive: the same address hashes the same either way.
        XCTAssertEqual(h, IndicatorNormalizer.normalizeIdentityToHash(.emailAddress, "nandi@example.com"))
    }

    // Spaces and dashes are how people write numbers, and must not change the
    // answer - but the country code must.
    func testAPhoneNumberKeepsItsLeadingPlusAndDigitsOnly() {
        let a = IndicatorNormalizer.normalizeIdentityToHash(.phoneNumber, "+27 82 555 0142")
        let b = IndicatorNormalizer.normalizeIdentityToHash(.phoneNumber, "+27825550142")
        let c = IndicatorNormalizer.normalizeIdentityToHash(.phoneNumber, "+27-82-555-0142")
        XCTAssertEqual(a, b)
        XCTAssertEqual(a, c)
        XCTAssertNotEqual(a, IndicatorNormalizer.normalizeIdentityToHash(.phoneNumber, "27825550142"))
    }

    func testAPhoneNumberWithNoDigitsNormalisesToNothing() {
        XCTAssertNil(IndicatorNormalizer.normalizeIdentityToHash(.phoneNumber, "----"))
        XCTAssertNil(IndicatorNormalizer.normalizeIdentityToHash(.emailAddress, "  "))
    }

    func testAFileArtifactHashesItsContent() {
        let a = FileArtifact.fromContent(fileName: "invoice.pdf", content: Data("hello".utf8))!
        XCTAssertEqual(a.sizeBytes, 5)
        // SHA-256 of "hello", the published vector.
        XCTAssertEqual(a.sha256Hex,
            "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")
    }

    func testAFileArtifactNeedsAName() {
        XCTAssertNil(FileArtifact.fromContent(fileName: "  ", content: Data()))
    }

    // MARK: - File awareness

    func testAKnownBadHashIsReportedWithAClearInstruction() async {
        let c = corpus()
        c.add(kind: .fileHashSha256, normalizedKey: "abc123", verdict: .knownBad,
              note: "banking trojan dropper", protectiveGuidance: "Delete it.", source: "test set")

        let r = await FileThreatAwarenessAssessor(corpus: c, clock: { self.now })
            .inspect(FileArtifact(fileName: "invoice.pdf", sha256Hex: "ABC123", sizeBytes: 1))
        XCTAssertEqual(r.verdict, .knownBad)
        XCTAssertTrue(r.protectiveGuidance.contains("Do not open"))
        XCTAssertTrue(r.summary.contains("banking trojan dropper"))
    }

    // A clean result is NOT a clean bill of health, and must say so.
    func testAnUnknownHashIsNotCalledSafe() async {
        let r = await FileThreatAwarenessAssessor(corpus: corpus(), clock: { self.now })
            .inspect(FileArtifact(fileName: "cv.docx", sha256Hex: "ffff", sizeBytes: 1))
        XCTAssertEqual(r.verdict, .noKnownThreat)
        XCTAssertTrue(r.summary.contains("not proof of safety"))
    }

    func testAFileWithNoHashIsInconclusiveNotClean() async {
        let r = await FileThreatAwarenessAssessor(corpus: corpus(), clock: { self.now })
            .inspect(FileArtifact(fileName: "x", sha256Hex: "  ", sizeBytes: 0))
        XCTAssertEqual(r.verdict, .inconclusive)
    }

    // MARK: - Network awareness

    func testAKnownBadDomainIsFlaggedThroughItsWwwForm() async {
        let c = corpus()
        c.add(kind: .domainName, normalizedKey: "evil.com", verdict: .knownBad,
              note: "phishing", protectiveGuidance: "Report it.", source: "test set")

        let r = await NetworkThreatAwarenessAssessor(corpus: c, clock: { self.now })
            .inspect(NetworkIndicator.forDomain("WWW.Evil.com")!)
        XCTAssertEqual(r.verdict, .knownBad)
        XCTAssertTrue(r.protectiveGuidance.contains("Do not connect"))
    }

    func testASuspiciousLocationIsWarnedAboutMoreSoftly() async {
        let c = corpus()
        c.add(kind: .ipAddress, normalizedKey: "203.0.113.5", verdict: .suspicious,
              note: "seen in a scam campaign", protectiveGuidance: "Verify first.", source: "test set")

        let r = await NetworkThreatAwarenessAssessor(corpus: c, clock: { self.now })
            .inspect(NetworkIndicator.forIp("203.0.113.5")!)
        XCTAssertEqual(r.verdict, .suspicious)
        XCTAssertTrue(r.protectiveGuidance.contains("unless you are certain"))
    }

    func testAnEmptyIndicatorIsRefusedAtConstruction() {
        XCTAssertNil(NetworkIndicator.forUrl("  "))
        XCTAssertNil(NetworkIndicator.forDomain(""))
        XCTAssertNil(IdentityIndicator.email("  "))
    }

    // MARK: - Breach exposure

    func testAnExposedAddressGetsRotationGuidance() async {
        let c = corpus()
        let hash = IndicatorNormalizer.normalizeIdentityToHash(.emailAddress, "nandi@example.com")!
        c.add(kind: .emailAddress, normalizedKey: hash, verdict: .knownBad,
              note: "2024 forum breach", protectiveGuidance: "Check your other accounts.", source: "test set")

        let r = await BreachExposureAssessor(corpus: c, clock: { self.now })
            .inspect(IdentityIndicator.email("Nandi@Example.com")!)
        XCTAssertEqual(r.verdict, .knownBad)
        XCTAssertTrue(r.protectiveGuidance.contains("Change the password"))
        XCTAssertTrue(r.protectiveGuidance.contains("2-factor"))
        XCTAssertTrue(r.summary.contains("email address"))
    }

    // Absence of a match is not safety - breaches surface years later.
    func testAnUnfoundAddressStillGetsAdvice() async {
        let r = await BreachExposureAssessor(corpus: corpus(), clock: { self.now })
            .inspect(IdentityIndicator.username("nandi")!)
        XCTAssertEqual(r.verdict, .noKnownThreat)
        XCTAssertTrue(r.protectiveGuidance.contains("New breaches appear over time"))
        XCTAssertTrue(r.protectiveGuidance.contains("username"))
    }

    func testAnUnreadableIdentityIsInconclusive() async {
        let r = await BreachExposureAssessor(corpus: corpus(), clock: { self.now })
            .inspect(IdentityIndicator(kind: .phoneNumber, value: "----"))
        XCTAssertEqual(r.verdict, .inconclusive)
    }
}
