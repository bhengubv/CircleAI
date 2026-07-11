// IntegrationHttp.swift
//
// Injectable HTTP transport + shared low-level helpers for the external
// integration connectors (Calendar / Email / Geo / HomeAssistant / News).
//
// The C# connectors (CalDav, Google, MsGraph, Gmail, OpenMeteo, OSRM,
// HomeAssistant, Bluesky, Mastodon, NewsApi, Rss) all talk to remote HTTP APIs
// via a shared System.Net.Http.HttpClient. Per the port's "in-memory only;
// inject external/native/cloud/socket dependencies behind interfaces" rule, the
// raw HTTP call is abstracted behind `IIntegrationHttpTransport`. Each
// connector's URL construction, body building, auth header, and response
// parsing is ported verbatim and exercised against a deterministic in-memory
// fake (`FakeIntegrationHttpTransport`) — no sockets, no real calls.
//
// This mirrors HttpClient closely enough to preserve wire formats:
//   • `send(_:)` takes a fully-formed `IntegrationHttpRequest` (method, absolute
//     or base-relative URL, headers, body) and returns an
//     `IntegrationHttpResponse` (status, headers, body bytes).
//   • A default header bag (Authorization) is applied per-connector, matching
//     `HttpClient.DefaultRequestHeaders.Authorization`.
//
// The design deliberately parallels TelephonyHttp.swift so the two verticals
// read the same way, but keeps its own type names to avoid coupling.

import Foundation

// MARK: - Errors

/// Errors the integration connectors raise. Parallels the exceptions the C#
/// throws — `ArgumentException` / `ArgumentOutOfRangeException` /
/// `InvalidOperationException` / a failed `EnsureSuccessStatusCode`.
public enum IntegrationError: Error, Equatable, Sendable {
    /// A bad argument (C# `ArgumentException` / `ArgumentNullException`).
    case argument(String)
    /// An out-of-range argument (C# `ArgumentOutOfRangeException`).
    case argumentOutOfRange(String)
    /// An invalid operation / non-2xx response (C# `InvalidOperationException`
    /// or a failed `EnsureSuccessStatusCode`).
    case invalidOperation(String)
}

// MARK: - HTTP method

/// HTTP verb. Mirrors the subset the connectors use (GET / POST / PUT / PATCH /
/// DELETE / REPORT — the last for CalDAV).
public enum IntegrationHttpMethod: String, Sendable, Equatable {
    case get = "GET"
    case post = "POST"
    case put = "PUT"
    case patch = "PATCH"
    case delete = "DELETE"
    case report = "REPORT"
}

// MARK: - Content type

/// Body content type. Mirrors the flavours the connectors emit.
public enum IntegrationHttpContentType: String, Sendable, Equatable {
    case json = "application/json"
    case xml = "application/xml"
    case calendar = "text/calendar"
    case none = ""
}

// MARK: - Request

/// One HTTP request. `url` is resolved against the transport's base address
/// exactly like `HttpClient` resolves a relative request URI against
/// `BaseAddress` when it is not absolute.
public struct IntegrationHttpRequest: Sendable, Equatable {
    public let method: IntegrationHttpMethod
    /// Absolute URL, or a path resolved against the transport base address.
    public let url: String
    /// Extra per-request headers (default headers are merged in by the transport).
    public let headers: [String: String]
    /// Raw request body bytes. Empty for bodyless GET/DELETE.
    public let body: Data
    /// Declared content type of `body`.
    public let contentType: IntegrationHttpContentType

    public init(
        method: IntegrationHttpMethod,
        url: String,
        headers: [String: String] = [:],
        body: Data = Data(),
        contentType: IntegrationHttpContentType = .none
    ) {
        self.method = method
        self.url = url
        self.headers = headers
        self.body = body
        self.contentType = contentType
    }

    /// The body decoded as UTF-8 (for assertions / form parsing).
    public var bodyString: String {
        String(data: body, encoding: .utf8) ?? ""
    }
}

