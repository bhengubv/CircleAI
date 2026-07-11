// IntegrationCalendarTests.swift
//
// Exercises the three calendar connectors against FakeIntegrationHttpTransport,
// plus the pure ICS parse/build helpers on CalDavCalendarConnector and the JSON
// time parsers on the Google/MsGraph connectors. Mirrors the C# under
// src/CircleAI.Integration.Calendar/.

import XCTest
import Foundation
@testable import CircleAI

final class IntegrationCalendarTests: XCTestCase {

    // ── CalDAV ───────────────────────────────────────────────────────────────

    private func caldav(_ http: IIntegrationHttpTransport) -> CalDavCalendarConnector {
        CalDavCalendarConnector(
            opts: CalDavCalendarOptions(
                calendarUri: URL(string: "https://dav.example.com/cal/personal/")!,
                username: "user", password: "pw"),
            http: http)
    }

    func testCalDavProviderIdAndConfigured() {
        let c = caldav(FakeIntegrationHttpTransport())
        XCTAssertEqual(c.providerId, "caldav")
        XCTAssertTrue(c.isConfigured)

        let unconf = CalDavCalendarConnector(
            opts: CalDavCalendarOptions(calendarUri: URL(string: "https://x/")!, username: "", password: ""),
            http: FakeIntegrationHttpTransport())
        XCTAssertFalse(unconf.isConfigured)
    }

    func testCalDavConstructorSetsBasicAuthHeader() {
        let http = FakeIntegrationHttpTransport()
        _ = caldav(http)
        // "user:pw" base64.
        let expected = "Basic " + Data("user:pw".utf8).base64EncodedString()
        XCTAssertEqual(http.defaultHeaders["Authorization"], expected)
    }

    func testCalDavListEventsParsesReportResponse() async throws {
        let http = FakeIntegrationHttpTransport()
        let ics = """
            BEGIN:VCALENDAR
            BEGIN:VEVENT
            UID:evt-1
            SUMMARY:Team Sync
            DESCRIPTION:Weekly
            LOCATION:Room A
            DTSTART:20241002T090000Z
            DTEND:20241002T100000Z
            END:VEVENT
            END:VCALENDAR
            """
        // Wrap the ICS in a multistatus calendar-data element.
        let body = """
            <?xml version="1.0"?>
            <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:response><D:propstat><D:prop>
                <C:calendar-data>\(ics)</C:calendar-data>
              </D:prop></D:propstat></D:response>
            </D:multistatus>
            """
        http.on(.report, urlContains: "/cal/personal/", text: body)

        let events = try await caldav(http).listEvents(
            fromUtc: Date(timeIntervalSince1970: 0), toUtc: Date(timeIntervalSince1970: 1_800_000_000))
        XCTAssertEqual(events.count, 1)
        let e = events[0]
        XCTAssertEqual(e.eventId, "evt-1")
        XCTAssertEqual(e.title, "Team Sync")
        XCTAssertEqual(e.description, "Weekly")
        XCTAssertEqual(e.location, "Room A")
        XCTAssertFalse(e.isAllDay) // 09:00, not midnight
        XCTAssertTrue(e.attendees.isEmpty)
        // The REPORT request body carried the time-range filter.
        XCTAssertTrue(http.lastRequest?.bodyString.contains("time-range") ?? false)
        XCTAssertEqual(http.lastRequest?.headers["Depth"], "1")
    }

    func testCalDavListEventsAllDayHeuristic() async throws {
        let http = FakeIntegrationHttpTransport()
        let ics = """
            BEGIN:VEVENT
            UID:allday
            SUMMARY:Holiday
            DTSTART:20241225
            DTEND:20241226
            END:VEVENT
            """
        http.on(.report, urlContains: "/cal/", text: "<C:calendar-data xmlns:C=\"urn:ietf:params:xml:ns:caldav\">\(ics)</C:calendar-data>")
        let events = try await caldav(http).listEvents(fromUtc: Date(timeIntervalSince1970: 0), toUtc: Date())
        XCTAssertEqual(events.count, 1)
        XCTAssertTrue(events[0].isAllDay)
        XCTAssertNil(events[0].description) // absent DESCRIPTION → nil
    }

