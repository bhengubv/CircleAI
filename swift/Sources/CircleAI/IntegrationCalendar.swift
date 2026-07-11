// IntegrationCalendar.swift
//
// Port of the CircleAI.Integration.Calendar vertical (collapsing the C# folder's
// three files into one, per the tree's flat convention):
//   • CalDavCalendarConnector.cs  → CalDavCalendarOptions + CalDavCalendarConnector
//   • GoogleCalendarConnector.cs  → GoogleCalendarOptions + GoogleCalendarConnector
//   • MsGraphCalendarConnector.cs → MsGraphCalendarOptions + MsGraphCalendarConnector
//
// All three are `ICalendarConnector`s. The raw HTTP is the injected
// `IIntegrationHttpTransport`; every URL, XML/JSON body, auth header, and parse
// step is ported verbatim so the wire format is preserved and asserted against
// `FakeIntegrationHttpTransport` (no real calls).
//
// Porting notes:
//   • The Google/MsGraph "access token provider" callback keeps its C# shape as
//     a `@Sendable () async throws -> String?` closure. `EnsureAuthAsync` throws
//     `IntegrationError.invalidOperation` when the token is blank (C#
//     `InvalidOperationException`) and otherwise sets the Bearer default header.
//   • CalDAV's minimal ICS parser/builder is ported line-for-line; the .NET
//     `Regex` is the `NSRegularExpression` analogue. `Escape` and the all-day
//     heuristic match exactly.

import Foundation

// MARK: - CalendarEvent helper

public extension CalendarEvent {
    /// Non-mutating copy with a replaced `eventId` — the Swift analogue of the
    /// C# `ev with { EventId = … }` used by the create paths. (`CalendarEvent`
    /// itself is declared in ProactiveBriefing.swift.)
    func withEventId(_ id: String) -> CalendarEvent {
        CalendarEvent(
            eventId: id, calendarId: calendarId, title: title, description: description,
            location: location, startUtc: startUtc, endUtc: endUtc, isAllDay: isAllDay,
            attendees: attendees)
    }
}

// MARK: - CalDAV

/// CalDAV connector config. Port of the C# `CalDavCalendarOptions` record.
public struct CalDavCalendarOptions: Sendable, Equatable {
    /// Full URL of the calendar collection.
    public let calendarUri: URL
    /// CalDAV username.
    public let username: String
    /// CalDAV password (often an app-specific password).
    public let password: String

    public init(calendarUri: URL, username: String, password: String) {
        self.calendarUri = calendarUri
        self.username = username
        self.password = password
    }
}

/// Generic CalDAV `ICalendarConnector` — covers iCloud, Fastmail, Posteo,
/// Nextcloud, ownCloud, any CalDAV server. Port of the C#
/// `CalDavCalendarConnector`.
public final class CalDavCalendarConnector: ICalendarConnector, @unchecked Sendable {
    private let http: IIntegrationHttpTransport
    private let opts: CalDavCalendarOptions

    public init(opts: CalDavCalendarOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
        // C# sets HTTP Basic in the constructor.
        let raw = "\(opts.username):\(opts.password)"
        let creds = Data(raw.utf8).base64EncodedString()
        var headers = http.defaultHeaders
        headers["Authorization"] = "Basic \(creds)"
        http.defaultHeaders = headers
    }

    public var providerId: String { "caldav" }
    public var isConfigured: Bool { !opts.username.isBlank && !opts.password.isBlank }

    public func listEvents(fromUtc: Date, toUtc: Date) async throws -> [CalendarEvent] {
        // CalDAV REPORT with time-range filter (verbatim XML).
        let xml = """
            <?xml version="1.0" encoding="utf-8" ?>
            <C:calendar-query xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:prop>
                <D:getetag/>
                <C:calendar-data/>
              </D:prop>
              <C:filter>
                <C:comp-filter name="VCALENDAR">
                  <C:comp-filter name="VEVENT">
                    <C:time-range start="\(IntegrationDates.caldavRange(fromUtc))" end="\(IntegrationDates.caldavRange(toUtc))"/>
                  </C:comp-filter>
                </C:comp-filter>
              </C:filter>
            </C:calendar-query>
            """
        let resp = try await http.send(IntegrationHttpRequest(
            method: .report,
            url: opts.calendarUri.absoluteString,
            headers: ["Depth": "1"],
            body: Data(xml.utf8),
            contentType: .xml))
        try resp.ensureSuccess()

        // C# extracts every <calendar-data> element's text, then ICS-parses each.
        let calId = opts.calendarUri.absoluteString
        var result: [CalendarEvent] = []
        for block in Self.extractCalendarData(resp.bodyString) {
            result.append(contentsOf: Self.parseIcs(block, calendarId: calId))
        }
        return result
    }

