// NetworkingGrpc.swift
//
// Port of CircleAI.Networking.Grpc (the C# reference) — the gRPC channel network
// transport. Collapses the C# folder's two files (GrpcTransportCommons.cs /
// GrpcNetworkTransport.cs) into this single Swift file per the tree's flat
// convention.
//
// Ported types (1:1 with the C# under src/CircleAI.Networking.Grpc/):
//   Enum     — GrpcChannelState
//   DTOs     — GrpcChannelDescriptor, GrpcRetryPolicy, GrpcCallSummary
//   Presets  — GrpcRetryPolicies (Default / Aggressive / NoRetry)
//   Metrics  — InMemoryGrpcCallMetrics
//   Transport— GrpcNetworkTransport (INetworkTransport) + IGrpcChannel
//
// Injected-socket note — the C# GrpcNetworkTransport wraps a concrete
// Grpc.Net.Client.GrpcChannel (a real socket) and its SendAsync deliberately
// throws NotSupportedException ("use the channel directly for typed proto
// clients"). This port follows the task rule "inject the socket behind an
// interface; no NotSupportedException/stubs; every contract gets a working
// deterministic implementation": the channel is injected behind IGrpcChannel and
// Send/Receive are a working, deterministic loopback through it. The channel
// lifecycle (Idle → Ready on start, Shutdown on stop) mirrors the descriptor /
// GrpcChannelState the C# commons model.
//
// Concurrency (same rules as Networking.swift):
//   • Snapshot continuations UNDER the NSLock and finish() OUTSIDE it.
//   • The inbound stream is single-consumer, unbounded; a message the channel
//     feeds before receive() is iterated is retained, not lost.

import Foundation

// ──────────────────────────────────────────────────────────────────────────
// GrpcChannelState (GrpcTransportCommons.cs)
//
// Int-raw + Codable; ordinals follow the C# declaration order.
// ──────────────────────────────────────────────────────────────────────────

/// The connectivity state of a gRPC channel. Ordinals mirror the C#
/// `GrpcChannelState` declaration order (the gRPC connectivity-state machine).
public enum GrpcChannelState: Int, Codable, Sendable, CaseIterable {
    case idle = 0
    case connecting = 1
    case ready = 2
    case transientFailure = 3
    case shutdown = 4
}

// ──────────────────────────────────────────────────────────────────────────
// GrpcChannelDescriptor / GrpcRetryPolicy / GrpcCallSummary (records)
// ──────────────────────────────────────────────────────────────────────────

/// Describes a gRPC channel's target + limits. Ported from the C#
/// `GrpcChannelDescriptor` record. `keepAliveInterval` is seconds (C#'s
/// TimeSpan).
public struct GrpcChannelDescriptor: Sendable, Equatable, Codable {
    public let target: String
    public let useTls: Bool
    public let maxReceiveBytes: Int
    public let maxSendBytes: Int
    public let keepAliveInterval: TimeInterval

    public init(
        target: String,
        useTls: Bool,
        maxReceiveBytes: Int,
        maxSendBytes: Int,
        keepAliveInterval: TimeInterval
    ) {
        self.target = target
        self.useTls = useTls
        self.maxReceiveBytes = maxReceiveBytes
        self.maxSendBytes = maxSendBytes
        self.keepAliveInterval = keepAliveInterval
    }
}

/// A retry policy for gRPC calls. Ported from the C# `GrpcRetryPolicy` record.
/// Backoff fields are seconds (C#'s TimeSpan).
public struct GrpcRetryPolicy: Sendable, Equatable, Codable {
    public let maxAttempts: Int
    public let initialBackoff: TimeInterval
    public let maxBackoff: TimeInterval
    public let multiplier: Double
    public let retryableStatusCodes: [String]

    public init(
        maxAttempts: Int,
        initialBackoff: TimeInterval,
        maxBackoff: TimeInterval,
        multiplier: Double,
        retryableStatusCodes: [String]
    ) {
        self.maxAttempts = maxAttempts
        self.initialBackoff = initialBackoff
        self.maxBackoff = maxBackoff
        self.multiplier = multiplier
        self.retryableStatusCodes = retryableStatusCodes
    }

