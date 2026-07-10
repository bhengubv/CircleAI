// NetworkingHttpTests.swift
//
// Validates the CircleAI.Networking.Http port (NetworkingHttp.swift): record
// Codable, HttpStatusFamily predicates, HttpCacheKey Hashable, the
// InMemoryHttpRequestMetrics (newest-first + 2xx-only latency average), the
// MessagePriority .NET-name mapping, Uri.EscapeDataString parity, and the
// HttpNetworkTransport send algorithm — URL construction, headers, the 3-attempt
// retry loop with give-up-after-3, and 2xx early return — driven by a scripted
// IHttpMessageSender. Also confirms receive() is an immediately-completed stream.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingHttpTests: XCTestCase {

    // ── A scripted sender: returns/queued outcomes, records every request ─────
    private final class ScriptedSender: IHttpMessageSender, @unchecked Sendable {
        enum Outcome { case status(Int); case transient }
        private let lock = NSLock()
        private var script: [Outcome]
        private var _requests: [HttpOutboundRequest] = []

        init(_ script: [Outcome]) { self.script = script }

        func post(_ request: HttpOutboundRequest) async throws -> HttpSendResult {
            lock.lock()
            _requests.append(request)
            let outcome = script.isEmpty ? Outcome.status(200) : script.removeFirst()
            lock.unlock()
            switch outcome {
            case .status(let code): return HttpSendResult(statusCode: code, responseBytes: 0)
            case .transient: throw HttpSendError.transient
            }
        }

        var requests: [HttpOutboundRequest] { lock.lock(); defer { lock.unlock() }; return _requests }
        var attemptCount: Int { requests.count }
    }

    private func payload(id: String = "pid", dest: String? = "device-1",
                         priority: MessagePriority = .normal,
                         data: Data = Data([1, 2, 3]),
                         contentType: String = "application/json") -> NetworkPayload {
        NetworkPayload(id: id, sourceId: nil, destinationId: dest, data: data,
                       priority: priority, ttl: nil, contentType: contentType,
                       metadata: [:], createdAt: Date())
    }

    // ── HttpStatusFamily ─────────────────────────────────────────────────────

    func testStatusFamilyPredicates() {
        XCTAssertTrue(HttpStatusFamily.is2xx(200));  XCTAssertTrue(HttpStatusFamily.is2xx(299))
        XCTAssertFalse(HttpStatusFamily.is2xx(300))
        XCTAssertTrue(HttpStatusFamily.is3xx(301))
        XCTAssertTrue(HttpStatusFamily.is4xx(404))
        XCTAssertTrue(HttpStatusFamily.is5xx(503))
    }

    func testStatusFamilyShouldRetry() {
        for s in [408, 425, 429, 500, 502, 503, 504] {
            XCTAssertTrue(HttpStatusFamily.shouldRetry(s), "\(s) should retry")
        }
        for s in [200, 301, 400, 401, 403, 404] {
            XCTAssertFalse(HttpStatusFamily.shouldRetry(s), "\(s) should not retry")
        }
    }

    // ── HttpCacheKey ─────────────────────────────────────────────────────────

    func testCacheKeyHashableEquality() {
        let a = HttpCacheKey(method: "GET", fullUri: "https://x/y", acceptHeader: "application/json")
        let b = HttpCacheKey(method: "GET", fullUri: "https://x/y", acceptHeader: "application/json")
        let c = HttpCacheKey(method: "POST", fullUri: "https://x/y", acceptHeader: "application/json")
        XCTAssertEqual(a, b)
        var set: Set<HttpCacheKey> = [a]
        XCTAssertTrue(set.contains(b))
        set.insert(c)
        XCTAssertEqual(set.count, 2)
    }

    func testEndpointDescriptorCodableWithNilHeaders() throws {
        let d = HttpEndpointDescriptor(method: "GET", baseUri: "https://x", path: "/y", defaultHeaders: nil)
        let data = try JSONEncoder().encode(d)
        let back = try JSONDecoder().decode(HttpEndpointDescriptor.self, from: data)
        XCTAssertEqual(d, back)
        XCTAssertNil(back.defaultHeaders)
    }

    // ── InMemoryHttpRequestMetrics ───────────────────────────────────────────

    func testMetricsRecentNewestFirst() {
        let m = InMemoryHttpRequestMetrics()
        m.log(HttpRequestSummary(endpointId: "e", statusCode: 200, latency: 0.01, responseBytes: 1, atUtc: Date(timeIntervalSince1970: 1)))
        m.log(HttpRequestSummary(endpointId: "e", statusCode: 200, latency: 0.02, responseBytes: 1, atUtc: Date(timeIntervalSince1970: 3)))
        m.log(HttpRequestSummary(endpointId: "e", statusCode: 200, latency: 0.03, responseBytes: 1, atUtc: Date(timeIntervalSince1970: 2)))
        XCTAssertEqual(m.recentRequests().map { $0.atUtc.timeIntervalSince1970 }, [3, 2, 1])
    }

    func testMetricsAvg2xxLatencyIgnoresNon2xx() {
        let m = InMemoryHttpRequestMetrics()
        // latency stored in seconds; average returned in ms.
        m.log(HttpRequestSummary(endpointId: "e", statusCode: 200, latency: 0.010, responseBytes: 0, atUtc: Date()))
        m.log(HttpRequestSummary(endpointId: "e", statusCode: 204, latency: 0.030, responseBytes: 0, atUtc: Date()))
        m.log(HttpRequestSummary(endpointId: "e", statusCode: 500, latency: 9.999, responseBytes: 0, atUtc: Date())) // ignored
        m.log(HttpRequestSummary(endpointId: "other", statusCode: 200, latency: 5.0, responseBytes: 0, atUtc: Date())) // other endpoint
        // (10ms + 30ms) / 2 = 20ms.
        XCTAssertEqual(m.avg2xxLatencyMs("e"), 20.0, accuracy: 0.0001)
        XCTAssertEqual(m.avg2xxLatencyMs("empty"), 0.0, accuracy: 0.0001)
    }

    // ── MessagePriority .NET-name mapping ────────────────────────────────────

    func testMessagePriorityDotNetNames() {
        XCTAssertEqual(MessagePriority.low.dotNetName,       "Low")
        XCTAssertEqual(MessagePriority.normal.dotNetName,    "Normal")
        XCTAssertEqual(MessagePriority.high.dotNetName,      "High")
        XCTAssertEqual(MessagePriority.urgent.dotNetName,    "Urgent")
        XCTAssertEqual(MessagePriority.emergency.dotNetName, "Emergency")
    }

    // ── Uri.EscapeDataString parity ──────────────────────────────────────────

    func testEscapeDataStringMatchesDotNet() {
        // Unreserved set left untouched.
        XCTAssertEqual(HttpNetworkTransport.escapeDataString("abcXYZ0-9_.~"), "abcXYZ0-9_.~")
        // Space and slash are escaped.
        XCTAssertEqual(HttpNetworkTransport.escapeDataString("a b/c"), "a%20b%2Fc")
        // '@' and ':' escaped (device ids can contain them).
        XCTAssertEqual(HttpNetworkTransport.escapeDataString("user@host:5000"), "user%40host%3A5000")
        // Uppercase hex, multi-byte UTF-8 (é = C3 A9).
        XCTAssertEqual(HttpNetworkTransport.escapeDataString("é"), "%C3%A9")
    }

    func testBackoffSecondsSchedule() {
        XCTAssertEqual(HttpNetworkTransport.backoffSeconds(forAttempt: 0), 1.0, accuracy: 0.0001) // 2^0
        XCTAssertEqual(HttpNetworkTransport.backoffSeconds(forAttempt: 1), 2.0, accuracy: 0.0001) // 2^1
        XCTAssertEqual(HttpNetworkTransport.backoffSeconds(forAttempt: 2), 4.0, accuracy: 0.0001) // 2^2
    }

    // ── HttpNetworkTransport: lifecycle + basics ─────────────────────────────

    func testTransportKindAndAlwaysAvailable() {
        let t = HttpNetworkTransport(sender: ScriptedSender([]), baseUrl: "https://api.x/")
        XCTAssertEqual(t.kind, .http)
        XCTAssertTrue(t.isAvailable) // always available if configured
    }

    func testTransportTrimsTrailingSlashesInBaseUrl() async throws {
        let sender = ScriptedSender([.status(200)])
        let t = HttpNetworkTransport(sender: sender, baseUrl: "https://api.x///")
        try await t.send(payload(dest: "d"))
        XCTAssertEqual(sender.requests.first?.url, "https://api.x/messages/d")
    }

    func testTransportReceiveIsImmediatelyCompleted() async {
        let t = HttpNetworkTransport(sender: ScriptedSender([]), baseUrl: "https://api.x")
        var count = 0
        for await _ in t.receive() { count += 1 }
        XCTAssertEqual(count, 0)
    }

    // ── HttpNetworkTransport: URL + headers ──────────────────────────────────

    func testSendBuildsUrlWithEscapedDestination() async throws {
        let sender = ScriptedSender([.status(200)])
        let t = HttpNetworkTransport(sender: sender, baseUrl: "https://api.x")
        try await t.send(payload(dest: "user@host"))
        XCTAssertEqual(sender.requests.first?.url, "https://api.x/messages/user%40host")
    }

    func testSendBuildsUrlWithoutDestinationWhenNilOrEmpty() async throws {
        let s1 = ScriptedSender([.status(200)])
        let t1 = HttpNetworkTransport(sender: s1, baseUrl: "https://api.x")
        try await t1.send(payload(dest: nil))
        XCTAssertEqual(s1.requests.first?.url, "https://api.x/messages")

        let s2 = ScriptedSender([.status(200)])
        let t2 = HttpNetworkTransport(sender: s2, baseUrl: "https://api.x")
        try await t2.send(payload(dest: ""))
        XCTAssertEqual(s2.requests.first?.url, "https://api.x/messages")
    }

    func testSendSetsPayloadHeadersAndContentType() async throws {
        let sender = ScriptedSender([.status(200)])
        let t = HttpNetworkTransport(sender: sender, baseUrl: "https://api.x")
        try await t.send(payload(id: "abc123", dest: "d", priority: .urgent, contentType: "application/cbor"))
        let req = try XCTUnwrap(sender.requests.first)
        XCTAssertEqual(req.headers["X-Payload-Id"], "abc123")
        XCTAssertEqual(req.headers["X-Payload-Priority"], "Urgent")
        XCTAssertEqual(req.contentType, "application/cbor")
        XCTAssertEqual(req.body, Data([1, 2, 3]))
    }

    // ── HttpNetworkTransport: retry algorithm ────────────────────────────────

    func testSendSucceedsFirstTryOn2xx() async throws {
        let sender = ScriptedSender([.status(201)])
        let t = HttpNetworkTransport(sender: sender, baseUrl: "https://api.x")
        try await t.send(payload())
        XCTAssertEqual(sender.attemptCount, 1) // no retry on success
    }

    func testSendRetriesTransientThenSucceeds() async throws {
        // transient, transient, 200 → 3 attempts, success.
        let sender = ScriptedSender([.transient, .transient, .status(200)])
        let t = HttpNetworkTransport(sender: sender, baseUrl: "https://api.x")
        try await t.send(payload())
        XCTAssertEqual(sender.attemptCount, 3)
    }

    func testSendGivesUpAfterThreeTransient() async throws {
        // transient x3 → all 3 attempts fail, throws on the 3rd (no `when` guard).
        let sender = ScriptedSender([.transient, .transient, .transient])
        let t = HttpNetworkTransport(sender: sender, baseUrl: "https://api.x")
        do {
            try await t.send(payload())
            XCTFail("expected send to throw after exhausting retries")
        } catch {
            XCTAssertEqual(error as? HttpSendError, .transient)
        }
        XCTAssertEqual(sender.attemptCount, 3) // exactly 3, not more
    }

    func testSendRetriesNon2xxStatusThenThrows() async throws {
        // 503, 503, 503 → non-2xx treated like EnsureSuccessStatusCode failing,
        // retried within the loop, throws httpStatus after 3 attempts.
        let sender = ScriptedSender([.status(503), .status(503), .status(503)])
        let t = HttpNetworkTransport(sender: sender, baseUrl: "https://api.x")
        do {
            try await t.send(payload())
            XCTFail("expected send to throw on persistent 503")
        } catch {
            XCTAssertEqual(error as? HttpSendError, .httpStatus(503))
        }
        XCTAssertEqual(sender.attemptCount, 3)
    }

    func testSendBackoffHookInvokedBetweenRetries() async throws {
        let sender = ScriptedSender([.transient, .status(200)])
        let t = HttpNetworkTransport(sender: sender, baseUrl: "https://api.x")
        let backoffs = BackoffRecorder()
        t.onBackoff = { seconds in await backoffs.record(seconds) }
        try await t.send(payload())
        // One retry → one backoff, with 2^0 = 1s.
        let recorded = await backoffs.values
        XCTAssertEqual(recorded, [1.0])
    }

    // Actor to safely record backoff invocations from the @Sendable hook.
    private actor BackoffRecorder {
        private(set) var values: [Double] = []
        func record(_ v: Double) { values.append(v) }
    }
}
