// IntegrationContractsTests.swift
//
// Covers the integration-layer DTOs added/completed for the connector ports:
//   • Codable round-trips for the NEW Codable DTOs (HaEntity, RouteEstimate,
//     RoutePoint) declared in IntegrationContracts.swift.
//   • Equatable + the withEventId helper on CalendarEvent (declared in
//     ProactiveBriefing.swift, extended in IntegrationCalendar.swift).
//   • EmailMessage / NewsItem / WeatherSample value semantics (these live in
//     ProactiveBriefing.swift and are Sendable+Equatable, not Codable; the
//     news url field is a String, matching Contracts.cs `Uri` mapped to that
//     shared type).
//   • IntegrationServiceArg / IntegrationJsonValue equality + serialisation.
// Mirrors src/CircleAI.Integration/Contracts.cs.

import XCTest
import Foundation
@testable import CircleAI

final class IntegrationContractsTests: XCTestCase {

    private func roundTrip<T: Codable & Equatable>(_ value: T) throws -> T {
        try JSONDecoder().decode(T.self, from: try JSONEncoder().encode(value))
    }

    // ── New Codable DTOs ─────────────────────────────────────────────────────

    func testHaEntityCodableRoundTrip() throws {
        let e = HaEntity(
            entityId: "light.kitchen", friendlyName: "Kitchen", domain: "light",
            state: "on", attributes: ["brightness": "255", "color": "warm"])
        XCTAssertEqual(try roundTrip(e), e)
    }

    func testRouteEstimateCodableRoundTrip() throws {
        let r = RouteEstimate(
            distanceKm: 12.34, duration: 900,
            polyline: [RoutePoint(lat: -26.2, lon: 28.0), RoutePoint(lat: -26.3, lon: 28.1)])
        XCTAssertEqual(try roundTrip(r), r)
    }

    func testRoutePointCodableRoundTrip() throws {
        let p = RoutePoint(lat: 1.5, lon: -2.5)
        XCTAssertEqual(try roundTrip(p), p)
    }

    // ── Existing shared DTOs (value semantics) ───────────────────────────────

    func testCalendarEventEquatableAndWithEventId() {
        let ev = CalendarEvent(
            eventId: "old", calendarId: "c", title: "T", description: "d", location: "l",
            startUtc: Date(timeIntervalSince1970: 5), endUtc: Date(timeIntervalSince1970: 6),
            isAllDay: false, attendees: ["z@x.com"])
        XCTAssertEqual(ev, ev)
        let renamed = ev.withEventId("new")
        XCTAssertEqual(renamed.eventId, "new")
        XCTAssertEqual(renamed.title, "T")
        XCTAssertEqual(renamed.attendees, ["z@x.com"])
        XCTAssertEqual(renamed.startUtc, ev.startUtc)
        XCTAssertNotEqual(renamed, ev) // eventId differs
    }

    func testEmailMessageEquatable() {
        let m = EmailMessage(
            messageId: "m1", from: "s@x.com", to: ["a@x.com"], subject: "Hi",
            bodyText: "body", receivedUtc: Date(timeIntervalSince1970: 12345),
            unread: true, labels: ["INBOX", "UNREAD"])
        let same = EmailMessage(
            messageId: "m1", from: "s@x.com", to: ["a@x.com"], subject: "Hi",
            bodyText: "body", receivedUtc: Date(timeIntervalSince1970: 12345),
            unread: true, labels: ["INBOX", "UNREAD"])
        XCTAssertEqual(m, same)
    }

    func testNewsItemUrlIsStringField() {
        let n = NewsItem(
            itemId: "i1", sourceId: "src", title: "Title", summary: "Summary",
            url: "https://example.com/a",
            publishedUtc: Date(timeIntervalSince1970: 999), tags: ["tech", "ai"])
        XCTAssertEqual(n.url, "https://example.com/a")
        XCTAssertEqual(n.tags, ["tech", "ai"])
    }

    func testWeatherSampleEquatable() {
        let w = WeatherSample(
            atUtc: Date(timeIntervalSince1970: 42), tempC: 21.5, feelsLikeC: 20.0,
            precipMm: 0.2, windKph: 9.0, cloudPct: 40, condition: "partly cloudy")
        XCTAssertEqual(w, w)
    }

    // ── Service args / JSON value ────────────────────────────────────────────

    func testIntegrationServiceArgAndJsonValueEquality() {
        let a = IntegrationServiceArg("entity_id", .string("light.kitchen"))
        let b = IntegrationServiceArg("entity_id", .string("light.kitchen"))
        XCTAssertEqual(a, b)
        XCTAssertEqual(IntegrationJsonValue.int(3), .int(3))
        XCTAssertNotEqual(IntegrationJsonValue.int(3), .double(3))
        XCTAssertEqual(
            IntegrationJsonValue.object([("k", .bool(true))]),
            IntegrationJsonValue.object([("k", .bool(true))]))
    }

    func testJsonValueSerialisesToFoundationObject() throws {
        let v = IntegrationJsonValue.object([
            ("entity_id", .string("light.kitchen")),
            ("brightness", .int(128)),
        ])
        let obj = v.jsonObject
        let dict = try XCTUnwrap(obj as? [String: Any])
        XCTAssertEqual(dict["entity_id"] as? String, "light.kitchen")
        XCTAssertEqual(dict["brightness"] as? Int, 128)
    }
}