    public func createEvent(_ ev: CalendarEvent) async throws -> CalendarEvent {
        let uid = ev.eventId.isBlank ? UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased() : ev.eventId
        let withUid = ev.withEventId(uid)
        let ics = Self.buildIcs(withUid)
        let target = opts.calendarUri.appendingPathComponent(uid + ".ics").absoluteString

        let resp = try await http.send(IntegrationHttpRequest(
            method: .put,
            url: target,
            headers: ["If-None-Match": "*"],
            body: Data(ics.utf8),
            contentType: .calendar))
        try resp.ensureSuccess()
        return withUid
    }

    public func deleteEvent(calendarId: String, eventId: String) async throws {
        if eventId.isBlank { throw IntegrationError.argument("eventId required") }
        let target = opts.calendarUri.appendingPathComponent(eventId + ".ics").absoluteString
        let resp = try await http.send(IntegrationHttpRequest(method: .delete, url: target))
        // C#: tolerate 204 / 200 / 404; otherwise EnsureSuccessStatusCode.
        if resp.statusCode != 204 && resp.statusCode != 200 && resp.statusCode != 404 {
            try resp.ensureSuccess()
        }
    }

    // ── XML: pull out every <calendar-data> element's text ────────────────────

    /// Extract the text content of every CalDAV `<calendar-data>` element. The
    /// C# uses XDocument + the caldav namespace; here a namespace-agnostic scan
    /// over the (possibly prefixed) element name is used, which yields the same
    /// ICS blocks for well-formed CalDAV responses.
    static func extractCalendarData(_ xml: String) -> [String] {
        var blocks: [String] = []
        // Match <...:calendar-data ...>BODY</...:calendar-data> or the
        // unprefixed form, non-greedy, dot-matches-newline.
        let pattern = "<(?:[A-Za-z0-9]+:)?calendar-data[^>]*>(.*?)</(?:[A-Za-z0-9]+:)?calendar-data>"
        guard let rx = try? NSRegularExpression(pattern: pattern, options: [.dotMatchesLineSeparators]) else {
            return blocks
        }
        let ns = xml as NSString
        for m in rx.matches(in: xml, options: [], range: NSRange(location: 0, length: ns.length)) {
            if m.numberOfRanges >= 2 {
                let inner = ns.substring(with: m.range(at: 1))
                blocks.append(Self.xmlDecode(inner))
            }
        }
        return blocks
    }

    /// Decode the handful of XML entities CalDAV servers escape inside
    /// calendar-data text (&amp; &lt; &gt; &quot; &#13; &#10;).
    static func xmlDecode(_ s: String) -> String {
        var out = s
        out = out.replacingOccurrences(of: "&#13;", with: "\r")
        out = out.replacingOccurrences(of: "&#10;", with: "\n")
        out = out.replacingOccurrences(of: "&quot;", with: "\"")
        out = out.replacingOccurrences(of: "&apos;", with: "'")
        out = out.replacingOccurrences(of: "&lt;", with: "<")
        out = out.replacingOccurrences(of: "&gt;", with: ">")
        out = out.replacingOccurrences(of: "&amp;", with: "&")
        return out
    }

    // ── Minimal ICS parser (verbatim port) ───────────────────────────────────

    static func parseIcs(_ ics: String, calendarId: String) -> [CalendarEvent] {
        if ics.isBlank { return [] }
        var events: [CalendarEvent] = []
        let ns = ics as NSString
        guard let rxEvent = try? NSRegularExpression(
            pattern: "BEGIN:VEVENT(.*?)END:VEVENT",
            options: [.dotMatchesLineSeparators]) else { return events }

        for m in rxEvent.matches(in: ics, options: [], range: NSRange(location: 0, length: ns.length)) {
            guard m.numberOfRanges >= 2 else { continue }
            let body = ns.substring(with: m.range(at: 1))

            func get(_ key: String) -> String {
                // ^KEY(?:;[^:]*)?:(.*)$ per line — allow a parameters segment.
                // `.anchorsMatchLines` makes ^/$ match at line boundaries (the
                // C# `(?m)` inline flag).
                let pattern = "^\(NSRegularExpression.escapedPattern(for: key))(?:;[^:]*)?:(.*)$"
                guard let rx = try? NSRegularExpression(pattern: pattern, options: [.anchorsMatchLines]) else { return "" }
                let bns = body as NSString
                guard let hit = rx.firstMatch(in: body, options: [], range: NSRange(location: 0, length: bns.length)),
                      hit.numberOfRanges >= 2 else { return "" }
                return bns.substring(with: hit.range(at: 1)).trimmingCharacters(in: .whitespacesAndNewlines)
            }
            func time(_ key: String) -> Date {
                let v = get(key)
                if v.isEmpty { return IntegrationDates.minValue }
                return IntegrationDates.parseIcsTime(v)
            }

            let uid = get("UID")
            let title = get("SUMMARY")
            let desc = get("DESCRIPTION")
            let loc = get("LOCATION")
            let startUtc = time("DTSTART")
            let endUtc = time("DTEND")
            let isAllDay = startUtc != IntegrationDates.minValue
                && IntegrationDates.timeOfDaySeconds(startUtc) == 0
                && IntegrationDates.timeOfDaySeconds(endUtc) == 0

            events.append(CalendarEvent(
                eventId: uid,
                calendarId: calendarId,
                title: title,
                description: desc.isEmpty ? nil : desc,
                location: loc.isEmpty ? nil : loc,
                startUtc: startUtc,
                endUtc: endUtc,
                isAllDay: isAllDay,
                attendees: []))
        }
        return events
    }

