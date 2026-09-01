import XCTest
@testable import CircleAI

/// The ubiquity rails: the fallbacks for people with no data, no storage, or
/// no smartphone, and the catalogues the public claims are written from.
final class DistributionUbiquityTests: XCTestCase {

    // MARK: - Peer sync

    func testTheNullSyncHoldsNothingAndFindsNobody() async {
        let has = await NullFileSync.instance.has(contentHash: "abc")
        let got = await NullFileSync.instance.fetch(contentHash: "abc")
        let peers = await NullPeerAdvertiser.instance.discover()
        XCTAssertFalse(has)
        XCTAssertNil(got)
        XCTAssertTrue(peers.isEmpty)
    }

    // MARK: - Personality

    func testAPersonalityOutsideThePresetsIsRefused() {
        let w = DefaultAiPersonalityWizard()
        XCTAssertThrowsError(try w.select(sessionId: "s1", choice: PersonalityChoice("chaotic"))) { e in
            XCTAssertEqual(e as? DistributionError, .unknownPersonality("chaotic"))
        }
        XCTAssertNil(w.selected(sessionId: "s1"))
    }

    func testAPresetPersonalityIsStoredAndCaseInsensitive() throws {
        let w = DefaultAiPersonalityWizard()
        try w.select(sessionId: "s1", choice: PersonalityChoice("WARM"))
        XCTAssertEqual(w.selected(sessionId: "s1")?.name, "WARM")
        XCTAssertEqual(w.presets.count, 4)
    }

    func testASessionIdIsRequired() {
        let w = DefaultAiPersonalityWizard()
        XCTAssertThrowsError(try w.select(sessionId: "  ", choice: PersonalityChoice("warm")))
    }

    // MARK: - Offline queue

    func testWorkDoneOfflineWaitsInOrder() throws {
        let q = DefaultOfflineQueuedOperation()
        try q.enqueue("{\"a\":1}")
        try q.enqueue("{\"b\":2}")
        XCTAssertEqual(q.pending.count, 2)
        XCTAssertEqual(q.tryDequeue(), "{\"a\":1}")
        XCTAssertEqual(q.tryDequeue(), "{\"b\":2}")
        XCTAssertNil(q.tryDequeue())
        XCTAssertTrue(q.pending.isEmpty)
    }

    func testAnEmptyOperationIsRefusedRatherThanQueued() {
        XCTAssertThrowsError(try DefaultOfflineQueuedOperation().enqueue("  "))
    }

    // MARK: - USSD

    // Works on a phone with no data and no smartphone at all.
    func testTheUssdMenuWalksAndComesBack() throws {
        let u = DefaultUssdFallback()
        let root = try u.respond(ussdSession: "s1", input: "")
        XCTAssertTrue(root.contains("1. Balance"))

        let balance = try u.respond(ussdSession: "s1", input: "1")
        XCTAssertTrue(balance.contains("Balance:"))

        let back = try u.respond(ussdSession: "s1", input: "0")
        XCTAssertTrue(back.contains("1. Balance"))
    }

    // A mistyped digit must not end the call.
    func testAnUnknownKeyRedisplaysTheMenu() throws {
        let u = DefaultUssdFallback()
        _ = try u.respond(ussdSession: "s1", input: "")
        let again = try u.respond(ussdSession: "s1", input: "9")
        XCTAssertTrue(again.contains("1. Balance"))
    }

    func testTwoUssdSessionsDoNotShareState() throws {
        let u = DefaultUssdFallback()
        _ = try u.respond(ussdSession: "a", input: "1")   // a -> balance
        let b = try u.respond(ussdSession: "b", input: "")
        XCTAssertTrue(b.contains("1. Balance"), "b must still be at the root")
    }

    // MARK: - SMS and messaging

    func testAnSmsIsRecordedEvenWhenDeliveryFails() async {
        struct Boom: Error {}
        let sms = DefaultSmsFallback(delivery: { _, _ in throw Boom() })
        do {
            try await sms.answerViaSms(phoneNumber: "+27825550142", question: "what is the time")
            XCTFail("delivery should have thrown")
        } catch {}
        XCTAssertEqual(sms.sent.count, 1, "the attempt must still be visible")
    }

    // WhatsApp rejects a malformed number outright; SMS just fails.
    func testWhatsAppValidatesTheNumberAndSmsDoesNot() async throws {
        let wa = DefaultWhatsAppIntegration()
        do {
            try await wa.send(phoneNumber: "0825550142", message: "hi")
            XCTFail("a non-E.164 number must be refused")
        } catch let e as DistributionError {
            XCTAssertEqual(e, .invalidPhone("0825550142"))
        }
        try await wa.send(phoneNumber: "+27825550142", message: "hi")
        XCTAssertEqual(wa.outbox.count, 1)

        let sms = DefaultSmsFallback()
        try await sms.answerViaSms(phoneNumber: "0825550142", question: "q")
        XCTAssertEqual(sms.sent.count, 1)
    }

    func testE164Recognition() {
        XCTAssertTrue(DistributionPhone.isE164("+27825550142"))
        XCTAssertTrue(DistributionPhone.isE164("27825550142"))
        XCTAssertFalse(DistributionPhone.isE164("0825550142"), "a leading zero is not E.164")
        XCTAssertFalse(DistributionPhone.isE164("+27 82 555 0142"), "spaces are not E.164")
        XCTAssertFalse(DistributionPhone.isE164("12345"))
        XCTAssertFalse(DistributionPhone.isE164(""))
    }

    func testTelegramNeedsAChatAndABody() async {
        let t = DefaultTelegramIntegration()
        do { try await t.send(chatId: "", message: "x"); XCTFail() } catch {}
        do { try await t.send(chatId: "c", message: " "); XCTFail() } catch {}
        XCTAssertTrue(t.outbox.isEmpty)
    }
}
