// Grpc.kt
//
// Kotlin port of CircleAI.Networking.Grpc (src/CircleAI.Networking.Grpc/*.cs is the
// EXACT spec). An [INetworkTransport] backed by a gRPC channel — manages channel
// lifecycle, deadlines, and reconnection. The wire protocol (proto service) is
// defined by the consuming application, so the generic [send] path is intentionally
// unsupported (callers use the channel directly for typed proto clients).
//
// The C# reference depends on Grpc.Net.Client.GrpcChannel (a real socket-backed
// channel). Per the work unit ("in-memory, no real sockets; the socket is an
// injected interface"), the Kotlin port injects [IGrpcChannel] — the transport
// owns its lifecycle and exposes it via [channel], exactly as C# exposes the
// concrete GrpcChannel via its `Channel` property.
//
// Covers (C# → Kotlin):
//   GrpcTransportCommons.cs → GrpcChannelState (enum), GrpcChannelDescriptor,
//                             GrpcRetryPolicy, GrpcCallSummary (records → data
//                             classes), GrpcRetryPolicies (static → object),
//                             InMemoryGrpcCallMetrics
//   GrpcNetworkTransport.cs → GrpcNetworkTransport (INetworkTransport,
//                             AutoCloseable), IGrpcChannel (injected socket
//                             contract, standing in for Grpc.Net.Client.GrpcChannel)
//
// C# → Kotlin conventions:
//   record                        → data class
//   IReadOnlyList                  → List
//   TimeSpan                       → java.time.Duration
//   ConcurrentDictionary + lock    → ConcurrentHashMap + synchronized
//   Interlocked.Increment          → AtomicLong.incrementAndGet
//   IDisposable                    → AutoCloseable
//   NotSupportedException          → UnsupportedOperationException
//   Task / IAsyncEnumerable<T>     → suspend fun / Flow<T>
//   static class                   → object
package com.bhengubv.circleai.networking.grpc

import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

// ===========================================================================
// GrpcChannelState  (GrpcTransportCommons.cs)
// ===========================================================================

/** Connectivity state of a gRPC channel (mirrors gRPC's connectivity states). */
enum class GrpcChannelState { Idle, Connecting, Ready, TransientFailure, Shutdown }

// ===========================================================================
// Records  (GrpcTransportCommons.cs)
// ===========================================================================

/** Static description of a gRPC channel: target, TLS, size limits, keep-alive. */
data class GrpcChannelDescriptor(
    val target: String,
    val useTls: Boolean,
    val maxReceiveBytes: Int,
    val maxSendBytes: Int,
    val keepAliveInterval: Duration,
)

/** Retry policy for gRPC calls: attempts, backoff schedule, retryable status codes. */
data class GrpcRetryPolicy(
    val maxAttempts: Int,
    val initialBackoff: Duration,
    val maxBackoff: Duration,
    val multiplier: Double,
    val retryableStatusCodes: List<String>,
)

/** Summary of a single completed gRPC call. */
data class GrpcCallSummary(
    val method: String,
    val attempts: Int,
    val latency: Duration,
    val statusCode: String,
    val atUtc: Instant,
)

// ===========================================================================
// GrpcRetryPolicies  (GrpcTransportCommons.cs)
// ===========================================================================

/** The three canonical retry policies, matching the C# statics. */
object GrpcRetryPolicies {
    /** 3 attempts, 100ms→2s backoff (×2), retry on UNAVAILABLE + DEADLINE_EXCEEDED. */
    val Default: GrpcRetryPolicy = GrpcRetryPolicy(
        maxAttempts = 3,
        initialBackoff = Duration.ofMillis(100),
        maxBackoff = Duration.ofSeconds(2),
        multiplier = 2.0,
        retryableStatusCodes = listOf("UNAVAILABLE", "DEADLINE_EXCEEDED"),
    )

