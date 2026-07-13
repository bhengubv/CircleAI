// TelephonyCarrierBindingTests.swift
//
// Verifies the Twilio / Telnyx / Plivo ITelephonyCarrier bindings against a
// deterministic FakeHttpTransport (no real network). Asserts exact request
// paths, auth headers, form/JSON bodies, and response parsing, plus the
// fail-soft (unconfigured) branches, the control-plane calls issued by the
// sessions on transfer/hangUp, and the pending-media-stream send guard.
// Cross-checked against TwilioCarrier / TelnyxCarrier / PlivoCarrier.

import XCTest
import Foundation
@testable import CircleAI

final class TelephonyCarrierBindingTests: XCTestCase {

    // =================================================================
    // Shared helpers
    // =================================================================

    /// Parse a form body ("a=b&c=d") back into a dictionary with +→space and
    /// percent-decoding, so tests can assert on logical values.
    private func parseForm(_ body: String) -> [String: String] {
        var out: [String: String] = [:]
        for pair in body.split(separator: "&") {
            let kv = pair.split(separator: "=", maxSplits: 1, omittingEmptySubsequences: false)
            let k = String(kv[0])
            let vRaw = kv.count > 1 ? String(kv[1]) : ""
            let v = vRaw.replacingOccurrences(of: "+", with: " ").removingPercentEncoding ?? vRaw
            out[k] = v
        }
        return out
    }

    // =================================================================
    // Twilio
    // =================================================================

    func testTwilioIsConfiguredAndAuthHeader() {
        let http = FakeHttpTransport()
        _ = TwilioCarrier(http: http, options: TwilioOptions(accountSid: "AC123", authToken: "tok"))
        // Base address defaulted.
        XCTAssertEqual(http.baseAddress, URL(string: "https://api.twilio.com"))
        // Basic auth header = base64("AC123:tok").
        let expected = "Basic " + Data("AC123:tok".utf8).base64EncodedString()
        XCTAssertEqual(http.defaultHeaders["Authorization"], expected)
    }

    func testTwilioNotConfiguredNoAuthAndThrows() async throws {
        let http = FakeHttpTransport()
        let carrier = TwilioCarrier(http: http, options: TwilioOptions())
        XCTAssertFalse(carrier.isConfigured)
        XCTAssertNil(http.defaultHeaders["Authorization"])
        await XCTAssertThrowsErrorAsync(try await carrier.provisionNumber(countryCode: "ZA"))
        // ListNumbers fail-soft.
        let numbers = try await carrier.listNumbers()
        XCTAssertTrue(numbers.isEmpty)
    }

    func testTwilioProvisionNumberWireFormat() async throws {
        let http = FakeHttpTransport()
        // 1) availability search returns one number with a price.
        http.on(.get, where: { $0.contains("/AvailablePhoneNumbers/ZA/Local.json") }) { _ in
            .json("{\"available_phone_numbers\":[{\"phone_number\":\"+27210001111\",\"price\":\"1.50\"}]}")
        }
        // 2) reservation POST.
        http.on(.post, where: { $0.hasSuffix("/IncomingPhoneNumbers.json") }) { _ in
            .json("{\"sid\":\"PNxxx\"}")
        }
        let carrier = TwilioCarrier(http: http, options: TwilioOptions(accountSid: "AC1", authToken: "t"))
        let n = try await carrier.provisionNumber(countryCode: "ZA", areaCode: "21")
        XCTAssertEqual(n.phoneNumber, "+27210001111")
        XCTAssertEqual(n.carrierId, "twilio")
        XCTAssertEqual(n.monthlyRecurringCost, Decimal(string: "1.50"))

        // The search path must carry AreaCode + Limit.
        let getReq = try XCTUnwrap(http.requests.first { $0.method == .get })
        XCTAssertEqual(getReq.path,
            "/2010-04-01/Accounts/AC1/AvailablePhoneNumbers/ZA/Local.json?AreaCode=21&Limit=1")
        // Reservation body form-encodes PhoneNumber (the '+' becomes %2B).
        let postReq = try XCTUnwrap(http.requests.first { $0.method == .post })
        XCTAssertEqual(postReq.path, "/2010-04-01/Accounts/AC1/IncomingPhoneNumbers.json")
        XCTAssertEqual(postReq.bodyString, "PhoneNumber=%2B27210001111")
    }

