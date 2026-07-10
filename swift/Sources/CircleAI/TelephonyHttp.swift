// TelephonyHttp.swift
//
// Injectable HTTP transport for the telephony vertical.
//
// The C# carriers (Twilio / Telnyx / Plivo) and the tool-webhook path in
// DefaultToolCallRegistry all talk to remote HTTP APIs via a shared
// System.Net.Http.HttpClient. Per the port's "in-memory only; inject
// external/native/cloud/socket dependencies behind interfaces" rule, the raw
// HTTP call is abstracted behind `ITelephonyHttpTransport`. The carrier logic
// (path building, form/JSON body construction, auth header, response parsing)
// is ported verbatim and exercised against a deterministic in-memory fake
// (`FakeHttpTransport`) — no sockets, no real calls.
//
// This mirrors HttpClient closely enough to preserve wire formats:
//   • `send(_:)` takes a fully-formed `TelephonyHttpRequest` (method, absolute
//     or base-relative path, headers, body) and returns a
//     `TelephonyHttpResponse` (status, headers, body bytes).
//   • A default request header bag (Authorization) is applied per-carrier,
//     matching `HttpClient.DefaultRequestHeaders.Authorization`.

import Foundation

// MARK: - HTTP method

/// HTTP verb. Mirrors the subset the carriers use (GET / POST / PATCH / DELETE).
public enum TelephonyHttpMethod: String, Sendable, Equatable {
    case get = "GET"
    case post = "POST"
    case patch = "PATCH"
    case delete = "DELETE"
}

// MARK: - Content type

/// Body content type. Mirrors the two content flavours the carriers emit:
/// URL-encoded form (`application/x-www-form-urlencoded`) and JSON
/// (`application/json`).
public enum TelephonyHttpContentType: String, Sendable, Equatable {
    case form = "application/x-www-form-urlencoded"
    case json = "application/json"
    case none = ""
}

// MARK: - Request

/// One HTTP request. `path` is resolved against the transport's base address
/// exactly like `HttpClient` resolves a relative request URI against
/// `BaseAddress`.
public struct TelephonyHttpRequest: Sendable, Equatable {
    public let method: TelephonyHttpMethod
    /// Path (may be absolute or base-relative, e.g. "/v2/calls?filter=x").
    public let path: String
    /// Extra per-request headers (default request headers are merged in by the
    /// transport). Case-insensitive keys are the caller's responsibility.
    public let headers: [String: String]
    /// Raw request body bytes. Empty for bodyless GET/DELETE.
    public let body: Data
    /// Declared content type of `body`.
    public let contentType: TelephonyHttpContentType

    public init(
        method: TelephonyHttpMethod,
        path: String,
        headers: [String: String] = [:],
        body: Data = Data(),
        contentType: TelephonyHttpContentType = .none
    ) {
        self.method = method
        self.path = path
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

/// One HTTP response. Mirrors `HttpResponseMessage`: a status code, headers,
/// and a body. `isSuccessStatusCode` matches HttpClient's 200–299 test.
public struct TelephonyHttpResponse: Sendable, Equatable {
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

    /// Convenience: a 2xx response with a UTF-8 JSON string body.
    public static func json(_ body: String, statusCode: Int = 200) -> TelephonyHttpResponse {
        TelephonyHttpResponse(
            statusCode: statusCode,
            headers: ["Content-Type": "application/json"],
            body: Data(body.utf8))
    }

    /// Convenience: an error response with a plain-text body.
    public static func error(_ statusCode: Int, _ body: String = "") -> TelephonyHttpResponse {
        TelephonyHttpResponse(statusCode: statusCode, headers: [:], body: Data(body.utf8))
    }
}

// MARK: - Transport

/// Injectable HTTP transport. Real deployments back this with URLSession (or a
/// bridged native client); tests back it with `FakeHttpTransport`. Mirrors the
/// role of `HttpClient` in the C# carriers.
public protocol ITelephonyHttpTransport: AnyObject, Sendable {
    /// The base address requests are resolved against. `nil` until assigned —
    /// mirrors `HttpClient.BaseAddress` being settable once.
    var baseAddress: URL? { get set }

    /// Default request headers merged into every outgoing request. Mirrors
    /// `HttpClient.DefaultRequestHeaders` (notably `Authorization`).
    var defaultHeaders: [String: String] { get set }

    /// Send one request and await the response. Throws only on a genuine
    /// transport failure (mirrors `HttpClient.SendAsync` throwing on network
    /// error, not on a non-2xx status — non-2xx surfaces via the response).
    func send(_ request: TelephonyHttpRequest) async throws -> TelephonyHttpResponse
}

// MARK: - FakeHttpTransport

/// Deterministic in-memory `ITelephonyHttpTransport`. Records every request in
/// order and replies from a queued/scripted response table — the substrate the
/// carrier ports are tested against without touching a socket.
///
/// Matching order: for each incoming request the fake looks for a scripted
/// handler whose (method, path-predicate) matches; if none matches it falls
/// back to the default response (200 `{}` unless overridden). The full request
/// log is exposed for wire-format assertions.
public final class FakeHttpTransport: ITelephonyHttpTransport, @unchecked Sendable {

    /// One scripted route: match on method + a path predicate → a response
    /// (optionally computed from the request so a test can echo values back).
    public struct Route: @unchecked Sendable {
        public let method: TelephonyHttpMethod
        public let matches: @Sendable (String) -> Bool
        public let respond: @Sendable (TelephonyHttpRequest) -> TelephonyHttpResponse

        public init(
            method: TelephonyHttpMethod,
            matches: @escaping @Sendable (String) -> Bool,
            respond: @escaping @Sendable (TelephonyHttpRequest) -> TelephonyHttpResponse
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
    private var log: [TelephonyHttpRequest] = []
    private let defaultResponse: TelephonyHttpResponse

    public init(defaultResponse: TelephonyHttpResponse = .json("{}")) {
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
    public var requests: [TelephonyHttpRequest] {
        lock.lock(); defer { lock.unlock() }
        return log
    }

    /// The most recent request, or nil if none.
    public var lastRequest: TelephonyHttpRequest? {
        lock.lock(); defer { lock.unlock() }
        return log.last
    }

    /// Register a scripted route (checked in registration order).
    public func on(
        _ method: TelephonyHttpMethod,
        where matches: @escaping @Sendable (String) -> Bool,
        respond: @escaping @Sendable (TelephonyHttpRequest) -> TelephonyHttpResponse
    ) {
        lock.lock(); routes.append(Route(method: method, matches: matches, respond: respond)); lock.unlock()
    }

    /// Register a scripted route matching a fixed status + JSON body for any
    /// path of the given method whose path contains `pathContains`.
    public func on(
        _ method: TelephonyHttpMethod,
        pathContains: String,
        json: String,
        statusCode: Int = 200
    ) {
        on(method, where: { $0.contains(pathContains) }) { _ in
            .json(json, statusCode: statusCode)
        }
    }

    public func send(_ request: TelephonyHttpRequest) async throws -> TelephonyHttpResponse {
        lock.lock()
        log.append(request)
        // Merge default headers into a recorded snapshot is unnecessary for the
        // log (callers assert on defaultHeaders separately), but pick the route
        // now under the lock.
        let matched = routes.first { $0.method == request.method && $0.matches(request.path) }
        let fallback = defaultResponse
        lock.unlock()
        if let matched {
            return matched.respond(request)
        }
        return fallback
    }
}
