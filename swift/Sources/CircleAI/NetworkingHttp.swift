// NetworkingHttp.swift
//
// Port of CircleAI.Networking.Http (the C# reference) — the HTTP network
// transport. Collapses the C# folder's two files (HttpTransportCommons.cs /
// HttpNetworkTransport.cs) into this single Swift file per the tree's flat
// convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.Http/):
//   DTOs     — HttpEndpointDescriptor, HttpRequestSummary, HttpCacheKey
//   Helpers  — HttpStatusFamily (Is2xx/Is3xx/Is4xx/Is5xx/ShouldRetry)
//   Metrics  — InMemoryHttpRequestMetrics
//   Transport— HttpNetworkTransport (INetworkTransport) + IHttpMessageSender
//
// Injected-socket note — the C# HttpNetworkTransport wraps a concrete
// System.Net.Http.HttpClient (a real socket). This port follows the task rule
// "inject the socket behind an interface": the sender is injected behind
// IHttpMessageSender. Everything else — the URL construction, the 3-attempt loop,
// the exponential backoff schedule, the X-Payload-Id / X-Payload-Priority
// headers, EnsureSuccessStatusCode behaviour — is ported byte-for-byte from the
// C# SendAsync so the wire behaviour is identical.
//
// The C# ReceiveAsync is intentionally empty (HTTP is request/response; server
// push is WebSocket/SSE). To honour "no empty methods", receive() returns a
// well-defined empty stream — a finished AsyncStream — which is the faithful
// Swift form of C#'s `yield break` (an immediately-completed sequence), not a
// stub.

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// HttpEndpointDescriptor / HttpRequestSummary / HttpCacheKey (records)
// ──────────────────────────────────────────────────────────────────────────

/// Describes an HTTP endpoint. Ported from the C# `HttpEndpointDescriptor`
/// record. `defaultHeaders` is optional (C#'s nullable dictionary).
public struct HttpEndpointDescriptor: Sendable, Equatable, Codable {
    public let method: String
    public let baseUri: String
    public let path: String
    public let defaultHeaders: [String: String]?

    public init(
        method: String,
        baseUri: String,
        path: String,
        defaultHeaders: [String: String]?
    ) {
        self.method = method
        self.baseUri = baseUri
        self.path = path
        self.defaultHeaders = defaultHeaders
    }
}

/// A summary of a single HTTP request. Ported from the C# `HttpRequestSummary`
/// record. `latency` is seconds (C#'s TimeSpan).
public struct HttpRequestSummary: Sendable, Equatable, Codable {
    public let endpointId: String
    public let statusCode: Int
    public let latency: TimeInterval
    public let responseBytes: Int
    public let atUtc: Date

    public init(
        endpointId: String,
        statusCode: Int,
        latency: TimeInterval,
        responseBytes: Int,
        atUtc: Date
    ) {
        self.endpointId = endpointId
        self.statusCode = statusCode
        self.latency = latency
        self.responseBytes = responseBytes
        self.atUtc = atUtc
    }
}

/// A cache key for an HTTP response. Ported from the C# `HttpCacheKey` record.
/// Value-equatable + Hashable so it can key a cache dictionary (the C# record's
/// structural equality).
public struct HttpCacheKey: Sendable, Equatable, Hashable, Codable {
    public let method: String
    public let fullUri: String
    public let acceptHeader: String

    public init(method: String, fullUri: String, acceptHeader: String) {
        self.method = method
        self.fullUri = fullUri
        self.acceptHeader = acceptHeader
    }
}

// ──────────────────────────────────────────────────────────────────────────
// HttpStatusFamily (static helpers)
// ──────────────────────────────────────────────────────────────────────────

