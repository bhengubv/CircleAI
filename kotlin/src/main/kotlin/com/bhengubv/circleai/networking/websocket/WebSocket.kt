// WebSocket.kt
//
// Kotlin port of CircleAI.Networking.WebSocket (src/CircleAI.Networking.WebSocket/*.cs
// is the EXACT spec). A full-duplex [INetworkTransport] backed by a client WebSocket.
// Sends binary frames, receives into a 64 KiB buffer, and ends the receive pump on a
// close frame.
//
// The C# reference uses System.Net.WebSockets.ClientWebSocket (a real socket). Per the
// work unit ("in-memory, deterministic, socket injected"), the Kotlin port injects
// [IWebSocket] — connect / send-binary / receive-frame / close with a link state,
// standing in for ClientWebSocket. An [InMemoryWebSocket] loopback pair ships so the
// transport is fully exercisable with no real network; binary framing + close
// semantics are preserved.
//
// Covers (C# → Kotlin):
//   WebSocketTransportCommons.cs → WebSocketLinkState (enum, incl. Closed_Error),
//                                  WebSocketMessageType (enum),
//                                  WebSocketEndpointDescriptor,
//                                  WebSocketFrameSummary (records → data classes),
//                                  InMemoryWebSocketSessionRegistry
//   WebSocketTransport.cs        → WebSocketTransport (INetworkTransport,
//                                  AutoCloseable), IWebSocket (injected socket
//                                  contract), WebSocketFrame + InMemoryWebSocket
//                                  (loopback duplex stand-in)
//
// C# → Kotlin conventions:
//   record                          → data class
//   IReadOnlyList / IReadOnlyDict    → List / Map
//   ConcurrentDictionary + lock      → ConcurrentHashMap + synchronized
//   IAsyncDisposable                 → AutoCloseable
//   TimeSpan / Uri                   → java.time.Duration / java.net.URI
//   Task / IAsyncEnumerable<T>       → suspend fun / Flow<T>
//
// CONCURRENCY: the inbound channel is UNBOUNDED so the pump never blocks; the pump
// coroutine is launched during start() AFTER connect returns, reads frames until a
// Close frame (or a socket error) then completes the channel so the receive() flow
// ends. stop() sends a normal-closure close then completes the channel.
package com.bhengubv.circleai.networking.websocket

import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.launch
import java.net.URI
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// ===========================================================================
// Enums  (WebSocketTransportCommons.cs)
// ===========================================================================

/**
 * WebSocket link state machine. Member names match the C# enum exactly — including
 * `Closed_Error` (the underscored terminal-error state).
 */
enum class WebSocketLinkState { Closed, Connecting, Open, CloseSent, CloseReceived, Closed_Error }

/** WebSocket frame message type. */
enum class WebSocketMessageType { Text, Binary, Ping, Pong, Close }

// ===========================================================================
// Records  (WebSocketTransportCommons.cs)
// ===========================================================================

/** Description of a WebSocket endpoint: URI, headers, ping interval, subprotocols. */
data class WebSocketEndpointDescriptor(
    val uri: URI,
    val headers: Map<String, String>?,
    val pingInterval: Duration,
    val subprotocols: List<String>,
)

/** Summary of one WebSocket frame: session, type, byte count, time. */
data class WebSocketFrameSummary(
    val sessionId: String,
    val type: WebSocketMessageType,
    val bytes: Int,
    val atUtc: Instant,
)

// ===========================================================================
// InMemoryWebSocketSessionRegistry  (WebSocketTransportCommons.cs)
// ===========================================================================

/**
 * Deterministic in-memory store of WebSocket endpoints + per-session link state +
 * frame summaries. Mirrors the C# [ConcurrentDictionary] maps + `lock`ed frame list.
 * [state] defaults to [WebSocketLinkState.Closed].
 */
class InMemoryWebSocketSessionRegistry {
    private val endpoints = ConcurrentHashMap<String, WebSocketEndpointDescriptor>()
    private val states = ConcurrentHashMap<String, WebSocketLinkState>()
    private val frames = ArrayList<WebSocketFrameSummary>()
    private val lock = Any()

