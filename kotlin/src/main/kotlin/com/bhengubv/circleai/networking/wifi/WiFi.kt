// WiFi.kt
//
// Kotlin port of CircleAI.Networking.WiFi (src/CircleAI.Networking.WiFi/*.cs is the
// EXACT spec). An [INetworkTransport] using LAN UDP broadcast / unicast, plus a
// UDP-beacon [WiFiPeerDiscovery]. No Aether, no cloud, no infrastructure — works
// whenever devices share a WiFi network.
//
// The C# reference uses System.Net.Sockets.UdpClient (real sockets). Per the work
// unit ("in-memory, deterministic, socket injected"), the Kotlin port injects
// [IUdpSocket] — bind / send-to / receive-datagram with a broadcast flag, standing in
// for UdpClient. An [InMemoryUdpNetwork] (an in-process datagram bus keyed by port)
// ships so the transport + discovery are fully exercisable with no real sockets. The
// wire behaviour is preserved exactly: DataPort framing, the IP-parse decision
// between unicast and broadcast, and the `CIRCLEAI:BEACON:{nodeId}` beacon on
// DiscoveryPort.
//
// Covers (C# → Kotlin):
//   WiFiNetworkTransport.cs → WiFiNetworkTransport (INetworkTransport, AutoCloseable),
//                             DiscoveryPort / DataPort consts, IUdpSocket (injected
//                             socket contract), UdpDatagram + IUdpNetwork +
//                             InMemoryUdpNetwork (the deterministic datagram bus)
//   WiFiPeerDiscovery.cs    → WiFiPeerDiscovery (IPeerDiscovery), BeaconMagic
//
// C# → Kotlin conventions:
//   IPAddress.TryParse               → parseIp (returns non-null on a valid IPv4/IPv6)
//   IPAddress.Broadcast              → BROADCAST_ADDRESS ("255.255.255.255")
//   Encoding.UTF8                    → Charsets.UTF_8
//   UdpReceiveResult (buf + remote)  → UdpDatagram(data, fromAddress, fromPort)
//   IAsyncDisposable                 → AutoCloseable
//   Task / IAsyncEnumerable<T>       → suspend fun / Flow<T>
//
// CONCURRENCY: the inbound channel is UNBOUNDED so the pump never blocks; the pump
// coroutine is launched during start() after the receiver is bound, reads datagrams
// until the socket closes, then completes the channel so the receive() flow ends.
// The datagram bus subscribes a port's receiver SYNCHRONOUSLY on bind, so a datagram
// sent right after bind is delivered, never raced/lost.
package com.bhengubv.circleai.networking.wifi

import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.PeerInfo
import com.bhengubv.circleai.networking.PeerRole
import com.bhengubv.circleai.networking.TransportKind
import com.bhengubv.circleai.networking.aethernet.IPeerDiscovery
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.launch
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// ===========================================================================
// UDP socket injection  (stand-in for System.Net.Sockets.UdpClient)
// ===========================================================================

/** A received UDP datagram: the bytes plus the sender's address + port. */
data class UdpDatagram(val data: ByteArray, val fromAddress: String, val fromPort: Int) {
    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is UdpDatagram) return false
        return data.contentEquals(other.data) && fromAddress == other.fromAddress && fromPort == other.fromPort
    }

    override fun hashCode(): Int {
        var result = data.contentHashCode()
        result = 31 * result + fromAddress.hashCode()
        result = 31 * result + fromPort.hashCode()
        return result
    }
}

/**
 * The injected stand-in for UdpClient. A socket may be bound to a receive port (so it
 * can [receive] datagrams) or unbound (send-only). [sendTo] delivers [data] to
 * ([toAddress], [toPort]); a broadcast address fans out to every socket bound to that
 * port. [enableBroadcast] mirrors `UdpClient.EnableBroadcast`.
 */
interface IUdpSocket : AutoCloseable {
    /** Whether the socket is currently open. */
    val isOpen: Boolean

    /** Whether broadcast sends are permitted (mirrors UdpClient.EnableBroadcast). */
    var enableBroadcast: Boolean

