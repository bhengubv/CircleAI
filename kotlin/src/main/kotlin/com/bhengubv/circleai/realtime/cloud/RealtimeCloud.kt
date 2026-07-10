// RealtimeCloud.kt
//
// Kotlin port of CircleAI.Realtime.Cloud (IRealtimeTransport.cs) — the C#
// reference is the EXACT spec. Host-supplied WebSocket transport contract that
// framework-free vendor connectors (OpenAI / Gemini / Nova / ElevenLabs /
// Ultravox) drive; the ASP.NET / native host wires the real ClientWebSocket
// against it.
//
// Design fidelity notes:
//   * C# `IAsyncDisposable`                    -> `AutoCloseable` + suspend
//     `disposeAsync()` (the codebase convention).
//   * C# `ReadOnlyMemory<byte>`                -> `ByteArray`.
//   * C# `IAsyncEnumerable<T>`                 -> `kotlinx.coroutines.flow.Flow<T>`.
//   * C# `ValueTask<T>` / `ValueTask`          -> `suspend fun`.
//   * C# `IReadOnlyDictionary<string,string>?` -> `Map<String, String>?`.
//   * `NullRealtimeTransportFactory` throws on connect with the reference's exact
//     message — this is intentional parity (a "no factory wired" guard), not a stub.
//
// Plus a deterministic in-memory loopback transport + factory (a Kotlin dev/test
// helper, not part of the C# public surface) so the injected [IRealtimeTransport]
// seam has a real, hermetic implementation to exercise. It follows the wave's
// concurrency rules: UNBOUNDED buffering so a frame sent before a consumer
// attaches is retained (not lost), and each receive Flow drains its own channel.

package com.bhengubv.circleai.realtime.cloud

import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.net.URI
import java.util.concurrent.atomic.AtomicBoolean

// =====================================================================
// Contracts (IRealtimeTransport.cs)
// =====================================================================

/** WebSocket-style transport for a realtime session. Mirrors C# `IRealtimeTransport`. */
interface IRealtimeTransport : AutoCloseable {
    /** Send one JSON text frame. */
    suspend fun sendTextAsync(text: String)

    /** Send one binary frame. */
    suspend fun sendBinaryAsync(bytes: ByteArray)

    /** Stream incoming text frames. */
    fun receiveTextAsync(): Flow<String>

    /** Stream incoming binary frames. */
    fun receiveBinaryAsync(): Flow<ByteArray>

    /** Close the connection cleanly. */
    suspend fun closeAsync()

    /** True while the underlying socket is open. */
    val isOpen: Boolean

    /** Release the transport. Mirrors C# `IAsyncDisposable.DisposeAsync`. */
    suspend fun disposeAsync()

    /** [AutoCloseable] bridge — runs [disposeAsync] synchronously. */
    override fun close() {
        kotlinx.coroutines.runBlocking { disposeAsync() }
    }
}

/** Factory that produces transports for a given endpoint. Mirrors C# `IRealtimeTransportFactory`. */
interface IRealtimeTransportFactory {
    /** Connect to [endpoint] with the given [headers]. */
    suspend fun connectAsync(endpoint: URI, headers: Map<String, String>?): IRealtimeTransport
}

/**
 * Default transport factory that throws on connect — the host wires the real one.
 * Mirrors C# `NullRealtimeTransportFactory` (this throw is the documented "no
 * factory registered" guard, reproduced verbatim, not an unimplemented stub).
 */
class NullRealtimeTransportFactory private constructor() : IRealtimeTransportFactory {
    companion object {
        val Instance = NullRealtimeTransportFactory()
    }

    override suspend fun connectAsync(endpoint: URI, headers: Map<String, String>?): IRealtimeTransport =
        throw IllegalStateException(
            "No IRealtimeTransportFactory is registered. Add the host package that provides a real ClientWebSocket-based factory.",
        )
}

// =====================================================================
// In-memory loopback transport (Kotlin dev/test helper — not C# surface)
// =====================================================================

/**
 * Deterministic in-memory [IRealtimeTransport]. Everything written via
 * [sendTextAsync] / [sendBinaryAsync] is echoed back onto the matching receive
 * stream, so a connector can be exercised end-to-end with no socket. Buffering is
 * UNBOUNDED, so a frame sent before a consumer subscribes is retained and later
 * delivered (never dropped). [closeAsync] / [disposeAsync] complete both channels,
 * ending any active receive Flow and flipping [isOpen] to false.
 */
class InMemoryLoopbackTransport : IRealtimeTransport {
    private val text = Channel<String>(Channel.UNLIMITED)
    private val binary = Channel<ByteArray>(Channel.UNLIMITED)
    private val open = AtomicBoolean(true)

    override val isOpen: Boolean get() = open.get()

    override suspend fun sendTextAsync(text: String) {
        check(open.get()) { "transport is closed" }
        this.text.trySend(text)
    }

    override suspend fun sendBinaryAsync(bytes: ByteArray) {
        check(open.get()) { "transport is closed" }
        binary.trySend(bytes)
    }

    override fun receiveTextAsync(): Flow<String> = flow {
        for (t in text) emit(t)
    }

    override fun receiveBinaryAsync(): Flow<ByteArray> = flow {
        for (b in binary) emit(b)
    }

    override suspend fun closeAsync() {
        if (open.compareAndSet(true, false)) {
            text.close()
            binary.close()
        }
    }

    override suspend fun disposeAsync() {
        closeAsync()
    }
}

/** Factory that hands out [InMemoryLoopbackTransport]s. Kotlin dev/test helper. */
class InMemoryLoopbackTransportFactory : IRealtimeTransportFactory {
    override suspend fun connectAsync(endpoint: URI, headers: Map<String, String>?): IRealtimeTransport =
        InMemoryLoopbackTransport()
}