    static func buildIcs(_ ev: CalendarEvent) -> String {
        let dtStamp = IntegrationDates.icsStamp(Date())
        let dtStart = IntegrationDates.icsStamp(ev.startUtc)
        let dtEnd = IntegrationDates.icsStamp(ev.endUtc)
        var lines: [String] = []
        lines.append("BEGIN:VCALENDAR")
        lines.append("VERSION:2.0")
        lines.append("PRODID:-//CircleAI//Calendar//EN")
        lines.append("BEGIN:VEVENT")
        lines.append("UID:\(ev.eventId)")
        lines.append("DTSTAMP:\(dtStamp)")
        lines.append("DTSTART:\(dtStart)")
        lines.append("DTEND:\(dtEnd)")
        lines.append("SUMMARY:\(escape(ev.title))")
        if let d = ev.description, !d.isEmpty { lines.append("DESCRIPTION:\(escape(d))") }
        if let l = ev.location, !l.isEmpty { lines.append("LOCATION:\(escape(l))") }
        lines.append("END:VEVENT")
        lines.append("END:VCALENDAR")
        // C# uses StringBuilder.AppendLine → each line + Environment.NewLine, then
        // a trailing newline. Reproduce the trailing terminator with a final join.
        return lines.joined(separator: "\r\n") + "\r\n"
    }

    static func escape(_ s: String) -> String {
        s.replacingOccurrences(of: "\\", with: "\\\\")
            .replacingOccurrences(of: "\n", with: "\\n")
            .replacingOccurrences(of: ",", with: "\\,")
            .replacingOccurrences(of: ";", with: "\\;")
    }
}

// MARK: - Google Calendar

/// Google Calendar connector config. Port of the C# `GoogleCalendarOptions`.
public struct GoogleCalendarOptions: Sendable {
    /// Async callback returning a fresh Bearer token.
    public let accessTokenProvider: @Sendable () async throws -> String?
    /// Calendar to read/write. Default "primary".
    public let calendarId: String

    public init(
        calendarId: String = "primary",
        accessTokenProvider: @escaping @Sendable () async throws -> String?
    ) {
        self.calendarId = calendarId
        self.accessTokenProvider = accessTokenProvider
    }
}

/// Google Calendar v3 `ICalendarConnector`. Port of the C#
/// `GoogleCalendarConnector`. The host owns the OAuth flow; this lifts events
/// through the v3 REST API.
public final class GoogleCalendarConnector: ICalendarConnector, @unchecked Sendable {
    static let baseUri = "https://www.googleapis.com/calendar/v3/"
    private let http: IIntegrationHttpTransport
    private let opts: GoogleCalendarOptions

    public init(opts: GoogleCalendarOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
        if http.baseAddress == nil { http.baseAddress = URL(string: Self.baseUri) }
    }

    public var providerId: String { "google-calendar" }
    /// The C# `IsConfigured => _opts.AccessTokenProvider is not null`. A Swift
    /// non-optional closure is always present, so this is always true.
    public var isConfigured: Bool { true }