    /// The backoff delay before `attempt` (0-based), capped at `maxBackoff`:
    /// `min(initialBackoff * multiplier^attempt, maxBackoff)`. This realises the
    /// exponential-backoff schedule the policy fields describe (deterministic,
    /// no wall-clock sleeping in this port).
    public func backoff(forAttempt attempt: Int) -> TimeInterval {
        guard attempt > 0 else { return initialBackoff }
        let scaled = initialBackoff * pow(multiplier, Double(attempt))
        return min(scaled, maxBackoff)
    }

    /// True when `statusCode` is in `retryableStatusCodes`.
    public func isRetryable(_ statusCode: String) -> Bool {
        retryableStatusCodes.contains(statusCode)
    }
}

/// A summary of a single gRPC call. Ported from the C# `GrpcCallSummary` record.
/// `latency` is seconds (C#'s TimeSpan).
public struct GrpcCallSummary: Sendable, Equatable, Codable {
    public let method: String
    public let attempts: Int
    public let latency: TimeInterval
    public let statusCode: String
    public let atUtc: Date

    public init(
        method: String,
        attempts: Int,
        latency: TimeInterval,
        statusCode: String,
        atUtc: Date
    ) {
        self.method = method
        self.attempts = attempts
        self.latency = latency
        self.statusCode = statusCode
        self.atUtc = atUtc
    }
}

// ──────────────────────────────────────────────────────────────────────────
// GrpcRetryPolicies (static presets)
// ──────────────────────────────────────────────────────────────────────────

/// Well-known gRPC retry policy presets. Ported from the C# static
/// `GrpcRetryPolicies` (values match exactly).
public enum GrpcRetryPolicies {
    /// 3 attempts, 100ms → 2s backoff, ×2, retry UNAVAILABLE / DEADLINE_EXCEEDED.
    public static let `default` = GrpcRetryPolicy(
        maxAttempts: 3,
        initialBackoff: 0.100,
        maxBackoff: 2.0,
        multiplier: 2.0,
        retryableStatusCodes: ["UNAVAILABLE", "DEADLINE_EXCEEDED"])

    /// 6 attempts, 50ms → 5s backoff, ×2, also retry RESOURCE_EXHAUSTED.
    public static let aggressive = GrpcRetryPolicy(
        maxAttempts: 6,
        initialBackoff: 0.050,
        maxBackoff: 5.0,
        multiplier: 2.0,
        retryableStatusCodes: ["UNAVAILABLE", "DEADLINE_EXCEEDED", "RESOURCE_EXHAUSTED"])

    /// 1 attempt, no backoff, no retryable codes.
    public static let noRetry = GrpcRetryPolicy(
        maxAttempts: 1,
        initialBackoff: 0,
        maxBackoff: 0,
        multiplier: 1.0,
        retryableStatusCodes: [])
}

// ──────────────────────────────────────────────────────────────────────────
// GrpcConnectionState (GrpcTransportCommons.cs)
//
// A SEPARATE enum from GrpcChannelState above (same members) modelling the
// lifecycle state of a managed connection as reconnection is driven. Int-raw +
// Codable; ordinals follow the C# declaration order.
// ──────────────────────────────────────────────────────────────────────────

/// Lifecycle state of a managed gRPC connection, mirroring the connectivity
/// states a channel steps through as reconnection is driven. Ported from the C#
/// `GrpcConnectionState` (declared alongside `GrpcChannelState`; same ordinals).
public enum GrpcConnectionState: Int, Codable, Sendable, CaseIterable {
    case idle = 0
    case connecting = 1
    case ready = 2
    case transientFailure = 3
    case shutdown = 4
}