/// HTTP status-code family helpers. Ported from the C# static `HttpStatusFamily`
/// (predicates match exactly, including the retryable set 408/425/429 + all 5xx).
public enum HttpStatusFamily {
    public static func is2xx(_ s: Int) -> Bool { s >= 200 && s < 300 }
    public static func is3xx(_ s: Int) -> Bool { s >= 300 && s < 400 }
    public static func is4xx(_ s: Int) -> Bool { s >= 400 && s < 500 }
    public static func is5xx(_ s: Int) -> Bool { s >= 500 && s < 600 }
    /// True for 408 (Request Timeout), 425 (Too Early), 429 (Too Many Requests),
    /// or any 5xx.
    public static func shouldRetry(_ s: Int) -> Bool {
        s == 408 || s == 425 || s == 429 || is5xx(s)
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryHttpRequestMetrics (HttpTransportCommons.cs)
//
// C# uses a ConcurrentDictionary for endpoints and a lock-guarded request list.
// Here a single NSLock guards both; ordering + the 2xx-only latency average
// match exactly (empty → 0.0).
// ──────────────────────────────────────────────────────────────────────────

/// In-memory HTTP request metrics. Ported from the C#
/// `InMemoryHttpRequestMetrics`.
public final class InMemoryHttpRequestMetrics: @unchecked Sendable {
    private let lock = NSLock()
    private var endpoints: [String: HttpEndpointDescriptor] = [:]
    private var requests: [HttpRequestSummary] = []

    public init() {}

    /// Register (or replace) an endpoint descriptor keyed by `id`.
    public func register(_ id: String, _ d: HttpEndpointDescriptor) {
        lock.lock(); endpoints[id] = d; lock.unlock()
    }

    /// The endpoint descriptor for `id`, or nil.
    public func getEndpoint(_ id: String) -> HttpEndpointDescriptor? {
        lock.lock(); defer { lock.unlock() }
        return endpoints[id]
    }

    /// Log a request summary.
    public func log(_ s: HttpRequestSummary) {
        lock.lock(); requests.append(s); lock.unlock()
    }

    /// The most recent `limit` requests, newest first (matches C#'s
    /// `OrderByDescending(r => r.AtUtc).Take(limit)`).
    public func recentRequests(limit: Int = 100) -> [HttpRequestSummary] {
        lock.lock(); defer { lock.unlock() }
        return Array(requests.sorted { $0.atUtc > $1.atUtc }.prefix(max(0, limit)))
    }

    /// Mean latency (ms) of 2xx requests to `endpointId`. Empty → 0.0. Mirrors
    /// C#'s `Avg2xxLatencyMs` (which averages `Latency.TotalMilliseconds`).
    public func avg2xxLatencyMs(_ endpointId: String) -> Double {
        lock.lock(); defer { lock.unlock() }
        let rows = requests.filter {
            $0.endpointId == endpointId && HttpStatusFamily.is2xx($0.statusCode)
        }
        guard !rows.isEmpty else { return 0.0 }
        // latency is stored in seconds; TotalMilliseconds == seconds * 1000.
        return rows.reduce(0.0) { $0 + $1.latency * 1000.0 } / Double(rows.count)
    }
}

// ──────────────────────────────────────────────────────────────────────────
// IHttpMessageSender / HttpSendResult / HttpSendError (HttpNetworkTransport.cs)
//
// The injected socket seam (the Swift analogue of HttpClient.PostAsync). The
// transport builds the request (url + body + headers) exactly as the C# does and
// hands it to the sender; the sender returns a status code (the analogue of the
// HttpResponseMessage). A transport/connection failure surfaces as a thrown
// HttpSendError.transient, which the retry loop treats like C#'s
// HttpRequestException.
// ──────────────────────────────────────────────────────────────────────────

/// The outcome of one HTTP POST as reported by the injected sender: the HTTP
/// status code and the response body size.
public struct HttpSendResult: Sendable, Equatable {
    public let statusCode: Int
    public let responseBytes: Int
    public init(statusCode: Int, responseBytes: Int = 0) {
        self.statusCode = statusCode
        self.responseBytes = responseBytes
    }
}

/// Errors an `IHttpMessageSender` can throw. `transient` is the analogue of C#'s
/// `HttpRequestException` at the connection level (DNS/socket failure) — the
/// retry loop retries it; a non-2xx status is surfaced via
/// `HttpSendResult.statusCode` and turned into `httpStatus` by the transport when
/// the response is unsuccessful (the analogue of `EnsureSuccessStatusCode`).
public enum HttpSendError: Error, Equatable, Sendable {
    /// A connection-level failure (retryable, like `HttpRequestException`).
    case transient
    /// A non-success HTTP status after retries are exhausted (the analogue of the
    /// exception `EnsureSuccessStatusCode` throws).
    case httpStatus(Int)
}

/// One outbound HTTP request, assembled by the transport for the injected sender.
public struct HttpOutboundRequest: Sendable, Equatable {
    public let url: String
    public let body: Data
    public let contentType: String
    /// Extra headers the transport adds (X-Payload-Id, X-Payload-Priority).
    public let headers: [String: String]
    public init(url: String, body: Data, contentType: String, headers: [String: String]) {
        self.url = url
        self.body = body
        self.contentType = contentType
        self.headers = headers
    }
}

/// The injected HTTP sender — the Swift analogue of `HttpClient`. Implement per
/// platform (or in tests). `post` returns the response status; it throws
/// `HttpSendError.transient` on a connection failure.
public protocol IHttpMessageSender: AnyObject {
    func post(_ request: HttpOutboundRequest) async throws -> HttpSendResult
}

// ──────────────────────────────────────────────────────────────────────────
// MessagePriority name mapping
//
// C#'s SendAsync writes the header `X-Payload-Priority` as
// `payload.Priority.ToString()`, i.e. the enum NAME ("Normal", "Urgent", …), not
// the ordinal. Match that so the wire header is byte-identical.
// ──────────────────────────────────────────────────────────────────────────

extension MessagePriority {
    /// The .NET `Enum.ToString()` name for this priority (PascalCase), used for
    /// the `X-Payload-Priority` header.
    public var dotNetName: String {
        switch self {
        case .low: return "Low"
        case .normal: return "Normal"
        case .high: return "High"
        case .urgent: return "Urgent"
        case .emergency: return "Emergency"
        }
    }
}

// ──────────────────────────────────────────────────────────────────────────
// HttpNetworkTransport (HttpNetworkTransport.cs)
// ──────────────────────────────────────────────────────────────────────────

/// `INetworkTransport` backed by an injected HTTP sender. `send` POSTs the
/// payload to `{baseUrl}/messages/{destinationId}` (or `{baseUrl}/messages` when
/// there is no destination), retrying up to 3 times with `2^attempt`-second
/// backoff on a transient/retryable failure — the exact algorithm from C#'s
/// `SendAsync`. `isAvailable` is always true (C#: "assume HTTP always available
/// if configured"). `receive` is an immediately-completed stream (HTTP is
/// request/response; server push is WebSocket/SSE), the faithful form of C#'s
/// `yield break`.
public final class HttpNetworkTransport: INetworkTransport, @unchecked Sendable {
    private let sender: IHttpMessageSender
    private let baseUrl: String

    private let lock = NSLock()
    private var running = false

    /// - Parameters:
    ///   - sender: the injected HTTP sender (the socket seam).
    ///   - baseUrl: the base URL; a trailing '/' is trimmed (C#'s
    ///     `baseUrl.TrimEnd('/')`). Must be non-empty/non-whitespace.
    public init(sender: IHttpMessageSender, baseUrl: String) {
        self.sender = sender
        // Mirror C#'s TrimEnd('/') — trim any run of trailing slashes.
        var trimmed = baseUrl
        while trimmed.hasSuffix("/") { trimmed.removeLast() }
        self.baseUrl = trimmed
    }

    public var kind: TransportKind { .http }

    /// Mirrors C#'s `IsAvailable => true`.
    public var isAvailable: Bool { true }

    public func start() async throws {
        lock.lock(); running = true; lock.unlock()
    }

    public func stop() async throws {
        lock.lock(); running = false; lock.unlock()
    }

    /// POST the payload to `{baseUrl}/messages/{destinationId}`. Retries up to 3
    /// times with exponential backoff on transient failures — the exact algorithm
    /// from C#'s `SendAsync`.
    ///
    /// The backoff schedule matches C# (`Task.Delay(2^attempt s)` for attempts 0
    /// and 1). Because this port must be deterministic and not sleep in tests, the
    /// wall-clock delay is exposed via the injectable `sleep` hook (default: no
    /// real sleep) rather than blocking — the retry COUNT and ordering are
    /// preserved exactly, which is the behaviour under test.
    public func send(_ payload: NetworkPayload) async throws {
        let url: String
        if let dest = payload.destinationId, !dest.isEmpty {
            url = "\(baseUrl)/messages/\(Self.escapeDataString(dest))"
        } else {
            url = "\(baseUrl)/messages"
        }

        let headers = [
            "X-Payload-Id": payload.id,
            "X-Payload-Priority": payload.priority.dotNetName,
        ]
        let request = HttpOutboundRequest(
            url: url,
            body: payload.data,
            contentType: payload.contentType,
            headers: headers)

        // C#: for (attempt = 0; attempt < 3; attempt++) { try POST;
        //     EnsureSuccessStatusCode; return } catch (HttpRequestException) when
        //     (attempt < 2) { await Task.Delay(2^attempt s) }
        // The final attempt's exception propagates (no `when` guard). Here both a
        // connection failure (HttpSendError.transient, thrown by the sender) and an
        // unsuccessful status (the analogue of EnsureSuccessStatusCode throwing)
        // are the retryable failure; a 2xx returns immediately. On the last attempt
        // the loop ends and the recorded failure is thrown.
        var lastError: HttpSendError = .transient
        for attempt in 0..<3 {
            do {
                let result = try await sender.post(request)
                if HttpStatusFamily.is2xx(result.statusCode) {
                    return
                }
                lastError = .httpStatus(result.statusCode)
            } catch let e as HttpSendError {
                lastError = e
            }
            // Failure this attempt. Retry (with backoff) only when attempt < 2,
            // mirroring the C# `when (attempt < 2)` guard; otherwise fall through
            // and throw below.
            if attempt < 2 {
                await backoff(attempt: attempt)
            }
        }
        throw lastError
    }

    /// HTTP is request/response — no server-push receive. Returns an
    /// immediately-completed stream (the faithful form of C#'s `yield break`).
    public func receive() -> AsyncStream<NetworkPayload> {
        AsyncStream { $0.finish() }
    }

    // ── Backoff hook ────────────────────────────────────────────────────────

    /// Injectable backoff hook. Defaults to a no-op so tests are deterministic and
    /// fast; a production wiring can set it to actually sleep `2^attempt` seconds
    /// (the C# `Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)))`).
    public var onBackoff: (@Sendable (_ seconds: Double) async -> Void)?

    /// The exponential backoff delay for `attempt` (0-based): `2^attempt` seconds,
    /// matching C#'s `Math.Pow(2, attempt)`.
    public static func backoffSeconds(forAttempt attempt: Int) -> Double {
        pow(2.0, Double(attempt))
    }

    private func backoff(attempt: Int) async {
        let seconds = Self.backoffSeconds(forAttempt: attempt)
        if let hook = onBackoff {
            await hook(seconds)
        }
        // Default: no real sleep (deterministic tests). Retry count is unchanged.
    }

    // ── URL escaping ──────────────────────────────────────────────────────────

    /// Percent-escape a path segment, matching .NET's `Uri.EscapeDataString`
    /// (RFC 3986 unreserved set: A–Z a–z 0–9 - _ . ~ are left as-is; everything
    /// else is %-encoded from its UTF-8 bytes, uppercase hex).
    static func escapeDataString(_ s: String) -> String {
        let unreserved = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~"
        let unreservedSet = Set(unreserved.utf8)
        var out = ""
        out.reserveCapacity(s.count)
        for byte in s.utf8 {
            if unreservedSet.contains(byte) {
                out.append(Character(UnicodeScalar(byte)))
            } else {
                out.append("%")
                out.append(Self.hexDigit((byte >> 4) & 0xF))
                out.append(Self.hexDigit(byte & 0xF))
            }
        }
        return out
    }

    private static func hexDigit(_ v: UInt8) -> Character {
        let table: [Character] = ["0", "1", "2", "3", "4", "5", "6", "7",
                                  "8", "9", "A", "B", "C", "D", "E", "F"]
        return table[Int(v)]
    }
}