    /** Send [data] to ([toAddress], [toPort]). */
    suspend fun sendTo(data: ByteArray, toAddress: String, toPort: Int)

    /** Receive the next datagram (blocks); throws once the socket is closed. */
    suspend fun receive(): UdpDatagram

    /** Close the socket. */
    override fun close()
}

/**
 * Factory + router for [IUdpSocket]s — the injected stand-in for the OS UDP stack.
 * [openBound] binds a receive socket to [port]; [openUnbound] returns a send-only
 * socket. Routing: a unicast send reaches the single socket bound to the destination
 * port; a broadcast send ([BROADCAST_ADDRESS]) reaches EVERY socket bound to that
 * port (the sender does not receive its own broadcast — loopback, not echo).
 */
interface IUdpNetwork {
    /** Open a socket bound to [port] with the given source [address]. */
    fun openBound(port: Int, address: String = "127.0.0.1"): IUdpSocket

    /** Open an ephemeral send-only socket with the given source [address]. */
    fun openUnbound(address: String = "127.0.0.1"): IUdpSocket

    companion object {
        /** The IPv4 limited-broadcast address (C# `IPAddress.Broadcast`). */
        const val BROADCAST_ADDRESS: String = "255.255.255.255"
    }
}

// ===========================================================================
// InMemoryUdpNetwork  (deterministic in-process datagram bus)
// ===========================================================================

/**
 * Deterministic in-process datagram bus implementing [IUdpNetwork]. Sockets bound to
 * the same port share a receive fan-out; a broadcast send reaches all of them, a
 * unicast send reaches the first bound socket for the destination port. UNBOUNDED
 * per-socket inbox channels ensure a send never blocks.
 *
 * A sender's ephemeral source port is synthesised (negative, unique) so it never
 * collides with a real bound port; the sender is excluded from its own broadcast.
 */
class InMemoryUdpNetwork : IUdpNetwork {

    private val boundByPort = ConcurrentHashMap<Int, MutableList<InMemoryUdpSocket>>()
    private val lock = Any()
    private var ephemeralCounter = -1

    private fun nextEphemeralPort(): Int = synchronized(lock) { ephemeralCounter-- }

    override fun openBound(port: Int, address: String): IUdpSocket {
        val sock = InMemoryUdpSocket(this, address, port)
        synchronized(lock) {
            boundByPort.getOrPut(port) { ArrayList() }.add(sock)
        }
        return sock
    }

    override fun openUnbound(address: String): IUdpSocket =
        InMemoryUdpSocket(this, address, nextEphemeralPort())

    internal fun deregister(sock: InMemoryUdpSocket) {
        synchronized(lock) {
            boundByPort[sock.boundPort]?.let { list ->
                list.remove(sock)
                if (list.isEmpty()) boundByPort.remove(sock.boundPort)
            }
        }
    }

    /**
     * Route a datagram from [sender] to ([toAddress], [toPort]). Snapshot targets
     * under the lock, RELEASE it, then deliver — so a receiver's close/deregister
     * (which also takes the lock) can never self-deadlock.
     */
    internal fun route(sender: InMemoryUdpSocket, data: ByteArray, toAddress: String, toPort: Int) {
        val targets: List<InMemoryUdpSocket> = synchronized(lock) {
            val bound = boundByPort[toPort]?.toList() ?: emptyList()
            if (toAddress == IUdpNetwork.BROADCAST_ADDRESS) {
                // Broadcast: everyone bound to the port except the sender itself.
                bound.filter { it !== sender }
            } else {
                // Unicast: the first socket bound to the port (deterministic).
                bound.take(1)
            }
        }
        val datagram = UdpDatagram(data.copyOf(), sender.sourceAddress, sender.sourcePort)
        for (t in targets) t.deliver(datagram)
    }
}

