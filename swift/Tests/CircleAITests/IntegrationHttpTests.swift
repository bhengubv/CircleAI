// IntegrationHttpTests.swift
//
// Exercises the injected HTTP transport substrate and shared helpers used by
// every integration connector: FakeIntegrationHttpTransport routing + request
// logging, IntegrationHttpResponse.ensureSuccess, IntegrationUri escaping /
// absolute-or-blank, IntegrationDates parsing/formatting, and IntegrationJson
// readers.

import XCTest
import Foundation
@testable import CircleAI

final class IntegrationHttpTests: XCTestCase {

    // ── FakeIntegrationHttpTransport ─────────────────────────────────────────

    func testFakeTransportRoutesByMethodAndUrlAndLogs() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/hello", json: #"{"ok":true}"#)

        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: "https://x/hello"))
        XCTAssertEqual(resp.statusCode, 200)
        XCTAssertEqual(resp.bodyString, #"{"ok":true}"#)
        XCTAssertEqual(http.requests.count, 1)
        XCTAssertEqual(http.lastRequest?.url, "https://x/hello")
    }

    func testFakeTransportFallsBackToDefaultResponse() async throws {
        let http = FakeIntegrationHttpTransport(defaultResponse: .error(404, "nope"))
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: "https://x/unmatched"))
        XCTAssertEqual(resp.statusCode, 404)
        XCTAssertFalse(resp.isSuccessStatusCode)
    }

    func testFakeTransportMethodMismatchDoesNotMatch() async throws {
        let http = FakeIntegrationHttpTransport(defaultResponse: .error(418))
        http.on(.post, urlContains: "/thing", json: "{}")
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: "https://x/thing"))
        XCTAssertEqual(resp.statusCode, 418) // GET fell through to default
    }

    func testEnsureSuccessThrowsOnNon2xx() {
        XCTAssertThrowsError(try IntegrationHttpResponse.error(500).ensureSuccess()) { err in
            guard case IntegrationError.invalidOperation = err else {
                return XCTFail("expected invalidOperation, got \(err)")
            }
        }
        XCTAssertNoThrow(try IntegrationHttpResponse.json("{}").ensureSuccess())
    }

    func testDefaultHeadersRoundTripOnTransport() {
        let http = FakeIntegrationHttpTransport()
        http.defaultHeaders = ["Authorization": "Bearer abc"]
        XCTAssertEqual(http.defaultHeaders["Authorization"], "Bearer abc")
    }

    // ── IntegrationUri ───────────────────────────────────────────────────────

    func testEscapeDataStringMatchesDotNet() {
        // Space → %20, reserved chars escaped uppercase, unreserved untouched.
        XCTAssertEqual(IntegrationUri.escapeDataString("a b&c"), "a%20b%26c")
        XCTAssertEqual(IntegrationUri.escapeDataString("is:unread"), "is%3Aunread")
        XCTAssertEqual(IntegrationUri.escapeDataString("A-Z_a.z~9"), "A-Z_a.z~9")
    }

    func testAbsoluteOrBlank() {
        XCTAssertEqual(IntegrationUri.absoluteOrBlank("https://e.com/x").absoluteString, "https://e.com/x")
        XCTAssertEqual(IntegrationUri.absoluteOrBlank(nil).absoluteString, "about:blank")
        XCTAssertEqual(IntegrationUri.absoluteOrBlank("").absoluteString, "about:blank")
        // Relative (no scheme) → about:blank (C# UriKind.Absolute guard).
        XCTAssertEqual(IntegrationUri.absoluteOrBlank("/relative/path").absoluteString, "about:blank")
    }

    // ── IntegrationDates ─────────────────────────────────────────────────────

    func testParseUtcHandlesIsoAndRfc1123AndBlank() {
        XCTAssertEqual(IntegrationDates.parseUtc(nil), Date.distantPast)
        XCTAssertEqual(IntegrationDates.parseUtc("   "), Date.distantPast)
        XCTAssertEqual(IntegrationDates.parseUtc("not-a-date"), Date.distantPast)

        // ISO-8601 with fractional seconds and Z.
        let iso = IntegrationDates.parseUtc("2024-10-02T13:00:00.000Z")
        XCTAssertNotEqual(iso, Date.distantPast)
        // ISO-8601 plain.
        XCTAssertNotEqual(IntegrationDates.parseUtc("2024-10-02T13:00:00Z"), Date.distantPast)
        // RFC-1123 (RSS pubDate).
        XCTAssertNotEqual(IntegrationDates.parseUtc("Wed, 02 Oct 2024 13:00:00 GMT"), Date.distantPast)
    }

    func testIsoRoundTripStableToTheSecond() {
        let d = Date(timeIntervalSince1970: 1_700_000_000) // exact second
        let s = IntegrationDates.iso(d)
        let back = IntegrationDates.parseUtc(s)
        XCTAssertEqual(back.timeIntervalSince1970, d.timeIntervalSince1970, accuracy: 0.001)
    }

    func testIcsStampAndParseRoundTrip() {
        let d = Date(timeIntervalSince1970: 1_700_000_000)
        let stamp = IntegrationDates.icsStamp(d)
        XCTAssertEqual(stamp.count, 16) // yyyyMMddTHHmmssZ
        XCTAssertTrue(stamp.hasSuffix("Z"))
        let back = IntegrationDates.parseIcsTime(stamp)
        XCTAssertEqual(back.timeIntervalSince1970, d.timeIntervalSince1970, accuracy: 1.0)
    }

    func testTimeOfDaySecondsZeroAtMidnightUtc() {
        // 1970-01-01T00:00:00Z is midnight.
        XCTAssertEqual(IntegrationDates.timeOfDaySeconds(Date(timeIntervalSince1970: 0)), 0, accuracy: 0.0001)
        // +1h30m.
        XCTAssertEqual(IntegrationDates.timeOfDaySeconds(Date(timeIntervalSince1970: 5400)), 5400, accuracy: 0.0001)
    }

    func testDateOnlyFormatting() {
        // 2023-11-14 is the date for this instant (well after midnight UTC).
        let d = IntegrationDates.parseDateOnlyIso("2023-11-14")!
        XCTAssertEqual(IntegrationDates.dateOnly(d), "2023-11-14")
    }

    // ── IntegrationJson ──────────────────────────────────────────────────────

    func testJsonObjectAndArrayParsing() throws {
        let obj = try IntegrationJson.parseObject(Data(#"{"a":1,"b":"x"}"#.utf8))
        XCTAssertEqual(IntegrationJson.int(obj, "a"), 1)
        XCTAssertEqual(IntegrationJson.string(obj, "b"), "x")

        let arr = try IntegrationJson.parseArray(Data("[1,2,3]".utf8))
        XCTAssertEqual(arr.count, 3)
    }

    func testJsonParseObjectThrowsOnArrayRoot() {
        XCTAssertThrowsError(try IntegrationJson.parseObject(Data("[1]".utf8)))
        XCTAssertThrowsError(try IntegrationJson.parseObject(Data())) // empty
    }

    func testJsonBoolDistinguishesBooleanFromNumber() throws {
        let obj = try IntegrationJson.parseObject(Data(#"{"flag":false,"count":0}"#.utf8))
        XCTAssertEqual(IntegrationJson.bool(obj, "flag"), false)
        // count is a number, not a bool → nil.
        XCTAssertNil(IntegrationJson.bool(obj, "count"))
        // and int does not read the boolean.
        XCTAssertNil(IntegrationJson.int(obj, "flag"))
        XCTAssertEqual(IntegrationJson.int(obj, "count"), 0)
    }

    func testHaAttributeStringifyMatchesCsSwitch() throws {
        let obj = try IntegrationJson.parseObject(Data(#"{"s":"hi","n":42,"f":1.5,"b":true,"arr":[1,2]}"#.utf8))
        XCTAssertEqual(IntegrationJson.haAttributeString(obj["s"]!), "hi")
        XCTAssertEqual(IntegrationJson.haAttributeString(obj["n"]!), "42")
        XCTAssertEqual(IntegrationJson.haAttributeString(obj["b"]!), "true")
        // Non-scalar → JSON text.
        XCTAssertTrue(IntegrationJson.haAttributeString(obj["arr"]!).contains("1"))
    }
}