    func testTwilioProvisionNoNumbersThrows() async throws {
        let http = FakeHttpTransport()
        http.on(.get, where: { _ in true }) { _ in .json("{\"available_phone_numbers\":[]}") }
        let carrier = TwilioCarrier(http: http, options: TwilioOptions(accountSid: "AC1", authToken: "t"))
        await XCTAssertThrowsErrorAsync(try await carrier.provisionNumber(countryCode: "ZA"))
    }

    func testTwilioDialTwimlAndForm() async throws {
        let http = FakeHttpTransport()
        http.on(.post, where: { $0.hasSuffix("/Calls.json") }) { _ in
            .json("{\"sid\":\"CA999\"}")
        }
        let carrier = TwilioCarrier(http: http, options: TwilioOptions(accountSid: "AC1", authToken: "t"))
        let session = try await carrier.dial(
            fromNumber: "+27210001111",
            toNumber: "+27821234567",
            streamUrl: URL(string: "wss://host/s?a=1&b=2")!,
            options: OutboundDialOptions(detectAnsweringMachine: true, ringTimeoutSeconds: 20))

        XCTAssertEqual(session.info.callId, "CA999")
        XCTAssertEqual(session.info.direction, .outbound)
        XCTAssertEqual(session.info.mediaFormat, .mulaw8000)

        let req = try XCTUnwrap(http.lastRequest)
        XCTAssertEqual(req.path, "/2010-04-01/Accounts/AC1/Calls.json")
        let form = parseForm(req.bodyString)
        XCTAssertEqual(form["From"], "+27210001111")
        XCTAssertEqual(form["To"], "+27821234567")
        XCTAssertEqual(form["Timeout"], "20")
        XCTAssertEqual(form["MachineDetection"], "Enable")
        // The TwiML value must contain the HTML-encoded stream URL: the '&' in
        // the query is escaped to &amp; before form-encoding.
        let twiml = form["Twiml"] ?? ""
        XCTAssertEqual(twiml,
            "<Response><Connect><Stream url='wss://host/s?a=1&amp;b=2'/></Connect></Response>")
    }

    func testTwilioSessionHangUpIssuesRestAndPendingSendThrows() async throws {
        let http = FakeHttpTransport()
        let recorder = Locked<[String]>([])
        http.on(.post, where: { $0.contains("/Calls/") }) { req in
            recorder.mutate { $0.append(req.bodyString) }
            return .json("{}")
        }
        let carrier = TwilioCarrier(http: http, options: TwilioOptions(accountSid: "AC1", authToken: "t"))
        let pending = PendingMediaStream(callInfo: CallInfo(
            callId: "CA1", direction: .outbound, from: "+1", to: "+2",
            carrierId: "twilio", mediaFormat: .mulaw8000, startedAtUtc: Date()))
        let session = TwilioCallSession(media: pending, carrier: carrier)

        // Sending audio before attach must throw the friendly error.
        await XCTAssertThrowsErrorAsync(
            try await session.sendAudio(AudioFrame(pcm: Data(), format: .mulaw8000, offset: 0)))

        try await session.hangUp()
        XCTAssertEqual(session.status, .endedByAgent)
        // The REST call to complete the call was issued with Status=completed.
        XCTAssertTrue(recorder.value.contains("Status=completed"))
    }

    func testTwilioColdTransferRedirectsTwiml() async throws {
        let http = FakeHttpTransport()
        let bodies = Locked<[String]>([])
        http.on(.post, where: { $0.contains("/Calls/") }) { req in
            bodies.mutate { $0.append(req.bodyString) }
            return .json("{}")
        }
        let carrier = TwilioCarrier(http: http, options: TwilioOptions(accountSid: "AC1", authToken: "t"))
        // A ringing media stream (as produced by dial()'s PendingMediaStream) so
        // the session's locally-set status wins per the C# status-derivation rule.
        let media = InMemoryMediaStream(callInfo: CallInfo(
            callId: "CA5", direction: .inbound, from: "+1", to: "+2",
            carrierId: "twilio", mediaFormat: .mulaw8000, startedAtUtc: Date()),
            initialStatus: .ringing)
        let session = TwilioCallSession(media: media, carrier: carrier)
        try await session.transfer(targetNumber: "+27110001234", mode: .cold)
        XCTAssertEqual(session.status, .transferred)
        // Redirect TwiML form body carries the <Dial> verb (form-encoded).
        let sent = try XCTUnwrap(bodies.value.first)
        let decoded = parseForm(sent)["Twiml"] ?? ""
        XCTAssertEqual(decoded, "<Response><Dial>+27110001234</Dial></Response>")
    }

