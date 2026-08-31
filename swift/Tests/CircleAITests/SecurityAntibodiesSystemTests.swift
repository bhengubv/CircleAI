import XCTest
@testable import CircleAI

/// The facade: every path asks the gate first.
final class SecurityAntibodiesSystemTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func threat() -> DefensiveThreatContext {
        DefensiveThreatContext.raise(reason: "User was sent an unexpected invoice",
                                     severity: .elevated, raisedBy: "user", now: now)!
    }

    private func armedCorpus() -> InMemoryIndicatorCorpus {
        let c = InMemoryIndicatorCorpus()
        c.add(kind: .fileHashSha256, normalizedKey: "abc123", verdict: .knownBad,
              note: "dropper", protectiveGuidance: "Delete it.", source: "test set")
        c.add(kind: .domainName, normalizedKey: "evil.com", verdict: .knownBad,
              note: "phishing", protectiveGuidance: "Report it.", source: "test set")
        let hash = IndicatorNormalizer.normalizeIdentityToHash(.emailAddress, "nandi@example.com")!
        c.add(kind: .emailAddress, normalizedKey: hash, verdict: .knownBad,
              note: "2024 breach", protectiveGuidance: "Rotate it.", source: "test set")
        return c
    }

    private func consentedSystem() -> DefensiveAntibodySystem {
        let store = InMemoryAuthorizedUseConsentStore()
        for cap in AntibodyCapability.allCases {
            store.record(AuthorizedUseConsent.grant(cap, grantedBy: "Nandi", scope: "this incident",
                                                    duration: 3600, now: now)!)
        }
        return DefensiveAntibodySystem.create(
            gate: ExplicitConsentAuthorizedUseGate(consents: store, clock: { self.now }),
            corpus: armedCorpus(),
            clock: { self.now })
    }

    // MARK: - Deny by default

    // A build that has not opted in is a valid build, and it assesses nothing.
    func testTheDenyByDefaultSystemAssessesNothingAtAll() async {
        let s = DefensiveAntibodySystem.createDenyByDefault(clock: { self.now })
        let t = threat()

        let file = await s.assessFile(FileArtifact(fileName: "x.pdf", sha256Hex: "abc123", sizeBytes: 1), threat: t)
        let net = await s.assessNetworkIndicator(NetworkIndicator.forDomain("evil.com")!, threat: t)
        let id = await s.assessOwnIdentityExposure(IdentityIndicator.email("nandi@example.com")!, threat: t)

        for r in [file, net, id] {
            XCTAssertEqual(r.verdict, .notAssessed)
            XCTAssertFalse(r.wasAuthorized)
            XCTAssertEqual(r.source, "authorized-use gate")
        }
    }

    // A denial explains itself rather than throwing or returning nothing.
    func testADenialSaysWhyAndOffersAWayForward() async {
        let s = DefensiveAntibodySystem.createDenyByDefault(clock: { self.now })
        let r = await s.assessFile(FileArtifact(fileName: "x", sha256Hex: "abc123", sizeBytes: 1),
                                   threat: threat())
        XCTAssertTrue(r.summary.contains("gate denied it"))
        XCTAssertTrue(r.protectiveGuidance.contains("explicitly authorized"))
    }

    // The corpus is armed and the indicator IS known-bad - the only thing
    // stopping the answer is the missing consent. That is the design.
    func testAnArmedCorpusIsStillSilentWithoutConsent() async {
        let s = DefensiveAntibodySystem.create(gate: NullAuthorizedUseGate.instance,
                                               corpus: armedCorpus(), clock: { self.now })
        let r = await s.assessNetworkIndicator(NetworkIndicator.forDomain("evil.com")!, threat: threat())
        XCTAssertEqual(r.verdict, .notAssessed)
    }

    // MARK: - With consent

    func testWithConsentAllThreeAssessmentsRun() async {
        let s = consentedSystem()
        let t = threat()

        let file = await s.assessFile(FileArtifact(fileName: "invoice.pdf", sha256Hex: "abc123", sizeBytes: 9),
                                      threat: t)
        let net = await s.assessNetworkIndicator(NetworkIndicator.forDomain("www.evil.com")!, threat: t)
        let id = await s.assessOwnIdentityExposure(IdentityIndicator.email("Nandi@Example.com")!, threat: t)

        for r in [file, net, id] {
            XCTAssertTrue(r.wasAuthorized)
            XCTAssertEqual(r.verdict, .knownBad)
        }
    }

    func testACleanIndicatorComesBackAsNoKnownThreatNotAsSafe() async {
        let s = consentedSystem()
        let r = await s.assessNetworkIndicator(NetworkIndicator.forDomain("example.org")!, threat: threat())
        XCTAssertTrue(r.wasAuthorized)
        XCTAssertEqual(r.verdict, .noKnownThreat)
        XCTAssertTrue(r.summary.contains("not proof of safety"))
    }

    // Consent for the file capability must not let the identity check run.
    func testEachCapabilityIsGatedSeparatelyEndToEnd() async {
        let store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "u",
                                                scope: "s", duration: 3600, now: now)!)
        let s = DefensiveAntibodySystem.create(
            gate: ExplicitConsentAuthorizedUseGate(consents: store, clock: { self.now }),
            corpus: armedCorpus(), clock: { self.now })
        let t = threat()

        let file = await s.assessFile(FileArtifact(fileName: "x", sha256Hex: "abc123", sizeBytes: 1), threat: t)
        let id = await s.assessOwnIdentityExposure(IdentityIndicator.email("nandi@example.com")!, threat: t)

        XCTAssertTrue(file.wasAuthorized)
        XCTAssertFalse(id.wasAuthorized)
    }

    func testEveryResultCarriesGuidanceWhateverTheVerdict() async {
        let s = consentedSystem()
        let t = threat()
        let results = [
            await s.assessFile(FileArtifact(fileName: "a", sha256Hex: "abc123", sizeBytes: 1), threat: t),
            await s.assessFile(FileArtifact(fileName: "b", sha256Hex: "ffff", sizeBytes: 1), threat: t),
            await s.assessNetworkIndicator(NetworkIndicator.forUrl("https://example.org")!, threat: t),
            await DefensiveAntibodySystem.createDenyByDefault(clock: { self.now })
                .assessFile(FileArtifact(fileName: "c", sha256Hex: "abc123", sizeBytes: 1), threat: t),
        ]
        for r in results {
            XCTAssertFalse(r.protectiveGuidance.isEmpty)
            XCTAssertFalse(r.summary.isEmpty)
            XCTAssertEqual(r.assessedAtUtc, now)
        }
    }

    // MARK: - The corpus itself

    func testAnEntryWithoutGuidanceIsRefused() {
        let c = InMemoryIndicatorCorpus()
        XCTAssertFalse(c.add(kind: .domainName, normalizedKey: "x.com", verdict: .knownBad,
                             note: "n", protectiveGuidance: "  ", source: "s"))
        XCTAssertFalse(c.add(kind: .domainName, normalizedKey: " ", verdict: .knownBad,
                             note: "n", protectiveGuidance: "g", source: "s"))
        XCTAssertEqual(c.count, 0)
    }

    func testTheEmptyCorpusKnowsNothing() async {
        let r = await EmptyIndicatorCorpus.instance.lookup(.domainName, normalizedValue: "evil.com")
        XCTAssertNil(r)
    }
}