    func testCalDavCreateEventPutsIcsAndReturnsUid() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.put, where: { $0.hasSuffix(".ics") }) { _ in .init(statusCode: 201) }
        let ev = CalendarEvent(
            eventId: "", calendarId: "personal", title: "New, Event; test", description: nil, location: nil,
            startUtc: Date(timeIntervalSince1970: 1_700_000_000), endUtc: Date(timeIntervalSince1970: 1_700_003_600),
            isAllDay: false, attendees: [])
        let created = try await caldav(http).createEvent(ev)
        XCTAssertFalse(created.eventId.isEmpty) // generated UID
        let put = try XCTUnwrap(http.lastRequest)
        XCTAssertEqual(put.method, .put)
        XCTAssertTrue(put.url.hasSuffix("\(created.eventId).ics"))
        XCTAssertEqual(put.headers["If-None-Match"], "*")
        // SUMMARY is ICS-escaped (comma + semicolon).
        XCTAssertTrue(put.bodyString.contains("SUMMARY:New\\, Event\\; test"))
        XCTAssertTrue(put.bodyString.contains("BEGIN:VCALENDAR"))
    }

    func testCalDavDeleteTolerates404() async throws {
        let http = FakeIntegrationHttpTransport(defaultResponse: .error(404))
        try await caldav(http).deleteEvent(calendarId: "personal", eventId: "gone")
        XCTAssertEqual(http.lastRequest?.method, .delete)
    }

    func testCalDavDeleteThrowsOnServerError() async {
        let http = FakeIntegrationHttpTransport(defaultResponse: .error(500))
        do {
            try await caldav(http).deleteEvent(calendarId: "personal", eventId: "x")
            XCTFail("expected throw on 500")
        } catch {
            // expected
        }
    }

    func testCalDavDeleteRequiresEventId() async {
        do {
            try await caldav(FakeIntegrationHttpTransport()).deleteEvent(calendarId: "c", eventId: "  ")
            XCTFail("expected argument error")
        } catch IntegrationError.argument { /* ok */ } catch { XCTFail("wrong error \(error)") }
    }

    func testCalDavBuildIcsAndParseRoundTrip() {
        let ev = CalendarEvent(
            eventId: "rt-1", calendarId: "c", title: "Round Trip", description: "line",
            location: "Here", startUtc: Date(timeIntervalSince1970: 1_700_000_000),
            endUtc: Date(timeIntervalSince1970: 1_700_003_600), isAllDay: false, attendees: [])
        let ics = CalDavCalendarConnector.buildIcs(ev)
        let parsed = CalDavCalendarConnector.parseIcs(ics, calendarId: "c")
        XCTAssertEqual(parsed.count, 1)
        XCTAssertEqual(parsed[0].eventId, "rt-1")
        XCTAssertEqual(parsed[0].title, "Round Trip")
        XCTAssertEqual(parsed[0].description, "line")
        XCTAssertEqual(parsed[0].location, "Here")
    }

    // ── Google Calendar ──────────────────────────────────────────────────────

    private func google(_ http: IIntegrationHttpTransport, token: String? = "tok") -> GoogleCalendarConnector {
        GoogleCalendarConnector(
            opts: GoogleCalendarOptions(calendarId: "primary", accessTokenProvider: { token }),
            http: http)
    }

    func testGoogleListEventsParsesItemsSkippingCancelled() async throws {
        let http = FakeIntegrationHttpTransport()
        let json = """
        {"items":[
          {"id":"g1","status":"confirmed","summary":"Lunch","location":"Cafe",
           "start":{"dateTime":"2024-10-02T12:00:00Z"},"end":{"dateTime":"2024-10-02T13:00:00Z"},
           "attendees":[{"email":"a@x.com"},{"email":"b@x.com"}]},
          {"id":"g2","status":"cancelled","summary":"Skip me","start":{"dateTime":"2024-10-02T14:00:00Z"}},
          {"id":"g3","summary":"All day","start":{"date":"2024-12-25"},"end":{"date":"2024-12-26"}}
        ]}
        """
        http.on(.get, urlContains: "/calendar/v3/calendars/primary/events", json: json)

        let events = try await google(http).listEvents(fromUtc: Date(timeIntervalSince1970: 0), toUtc: Date())
        XCTAssertEqual(events.map { $0.eventId }, ["g1", "g3"]) // g2 cancelled skipped
        XCTAssertEqual(events[0].title, "Lunch")
        XCTAssertEqual(events[0].attendees, ["a@x.com", "b@x.com"])
        XCTAssertFalse(events[0].isAllDay)
        XCTAssertTrue(events[1].isAllDay)
        // Auth header applied.
        XCTAssertEqual(http.lastRequest?.url.contains("timeMin=") , true)
        XCTAssertEqual(http.defaultHeaders["Authorization"], "Bearer tok")
    }

    func testGoogleCreateEventPostsAndReturnsId() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.post, urlContains: "/events", json: #"{"id":"created-123"}"#)
        let ev = CalendarEvent(
            eventId: "", calendarId: "primary", title: "Meeting", description: "d", location: "l",
            startUtc: Date(timeIntervalSince1970: 1_700_000_000), endUtc: Date(timeIntervalSince1970: 1_700_003_600),
            isAllDay: false, attendees: ["c@x.com"])
        let created = try await google(http).createEvent(ev)
        XCTAssertEqual(created.eventId, "created-123")
        let post = try XCTUnwrap(http.lastRequest)
        XCTAssertEqual(post.method, .post)
        let body = try IntegrationJson.parseObject(post.body)
        XCTAssertEqual(IntegrationJson.string(body, "summary"), "Meeting")
        let start = try XCTUnwrap(IntegrationJson.object(body, "start"))
        XCTAssertEqual(IntegrationJson.string(start, "timeZone"), "UTC")
    }

    func testGoogleDeleteTolerates410() async throws {
        let http = FakeIntegrationHttpTransport(defaultResponse: .error(410))
        try await google(http).deleteEvent(calendarId: "primary", eventId: "g1")
        XCTAssertEqual(http.lastRequest?.method, .delete)
    }

    func testGoogleAuthFailureThrows() async {
        let http = FakeIntegrationHttpTransport()
        do {
            _ = try await google(http, token: nil).listEvents(fromUtc: Date(), toUtc: Date())
            XCTFail("expected auth failure")
        } catch IntegrationError.invalidOperation { /* ok */ } catch { XCTFail("wrong error \(error)") }
    }

    // ── MS Graph Calendar ────────────────────────────────────────────────────

    private func graph(_ http: IIntegrationHttpTransport, token: String? = "tok") -> MsGraphCalendarConnector {
        MsGraphCalendarConnector(
            opts: MsGraphCalendarOptions(calendarId: "primary", accessTokenProvider: { token }),
            http: http)
    }

    func testGraphListEventsParsesValueArray() async throws {
        let http = FakeIntegrationHttpTransport()
        let json = """
        {"value":[
          {"id":"m1","subject":"Review","bodyPreview":"notes","isAllDay":false,
           "location":{"displayName":"HQ"},
           "start":{"dateTime":"2024-10-02T09:00:00Z"},"end":{"dateTime":"2024-10-02T10:00:00Z"},
           "attendees":[{"emailAddress":{"address":"a@x.com"}}]}
        ]}
        """
        http.on(.get, urlContains: "/me/calendar/calendarView", json: json)
        let events = try await graph(http).listEvents(fromUtc: Date(timeIntervalSince1970: 0), toUtc: Date())
        XCTAssertEqual(events.count, 1)
        XCTAssertEqual(events[0].eventId, "m1")
        XCTAssertEqual(events[0].title, "Review")
        XCTAssertEqual(events[0].location, "HQ")
        XCTAssertEqual(events[0].attendees, ["a@x.com"])
    }

    func testGraphCreateEventReturnsId() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.post, urlContains: "/me/events", json: #"{"id":"ev-9"}"#)
        let ev = CalendarEvent(
            eventId: "", calendarId: "primary", title: "T", description: nil, location: nil,
            startUtc: Date(timeIntervalSince1970: 1_700_000_000), endUtc: Date(timeIntervalSince1970: 1_700_003_600),
            isAllDay: false, attendees: [])
        let created = try await graph(http).createEvent(ev)
        XCTAssertEqual(created.eventId, "ev-9")
    }

    func testGraphDeleteTolerates204() async throws {
        let http = FakeIntegrationHttpTransport(defaultResponse: .init(statusCode: 204))
        try await graph(http).deleteEvent(calendarId: "primary", eventId: "m1")
        XCTAssertEqual(http.lastRequest?.method, .delete)
    }
}