    // =================================================================
    // Telnyx
    // =================================================================

    func testTelnyxBearerAuthAndConfig() {
        let http = FakeHttpTransport()
        _ = TelnyxCarrier(http: http, options: TelnyxOptions(apiKey: "KEY123"))
        XCTAssertEqual(http.baseAddress, URL(string: "https://api.telnyx.com"))
        XCTAssertEqual(http.defaultHeaders["Authorization"], "Bearer KEY123")
    }

    func testTelnyxProvisionOrderBody() async throws {
        let http = FakeHttpTransport()
        http.on(.get, where: { $0.contains("/v2/available_phone_numbers") }) { _ in
            .json("{\"data\":[{\"phone_number\":\"+27210002222\",\"cost_information\":{\"monthly_cost\":\"2.00\"}}]}")
        }
        http.on(.post, where: { $0 == "/v2/number_orders" }) { _ in .json("{\"data\":{}}") }
        let carrier = TelnyxCarrier(http: http, options: TelnyxOptions(apiKey: "K"))
        let n = try await carrier.provisionNumber(countryCode: "ZA", areaCode: "21")
        XCTAssertEqual(n.phoneNumber, "+27210002222")
        XCTAssertEqual(n.monthlyRecurringCost, Decimal(string: "2.00"))

        let searchReq = try XCTUnwrap(http.requests.first { $0.method == .get })
        XCTAssertEqual(searchReq.path,
            "/v2/available_phone_numbers?filter[country_code]=ZA&filter[limit]=1&filter[national_destination_code]=21")
        let orderReq = try XCTUnwrap(http.requests.first { $0.method == .post })
        XCTAssertEqual(orderReq.contentType, .json)
        XCTAssertEqual(orderReq.bodyString, "{\"phone_numbers\":[{\"phone_number\":\"+27210002222\"}]}")
    }

    func testTelnyxDialRequiresConnectionId() async throws {
        let http = FakeHttpTransport()
        let carrier = TelnyxCarrier(http: http, options: TelnyxOptions(apiKey: "K")) // no connection id
        await XCTAssertThrowsErrorAsync(
            try await carrier.dial(fromNumber: "+1", toNumber: "+2", streamUrl: URL(string: "wss://x")!))
    }

    func testTelnyxDialJsonBodyOrder() async throws {
        let http = FakeHttpTransport()
        http.on(.post, where: { $0 == "/v2/calls" }) { _ in
            .json("{\"data\":{\"call_control_id\":\"cc-1\"}}")
        }
        let carrier = TelnyxCarrier(http: http, options: TelnyxOptions(apiKey: "K", callControlConnectionId: "conn-9"))
        let session = try await carrier.dial(
            fromNumber: "+27210002222", toNumber: "+27821234567",
            streamUrl: URL(string: "wss://host/s")!,
            options: OutboundDialOptions(detectAnsweringMachine: true, ringTimeoutSeconds: 25))
        XCTAssertEqual(session.info.callId, "cc-1")
        XCTAssertEqual(session.info.mediaFormat, .pcm16000)

        let req = try XCTUnwrap(http.lastRequest)
        // Exact JSON field order per the C# StringBuilder.
        XCTAssertEqual(req.bodyString,
            "{\"connection_id\":\"conn-9\",\"to\":\"+27821234567\",\"from\":\"+27210002222\"," +
            "\"stream_url\":\"wss://host/s\",\"stream_track\":\"both_tracks\"," +
            "\"timeout_secs\":25,\"answering_machine_detection\":\"detect\"}")
    }

