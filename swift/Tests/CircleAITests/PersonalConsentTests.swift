import XCTest
@testable import CircleAI

/// Consent. Every adapter method in this module checks it first, so most of
/// this file is about what happens when it is missing or stale.
final class PersonalConsentTests: XCTestCase {

    private let now = Date(timeIntervalSince1970: 1_782_896_400)

    /// The adapters check consent against the WALL CLOCK, exactly as the C#
    /// does, so a token for an adapter test has to be minted on it. Only the
    /// pure validity tests below, which pass an explicit `now`, use a fixed one.
    private func token(_ scopes: [ConsentScope], at base: Date = Date(),
                       expiresIn: TimeInterval = 3600) -> UserConsentToken {
        UserConsentToken(id: UUID(), uhidIdentityId: "uhid-1", scopes: scopes,
                         grantedAt: base, expiresAt: base.addingTimeInterval(expiresIn),
                         signature: Data([1, 2, 3]))
    }

    // MARK: - Validity

    func testAListedScopeInsideItsWindowIsValid() {
        let t = token([.calendarRead], at: now)
        XCTAssertTrue(t.isValid(for: .calendarRead, now: now))
        XCTAssertTrue(t.isValid(for: .calendarRead, now: now.addingTimeInterval(3599)))
    }

    // Reading a calendar is not permission to change it.
    func testAScopeThatWasNotGrantedIsNotValid() {
        let t = token([.calendarRead], at: now)
        XCTAssertFalse(t.isValid(for: .calendarWrite, now: now))
        XCTAssertFalse(t.isValid(for: .emailRead, now: now))
    }

    // A token that still LISTS a scope after it expired grants nothing.
    func testAnExpiredTokenGrantsNothingItStillLists() {
        let t = token([.calendarRead, .calendarWrite], at: now, expiresIn: 60)
        XCTAssertTrue(t.isValid(for: .calendarRead, now: now))
        XCTAssertFalse(t.isValid(for: .calendarRead, now: now.addingTimeInterval(60)))
        XCTAssertFalse(t.isValid(for: .calendarRead, now: now.addingTimeInterval(61)))
    }

    func testTheGuardNamesTheTokenAndTheScopeItRefused() {
        let t = token([.calendarRead], at: now)
        XCTAssertThrowsError(try ConsentGuard.require(t, .emailDraft, now: now)) { e in
            XCTAssertEqual(e as? ConsentError, .notGranted(tokenId: t.id, scope: .emailDraft))
            XCTAssertTrue((e as! ConsentError).description.contains("EmailDraft"))
        }
    }

    func testAnEmptyTokenGrantsNothing() {
        let t = token([], at: now)
        for scope in ConsentScope.allCases {
            XCTAssertFalse(t.isValid(for: scope, now: now), "\(scope.name) must not be granted")
        }
    }

    // MARK: - Calendar

    func testReadingACalendarWithoutReadConsentIsRefused() async {
        let t = token([.calendarWrite])
        do {
            _ = try await NullCalendarAdapter.instance.listEvents(from: now, to: now, consent: t)
            XCTFail("expected a refusal")
        } catch let e as ConsentError {
            XCTAssertEqual(e, .notGranted(tokenId: t.id, scope: .calendarRead))
        } catch { XCTFail("wrong error: \(error)") }
    }

    func testReadingACalendarWithConsentReturnsNothingRatherThanRefusing() async throws {
        let events = try await NullCalendarAdapter.instance
            .listEvents(from: now, to: now, consent: token([.calendarRead]))
        XCTAssertTrue(events.isEmpty)
    }

    // Consent is checked BEFORE the adapter admits it is not bound - the order
    // matters, because a missing binding must not leak that consent was absent.
    func testWritingChecksConsentBeforeAdmittingItIsUnbound() async {
        let t = token([.calendarRead])
        let ev = PersonalCalendarEvent(externalId: "x", title: "t", startUtc: now, endUtc: now)
        do {
            _ = try await NullCalendarAdapter.instance.createEvent(ev, consent: t)
            XCTFail("expected a refusal")
        } catch let e as ConsentError {
            XCTAssertEqual(e.description.contains("CalendarWrite"), true)
        } catch { XCTFail("expected a consent error, got \(error)") }
    }

    func testWritingWithConsentSaysWhatToBind() async {
        let t = token([.calendarWrite])
        let ev = PersonalCalendarEvent(externalId: "x", title: "t", startUtc: now, endUtc: now)
        for op in ["create", "update", "delete"] {
            do {
                switch op {
                case "create": _ = try await NullCalendarAdapter.instance.createEvent(ev, consent: t)
                case "update": _ = try await NullCalendarAdapter.instance.updateEvent(ev, consent: t)
                default: try await NullCalendarAdapter.instance.deleteEvent(id: ev.id, consent: t)
                }
                XCTFail("expected \(op) to refuse")
            } catch let e as PersonalAdapterError {
                XCTAssertTrue(e.description.contains("Bind a concrete adapter"))
            } catch { XCTFail("wrong error for \(op): \(error)") }
        }
    }

    // MARK: - Contacts and email

    func testContactsNeedReadConsent() async {
        do {
            _ = try await NullContactsAdapter.instance.search("nandi", consent: token([.emailRead]))
            XCTFail("expected a refusal")
        } catch let e as ConsentError {
            XCTAssertEqual(e, .notGranted(tokenId: e.tokenId, scope: .contactsRead))
        } catch { XCTFail("wrong error") }
    }

    func testContactsWithConsentComeBackEmpty() async throws {
        let t = token([.contactsRead])
        let found = try await NullContactsAdapter.instance.search("nandi", consent: t)
        let one = try await NullContactsAdapter.instance.getByExternalId("x", consent: t)
        XCTAssertTrue(found.isEmpty)
        XCTAssertNil(one)
    }

    func testReadingMailNeedsReadAndDraftingNeedsDraft() async throws {
        let readOnly = token([.emailRead])
        let messages = try await NullEmailAdapter.instance.listRecent(count: 5, consent: readOnly)
        XCTAssertTrue(messages.isEmpty)

        do {
            _ = try await NullEmailAdapter.instance.draftReply(toExternalId: "m1",
                                                               bodyPlain: "sure", consent: readOnly)
            XCTFail("read consent must not authorise drafting")
        } catch let e as ConsentError {
            XCTAssertTrue(e.description.contains("EmailDraft"))
        } catch { XCTFail("wrong error") }
    }

    func testDraftingWithConsentReturnsADraftId() async throws {
        let id = try await NullEmailAdapter.instance.draftReply(
            toExternalId: "m1", bodyPlain: "sure", consent: token([.emailDraft]))
        XCTAssertNotEqual(id, UUID(uuidString: "00000000-0000-0000-0000-000000000000"))
    }

    // MARK: - Domain

    func testTheDomainDeclaresItsCompliance() {
        XCTAssertEqual(PersonalDomainContext.complianceFlags, ["POPIA"])
        XCTAssertTrue(PersonalDomainContext.systemPromptSnippet.contains("POPIA"))
        XCTAssertTrue(PersonalDomainContext.suggestedTools.contains("calendar"))
    }
}

private extension ConsentError {
    var tokenId: UUID {
        switch self { case .notGranted(let id, _): return id }
    }
}
