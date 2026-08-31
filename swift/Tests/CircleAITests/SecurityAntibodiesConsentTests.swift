import XCTest
@testable import CircleAI

/// Revocation, and the rule that a threat is mandatory.
final class SecurityAntibodiesConsentTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    private func threat() -> DefensiveThreatContext {
        DefensiveThreatContext.raise(reason: "suspicious attachment", severity: .elevated,
                                     raisedBy: "user", now: now)!
    }

    private func request(_ cap: AntibodyCapability = .fileReputationAwareness,
                         threat t: DefensiveThreatContext? = nil) -> AuthorizedUseRequest {
        AuthorizedUseRequest(requestId: UUID(), capability: cap, threat: t ?? threat(),
                             justification: "warn the user", requestedAtUtc: now)
    }

    func testRevokingTakesTheAuthorizationAwayImmediately() async {
        let store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "u",
                                                scope: "s", duration: 3600, now: now)!)
        let gate = ExplicitConsentAuthorizedUseGate(consents: store, clock: { self.now })

        let before = await gate.requestAuthorization(request())
        XCTAssertTrue(before.granted)

        store.revoke(.fileReputationAwareness)
        let after = await gate.requestAuthorization(request())
        XCTAssertFalse(after.granted)
    }

    func testRevokeAllClearsEverything() async {
        let store = InMemoryAuthorizedUseConsentStore()
        for cap in AntibodyCapability.allCases {
            store.record(AuthorizedUseConsent.grant(cap, grantedBy: "u", scope: "s",
                                                    duration: 3600, now: now)!)
        }
        store.revokeAll()
        let gate = ExplicitConsentAuthorizedUseGate(consents: store, clock: { self.now })
        for cap in AntibodyCapability.allCases {
            let d = await gate.requestAuthorization(request(cap))
            XCTAssertFalse(d.granted, "\(cap.name) must be denied after revokeAll")
        }
    }

    // Consent alone is not enough. Without a named threat this is a capability
    // being used "just to check", which is what the module exists to refuse.
    func testConsentWithoutAThreatStillDenies() async {
        let store = InMemoryAuthorizedUseConsentStore()
        store.record(AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "u",
                                                scope: "s", duration: 3600, now: now)!)
        let gate = ExplicitConsentAuthorizedUseGate(consents: store, clock: { self.now })

        let empty = DefensiveThreatContext(reason: "   ", severity: .elevated, raisedBy: "u",
                                           raisedAtUtc: now, correlationId: UUID())
        let d = await gate.requestAuthorization(request(.fileReputationAwareness, threat: empty))
        XCTAssertFalse(d.granted)
        XCTAssertTrue(d.reason.contains("only under a defined threat"))
    }

    func testAThreatNeedsAReasonAndSomebodyRaisingIt() {
        XCTAssertNil(DefensiveThreatContext.raise(reason: "  ", severity: .high, raisedBy: "u"))
        XCTAssertNil(DefensiveThreatContext.raise(reason: "real", severity: .high, raisedBy: " "))
        XCTAssertNotNil(DefensiveThreatContext.raise(reason: "real", severity: .high, raisedBy: "u"))
    }

    // A consent with no end is not a consent.
    func testAConsentNeedsAPositiveDurationAndAGranter() {
        XCTAssertNil(AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "u",
                                                scope: "s", duration: 0))
        XCTAssertNil(AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "u",
                                                scope: "s", duration: -60))
        XCTAssertNil(AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: " ",
                                                scope: "s", duration: 60))
        XCTAssertNil(AuthorizedUseConsent.grant(.fileReputationAwareness, grantedBy: "u",
                                                scope: " ", duration: 60))
    }

    func testARequestNeedsAJustification() {
        XCTAssertNil(AuthorizedUseRequest.again(.fileReputationAwareness, threat: threat(),
                                                justification: "  "))
        XCTAssertNotNil(AuthorizedUseRequest.again(.fileReputationAwareness, threat: threat(),
                                                   justification: "warn the user"))
    }

    func testSeverityOrders() {
        XCTAssertTrue(DefensiveThreatSeverity.informational < .elevated)
        XCTAssertTrue(DefensiveThreatSeverity.elevated < .high)
        XCTAssertTrue(DefensiveThreatSeverity.high < .critical)
    }

    func testADecisionCarriesTheRequestItAnswers() {
        let r = request()
        let denied = AuthorizationDecision.deny(r, reason: "no", now: now)
        XCTAssertEqual(denied.requestId, r.requestId)
        XCTAssertEqual(denied.capability, r.capability)
        XCTAssertNil(denied.expiresAtUtc)
    }
}