    func testTelnyxSessionTransferAndHangUpActions() async throws {
        let http = FakeHttpTransport()
        let paths = Locked<[String]>([])
        http.on(.post, where: { $0.contains("/v2/calls/") }) { req in
            paths.mutate { $0.append(req.path) }
            return .json("{}")
        }
        let carrier = TelnyxCarrier(http: http, options: TelnyxOptions(apiKey: "K", callControlConnectionId: "c"))
        let media = InMemoryMediaStream(callInfo: CallInfo(
            callId: "cc-7", direction: .inbound, from: "+1", to: "+2",
            carrierId: "telnyx", mediaFormat: .pcm16000, startedAtUtc: Date()),
            initialStatus: .ringing)
        let session = TelnyxCallSession(media: media, carrier: carrier)

        try await session.transfer(targetNumber: "+27110000000", mode: .cold)
        XCTAssertEqual(session.status, .transferred)
        try await session.hangUp()
        XCTAssertTrue(paths.value.contains("/v2/calls/cc-7/actions/transfer"))
        XCTAssertTrue(paths.value.contains("/v2/calls/cc-7/actions/hangup"))
    }

    // =================================================================
    // Plivo
    // =================================================================

    func testPlivoBasicAuthAndConfig() {
        let http = FakeHttpTransport()
        _ = PlivoCarrier(http: http, options: PlivoOptions(authId: "MAID", authToken: "sec"))
        XCTAssertEqual(http.baseAddress, URL(string: "https://api.plivo.com"))
        let expected = "Basic " + Data("MAID:sec".utf8).base64EncodedString()
        XCTAssertEqual(http.defaultHeaders["Authorization"], expected)
    }

    func testPlivoProvisionBuysNumber() async throws {
        let http = FakeHttpTransport()
        http.on(.get, where: { $0.contains("/PhoneNumber/?country_iso=ZA") }) { _ in
            .json("{\"objects\":[{\"number\":\"27210003333\",\"monthly_rental_rate\":\"0.80\"}]}")
        }
        http.on(.post, where: { $0.contains("/PhoneNumber/27210003333/") }) { _ in .json("{}") }
        let carrier = PlivoCarrier(http: http, options: PlivoOptions(authId: "AID", authToken: "t"))
        let n = try await carrier.provisionNumber(countryCode: "ZA", areaCode: "21")
        XCTAssertEqual(n.phoneNumber, "27210003333")
        XCTAssertEqual(n.monthlyRecurringCost, Decimal(string: "0.80"))
        let getReq = try XCTUnwrap(http.requests.first { $0.method == .get })
        XCTAssertEqual(getReq.path, "/v1/Account/AID/PhoneNumber/?country_iso=ZA&limit=1&pattern=21")
        // Buy body form-encodes app_id="" (empty value).
        let postReq = try XCTUnwrap(http.requests.first { $0.method == .post })
        XCTAssertEqual(postReq.bodyString, "app_id=")
    }

    func testPlivoDialRequiresAnswerUrlBase() async throws {
        let http = FakeHttpTransport()
        let carrier = PlivoCarrier(http: http, options: PlivoOptions(authId: "AID", authToken: "t"))
        await XCTAssertThrowsErrorAsync(
            try await carrier.dial(fromNumber: "+1", toNumber: "+2", streamUrl: URL(string: "wss://x")!))
    }

    func testPlivoDialComposesAnswerUrlWithStream() async throws {
        let http = FakeHttpTransport()
        http.on(.post, where: { $0 == "/v1/Account/AID/Call/" }) { _ in
            .json("{\"request_uuid\":\"req-1\"}")
        }
        let carrier = PlivoCarrier(http: http, options: PlivoOptions(
            authId: "AID", authToken: "t",
            answerUrlBase: URL(string: "https://host/answer?tenant=abc")!))
        let session = try await carrier.dial(
            fromNumber: "+27210003333", toNumber: "+27821234567",
            streamUrl: URL(string: "wss://host/stream")!,
            options: OutboundDialOptions(ringTimeoutSeconds: 40))
        XCTAssertEqual(session.info.callId, "req-1")
        XCTAssertEqual(session.info.mediaFormat, .mulaw8000)

        let req = try XCTUnwrap(http.lastRequest)
        XCTAssertEqual(req.path, "/v1/Account/AID/Call/")
        let form = parseForm(req.bodyString)
        XCTAssertEqual(form["from"], "+27210003333")
        XCTAssertEqual(form["to"], "+27821234567")
        XCTAssertEqual(form["ring_timeout"], "40")
        // answer_url = base with existing query + &stream=<escaped wss>. After
        // one form-decoding layer, the inner stream value is still escaped by
        // composeAnswerUrl's EscapeDataString, so it reads `stream=wss%3A...`.
        let answer = form["answer_url"] ?? ""
        let expectedAnswer = "https://host/answer?tenant=abc&stream=" +
            TelephonyUri.escapeDataString("wss://host/stream")
        XCTAssertEqual(answer, expectedAnswer)
    }

