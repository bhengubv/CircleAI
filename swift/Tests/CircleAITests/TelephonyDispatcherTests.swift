// TelephonyDispatcherTests.swift
//
// Verifies the inbound dispatcher fan-out + subscription lifecycle
// (FakeInboundCallDispatcher / NoopCallSubscription) and the pending media
// streams (PendingMediaStream / TelnyxPendingMediaStream /
// PlivoPendingMediaStream): they yield no audio/DTMF, guard sendAudio, and
// transition to EndedByAgent on end(). Cross-checked against the C# pending
// streams + NullInboundCallDispatcher semantics.

import XCTest
import Foundation
@testable import CircleAI

final class TelephonyDispatcherTests: XCTestCase {

    func testInboundDispatcherDeliversToAllSubscribers() async throws {
        let dispatcher = FakeInboundCallDispatcher(carrierId: "fake")
        XCTAssertEqual(dispatcher.carrierId, "fake")

        let seenA = Locked<[String]>([])
        let seenB = Locked<[String]>([])
        let subA = dispatcher.subscribe { session in
            seenA.mutate { $0.append(session.info.callId) }
        }
        _ = dispatcher.subscribe { session in
            seenB.mutate { $0.append(session.info.callId) }
        }

        let session = TestCallSession(info: CallInfo(
            callId: "inbound-1", direction: .inbound, from: "+1", to: "+2",
            carrierId: "fake", mediaFormat: .pcm16000, startedAtUtc: Date()))
        await dispatcher.deliver(session)

        XCTAssertEqual(seenA.value, ["inbound-1"])
        XCTAssertEqual(seenB.value, ["inbound-1"])

        // Dispose A → only B receives the next delivery.
        subA.dispose()
        let session2 = TestCallSession(info: CallInfo(
            callId: "inbound-2", direction: .inbound, from: "+1", to: "+2",
            carrierId: "fake", mediaFormat: .pcm16000, startedAtUtc: Date()))
        await dispatcher.deliver(session2)
        XCTAssertEqual(seenA.value, ["inbound-1"])          // unchanged
        XCTAssertEqual(seenB.value, ["inbound-1", "inbound-2"])
    }

    func testSubscriptionDisposeIsIdempotent() {
        let dispatcher = FakeInboundCallDispatcher()
        let sub = dispatcher.subscribe { _ in }
        sub.dispose()
        sub.dispose() // second dispose must be a harmless no-op
    }

    func testNoopCallSubscriptionDoesNothing() {
        NoopCallSubscription.instance.dispose() // must not crash
    }

    // ── Pending media streams ─────────────────────────────────────────────

    private func pendingInfo(_ carrier: String) -> CallInfo {
        CallInfo(callId: "p", direction: .outbound, from: "+1", to: "+2",
                 carrierId: carrier, mediaFormat: .mulaw8000, startedAtUtc: Date())
    }

    func testTwilioPendingStreamYieldsNothingAndGuardsSend() async throws {
        let s = PendingMediaStream(callInfo: pendingInfo("twilio"))
        XCTAssertEqual(s.currentStatus, .ringing)
        // No audio / no DTMF.
        var audioCount = 0
        for await _ in s.receiveAudio() { audioCount += 1 }
        var dtmfCount = 0
        for await _ in s.receiveDtmf() { dtmfCount += 1 }
        XCTAssertEqual(audioCount, 0)
        XCTAssertEqual(dtmfCount, 0)
        // Send before attach throws.
        await XCTAssertThrowsErrorAsync(
            try await s.sendAudio(AudioFrame(pcm: Data(), format: .mulaw8000, offset: 0)))
        // end() → EndedByAgent.
        try await s.end()
        XCTAssertEqual(s.currentStatus, .endedByAgent)
    }

    func testTelnyxPendingStreamEndTransitions() async throws {
        let s = TelnyxPendingMediaStream(callInfo: pendingInfo("telnyx"))
        XCTAssertEqual(s.currentStatus, .ringing)
        await XCTAssertThrowsErrorAsync(
            try await s.sendAudio(AudioFrame(pcm: Data(), format: .pcm16000, offset: 0)))
        try await s.end()
        XCTAssertEqual(s.currentStatus, .endedByAgent)
    }

    func testPlivoPendingStreamEndTransitions() async throws {
        let s = PlivoPendingMediaStream(callInfo: pendingInfo("plivo"))
        XCTAssertEqual(s.currentStatus, .ringing)
        await XCTAssertThrowsErrorAsync(
            try await s.sendAudio(AudioFrame(pcm: Data(), format: .mulaw8000, offset: 0)))
        try await s.end()
        XCTAssertEqual(s.currentStatus, .endedByAgent)
    }

    // ── Support helpers wire format (spot checks) ─────────────────────────

    func testEscapeDataStringMatchesDotNet() {
        // Space → %20 (not +); unreserved left intact; '+' → %2B.
        XCTAssertEqual(TelephonyUri.escapeDataString("a b"), "a%20b")
        XCTAssertEqual(TelephonyUri.escapeDataString("+27-8_2.1~x"), "%2B27-8_2.1~x")
        XCTAssertEqual(TelephonyUri.escapeDataString("wss://h/s?a=1"), "wss%3A%2F%2Fh%2Fs%3Fa%3D1")
    }

    func testFormEncodingUsesPlusForSpace() {
        let form = FormUrlEncoded([("k1", "a b"), ("k2", "+27")])
        // Space → +, '+' → %2B.
        XCTAssertEqual(form.encoded, "k1=a+b&k2=%2B27")
    }

    func testHtmlEncodeMatchesWebUtility() {
        XCTAssertEqual(TelephonyUri.htmlEncode("<a & b>'\""), "&lt;a &amp; b&gt;&#39;&quot;")
        // Ordinary URL chars pass through.
        XCTAssertEqual(TelephonyUri.htmlEncode("wss://h/s?a=1"), "wss://h/s?a=1")
    }

    func testJsonParseDecimalNumberAndString() {
        XCTAssertEqual(TelephonyJson.parseDecimal(["price": "1.50"], "price"), Decimal(string: "1.50"))
        XCTAssertEqual(TelephonyJson.parseDecimal(["price": 2], "price"), Decimal(2))
        XCTAssertNil(TelephonyJson.parseDecimal(["price": true], "price"))   // bool excluded
        XCTAssertNil(TelephonyJson.parseDecimal(["other": "x"], "price"))    // missing
    }
}