// MARK: - Response

/// One HTTP response. Mirrors `HttpResponseMessage`: a status code, headers, and
/// a body. `isSuccessStatusCode` matches HttpClient's 200–299 test.
public struct IntegrationHttpResponse: Sendable, Equatable {
    public let statusCode: Int
    public let headers: [String: String]
    public let body: Data

    public init(statusCode: Int, headers: [String: String] = [:], body: Data = Data()) {
        self.statusCode = statusCode
        self.headers = headers
        self.body = body
    }

    /// True for 2xx. Mirrors `HttpResponseMessage.IsSuccessStatusCode`.
    public var isSuccessStatusCode: Bool { statusCode >= 200 && statusCode < 300 }

    /// The body decoded as UTF-8.
    public var bodyString: String {
        String(data: body, encoding: .utf8) ?? ""
    }

    /// Mirror of `HttpResponseMessage.EnsureSuccessStatusCode()`: throw on a
    /// non-2xx status.
    public func ensureSuccess() throws {
        if !isSuccessStatusCode {
            throw IntegrationError.invalidOperation(
                "Response status code does not indicate success: \(statusCode).")
        }
    }

    /// Convenience: a 2xx response with a UTF-8 JSON string body.
    public static func json(_ body: String, statusCode: Int = 200) -> IntegrationHttpResponse {
        IntegrationHttpResponse(
            statusCode: statusCode,
            headers: ["Content-Type": "application/json"],
            body: Data(body.utf8))
    }

    /// Convenience: a 2xx response with a UTF-8 text/xml body.
    public static func text(_ body: String, statusCode: Int = 200) -> IntegrationHttpResponse {
        IntegrationHttpResponse(
            statusCode: statusCode,
            headers: ["Content-Type": "text/plain"],
            body: Data(body.utf8))
    }

    /// Convenience: an error response with a plain-text body.
    public static func error(_ statusCode: Int, _ body: String = "") -> IntegrationHttpResponse {
        IntegrationHttpResponse(statusCode: statusCode, headers: [:], body: Data(body.utf8))
    }
}

// MARK: - Transport

/// Injectable HTTP transport. Real deployments back this with URLSession (or a
/// bridged native client); tests back it with `FakeIntegrationHttpTransport`.
/// Mirrors the role of `HttpClient` in the C# connectors.
public protocol IIntegrationHttpTransport: AnyObject, Sendable {
    /// The base address requests are resolved against when a request URL is not
    /// absolute. `nil` until assigned — mirrors `HttpClient.BaseAddress`.
    var baseAddress: URL? { get set }

    /// Default request headers merged into every outgoing request. Mirrors
    /// `HttpClient.DefaultRequestHeaders` (notably `Authorization`).
    var defaultHeaders: [String: String] { get set }

    /// Send one request and await the response. Throws only on a genuine
    /// transport failure (mirrors `HttpClient.SendAsync` throwing on a network
    /// error, not on a non-2xx status — non-2xx surfaces via the response).
    func send(_ request: IntegrationHttpRequest) async throws -> IntegrationHttpResponse
}

// MARK: - FakeIntegrationHttpTransport

/// Deterministic in-memory `IIntegrationHttpTransport`. Records every request in
/// order and replies from a scripted response table — the substrate the
/// connector ports are tested against without touching a socket.
///
/// Matching order: for each incoming request the fake looks for a scripted
/// handler whose (method, URL-predicate) matches; if none matches it falls back
/// to the default response (200 `{}` unless overridden). The full request log is
/// exposed for wire-format assertions.
public final class FakeIntegrationHttpTransport: IIntegrationHttpTransport, @unchecked Sendable {

