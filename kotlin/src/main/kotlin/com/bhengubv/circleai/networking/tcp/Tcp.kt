// Tcp.kt
//
// Kotlin port of CircleAI.Networking.Tcp (src/CircleAI.Networking.Tcp/*.cs is the
// EXACT spec). An [INetworkTransport] over raw TCP: acts as a client when a remote
// endpoint is set, or a server listener when only a listen port is set. Framing is a
// 4-byte little-endian length prefix followed by the payload bytes — replicated
// byte-for-byte from the C# `BitConverter.GetBytes(data.Length)` + `ReadExactlyAsync`.
//
// The C# reference uses System.Net.Sockets (TcpClient / TcpListener / NetworkStream —
// real sockets). Per the work unit ("in-memory, deterministic, socket injected"),
// the Kotlin port injects [ITcpConnection] — a bidirectional byte stream with
// write + read-exactly, standing in for NetworkStream. An [InMemoryTcpConnection]
// duplex pair ships so the frame codec is fully exercisable with no real sockets; the
// little-endian length framing is identical on the wire.
//
// Covers (C# → Kotlin):
//   TcpTransportCommons.cs  → TcpConnectionState (enum), TcpEndpointDescriptor,
//                             TcpThroughputSample (records → data classes),
//                             TcpKnownPorts (static consts → object),
//                             InMemoryTcpConnectionRegistry
//   TcpNetworkTransport.cs  → TcpNetworkTransport (INetworkTransport, AutoCloseable),
//                             ITcpConnection (injected socket contract standing in for
//                             NetworkStream), InMemoryTcpConnection (loopback duplex),
//                             ITcpConnector (client-connect hook) + ITcpListener
//                             (server-accept hook)
//
// C# → Kotlin conventions:
//   record                          → data class
//   IReadOnlyList                    → List
//   ConcurrentDictionary + lock      → ConcurrentHashMap + synchronized
//   IAsyncDisposable                 → AutoCloseable
//   TimeSpan                         → java.time.Duration
//   BitConverter (LE) length prefix  → ByteBuffer(LITTLE_ENDIAN)
//   NetworkStream.ReadExactlyAsync   → ITcpConnection.readExactly
//   Task / IAsyncEnumerable<T>       → suspend fun / Flow<T>
//   static class                     → object
//
// CONCURRENCY: the inbound channel is UNBOUNDED so the pump never blocks; the pump
// coroutine is launched during start() AFTER the connection is established, and reads
// frames until the connection closes (readExactly throws / returns end), then
// completes the channel so the receive() flow ends. stop()/close() closes the
// connection which unblocks the pump.
package com.bhengubv.circleai.networking.tcp

import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.launch
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// ===========================================================================
// TcpConnectionState  (TcpTransportCommons.cs)
// ===========================================================================

/** Lifecycle state of a TCP connection. */
enum class TcpConnectionState { Disconnected, Connecting, Connected, Closing, Failed }

// ===========================================================================
// Records  (TcpTransportCommons.cs)
// ===========================================================================

/** Description of a TCP endpoint: host, port, socket options, connect timeout. */
data class TcpEndpointDescriptor(
    val host: String,
    val port: Int,
    val noDelay: Boolean,
    val keepAlive: Boolean,
    val connectTimeout: Duration,
)

/** One throughput sample (bytes sent/received) for an endpoint. */
data class TcpThroughputSample(
    val endpointId: String,
    val bytesSent: Long,
    val bytesReceived: Long,
    val atUtc: Instant,
)

// ===========================================================================
// TcpKnownPorts  (TcpTransportCommons.cs)
// ===========================================================================

/** Well-known TCP port constants. Matches the C# statics. */
object TcpKnownPorts {
    const val HTTP = 80
    const val HTTPS = 443
    const val SSH = 22
    const val SMTP = 25
    const val IMAP = 143
    const val IMAP_SSL = 993
    const val POP3 = 110
    const val POP3_SSL = 995
    const val MQTT = 1883
    const val MQTT_SSL = 8883
}

// ===========================================================================
// InMemoryTcpConnectionRegistry  (TcpTransportCommons.cs)
// ===========================================================================

/**
 * Deterministic in-memory store of TCP endpoints + per-endpoint connection state +
 * throughput samples. Mirrors the C# [ConcurrentDictionary] maps + `lock`ed
 * throughput list. [state] defaults to [TcpConnectionState.Disconnected].
 */