/** An [IUdpSocket] on an [InMemoryUdpNetwork]. */
class InMemoryUdpSocket internal constructor(
    private val network: InMemoryUdpNetwork,
    val sourceAddress: String,
    val boundPort: Int,
) : IUdpSocket {

    // For an unbound (send-only) socket the "bound" port is a synthetic negative id;
    // its source port for datagram provenance is that same id.
    val sourcePort: Int get() = boundPort

    private val inbox = Channel<UdpDatagram>(Channel.UNLIMITED)
    @Volatile private var open = true
    @Volatile override var enableBroadcast: Boolean = false

    override val isOpen: Boolean get() = open

    override suspend fun sendTo(data: ByteArray, toAddress: String, toPort: Int) {
        check(open) { "UDP socket is closed." }
        network.route(this, data, toAddress, toPort)
    }

    override suspend fun receive(): UdpDatagram {
        val result = inbox.receiveCatching()
        if (result.isClosed) throw java.net.SocketException("UDP socket closed.")
        return result.getOrThrow()
    }

    internal fun deliver(datagram: UdpDatagram) {
        if (open) inbox.trySend(datagram)
    }

    override fun close() {
        if (!open) return
        open = false
        network.deregister(this)
        inbox.close()
    }
}

// ===========================================================================
// WiFiNetworkTransport  (WiFiNetworkTransport.cs)
// ===========================================================================

/**
 * [INetworkTransport] using LAN UDP broadcast / unicast. Discovery beacons ride
 * [DiscoveryPort] (47890); data rides [DataPort] (47891).
 *
 * [start] binds a receiver on [DataPort] (broadcast-enabled) and opens a send socket,
 * launching an inbound pump. [send] unicasts to the destination when the payload's
 * destination id parses as an IP address, otherwise broadcasts to [DataPort] — the
 * exact C# `IPAddress.TryParse` decision. [stop]/[close] closes both sockets and
 * completes the inbox (ending [receive]). Availability is "receiver bound", matching
 * C# `_receiver is not null`.
 *
 * @param network injected UDP stack (stand-in for the OS UDP sockets).
 * @param sourceAddress the local address reported as the datagram source.
 * @param scope coroutine scope the inbound pump runs in (injectable for tests).
 */
class WiFiNetworkTransport(
    private val network: IUdpNetwork,
    private val sourceAddress: String = "127.0.0.1",
    private val scope: CoroutineScope = CoroutineScope(Dispatchers.Default),
) : INetworkTransport, AutoCloseable {

    private var sender: IUdpSocket? = null
    private var receiver: IUdpSocket? = null
    private val inbound = Channel<NetworkPayload>(Channel.UNLIMITED)

    override val kind: TransportKind get() = TransportKind.WiFi
    override val isAvailable: Boolean get() = receiver != null

    override suspend fun start() {
        sender = network.openUnbound(sourceAddress)
        val r = network.openBound(DataPort, sourceAddress).apply { enableBroadcast = true }
        receiver = r
        scope.launch { pump(r) }
    }

    override suspend fun stop() {
        closeNow()
    }

    private fun closeNow() {
        receiver?.close()
        sender?.close()
        receiver = null
        sender = null
        inbound.close()
    }

    /**
     * Unicast to a parseable destination IP, else broadcast to [DataPort]. Mirrors
     * the C# `IPAddress.TryParse(dest, out var ip)` branch exactly.
     */
    override suspend fun send(payload: NetworkPayload) {
        val s = requireNotNull(sender) { "Transport is not started." }
        val data = payload.data
        val dest = payload.destinationId
        if (!dest.isNullOrEmpty() && parseIp(dest) != null) {
            s.sendTo(data, dest, DataPort)
        } else {
            s.enableBroadcast = true
            s.sendTo(data, IUdpNetwork.BROADCAST_ADDRESS, DataPort)
        }
    }

    override fun receive(): Flow<NetworkPayload> = flow {
        for (p in inbound) emit(p)
    }

    private suspend fun pump(r: IUdpSocket) {
        while (true) {
            val datagram = try {
                r.receive()
            } catch (_: Throwable) {
                break
            }
            inbound.trySend(NetworkPayload.create(datagram.data))
        }
        inbound.close()
    }

    override fun close() {
        closeNow()
    }

    companion object {
        /** UDP port carrying discovery beacons. */
        const val DiscoveryPort: Int = 47890

        /** UDP port carrying data payloads. */
        const val DataPort: Int = 47891

        /**
         * Parse [value] as an IPv4/IPv6 literal, returning the canonical string form
         * or null if it is not a valid address. Analogue of C# `IPAddress.TryParse`.
         * A bare host name (which is not a literal IP) returns null so it falls
         * through to the broadcast branch — matching C#.
         */
        fun parseIp(value: String): String? {
            if (value.isBlank()) return null
            // Reject bare hostnames: an IPv4 literal is dotted-decimal; IPv6 contains ':'.
            val looksV4 = value.count { it == '.' } == 3 && value.all { it.isDigit() || it == '.' }
            val looksV6 = value.contains(':')
            if (!looksV4 && !looksV6) return null
            return try {
                val addr = java.net.InetAddress.getByName(value)
                // getByName resolves hostnames too; guard by re-checking the literal shape.
                if (looksV4) {
                    val parts = value.split('.')
                    if (parts.size == 4 && parts.all { it.toIntOrNull()?.let { n -> n in 0..255 } == true }) {
                        addr.hostAddress
                    } else {
                        null
                    }
                } else {
                    addr.hostAddress
                }
            } catch (_: Exception) {
                null
            }
        }
    }
}