    func testPlivoComposeAnswerUrlNoExistingQuery() {
        let composed = PlivoCarrier.composeAnswerUrl(
            URL(string: "https://host/answer")!,
            streamUrl: URL(string: "wss://h/s")!)
        // No existing query → single "?stream=..." with escaped value.
        XCTAssertEqual(composed,
            "https://host/answer?stream=" + TelephonyUri.escapeDataString("wss://h/s"))
    }

    func testPlivoHangUpDeletesAndTransferReplaysXml() async throws {
        let http = FakeHttpTransport()
        let deletes = Locked<[String]>([])
        let posts = Locked<[TelephonyHttpRequest]>([])
        http.on(.delete, where: { $0.contains("/Call/") }) { req in
            deletes.mutate { $0.append(req.path) }
            return .json("{}")
        }
        http.on(.post, where: { $0.contains("/Call/") }) { req in
            posts.mutate { $0.append(req) }
            return .json("{}")
        }
        let carrier = PlivoCarrier(http: http, options: PlivoOptions(authId: "AID", authToken: "t"))
        let media = InMemoryMediaStream(callInfo: CallInfo(
            callId: "uuid-3", direction: .inbound, from: "+1", to: "+2",
            carrierId: "plivo", mediaFormat: .mulaw8000, startedAtUtc: Date()),
            initialStatus: .ringing)
        let session = PlivoCallSession(media: media, carrier: carrier)

        try await session.transfer(targetNumber: "+27110000000", mode: .cold)
        XCTAssertEqual(session.status, .transferred)
        // Transfer aleg_url carries the escaped data:application/xml payload.
        let transferReq = try XCTUnwrap(posts.value.first)
        XCTAssertEqual(transferReq.path, "/v1/Account/AID/Call/uuid-3/")
        let expectedXml = "<Response><Dial><Number>+27110000000</Number></Dial></Response>"
        let expectedAleg = "data:application/xml," + TelephonyUri.escapeDataString(expectedXml)
        // The form value is itself form-encoded; decode the outer layer.
        let alegDecoded = parseForm(transferReq.bodyString)["aleg_url"] ?? ""
        XCTAssertEqual(alegDecoded, expectedAleg)

        try await session.hangUp()
        XCTAssertTrue(deletes.value.contains("/v1/Account/AID/Call/uuid-3/"))
    }

    // =================================================================
    // Shared: listNumbers parsing
    // =================================================================

    func testTwilioListNumbersParsesArray() async throws {
        let http = FakeHttpTransport()
        http.on(.get, where: { $0.contains("/IncomingPhoneNumbers.json") }) { _ in
            .json("{\"incoming_phone_numbers\":[{\"phone_number\":\"+271\"},{\"phone_number\":\"+272\"}]}")
        }
        let carrier = TwilioCarrier(http: http, options: TwilioOptions(accountSid: "AC1", authToken: "t"))
        let list = try await carrier.listNumbers()
        XCTAssertEqual(list.map(\.phoneNumber), ["+271", "+272"])
    }

    func testTelnyxListNumbersFailSoftOnError() async throws {
        let http = FakeHttpTransport()
        http.on(.get, where: { _ in true }) { _ in .error(503) }
        let carrier = TelnyxCarrier(http: http, options: TelnyxOptions(apiKey: "K"))
        let list = try await carrier.listNumbers()
        XCTAssertTrue(list.isEmpty)
    }
}