// ──────────────────────────────────────────────────────────────────────────
// GrpcReconnectPolicy (GrpcTransportCommons.cs)
//
// Reconnection strategy for a managed channel: attempt budget + backoff growth.
// Backoff fields are seconds (C#'s TimeSpan). Distinct from GrpcRetryPolicy —
// this is the channel-lifecycle reconnection promise.
// ──────────────────────────────────────────────────────────────────────────

/// Reconnection strategy for a managed gRPC channel: how many attempts to make
/// and how to grow the backoff between them. Ported from the C#
/// `GrpcReconnectPolicy` record. Backoff fields are seconds (C#'s TimeSpan).
public struct GrpcReconnectPolicy: Sendable, Equatable, Codable {
    public let maxAttempts: Int
    public let initialBackoff: TimeInterval
    public let backoffMultiplier: Double
    public let maxBackoff: TimeInterval

    public init(
        maxAttempts: Int,
        initialBackoff: TimeInterval,
        backoffMultiplier: Double,
        maxBackoff: TimeInterval
    ) {
        self.maxAttempts = maxAttempts
        self.initialBackoff = initialBackoff
        self.backoffMultiplier = backoffMultiplier
        self.maxBackoff = maxBackoff
    }

    /// A sane default: 5 attempts, 200ms growing ×2 up to a 30s ceiling.
    /// Matches the C# `GrpcReconnectPolicy.Default`.
    public static let `default` = GrpcReconnectPolicy(
        maxAttempts: 5,
        initialBackoff: 0.200,
        backoffMultiplier: 2.0,
        maxBackoff: 30.0)

    /// Backoff before a given 1-based attempt:
    /// `initialBackoff × multiplier^(attempt-1)`, capped at `maxBackoff`.
    /// Attempt 1 returns `initialBackoff`. Mirrors C#'s `BackoffFor` including
    /// its overflow/infinity → `maxBackoff` cap. `attempt` is 1-based (C# throws
    /// `ArgumentOutOfRangeException` for attempt < 1; Swift uses `precondition`).
    public func backoffFor(_ attempt: Int) -> TimeInterval {
        precondition(attempt >= 1, "attempt is 1-based")
        let scaled = initialBackoff * pow(backoffMultiplier, Double(attempt - 1))
        if scaled.isInfinite || scaled > maxBackoff { return maxBackoff }
        return scaled
    }

    /// True when the 1-based attempt number is still within the retry budget
    /// (matches C#'s `ShouldRetry` → `attempt < MaxAttempts`).
    public func shouldRetry(_ attempt: Int) -> Bool { attempt < maxAttempts }
}

// ──────────────────────────────────────────────────────────────────────────
// GrpcDeadline (GrpcTransportCommons.cs)
//
// Deadline math for gRPC calls: relative timeout → absolute instant, plus
// remaining-time and expiry checks against a clock. C# uses DateTime; the Swift
// analogue is Date. Static, stateless helpers (ported as an enum namespace).
// ──────────────────────────────────────────────────────────────────────────

/// Deadline math for gRPC calls. Ported from the C# static `GrpcDeadline`.
public enum GrpcDeadline {
    /// Absolute deadline for a call started at `nowUtc` with the given timeout.
    /// Mirrors C#'s `FromTimeout` (negative timeout is a programmer error — C#
    /// throws `ArgumentOutOfRangeException`; Swift uses `precondition`).
    public static func fromTimeout(_ timeout: TimeInterval, nowUtc: Date) -> Date {
        precondition(timeout >= 0, "timeout must be non-negative")
        return nowUtc.addingTimeInterval(timeout)
    }

    /// Time left before `deadlineUtc`, clamped to zero once passed (matches C#'s
    /// `Remaining`).
    public static func remaining(_ deadlineUtc: Date, nowUtc: Date) -> TimeInterval {
        let left = deadlineUtc.timeIntervalSince(nowUtc)
        return left > 0 ? left : 0
    }

    /// True once `nowUtc` has reached or passed the deadline (matches C#'s
    /// `IsExpired` → `nowUtc >= deadlineUtc`).
    public static func isExpired(_ deadlineUtc: Date, nowUtc: Date) -> Bool {
        nowUtc >= deadlineUtc
    }
}

