// NetworkingGrpcTests.swift
//
// Validates the CircleAI.Networking.Grpc port (NetworkingGrpc.swift): enum
// ordinals, record Codable, the retry-policy presets + backoff/isRetryable
// helpers, the InMemoryGrpcCallMetrics (grpc-{n} id, newest-first calls, default
// Idle state), and the GrpcNetworkTransport wired to a deterministic loopback
// IGrpcChannel — availability lifecycle, send-after-stop, and the inbound stream.

import XCTest
import Foundation
@testable import CircleAI

final class NetworkingGrpcTests: XCTestCase {

    // ── A deterministic loopback channel (the injected "socket") ──────────────
    private final class LoopbackGrpcChannel: IGrpcChannel, @unchecked Sendable {
        let descriptor: GrpcChannelDescriptor
        private let lock = NSLock()
        private var inbound: IGrpcInboundWriter?
        private(set) var openCount = 0
        private(set) var shutdownCount = 0

        init(descriptor: GrpcChannelDescriptor =
             GrpcChannelDescriptor(target: "dns:///svc", useTls: true,
                                   maxReceiveBytes: 1 << 20, maxSendBytes: 1 << 20,
                                   keepAliveInterval: 30)) {
            self.descriptor = descriptor
        }

        func open(inbound: IGrpcInboundWriter) async throws {
            lock.lock(); self.inbound = inbound; openCount += 1; lock.unlock()
        }
        func shutdown() async throws {
            lock.lock(); shutdownCount += 1; lock.unlock()
        }
        func call(_ payload: NetworkPayload) async throws {
            lock.lock(); let sink = inbound; lock.unlock()
            sink?.push(payload)  // loopback: echo call to the inbound stream
        }
    }

    // ── GrpcChannelState ordinals ────────────────────────────────────────────

    func testChannelStateOrdinals() {
        XCTAssertEqual(GrpcChannelState.idle.rawValue,             0)
        XCTAssertEqual(GrpcChannelState.connecting.rawValue,       1)
        XCTAssertEqual(GrpcChannelState.ready.rawValue,            2)
        XCTAssertEqual(GrpcChannelState.transientFailure.rawValue, 3)
        XCTAssertEqual(GrpcChannelState.shutdown.rawValue,         4)
        XCTAssertEqual(GrpcChannelState.allCases.count,            5)
    }

    // ── Retry policy presets + helpers ───────────────────────────────────────

    func testRetryPolicyPresets() {
        XCTAssertEqual(GrpcRetryPolicies.default.maxAttempts, 3)
        XCTAssertEqual(GrpcRetryPolicies.default.initialBackoff, 0.100, accuracy: 0.0001)
        XCTAssertEqual(GrpcRetryPolicies.default.maxBackoff, 2.0, accuracy: 0.0001)
        XCTAssertEqual(GrpcRetryPolicies.default.retryableStatusCodes, ["UNAVAILABLE", "DEADLINE_EXCEEDED"])

        XCTAssertEqual(GrpcRetryPolicies.aggressive.maxAttempts, 6)
        XCTAssertTrue(GrpcRetryPolicies.aggressive.retryableStatusCodes.contains("RESOURCE_EXHAUSTED"))

        XCTAssertEqual(GrpcRetryPolicies.noRetry.maxAttempts, 1)
        XCTAssertTrue(GrpcRetryPolicies.noRetry.retryableStatusCodes.isEmpty)
    }

    func testRetryPolicyBackoffSchedule() {
        let p = GrpcRetryPolicies.default // 100ms initial, ×2, cap 2s
        XCTAssertEqual(p.backoff(forAttempt: 0), 0.100, accuracy: 0.0001)
        XCTAssertEqual(p.backoff(forAttempt: 1), 0.200, accuracy: 0.0001)
        XCTAssertEqual(p.backoff(forAttempt: 2), 0.400, accuracy: 0.0001)
        // Capped at maxBackoff (2s): 100ms * 2^5 = 3.2s → clamped to 2.0.
        XCTAssertEqual(p.backoff(forAttempt: 5), 2.0, accuracy: 0.0001)
    }

    func testRetryPolicyIsRetryable() {
        let p = GrpcRetryPolicies.default
        XCTAssertTrue(p.isRetryable("UNAVAILABLE"))
        XCTAssertTrue(p.isRetryable("DEADLINE_EXCEEDED"))
        XCTAssertFalse(p.isRetryable("PERMISSION_DENIED"))
    }