class InMemoryTcpConnectionRegistry {
    private val endpoints = ConcurrentHashMap<String, TcpEndpointDescriptor>()
    private val states = ConcurrentHashMap<String, TcpConnectionState>()
    private val throughput = ArrayList<TcpThroughputSample>()
    private val lock = Any()

    /** Register (or replace) an endpoint by id. */
    fun register(id: String, d: TcpEndpointDescriptor) {
        endpoints[id] = d
    }

    /** The endpoint for [id], or null if unknown. */
    fun get(id: String): TcpEndpointDescriptor? = endpoints[id]

    /** Set the connection state for [id]. */
    fun setState(id: String, s: TcpConnectionState) {
        states[id] = s
    }

    /** The connection state for [id], or [TcpConnectionState.Disconnected] if unset. */
    fun state(id: String): TcpConnectionState =
        states[id] ?: TcpConnectionState.Disconnected

    /** Record a throughput sample. */
    fun recordSample(s: TcpThroughputSample) {
        synchronized(lock) { throughput.add(s) }
    }

    /** Total bytes sent across all samples for [id]. */
    fun totalBytesSent(id: String): Long =
        synchronized(lock) { throughput.filter { it.endpointId == id }.sumOf { it.bytesSent } }
}

// ===========================================================================
// ITcpConnection  (injected socket contract standing in for NetworkStream)
// ===========================================================================

/**
 * A bidirectional byte stream — the injected stand-in for NetworkStream. [write]
 * appends bytes to the peer's read side; [readExactly] fills [count] bytes or throws
 * when the stream is closed before that many arrive (the analogue of
 * `NetworkStream.ReadExactlyAsync`, which throws EndOfStreamException).
 */
interface ITcpConnection : AutoCloseable {
    /** Whether the connection is open. */
    val isConnected: Boolean

    /** Write [data] to the peer. */
    suspend fun write(data: ByteArray)

    /** Read exactly [count] bytes, or throw if the stream ends first. */
    suspend fun readExactly(count: Int): ByteArray

    /** Close the connection. */
    override fun close()
}

/**
 * Establishes an outbound [ITcpConnection] to a remote endpoint — the injected
 * stand-in for `TcpClient.ConnectAsync`. Deterministic connectors return a ready
 * in-memory duplex.
 */
interface ITcpConnector {
    /** Connect to [descriptor], returning the live connection. */
    suspend fun connect(descriptor: TcpEndpointDescriptor): ITcpConnection
}

/**
 * Accepts inbound [ITcpConnection]s on a listen port — the injected stand-in for
 * `TcpListener`. [start] binds; [accept] yields the next inbound connection (or null
 * when the listener is stopped); [stop] unbinds.
 */
interface ITcpListener {
    /** Bind and begin listening. */
    fun start()

    /** The next accepted connection, or null once the listener is stopped. */
    suspend fun accept(): ITcpConnection?

    /** Stop listening. */
    fun stop()
}

// ===========================================================================
// InMemoryTcpConnection  (loopback duplex stand-in)
// ===========================================================================

/**
 * A loopback duplex [ITcpConnection] pair. [pair] returns two ends wired together:
 * bytes written to one end can be [readExactly]'d from the other. Backed by an
 * UNBOUNDED byte channel per direction so writes never block; closing either end
 * closes both directions and unblocks a pending read (which then throws, mirroring
 * NetworkStream end-of-stream).
 */
class InMemoryTcpConnection private constructor(
    private val outbound: Channel<Byte>,
    private val inbound: Channel<Byte>,
    private val shared: SharedState,
) : ITcpConnection {

    private class SharedState {
        @Volatile var open = true
    }

    override val isConnected: Boolean get() = shared.open

    override suspend fun write(data: ByteArray) {
        check(shared.open) { "TCP connection is closed." }
        for (b in data) outbound.send(b)
    }

    override suspend fun readExactly(count: Int): ByteArray {
        val buf = ByteArray(count)
        var i = 0
        while (i < count) {
            val b = inbound.receiveCatchingClosed()
                ?: throw java.io.EOFException("Stream closed before $count bytes were read (got $i).")
            buf[i++] = b
        }
        return buf
    }

    override fun close() {
        shared.open = false
        outbound.close()
        inbound.close()
    }

    private suspend fun Channel<Byte>.receiveCatchingClosed(): Byte? {
        val result = receiveCatching()
        return if (result.isClosed) null else result.getOrThrow()
    }

    companion object {
        /** Two loopback ends wired together (a→b write reads on b, and vice-versa). */
        fun pair(): Pair<InMemoryTcpConnection, InMemoryTcpConnection> {
            val aToB = Channel<Byte>(Channel.UNLIMITED)
            val bToA = Channel<Byte>(Channel.UNLIMITED)
            val shared = SharedState()
            val a = InMemoryTcpConnection(outbound = aToB, inbound = bToA, shared = shared)
            val b = InMemoryTcpConnection(outbound = bToA, inbound = aToB, shared = shared)
            return a to b
        }
    }
}