    /** Register (or replace) an endpoint by session id. */
    fun register(sessionId: String, d: WebSocketEndpointDescriptor) {
        endpoints[sessionId] = d
    }

    /** The endpoint for [sessionId], or null if unknown. */
    fun get(sessionId: String): WebSocketEndpointDescriptor? = endpoints[sessionId]

    /** Set the link state for [sessionId]. */
    fun setState(sessionId: String, s: WebSocketLinkState) {
        states[sessionId] = s
    }

    /** The link state for [sessionId], or [WebSocketLinkState.Closed] if unset. */
    fun state(sessionId: String): WebSocketLinkState =
        states[sessionId] ?: WebSocketLinkState.Closed

    /** Record a frame summary. */
    fun recordFrame(f: WebSocketFrameSummary) {
        synchronized(lock) { frames.add(f) }
    }

    /** Total bytes across all frames for [sessionId]. */
    fun totalBytes(sessionId: String): Long =
        synchronized(lock) { frames.filter { it.sessionId == sessionId }.sumOf { it.bytes.toLong() } }

    /** Number of frames of [type] for [sessionId]. */
    fun frameCount(sessionId: String, type: WebSocketMessageType): Int =
        synchronized(lock) { frames.count { it.sessionId == sessionId && it.type == type } }
}

// ===========================================================================
// IWebSocket  (injected socket contract standing in for ClientWebSocket)
// ===========================================================================

/** A single received WebSocket frame: its type and payload bytes. */
data class WebSocketFrame(val type: WebSocketMessageType, val payload: ByteArray) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is WebSocketFrame) return false
        return type == other.type && payload.contentEquals(other.payload)
    }

    override fun hashCode(): Int = 31 * type.hashCode() + payload.contentHashCode()
}

/**
 * The injected stand-in for ClientWebSocket. [state] tracks the link; [connect]
 * opens it; [sendBinary] writes a binary frame; [receive] blocks for the next frame
 * (a [WebSocketMessageType.Close] frame signals the peer closed); [close] sends a
 * normal-closure. Implementations must preserve binary payloads exactly.
 */
interface IWebSocket : AutoCloseable {
    /** Current link state. */
    val state: WebSocketLinkState

    /** Open the connection to [uri]. */
    suspend fun connect(uri: URI)

    /** Send [payload] as a binary frame. */
    suspend fun sendBinary(payload: ByteArray)

    /** Receive the next frame; a Close frame indicates the peer closed. */
    suspend fun receive(): WebSocketFrame

    /** Send a normal-closure close frame with [reason]. */
    suspend fun close(reason: String)

    override fun close() {}
}

// ===========================================================================
// InMemoryWebSocket  (loopback duplex stand-in)
// ===========================================================================

/**
 * A loopback duplex [IWebSocket] pair. [pair] returns two ends wired together:
 * a binary frame sent on one end is received on the other. Backed by UNBOUNDED frame
 * channels so sends never block; closing an end enqueues a [WebSocketMessageType.Close]
 * frame to the peer and flips both to a closed state (the peer's next [receive]
 * returns the Close frame, ending its pump — mirroring ClientWebSocket close).
 */