    /// One scripted route: match on method + a URL predicate → a response
    /// (optionally computed from the request so a test can echo values back).
    public struct Route: @unchecked Sendable {
        public let method: IntegrationHttpMethod
        public let matches: @Sendable (String) -> Bool
        public let respond: @Sendable (IntegrationHttpRequest) -> IntegrationHttpResponse

        public init(
            method: IntegrationHttpMethod,
            matches: @escaping @Sendable (String) -> Bool,
            respond: @escaping @Sendable (IntegrationHttpRequest) -> IntegrationHttpResponse
        ) {
            self.method = method
            self.matches = matches
            self.respond = respond
        }
    }

    private let lock = NSLock()
    private var _baseAddress: URL?
    private var _defaultHeaders: [String: String] = [:]
    private var routes: [Route] = []
    private var log: [IntegrationHttpRequest] = []
    private let defaultResponse: IntegrationHttpResponse

    public init(defaultResponse: IntegrationHttpResponse = .json("{}")) {
        self.defaultResponse = defaultResponse
    }

    public var baseAddress: URL? {
        get { lock.lock(); defer { lock.unlock() }; return _baseAddress }
        set { lock.lock(); _baseAddress = newValue; lock.unlock() }
    }

    public var defaultHeaders: [String: String] {
        get { lock.lock(); defer { lock.unlock() }; return _defaultHeaders }
        set { lock.lock(); _defaultHeaders = newValue; lock.unlock() }
    }

    /// The requests seen so far, in send order. Snapshot copy.
    public var requests: [IntegrationHttpRequest] {
        lock.lock(); defer { lock.unlock() }
        return log
    }

    /// The most recent request, or nil if none.
    public var lastRequest: IntegrationHttpRequest? {
        lock.lock(); defer { lock.unlock() }
        return log.last
    }

    /// Register a scripted route (checked in registration order).
    public func on(
        _ method: IntegrationHttpMethod,
        where matches: @escaping @Sendable (String) -> Bool,
        respond: @escaping @Sendable (IntegrationHttpRequest) -> IntegrationHttpResponse
    ) {
        lock.lock(); routes.append(Route(method: method, matches: matches, respond: respond)); lock.unlock()
    }

    /// Register a scripted route matching a fixed status + JSON body for any URL
    /// of the given method that contains `urlContains`.
    public func on(
        _ method: IntegrationHttpMethod,
        urlContains: String,
        json: String,
        statusCode: Int = 200
    ) {
        on(method, where: { $0.contains(urlContains) }) { _ in
            .json(json, statusCode: statusCode)
        }
    }

    /// Register a scripted route replying with a text/xml body for any URL of
    /// the given method that contains `urlContains`.
    public func on(
        _ method: IntegrationHttpMethod,
        urlContains: String,
        text: String,
        statusCode: Int = 200
    ) {
        on(method, where: { $0.contains(urlContains) }) { _ in
            .text(text, statusCode: statusCode)
        }
    }

    public func send(_ request: IntegrationHttpRequest) async throws -> IntegrationHttpResponse {
        lock.lock()
        log.append(request)
        let matched = routes.first { $0.method == request.method && $0.matches(request.url) }
        let fallback = defaultResponse
        lock.unlock()
        if let matched {
            return matched.respond(request)
        }
        return fallback
    }
}

// MARK: - IntegrationUri

/// URI escaping matching the .NET APIs the connectors call.
public enum IntegrationUri {

    /// Mirror of `System.Uri.EscapeDataString`: percent-encode everything except
    /// the RFC 3986 "unreserved" set `A–Z a–z 0–9 - . _ ~`. Space → `%20`.
    /// Bytes are UTF-8; hex digits are UPPERCASE.
    public static func escapeDataString(_ s: String) -> String {
        let unreserved = Set("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~".unicodeScalars)
        var out = ""
        for byte in Array(s.utf8) {
            let scalar = Unicode.Scalar(byte)
            if unreserved.contains(scalar) {
                out.unicodeScalars.append(scalar)
            } else {
                out += String(format: "%%%02X", byte)
            }
        }
        return out
    }