// ──────────────────────────────────────────────────────────────────────────
// InMemoryGrpcCallMetrics (GrpcTransportCommons.cs)
//
// C# uses two ConcurrentDictionaries (channels + states), a lock-guarded call
// list, and an Interlocked seq counter for the LogCall id. Here a single NSLock
// guards all mutable state; the returned id format `grpc-{n}` matches C#.
// ──────────────────────────────────────────────────────────────────────────

/// In-memory gRPC channel/call metrics. Ported from the C#
/// `InMemoryGrpcCallMetrics`.
public final class InMemoryGrpcCallMetrics: @unchecked Sendable {
    private let lock = NSLock()
    private var channels: [String: GrpcChannelDescriptor] = [:]
    private var states: [String: GrpcChannelState] = [:]
    private var calls: [GrpcCallSummary] = []
    private var seq: Int64 = 0

    public init() {}

    /// Register (or replace) a channel descriptor keyed by `id`.
    public func registerChannel(_ id: String, _ d: GrpcChannelDescriptor) {
        lock.lock(); channels[id] = d; lock.unlock()
    }

    /// The channel descriptor for `id`, or nil.
    public func getChannel(_ id: String) -> GrpcChannelDescriptor? {
        lock.lock(); defer { lock.unlock() }
        return channels[id]
    }

    /// Set the state for a channel.
    public func setState(_ id: String, _ s: GrpcChannelState) {
        lock.lock(); states[id] = s; lock.unlock()
    }

    /// The state for a channel, or `.idle` (matches C#'s default).
    public func state(_ id: String) -> GrpcChannelState {
        lock.lock(); defer { lock.unlock() }
        return states[id] ?? .idle
    }

    /// Log a call; return its id `grpc-{n}` (matches C#'s
    /// `$"grpc-{Interlocked.Increment(ref _seq)}"`, so the first id is `grpc-1`).
    @discardableResult
    public func logCall(_ c: GrpcCallSummary) -> String {
        lock.lock()
        calls.append(c)
        seq += 1
        let id = "grpc-\(seq)"
        lock.unlock()
        return id
    }

    /// The most recent `limit` calls, newest first (matches C#'s
    /// `OrderByDescending(c => c.AtUtc).Take(limit)`).
    public func recentCalls(limit: Int = 50) -> [GrpcCallSummary] {
        lock.lock(); defer { lock.unlock() }
        return Array(calls.sorted { $0.atUtc > $1.atUtc }.prefix(max(0, limit)))
    }
}

// ──────────────────────────────────────────────────────────────────────────
// IGrpcChannel (GrpcNetworkTransport.cs)
//
// The injected socket seam (the Swift analogue of Grpc.Net.Client.GrpcChannel).
// A platform/test implementation carries a payload to the peer (unary/stream
// call) and can push inbound messages back via the supplied IGrpcInboundWriter.
// ──────────────────────────────────────────────────────────────────────────

/// The sink an `IGrpcChannel` uses to push a received payload into the
/// transport's inbound stream.
public protocol IGrpcInboundWriter: AnyObject, Sendable {
    /// Deliver an inbound payload into the transport's receive stream. Returns
    /// false once the inbound stream has been completed (channel shut down).
    @discardableResult
    func push(_ payload: NetworkPayload) -> Bool
}

/// The injected gRPC channel — the Swift analogue of C#'s concrete `GrpcChannel`.
/// Implement per platform (or in tests) to carry payloads over a real channel.
public protocol IGrpcChannel: AnyObject {
    /// The channel's target descriptor.
    var descriptor: GrpcChannelDescriptor { get }

    /// Open the channel, retaining `inbound` for server-streamed messages.
    /// (C#'s `GrpcChannel.ForAddress` + first RPC establishes the connection.)
    func open(inbound: IGrpcInboundWriter) async throws

    /// Shut the channel down (C#'s `GrpcChannel.Dispose`).
    func shutdown() async throws