    public func listEvents(fromUtc: Date, toUtc: Date) async throws -> [CalendarEvent] {
        try await ensureAuth()
        let path = "calendars/\(IntegrationUri.escapeDataString(opts.calendarId))/events"
            + "?timeMin=\(IntegrationUri.escapeDataString(IntegrationDates.iso(fromUtc)))"
            + "&timeMax=\(IntegrationUri.escapeDataString(IntegrationDates.iso(toUtc)))"
            + "&singleEvents=true&orderBy=startTime&maxResults=250"
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: Self.baseUri + path))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)

        var list: [CalendarEvent] = []
        guard let items = IntegrationJson.array(doc, "items") else { return list }
        for case let ev as [String: Any] in items {
            if let status = IntegrationJson.string(ev, "status"), status == "cancelled" { continue }
            let (startUtc, isAllDay) = Self.parseTime(ev, "start")
            let (endUtc, _) = Self.parseTime(ev, "end")
            var attendees: [String] = []
            if let atts = IntegrationJson.array(ev, "attendees") {
                for case let a as [String: Any] in atts {
                    // C#: only when the "email" property exists → GetString() ?? "".
                    if a.keys.contains("email") { attendees.append(IntegrationJson.string(a, "email") ?? "") }
                }
            }
            list.append(CalendarEvent(
                eventId: IntegrationJson.string(ev, "id") ?? "",
                calendarId: opts.calendarId,
                title: IntegrationJson.string(ev, "summary") ?? "",
                description: IntegrationJson.string(ev, "description"),
                location: IntegrationJson.string(ev, "location"),
                startUtc: startUtc,
                endUtc: endUtc,
                isAllDay: isAllDay,
                attendees: attendees))
        }
        return list
    }

    public func createEvent(_ ev: CalendarEvent) async throws -> CalendarEvent {
        try await ensureAuth()
        var body: [String: Any] = [
            "summary": ev.title,
            "attendees": ev.attendees.map { ["email": $0] },
        ]
        if let d = ev.description { body["description"] = d }
        if let l = ev.location { body["location"] = l }
        if ev.isAllDay {
            body["start"] = ["date": IntegrationDates.dateOnly(ev.startUtc)]
            body["end"] = ["date": IntegrationDates.dateOnly(ev.endUtc)]
        } else {
            body["start"] = ["dateTime": IntegrationDates.iso(ev.startUtc), "timeZone": "UTC"]
            body["end"] = ["dateTime": IntegrationDates.iso(ev.endUtc), "timeZone": "UTC"]
        }
        let resp = try await http.send(IntegrationHttpRequest(
            method: .post,
            url: Self.baseUri + "calendars/\(IntegrationUri.escapeDataString(ev.calendarId))/events",
            body: try IntegrationJson.encode(body),
            contentType: .json))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)
        return ev.withEventId(IntegrationJson.string(doc, "id") ?? "")
    }

    public func deleteEvent(calendarId: String, eventId: String) async throws {
        if calendarId.isBlank { throw IntegrationError.argument("calendarId required") }
        if eventId.isBlank { throw IntegrationError.argument("eventId required") }
        try await ensureAuth()
        let resp = try await http.send(IntegrationHttpRequest(
            method: .delete,
            url: Self.baseUri + "calendars/\(IntegrationUri.escapeDataString(calendarId))/events/\(IntegrationUri.escapeDataString(eventId))"))
        // C#: tolerate 204 / 410 (Gone); otherwise EnsureSuccessStatusCode.
        if resp.statusCode != 204 && resp.statusCode != 410 {
            try resp.ensureSuccess()
        }
    }

    private func ensureAuth() async throws {
        let token = try await opts.accessTokenProvider()
        guard let token, !token.isBlank else {
            throw IntegrationError.invalidOperation("Google Calendar access token unavailable; refresh OAuth.")
        }
        var headers = http.defaultHeaders
        headers["Authorization"] = "Bearer \(token)"
        http.defaultHeaders = headers
    }

    static func parseTime(_ parent: [String: Any], _ property: String) -> (Date, Bool) {
        guard let node = IntegrationJson.object(parent, property) else { return (IntegrationDates.minValue, false) }
        if let dt = IntegrationJson.string(node, "dateTime") {
            let d = IntegrationDates.parseUtc(dt)
            if d != IntegrationDates.minValue { return (d, false) }
        }
        if let dOnly = IntegrationJson.string(node, "date"), let date = IntegrationDates.parseDateOnlyIso(dOnly) {
            return (date, true)
        }
        return (IntegrationDates.minValue, false)
    }
}

// MARK: - Microsoft Graph Calendar

/// MS Graph calendar connector config. Port of the C# `MsGraphCalendarOptions`.
public struct MsGraphCalendarOptions: Sendable {
    /// Async callback returning a fresh Bearer token.
    public let accessTokenProvider: @Sendable () async throws -> String?
    /// Calendar to read/write. Default "primary".
    public let calendarId: String