    /// Mirror of the C# `Uri.TryCreate(s, UriKind.Absolute, out _)` guard used by
    /// the news sources: returns the parsed absolute URL, or `about:blank` when
    /// the string is empty or not an absolute URL.
    public static func absoluteOrBlank(_ s: String?) -> URL {
        URL(string: absoluteOrBlankString(s)) ?? URL(string: "about:blank")!
    }

    /// String form of `absoluteOrBlank` — the news sources store `NewsItem.url`
    /// as a `String`. Returns the input when it parses as an absolute URI (has a
    /// scheme + host), otherwise "about:blank" (the C# `Uri.TryCreate(...,
    /// Absolute) ? ux : new Uri("about:blank")` behaviour).
    public static func absoluteOrBlankString(_ s: String?) -> String {
        guard let s, !s.isBlank, let u = URL(string: s), u.scheme != nil, !(u.host ?? "").isEmpty else {
            return "about:blank"
        }
        return s
    }
}

// MARK: - IntegrationDates

/// Date parsing/formatting that stands in for `System.DateTimeOffset` +
/// `System.Globalization` in the connectors. `DateTimeOffset.MinValue` maps to
/// `Date.distantPast` (the tree sentinel).
public enum IntegrationDates {

    /// The sentinel used wherever the C# returns `DateTimeOffset.MinValue`.
    public static let minValue: Date = .distantPast

    private static let isoFractional: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    private static let isoPlain: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        return f
    }()

    /// RFC-1123 ("Wed, 02 Oct 2024 13:00:00 GMT") — the shape RSS `pubDate`
    /// commonly uses; `DateTimeOffset.TryParse` accepts it.
    private static let rfc1123: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        return f
    }()

    /// Compact basic-ISO ("yyyyMMdd'T'HHmmss'Z'") — the shape ICS DTSTART/DTEND
    /// carry (`DateTimeOffset.TryParseExact(..., "yyyyMMddTHHmmssZ", ...)`).
    private static let icsDateTime: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyyMMdd'T'HHmmss'Z'"
        return f
    }()

    /// Compact basic-ISO date-only ("yyyyMMdd") — the all-day ICS value.
    private static let icsDateOnly: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyyMMdd"
        return f
    }()

    /// Best-effort UTC parse mirroring `DateTimeOffset.TryParse(...
    /// AssumeUniversal).ToUniversalTime()`. Returns `Date.distantPast` when the
    /// value is blank or unparseable, matching the C# `? ... : MinValue`.
    public static func parseUtc(_ s: String?) -> Date {
        guard let s, !s.isBlank else { return minValue }
        if let d = isoFractional.date(from: s) { return d }
        if let d = isoPlain.date(from: s) { return d }
        if let d = rfc1123.date(from: s) { return d }
        // A bare date "yyyy-MM-dd" (Google all-day `date`) — parse as midnight UTC.
        if let d = parseDateOnlyIso(s) { return d }
        return minValue
    }

    /// Parse a compact ICS DTSTART/DTEND value: try "yyyyMMddTHHmmssZ" then the
    /// all-day "yyyyMMdd". Returns `Date.distantPast` when neither matches.
    public static func parseIcsTime(_ s: String?) -> Date {
        guard let s, !s.isBlank else { return minValue }
        if let d = icsDateTime.date(from: s) { return d }
        if let d = icsDateOnly.date(from: s) { return d }
        return minValue
    }

    /// Parse a bare "yyyy-MM-dd" as midnight UTC (Google Calendar all-day).
    public static func parseDateOnlyIso(_ s: String) -> Date? {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd"
        return f.date(from: s)
    }

    /// Format an instant as round-trip ISO-8601 ("O") — the shape the connectors
    /// send in query strings / request bodies. Always UTC, fractional seconds.
    public static func iso(_ date: Date) -> String {
        isoFractional.string(from: date)
    }

    /// Format an instant as compact ICS ("yyyyMMddTHHmmssZ").
    public static func icsStamp(_ date: Date) -> String {
        icsDateTime.string(from: date)
    }

    /// Format an instant as bare UTC date ("yyyy-MM-dd") for all-day payloads.
    public static func dateOnly(_ date: Date) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd"
        return f.string(from: date)
    }

    /// CalDAV time-range value ("yyyyMMddTHHmmssZ"), identical to `icsStamp`.
    public static func caldavRange(_ date: Date) -> String { icsStamp(date) }

    /// The wall-clock time-of-day (seconds since midnight, UTC) of an instant —
    /// the Swift form of `DateTimeOffset.TimeOfDay`.
    public static func timeOfDaySeconds(_ date: Date) -> TimeInterval {
        var cal = Calendar(identifier: .gregorian)
        cal.timeZone = TimeZone(identifier: "UTC")!
        let c = cal.dateComponents([.hour, .minute, .second, .nanosecond], from: date)
        return TimeInterval(c.hour ?? 0) * 3600 + TimeInterval(c.minute ?? 0) * 60
            + TimeInterval(c.second ?? 0) + TimeInterval(c.nanosecond ?? 0) / 1_000_000_000
    }
}