// ===========================================================================
// WiFiPeerDiscovery  (WiFiPeerDiscovery.cs)
// ===========================================================================

/**
 * Discovers nearby Circle AI devices on the same LAN via UDP broadcast beacons. No
 * Aether, no cloud, no infrastructure. A beacon is the bytes
 * `CIRCLEAI:BEACON:{nodeId}` on [WiFiNetworkTransport.DiscoveryPort]; [discover]
 * binds that port and yields a [PeerInfo] for every well-formed beacon, [announce]
 * broadcasts one for the local node.
 *
 * @param network injected UDP stack (stand-in for the OS UDP sockets).
 * @param sourceAddress the local address reported as the beacon source.
 * @param now clock for the [PeerInfo.lastSeen] stamp (injectable for tests).
 */
class WiFiPeerDiscovery(
    private val network: IUdpNetwork,
    private val sourceAddress: String = "127.0.0.1",
    private val now: () -> Instant = { Instant.now() },
) : IPeerDiscovery {

    /**
     * Bind [WiFiNetworkTransport.DiscoveryPort] and emit a [PeerInfo] for each
     * `CIRCLEAI:BEACON:` datagram. The flow completes when the socket closes (the
     * collector cancelling closes it), mirroring the C# `yield break` on receive
     * failure. Non-beacon datagrams are ignored.
     */
    override fun discover(): Flow<PeerInfo> = flow {
        val udp = network.openBound(WiFiNetworkTransport.DiscoveryPort, sourceAddress).apply {
            enableBroadcast = true
        }
        try {
            while (true) {
                val datagram = try {
                    udp.receive()
                } catch (_: Throwable) {
                    break
                }
                val msg = String(datagram.data, Charsets.UTF_8)
                if (msg.startsWith(BeaconMagic)) {
                    val nodeId = msg.substring(BeaconMagic.length)
                    emit(
                        PeerInfo(
                            nodeId = nodeId,
                            displayName = "WiFi/${datagram.fromAddress}",
                            supportedTransports = listOf(TransportKind.WiFi),
                            role = PeerRole.Peer,
                            signalStrengthDbm = null,
                            lastSeen = now(),
                        ),
                    )
                }
            }
        } finally {
            udp.close()
        }
    }

    /** Broadcast a `CIRCLEAI:BEACON:{nodeId}` beacon for [localInfo] on the discovery port. */
    override suspend fun announce(localInfo: PeerInfo) {
        val udp = network.openUnbound(sourceAddress).apply { enableBroadcast = true }
        try {
            val beacon = "$BeaconMagic${localInfo.nodeId}".toByteArray(Charsets.UTF_8)
            udp.sendTo(beacon, IUdpNetwork.BROADCAST_ADDRESS, WiFiNetworkTransport.DiscoveryPort)
        } finally {
            udp.close()
        }
    }

    companion object {
        /** Beacon prefix identifying a Circle AI discovery datagram. */
        const val BeaconMagic: String = "CIRCLEAI:BEACON:"
    }
}