    public init(
        calendarId: String = "primary",
        accessTokenProvider: @escaping @Sendable () async throws -> String?
    ) {
        self.calendarId = calendarId
        self.accessTokenProvider = accessTokenProvider
    }
}

/// Microsoft Graph 1.0 `ICalendarConnector` for Outlook / Microsoft 365. Port of
/// the C# `MsGraphCalendarConnector`.
public final class MsGraphCalendarConnector: ICalendarConnector, @unchecked Sendable {
    static let baseUri = "https://graph.microsoft.com/v1.0/"
    private let http: IIntegrationHttpTransport
    private let opts: MsGraphCalendarOptions

    public init(opts: MsGraphCalendarOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
        if http.baseAddress == nil { http.baseAddress = URL(string: Self.baseUri) }
    }

    public var providerId: String { "ms-graph-calendar" }
    public var isConfigured: Bool { true }

    public func listEvents(fromUtc: Date, toUtc: Date) async throws -> [CalendarEvent] {
        try await ensureAuth()
        let path = "me/calendar/calendarView"
            + "?startDateTime=\(IntegrationUri.escapeDataString(IntegrationDates.iso(fromUtc)))"
            + "&endDateTime=\(IntegrationUri.escapeDataString(IntegrationDates.iso(toUtc)))"
            + "&$top=250&$orderby=start/dateTime"
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: Self.baseUri + path))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)

        var list: [CalendarEvent] = []
        guard let arr = IntegrationJson.array(doc, "value") else { return list }
        for case let ev as [String: Any] in arr {
            var attendees: [String] = []
            if let atts = IntegrationJson.array(ev, "attendees") {
                for case let a as [String: Any] in atts {
                    if let em = IntegrationJson.object(a, "emailAddress"), let addr = IntegrationJson.string(em, "address") {
                        attendees.append(addr)
                    }
                }
            }
            let startUtc = Self.parseGraphTime(ev, "start")
            let endUtc = Self.parseGraphTime(ev, "end")
            let allDay = IntegrationJson.bool(ev, "isAllDay") ?? false
            var location: String? = nil
            if let loc = IntegrationJson.object(ev, "location") { location = IntegrationJson.string(loc, "displayName") }
            list.append(CalendarEvent(
                eventId: IntegrationJson.string(ev, "id") ?? "",
                calendarId: opts.calendarId,
                title: IntegrationJson.string(ev, "subject") ?? "",
                description: IntegrationJson.string(ev, "bodyPreview"),
                location: location,
                startUtc: startUtc,
                endUtc: endUtc,
                isAllDay: allDay,
                attendees: attendees))
        }
        return list
    }

    public func createEvent(_ ev: CalendarEvent) async throws -> CalendarEvent {
        try await ensureAuth()
        let body: [String: Any] = [
            "subject": ev.title,
            "body": ["contentType": "text", "content": ev.description ?? ""],
            "start": ["dateTime": IntegrationDates.iso(ev.startUtc), "timeZone": "UTC"],
            "end": ["dateTime": IntegrationDates.iso(ev.endUtc), "timeZone": "UTC"],
            "isAllDay": ev.isAllDay,
            "location": ["displayName": ev.location ?? ""],
            "attendees": ev.attendees.map { ["emailAddress": ["address": $0], "type": "required"] },
        ]
        let resp = try await http.send(IntegrationHttpRequest(
            method: .post, url: Self.baseUri + "me/events",
            body: try IntegrationJson.encode(body), contentType: .json))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)
        return ev.withEventId(IntegrationJson.string(doc, "id") ?? "")
    }

    public func deleteEvent(calendarId: String, eventId: String) async throws {
        if eventId.isBlank { throw IntegrationError.argument("eventId required") }
        try await ensureAuth()
        let resp = try await http.send(IntegrationHttpRequest(
            method: .delete, url: Self.baseUri + "me/events/\(IntegrationUri.escapeDataString(eventId))"))
        if resp.statusCode != 204 { try resp.ensureSuccess() }
    }

    private func ensureAuth() async throws {
        let token = try await opts.accessTokenProvider()
        guard let token, !token.isBlank else {
            throw IntegrationError.invalidOperation("Microsoft Graph access token unavailable; refresh OAuth.")
        }
        var headers = http.defaultHeaders
        headers["Authorization"] = "Bearer \(token)"
        http.defaultHeaders = headers
    }

    static func parseGraphTime(_ parent: [String: Any], _ property: String) -> Date {
        guard let node = IntegrationJson.object(parent, property),
              let dt = IntegrationJson.string(node, "dateTime"), !dt.isEmpty else {
            return IntegrationDates.minValue
        }
        return IntegrationDates.parseUtc(dt)
    }
}