class InMemoryWebSocket private constructor(
    private val outbound: Channel<WebSocketFrame>,
    private val inbound: Channel<WebSocketFrame>,
) : IWebSocket {

    @Volatile private var linkState: WebSocketLinkState = WebSocketLinkState.Closed

    override val state: WebSocketLinkState get() = linkState

    override suspend fun connect(uri: URI) {
        linkState = WebSocketLinkState.Open
    }

    override suspend fun sendBinary(payload: ByteArray) {
        check(linkState == WebSocketLinkState.Open) { "WebSocket is not open." }
        outbound.send(WebSocketFrame(WebSocketMessageType.Binary, payload))
    }

    override suspend fun receive(): WebSocketFrame {
        val result = inbound.receiveCatching()
        return if (result.isClosed) {
            // The inbound side was closed (peer or local teardown). Surface a Close
            // frame so the transport pump ends — mirroring ClientWebSocket, where a
            // pending ReceiveAsync completes/throws once the socket is torn down.
            linkState = WebSocketLinkState.CloseReceived
            WebSocketFrame(WebSocketMessageType.Close, ByteArray(0))
        } else {
            val frame = result.getOrThrow()
            if (frame.type == WebSocketMessageType.Close) linkState = WebSocketLinkState.CloseReceived
            frame
        }
    }

    override suspend fun close(reason: String) {
        if (linkState == WebSocketLinkState.Open) {
            // Signal the peer with a Close frame before tearing down.
            outbound.trySend(WebSocketFrame(WebSocketMessageType.Close, reason.toByteArray(Charsets.UTF_8)))
        }
        linkState = WebSocketLinkState.CloseSent
        // Close both directions: the peer's receive sees the Close frame we just
        // queued, and our OWN pending receive on `inbound` unblocks (Close), so a
        // local close() ends the local receive loop even with no peer activity.
        outbound.close()
        inbound.close()
    }

    override fun close() {
        linkState = WebSocketLinkState.Closed
        outbound.close()
        inbound.close()
    }

    companion object {
        /** Two loopback ends wired together (a send reads on b, and vice-versa). */
        fun pair(): Pair<InMemoryWebSocket, InMemoryWebSocket> {
            val aToB = Channel<WebSocketFrame>(Channel.UNLIMITED)
            val bToA = Channel<WebSocketFrame>(Channel.UNLIMITED)
            val a = InMemoryWebSocket(outbound = aToB, inbound = bToA)
            val b = InMemoryWebSocket(outbound = bToA, inbound = aToB)
            return a to b
        }
    }
}

// ===========================================================================
// WebSocketTransport  (WebSocketTransport.cs)
// ===========================================================================

/**
 * Full-duplex [INetworkTransport] backed by a client WebSocket. [start] connects the
 * injected [IWebSocket] and launches an inbound pump; [send] writes a binary frame;
 * [stop] sends a normal-closure close then completes the inbox (ending [receive]);
 * [close] disposes the socket.
 *
 * The pump reads frames until a [WebSocketMessageType.Close] frame (or a socket
 * error), surfacing a [NetworkPayload] per non-close frame — matching the C# pump
 * that breaks on `Close` and on `WebSocketException`/`OperationCanceledException`.
 * Availability is `state == Open`, matching C#.
 *
 * @param socket injected WebSocket (stand-in for ClientWebSocket).
 * @param endpoint the ws:// or wss:// URI to connect to.
 * @param scope coroutine scope the inbound pump runs in (injectable for tests).
 */
class WebSocketTransport(
    private val socket: IWebSocket,
    endpoint: String,
    private val scope: CoroutineScope = CoroutineScope(Dispatchers.Default),
) : INetworkTransport, AutoCloseable {

    private val endpoint: URI = URI(endpoint)
    private val inbound = Channel<NetworkPayload>(Channel.UNLIMITED)
    @Volatile private var started = false

    override val kind: TransportKind get() = TransportKind.WebSocket
    override val isAvailable: Boolean get() = socket.state == WebSocketLinkState.Open

    override suspend fun start() {
        socket.connect(endpoint)
        started = true
        scope.launch { pump() }
    }

    override suspend fun stop() {
        if (started) {
            socket.close("stop")
        }
        inbound.close()
    }

    override suspend fun send(payload: NetworkPayload) {
        socket.sendBinary(payload.data)
    }

    override fun receive(): Flow<NetworkPayload> = flow {
        for (p in inbound) emit(p)
    }

    /**
     * Read frames until a Close frame or a socket error, then complete the inbox.
     * Mirrors the C# pump: break on Close, break on WebSocketException /
     * OperationCanceledException, `TryComplete()` at the end.
     */
    private suspend fun pump() {
        while (true) {
            val frame = try {
                socket.receive()
            } catch (_: Throwable) {
                break
            }
            if (frame.type == WebSocketMessageType.Close) break
            inbound.trySend(NetworkPayload.create(frame.payload))
        }
        inbound.close()
    }

    override fun close() {
        socket.close()
        inbound.close()
    }
}