    /** 6 attempts, 50ms→5s backoff (×2), also retries RESOURCE_EXHAUSTED. */
    val Aggressive: GrpcRetryPolicy = GrpcRetryPolicy(
        maxAttempts = 6,
        initialBackoff = Duration.ofMillis(50),
        maxBackoff = Duration.ofSeconds(5),
        multiplier = 2.0,
        retryableStatusCodes = listOf("UNAVAILABLE", "DEADLINE_EXCEEDED", "RESOURCE_EXHAUSTED"),
    )

    /** 1 attempt, no backoff, no retries. */
    val NoRetry: GrpcRetryPolicy = GrpcRetryPolicy(
        maxAttempts = 1,
        initialBackoff = Duration.ZERO,
        maxBackoff = Duration.ZERO,
        multiplier = 1.0,
        retryableStatusCodes = emptyList(),
    )
}

// ===========================================================================
// InMemoryGrpcCallMetrics  (GrpcTransportCommons.cs)
// ===========================================================================

/**
 * Deterministic in-memory metrics for gRPC channels + calls. Mirrors the C#
 * [ConcurrentDictionary] maps + `lock`ed call list + `Interlocked` sequence.
 * [logCall] returns a monotonic `grpc-N` correlation id; [recentCalls] is newest
 * first.
 */
class InMemoryGrpcCallMetrics {
    private val channels = ConcurrentHashMap<String, GrpcChannelDescriptor>()
    private val states = ConcurrentHashMap<String, GrpcChannelState>()
    private val calls = ArrayList<GrpcCallSummary>()
    private val lock = Any()
    private val seq = AtomicLong(0)

    /** Register (or replace) a channel descriptor by id. */
    fun registerChannel(id: String, d: GrpcChannelDescriptor) {
        channels[id] = d
    }

    /** The channel descriptor for [id], or null if unknown. */
    fun getChannel(id: String): GrpcChannelDescriptor? = channels[id]

    /** Set the connectivity state for channel [id]. */
    fun setState(id: String, s: GrpcChannelState) {
        states[id] = s
    }

    /** The connectivity state for [id], or [GrpcChannelState.Idle] if unset. */
    fun state(id: String): GrpcChannelState = states[id] ?: GrpcChannelState.Idle

    /** Log a completed call; returns a monotonic `grpc-N` correlation id. */
    fun logCall(c: GrpcCallSummary): String {
        synchronized(lock) { calls.add(c) }
        return "grpc-${seq.incrementAndGet()}"
    }

    /** The [limit] most recent call summaries, newest first. */
    fun recentCalls(limit: Int = 50): List<GrpcCallSummary> =
        synchronized(lock) {
            calls.sortedByDescending { it.atUtc }.take(limit)
        }
}

// ===========================================================================
// GrpcConnectionState  (GrpcTransportCommons.cs)
// ===========================================================================

/**
 * Lifecycle state of a managed gRPC connection, mirroring the connectivity
 * states a channel steps through as reconnection is driven.
 */
enum class GrpcConnectionState { Idle, Connecting, Ready, TransientFailure, Shutdown }

// ===========================================================================
// GrpcReconnectPolicy  (GrpcTransportCommons.cs)
// ===========================================================================

/**
 * Reconnection strategy for a managed gRPC channel: how many attempts to make and
 * how to grow the backoff between them. Fulfils the channel-lifecycle and
 * reconnection promise of `GrpcNetworkTransport` without any transport deps.
 */