// MARK: - IntegrationJson

/// Thin JSON read/write helpers over `JSONSerialization`, standing in for
/// `System.Text.Json.JsonDocument`. Objects are `[String: Any]`, arrays are
/// `[Any]`, matching the untyped element traversal the connectors do.
public enum IntegrationJson {

    /// Parse a JSON body into a dictionary. Throws
    /// `IntegrationError.invalidOperation` when the payload is not a JSON object.
    public static func parseObject(_ data: Data) throws -> [String: Any] {
        guard !data.isEmpty else {
            throw IntegrationError.invalidOperation("Empty JSON response.")
        }
        let obj = try JSONSerialization.jsonObject(with: data, options: [])
        guard let dict = obj as? [String: Any] else {
            throw IntegrationError.invalidOperation("Expected a JSON object at the response root.")
        }
        return dict
    }

    /// Parse a JSON body into a top-level array. Throws when the payload is not
    /// a JSON array (some endpoints, e.g. HomeAssistant `/api/states` and the
    /// Mastodon timeline, return a bare array).
    public static func parseArray(_ data: Data) throws -> [Any] {
        guard !data.isEmpty else {
            throw IntegrationError.invalidOperation("Empty JSON response.")
        }
        let obj = try JSONSerialization.jsonObject(with: data, options: [])
        guard let arr = obj as? [Any] else {
            throw IntegrationError.invalidOperation("Expected a JSON array at the response root.")
        }
        return arr
    }

    /// Serialise a value to compact JSON bytes (used for request bodies).
    public static func encode(_ value: Any) throws -> Data {
        try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
    }

    /// A JSON string value at `key`, or nil. Booleans/numbers are not coerced —
    /// this matches `TryGetProperty(...) && ValueKind == String`.
    public static func string(_ obj: [String: Any], _ key: String) -> String? {
        obj[key] as? String
    }

    /// A nested object at `key`, or nil.
    public static func object(_ obj: [String: Any], _ key: String) -> [String: Any]? {
        obj[key] as? [String: Any]
    }

    /// A nested array at `key`, or nil.
    public static func array(_ obj: [String: Any], _ key: String) -> [Any]? {
        obj[key] as? [Any]
    }

