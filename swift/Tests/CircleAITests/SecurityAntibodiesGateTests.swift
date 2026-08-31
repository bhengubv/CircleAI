import XCTest
@testable import CircleAI

/// The gate. Deny by default is the whole design, so most of this file is
/// about the ways an antibody must NOT run.
final class SecurityAntibodiesGateTests: XCTestCase {

    let now = Date(timeIntervalSince1970: 1_782_896_400)

    func threat(_ reason: String = "User reported a suspicious attachment") -> DefensiveThreatContext {
        DefensiveThreatContext.raise(reason: reason, severity: .elevated, raisedBy: "user", now: now)!
    }

    func request(_ cap: AntibodyCapability = .fileReputationAwareness,
                 threat t: DefensiveThreatContext? = nil) -> AuthorizedUseRequest {
        AuthorizedUseRequest(requestId: UUID(), capability: cap, threat: t ?? threat(),
                             justification: "warn the user", requestedAtUtc: now)
    }

    // MARK: - The null gate

    func testTheDefaultGateCannotGrantAnything() async {
        let d = await NullAuthorizedUseGate.instance.requestAuthorization(request())
        XCTAssertFalse(d.granted)
        XCTAssertEqual(d.reason, NullAuthorizedUseGate.denialReason)
        XCTAssertNil(d.expiresAtUtc)
    }

    func testTheDefaultGateDeniesEveryCapability() async {
        for cap in AntibodyCapability.allCases {
            let d = await NullAuthorizedUseGate.instance.requestAuthorization(request(cap))
            XCTAssertFalse(d.granted, "\(cap.name) must be denied")
        }
    }

    // MARK: - The consent gate

    func testNoConsentMeansNoAuthorization() async {
        let gate = ExplicitConsentAuthorizedUseGate(consents: InMemoryAuthorizedUseConsentStore(),
                                                    clock: { self.now })
        let d = await gate.requestAuthorization(request())
        XCTAssertFalse(d.granted)
        XCTAssertTrue(d.reason.contains("denied by default"))
    }

    func testAnActiveConsentAuthorizesAndCarriesItsExpiry() async {
        let store = InMemoryAuthorizedUseConsentStore()
        let consent = AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "Nandi",
                                                 scope: "one file", duration: 3600, now: now)!
        store.record(consent)
        let gate = ExplicitConsentAuthorizedUseGate(consents: store, clock: { self.now })

        let d = await gate.requestAuthorization(request())
        XCTAssertTrue(d.granted)
        XCTAssertEqual(d.expiresAtUtc, consent.expiresAtUtc)
        XCTAssertTrue(d.reason.contains("Nandi"))
    }

    // Consent for one capability must not unlock the others.
    func testConsentDoesNotSpreadToOtherCapabilities() async {
        let store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "u",
                                                scope: "s", duration: 3600, now: now)!)
        let gate = ExplicitConsentAuthorizedUseGate(consents: store, clock: { self.now })

        let file = await gate.requestAuthorization(request(.fileReputationAwareness))
        let net = await gate.requestAuthorization(request(.networkIndicatorAwareness))
        XCTAssertTrue(file.granted)
        XCTAssertFalse(net.granted)
    }

    // An expired consent is exactly as good as no consent.
    func testAnExpiredConsentAuthorizesNothing() async {
        let store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "u",
                                                scope: "s", duration: 60, now: now)!)
        let later = now.addingTimeInterval(120)
        let gate = ExplicitConsentAuthorizedUseGate(consents: store, clock: { later })
        let d = await gate.requestAuthorization(request())
        XCTAssertFalse(d.granted)
    }

    func testAConsentIsNotActiveBeforeItWasGranted() {
        let c = AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "u",
                                           scope: "s", duration: 60, now: now)!
        XCTAssertFalse(c.isActive(for: .fileReputationAwareness, now: now.addingTimeInterval(-1)))
        XCTAssertTrue(c.isActive(for: .fileReputationAwareness, now: now))
        // Half-open: dead the instant it expires.
        XCTAssertFalse(c.isActive(for: .fileReputationAwareness, now: c.expiresAtUtc))
    }
}