data class GrpcReconnectPolicy(
    val maxAttempts: Int,
    val initialBackoff: Duration,
    val backoffMultiplier: Double,
    val maxBackoff: Duration,
) {
    /**
     * Backoff before a given 1-based attempt: `initialBackoff × multiplier^(attempt-1)`,
     * capped at [maxBackoff]. Attempt 1 returns [initialBackoff]. Overflow-safe:
     * an infinite or over-cap scaled value clamps to [maxBackoff] (mirrors the C#
     * `double.IsInfinity(scaled) || scaled > capMs` guard).
     */
    fun backoffFor(attempt: Int): Duration {
        require(attempt >= 1) { "attempt is 1-based" }
        val scaled = initialBackoff.toMillis().toDouble() * Math.pow(backoffMultiplier, (attempt - 1).toDouble())
        val capMs = maxBackoff.toMillis().toDouble()
        if (scaled.isInfinite() || scaled > capMs) return maxBackoff
        return Duration.ofMillis(scaled.toLong())
    }

    /** True when the 1-based attempt number is still within the retry budget. */
    fun shouldRetry(attempt: Int): Boolean = attempt < maxAttempts

    companion object {
        /** A sane default: 5 attempts, 200ms growing ×2 up to a 30s ceiling. */
        val Default: GrpcReconnectPolicy =
            GrpcReconnectPolicy(5, Duration.ofMillis(200), 2.0, Duration.ofSeconds(30))
    }
}

// ===========================================================================
// GrpcDeadline  (GrpcTransportCommons.cs)
// ===========================================================================

/**
 * Deadline math for gRPC calls: turns a relative timeout into the absolute UTC
 * instant a call must complete by, and reports remaining time against a clock.
 */
object GrpcDeadline {
    /** Absolute deadline for a call started at [nowUtc] with the given timeout. */
    fun fromTimeout(timeout: Duration, nowUtc: Instant): Instant {
        require(!timeout.isNegative) { "timeout" }
        return nowUtc.plus(timeout)
    }

    /** Time left before [deadlineUtc], clamped to zero once passed. */
    fun remaining(deadlineUtc: Instant, nowUtc: Instant): Duration {
        val left = Duration.between(nowUtc, deadlineUtc)
        return if (left > Duration.ZERO) left else Duration.ZERO
    }

    /** True once [nowUtc] has reached or passed the deadline. */
    fun isExpired(deadlineUtc: Instant, nowUtc: Instant): Boolean = !nowUtc.isBefore(deadlineUtc)
}

// ===========================================================================
// IGrpcChannel  (injected socket contract for GrpcNetworkTransport)
// ===========================================================================

/**
 * Minimal handle to a gRPC channel — the injected stand-in for
 * Grpc.Net.Client.GrpcChannel. The transport owns its lifecycle and closes it on
 * teardown; typed proto clients are created from it directly.
 */
interface IGrpcChannel : AutoCloseable {
    /** The channel target address (e.g. `https://host:443`). */
    val target: String
}

// ===========================================================================
// GrpcNetworkTransport  (GrpcNetworkTransport.cs)
// ===========================================================================

/**
 * [INetworkTransport] backed by a gRPC channel. Manages channel lifecycle,
 * deadlines, and reconnection. The wire protocol (proto service) is defined by the
 * consuming application.
 *
 * [start]/[stop] flip the running flag (which drives [isAvailable]); [send] is
 * intentionally unsupported — gRPC streaming calls are protocol-specific, so callers
 * use [channel] directly for typed proto clients; [receive] yields nothing (the
 * generic pull path is not a gRPC concept). [close] disposes the channel.
 */
class GrpcNetworkTransport(
    private val grpcChannel: IGrpcChannel,
) : INetworkTransport, AutoCloseable {

    @Volatile private var running = false

    override val kind: TransportKind get() = TransportKind.Grpc
    override val isAvailable: Boolean get() = running

    override suspend fun start() {
        running = true
    }

    override suspend fun stop() {
        running = false
    }

    /**
     * gRPC streaming calls are protocol-specific. This method is intentionally not a
     * generic send path — callers use [channel] directly for typed proto clients.
     * Throws [UnsupportedOperationException] (mirrors C# `NotSupportedException`).
     */
    override suspend fun send(payload: NetworkPayload): Nothing =
        throw UnsupportedOperationException(
            "Use the gRPC channel directly for typed proto clients. " +
                "GrpcNetworkTransport.send is not a generic send path.",
        )

    override fun receive(): Flow<NetworkPayload> = emptyFlow()

    /** Exposes the underlying channel for typed gRPC client creation. */
    val channel: IGrpcChannel get() = grpcChannel

    override fun close() {
        grpcChannel.close()
    }
}