    /// A double at `key` (number-or-numeric), or nil.
    public static func double(_ obj: [String: Any], _ key: String) -> Double? {
        switch obj[key] {
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return nil }
            return n.doubleValue
        case let s as String:
            return Double(s)
        default:
            return nil
        }
    }

    /// An Int at `key` (number-or-numeric), or nil.
    public static func int(_ obj: [String: Any], _ key: String) -> Int? {
        switch obj[key] {
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return nil }
            return n.intValue
        case let s as String:
            return Int(s)
        default:
            return nil
        }
    }

    /// A bool at `key` where the JSON value is explicitly a boolean, else nil.
    /// A JSON number (even 0/1) is NOT treated as a bool — this matches
    /// `ValueKind == True/False` rather than a numeric coercion.
    public static func bool(_ obj: [String: Any], _ key: String) -> Bool? {
        guard let value = obj[key] else { return nil }
        // On Darwin, JSON booleans decode to an NSNumber backed by CFBoolean.
        if let n = value as? NSNumber, CFGetTypeID(n) == CFBooleanGetTypeID() {
            return n.boolValue
        }
        // A pure Swift Bool (defensive; e.g. non-JSONSerialization sources).
        if let b = value as? Bool, !(value is NSNumber) {
            return b
        }
        return nil
    }

    /// Stringify a HomeAssistant attribute value exactly like the C# switch:
    /// String → itself; Number → its text; True/False → "true"/"false";
    /// anything else → its JSON text.
    public static func haAttributeString(_ value: Any) -> String {
        switch value {
        case let s as String:
            return s
        case let n as NSNumber:
            if CFGetTypeID(n) == CFBooleanGetTypeID() { return n.boolValue ? "true" : "false" }
            return numberText(n)
        case let b as Bool:
            return b ? "true" : "false"
        default:
            if let data = try? JSONSerialization.data(withJSONObject: value, options: []),
               let s = String(data: data, encoding: .utf8) {
                return s
            }
            return String(describing: value)
        }
    }

    /// Whether a HomeAssistant attribute JSON value is a String (used to gate
    /// `friendly_name`, which the C# only reads when `ValueKind == String`).
    public static func isJsonString(_ value: Any) -> Bool {
        if value is String { return true }
        return false
    }

    /// Render an NSNumber as .NET-ish text: integral numbers without a decimal
    /// point, fractional numbers with the shortest round-trippable form.
    private static func numberText(_ n: NSNumber) -> String {
        let d = n.doubleValue
        if d == d.rounded() && abs(d) < 1e15 {
            return String(Int64(d))
        }
        return n.stringValue
    }
}

// MARK: - IntegrationJsonValue

/// A JSON value for home-automation service arguments. The Swift stand-in for
/// the C# `object?` values in `IReadOnlyDictionary<string, object?>` — enough of
/// the JSON type space for HA service payloads (`entity_id`, brightness,
/// on/off), serialisable back to JSON.
public indirect enum IntegrationJsonValue: Sendable, Equatable {
    case string(String)
    case int(Int)
    case double(Double)
    case bool(Bool)
    case array([IntegrationJsonValue])
    case object([(String, IntegrationJsonValue)])
    case null

    public static func == (lhs: IntegrationJsonValue, rhs: IntegrationJsonValue) -> Bool {
        switch (lhs, rhs) {
        case let (.string(a), .string(b)): return a == b
        case let (.int(a), .int(b)): return a == b
        case let (.double(a), .double(b)): return a == b
        case let (.bool(a), .bool(b)): return a == b
        case let (.array(a), .array(b)): return a == b
        case let (.object(a), .object(b)):
            return a.count == b.count && zip(a, b).allSatisfy { $0.0 == $1.0 && $0.1 == $1.1 }
        case (.null, .null): return true
        default: return false
        }
    }

    /// The `JSONSerialization`-ready foundation object for this value.
    public var jsonObject: Any {
        switch self {
        case .string(let s): return s
        case .int(let i): return i
        case .double(let d): return d
        case .bool(let b): return b
        case .array(let a): return a.map { $0.jsonObject }
        case .object(let pairs):
            var dict: [String: Any] = [:]
            for (k, v) in pairs { dict[k] = v.jsonObject }
            return dict
        case .null: return NSNull()
        }
    }
}