// ===========================================================================
// TcpNetworkTransport  (TcpNetworkTransport.cs)
// ===========================================================================

/**
 * [INetworkTransport] over raw TCP. Client mode when [remote] is set (connects via
 * the injected [ITcpConnector] and pumps inbound frames); server mode when only
 * [listenPort] is set (binds the injected [ITcpListener]).
 *
 * Frame codec (identical to C#): each send writes a 4-byte LITTLE-ENDIAN length
 * prefix followed by the payload bytes; the pump reads the 4-byte length then that
 * many bytes and surfaces a [NetworkPayload] per frame.
 *
 * Availability tracks the client connection (`isAvailable == connection.isConnected`),
 * matching C# `_client?.Connected ?? false`.
 *
 * @param connector injected outbound-connection hook (stand-in for TcpClient).
 * @param remote the remote endpoint to dial (client mode). Mutually exclusive-ish with
 *   [listenPort]; if both are null, start() is a no-op (matches C#).
 * @param listener injected inbound-accept hook (stand-in for TcpListener).
 * @param listenPort the port to bind (server mode).
 * @param scope coroutine scope the inbound pump runs in (injectable for tests).
 */
class TcpNetworkTransport(
    private val connector: ITcpConnector? = null,
    private val remote: TcpEndpointDescriptor? = null,
    private val listener: ITcpListener? = null,
    private val listenPort: Int? = null,
    private val scope: CoroutineScope = CoroutineScope(Dispatchers.Default),
) : INetworkTransport, AutoCloseable {

    private val inbound = Channel<NetworkPayload>(Channel.UNLIMITED)
    @Volatile private var connection: ITcpConnection? = null
    @Volatile private var started = false

    override val kind: TransportKind get() = TransportKind.Tcp
    override val isAvailable: Boolean get() = connection?.isConnected ?: false

    override suspend fun start() {
        if (remote != null) {
            val conn = requireNotNull(connector) { "A connector is required for client mode." }
                .connect(remote)
            connection = conn
            started = true
            // Launch the pump AFTER the connection is live so no frame is missed.
            scope.launch { pump(conn) }
        } else if (listenPort != null) {
            val l = requireNotNull(listener) { "A listener is required for server mode." }
            l.start()
            started = true
        }
    }

    override suspend fun stop() {
        closeNow()
    }

    /** Synchronous teardown: close the connection + stop the listener, complete the inbox. */
    private fun closeNow() {
        if (!started) {
            inbound.close()
            return
        }
        started = false
        connection?.close()
        connection = null
        listener?.stop()
        inbound.close()
    }

    /**
     * Frame [payload]: write a 4-byte LITTLE-ENDIAN length prefix then the data.
     * Mirrors C#'s `BitConverter.GetBytes(data.Length)` (little-endian on all
     * supported platforms) + two `WriteAsync` calls.
     */
    override suspend fun send(payload: NetworkPayload) {
        val conn = connection ?: throw IllegalStateException("Not connected.")
        val data = payload.data
        val len = ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(data.size).array()
        conn.write(len)
        conn.write(data)
    }

    override fun receive(): Flow<NetworkPayload> = flow {
        for (p in inbound) emit(p)
    }

    /**
     * Read frames until the connection ends: 4-byte LE length, then that many bytes.
     * On any read failure (closed stream) the loop breaks and the inbox completes —
     * matching the C# `catch { break; }` + `TryComplete()`.
     */
    private suspend fun pump(conn: ITcpConnection) {
        while (true) {
            try {
                val lenBuf = conn.readExactly(4)
                val len = ByteBuffer.wrap(lenBuf).order(ByteOrder.LITTLE_ENDIAN).int
                val data = conn.readExactly(len)
                inbound.trySend(NetworkPayload.create(data))
            } catch (_: Throwable) {
                break
            }
        }
        inbound.close()
    }

    override fun close() {
        closeNow()
    }
}