    func testCallSummaryCodableRoundTrip() throws {
        let c = GrpcCallSummary(method: "/svc/Method", attempts: 2, latency: 0.05,
                                statusCode: "OK", atUtc: Date(timeIntervalSince1970: 10))
        let data = try JSONEncoder().encode(c)
        let back = try JSONDecoder().decode(GrpcCallSummary.self, from: data)
        XCTAssertEqual(c, back)
    }

    // ── InMemoryGrpcCallMetrics ──────────────────────────────────────────────

    func testMetricsChannelRegistryAndDefaultState() {
        let m = InMemoryGrpcCallMetrics()
        XCTAssertNil(m.getChannel("c1"))
        XCTAssertEqual(m.state("c1"), .idle) // default
        let d = GrpcChannelDescriptor(target: "t", useTls: false, maxReceiveBytes: 1, maxSendBytes: 1, keepAliveInterval: 5)
        m.registerChannel("c1", d)
        m.setState("c1", .ready)
        XCTAssertEqual(m.getChannel("c1"), d)
        XCTAssertEqual(m.state("c1"), .ready)
    }

    func testMetricsLogCallIdFormatAndOrdering() {
        let m = InMemoryGrpcCallMetrics()
        let id1 = m.logCall(GrpcCallSummary(method: "/a", attempts: 1, latency: 0.01, statusCode: "OK", atUtc: Date(timeIntervalSince1970: 1)))
        let id2 = m.logCall(GrpcCallSummary(method: "/b", attempts: 1, latency: 0.01, statusCode: "OK", atUtc: Date(timeIntervalSince1970: 3)))
        let id3 = m.logCall(GrpcCallSummary(method: "/c", attempts: 1, latency: 0.01, statusCode: "OK", atUtc: Date(timeIntervalSince1970: 2)))
        // grpc-{n}, 1-based (matches Interlocked.Increment starting from 0→1).
        XCTAssertEqual(id1, "grpc-1")
        XCTAssertEqual(id2, "grpc-2")
        XCTAssertEqual(id3, "grpc-3")
        // Newest first by AtUtc.
        XCTAssertEqual(m.recentCalls().map { $0.method }, ["/b", "/c", "/a"])
        XCTAssertEqual(m.recentCalls(limit: 1).map { $0.method }, ["/b"])
    }

    // ── GrpcNetworkTransport ─────────────────────────────────────────────────

    func testTransportKindAndAvailabilityLifecycle() async throws {
        let t = GrpcNetworkTransport(channel: LoopbackGrpcChannel())
        XCTAssertEqual(t.kind, .grpc)
        XCTAssertFalse(t.isAvailable) // not running yet
        try await t.start()
        XCTAssertTrue(t.isAvailable)
        try await t.stop()
        XCTAssertFalse(t.isAvailable)
    }

    func testTransportExposesDescriptor() {
        let d = GrpcChannelDescriptor(target: "dns:///x", useTls: true, maxReceiveBytes: 5, maxSendBytes: 6, keepAliveInterval: 12)
        let t = GrpcNetworkTransport(channel: LoopbackGrpcChannel(descriptor: d))
        XCTAssertEqual(t.descriptor, d)
    }

    func testTransportOpenAndShutdownReachChannel() async throws {
        let ch = LoopbackGrpcChannel()
        let t = GrpcNetworkTransport(channel: ch)
        try await t.start()
        XCTAssertEqual(ch.openCount, 1)
        try await t.stop()
        XCTAssertEqual(ch.shutdownCount, 1)
    }

    func testTransportSendLoopsBackThroughChannel() async throws {
        let t = GrpcNetworkTransport(channel: LoopbackGrpcChannel())
        try await t.start()
        let stream = t.receive()
        try await t.send(NetworkPayload.create(data: Data([1])))
        try await t.send(NetworkPayload.create(data: Data([2])))
        try await t.stop()

        var got: [Data] = []
        for await p in stream { got.append(p.data) }
        XCTAssertEqual(got, [Data([1]), Data([2])])
    }

    func testTransportSendAfterStopThrows() async throws {
        let t = GrpcNetworkTransport(channel: LoopbackGrpcChannel())
        try await t.start()
        try await t.stop()
        do {
            try await t.send(NetworkPayload.create(data: Data([1])))
            XCTFail("expected send after stop to throw")
        } catch {
            XCTAssertEqual(error as? NetworkError, .transportStopped)
        }
    }
}