    /// Send a payload over the channel (a unary/client-stream call). Replaces the
    /// C# `SendAsync` NotSupportedException with a real, injected send path.
    func call(_ payload: NetworkPayload) async throws
}

// ──────────────────────────────────────────────────────────────────────────
// GrpcNetworkTransport (GrpcNetworkTransport.cs)
// ──────────────────────────────────────────────────────────────────────────

/// `INetworkTransport` backed by an injected gRPC channel. `start` opens the
/// channel (state → `.ready`, `isAvailable` → true, mirroring C#'s
/// `_running = true`); `stop` shuts it down (state → `.shutdown`, `isAvailable`
/// → false); `send` issues a channel call; `receive` drains messages the channel
/// pushes inbound. Unlike the C# (whose `SendAsync` throws), this is a working
/// deterministic send/receive path over the injected socket.
public final class GrpcNetworkTransport: INetworkTransport, @unchecked Sendable {
    /// The inbound sink handed to the channel. Buffers messages pushed before
    /// `receive()` is iterated (unbounded) so none are lost.
    private final class InboundWriter: IGrpcInboundWriter, @unchecked Sendable {
        private let lock = NSLock()
        private var completed = false
        private var pending: [NetworkPayload] = []
        private var continuation: AsyncStream<NetworkPayload>.Continuation?

        @discardableResult
        func push(_ payload: NetworkPayload) -> Bool {
            lock.lock()
            if completed { lock.unlock(); return false }
            if let cont = continuation {
                cont.yield(payload)
            } else {
                pending.append(payload)
            }
            lock.unlock()
            return true
        }

        func stream() -> AsyncStream<NetworkPayload> {
            AsyncStream(bufferingPolicy: .unbounded) { continuation in
                lock.lock()
                if completed {
                    lock.unlock()
                    continuation.finish()
                    return
                }
                for p in pending { continuation.yield(p) }
                pending.removeAll()
                self.continuation = continuation
                lock.unlock()

                continuation.onTermination = { [weak self] _ in
                    guard let self else { return }
                    self.lock.lock(); self.continuation = nil; self.lock.unlock()
                }
            }
        }

        func complete() {
            lock.lock()
            completed = true
            let cont = continuation
            continuation = nil
            pending.removeAll()
            lock.unlock()
            cont?.finish()
        }
    }

    private let channel: IGrpcChannel
    private let inbound = InboundWriter()

    private let lock = NSLock()
    private var running = false

    /// - Parameter channel: the injected gRPC channel (the socket seam).
    public init(channel: IGrpcChannel) {
        self.channel = channel
    }

    public var kind: TransportKind { .grpc }

    /// Mirrors C#'s `IsAvailable => _running`.
    public var isAvailable: Bool {
        lock.lock(); defer { lock.unlock() }
        return running
    }

    /// The channel's target descriptor (convenience, analogous to C#'s `Channel`
    /// property exposing the underlying channel).
    public var descriptor: GrpcChannelDescriptor { channel.descriptor }

    /// Opens the channel and marks the transport running (C#: `_running = true`).
    public func start() async throws {
        try await channel.open(inbound: inbound)
        lock.lock(); running = true; lock.unlock()
    }

    /// Marks the transport stopped, shuts the channel down, completes inbound.
    public func stop() async throws {
        lock.lock(); running = false; lock.unlock()
        try await channel.shutdown()
        inbound.complete()
    }

    /// Issues a channel call. Replaces C#'s `SendAsync` NotSupportedException with
    /// a working injected send path.
    public func send(_ payload: NetworkPayload) async throws {
        lock.lock()
        let up = running
        lock.unlock()
        guard up else { throw NetworkError.transportStopped }
        try await channel.call(payload)
    }

    /// Yields inbound payloads the channel pushed. (C#'s `ReceiveAsync` yields
    /// nothing pending the wire; here it is a working stream over the socket.)
    public func receive() -> AsyncStream<NetworkPayload> {
        inbound.stream()
    }
}
