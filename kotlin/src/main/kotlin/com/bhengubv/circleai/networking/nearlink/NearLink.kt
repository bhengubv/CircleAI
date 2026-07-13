// NearLink.kt
//
// Kotlin port of CircleAI.Networking.NearLink (src/CircleAI.Networking.NearLink/*.cs
// is the EXACT spec). An [INetworkTransport] for Huawei SLE / NearLink — up to 600 m
// range, 12 Mbps, bridging BLE and WiFi Direct. Platform ops (the Huawei DevEco
// NearLink SDK on HarmonyOS, or the NearLink HAL on compatible Android devices) are
// injected behind [INearLinkAdapter]; this transport wires the adapter to a
// channel-based receive loop. No Aether required; no real radios here.
//
// Covers (C# → Kotlin):
//   NearLinkTransportCommons.cs → NearLinkPairingState (enum),
//                                 NearLinkPowerProfile (enum),
//                                 NearLinkDevice, NearLinkSession,
//                                 NearLinkThroughputSample (records → data classes),
//                                 InMemoryNearLinkRegistry
//   NearLinkTransport.cs        → NearLinkTransport (INetworkTransport),
//                                 INearLinkAdapter (injected platform contract)
//
// C# → Kotlin conventions:
//   record                          → data class
//   IReadOnlyList                    → List
//   ChannelWriter<NetworkPayload>    → SendChannel<NetworkPayload>
//   ConcurrentDictionary + lock      → ConcurrentHashMap + synchronized
//   Task / IAsyncEnumerable<T>       → suspend fun / Flow<T>
//   DefaultIfEmpty(-127).Average()   → averageOrDefault(-127.0)
//
// CONCURRENCY: the inbound channel is UNBOUNDED so the adapter's writes never
// block; start() hands the adapter the write-only SendChannel view (mirroring C#'s
// ChannelWriter) SYNCHRONOUSLY before returning, so a payload pushed the instant the
// adapter comes up is captured, never raced/lost. stop() first stops the adapter,
// then completes the channel so the receive() flow ends.
package com.bhengubv.circleai.networking.nearlink

import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.channels.SendChannel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap

// ===========================================================================
// Enums  (NearLinkTransportCommons.cs)
// ===========================================================================

/** Pairing lifecycle of a NearLink device. */
enum class NearLinkPairingState { Unpaired, Pairing, Paired, PairingFailed }

/** Power/throughput trade-off profile for a NearLink link. */
enum class NearLinkPowerProfile { LowEnergy, Balanced, HighThroughput }

// ===========================================================================
// Records  (NearLinkTransportCommons.cs)
// ===========================================================================

/** A NearLink / SLE device and its firmware identity. */
data class NearLinkDevice(
    val deviceId: String,
    val friendlyName: String,
    val manufacturerId: String,
    val firmwareVersion: String,
)

/** An open NearLink session against a device with a chosen power profile. */
data class NearLinkSession(
    val sessionId: String,
    val deviceId: String,
    val powerProfile: NearLinkPowerProfile,
    val startedUtc: Instant,
)

/** One throughput sample (read/write kbps + RSSI) for a device. */
data class NearLinkThroughputSample(
    val deviceId: String,
    val kbpsRead: Double,
    val kbpsWrite: Double,
    val rssiDbm: Int,
    val atUtc: Instant,
)

// ===========================================================================
// InMemoryNearLinkRegistry  (NearLinkTransportCommons.cs)
// ===========================================================================

/**
 * Deterministic in-memory store of NearLink devices + per-device pairing state +
 * open sessions + throughput samples. Mirrors the C# [ConcurrentDictionary] maps +
 * `lock`ed throughput list.
 *
 * [devices] is ordered by friendly name; [pairingState] defaults to
 * [NearLinkPairingState.Unpaired]; [avgRssi] returns -127.0 when there are no
 * samples for the device (matching C# `DefaultIfEmpty(-127).Average()`).
 */
class InMemoryNearLinkRegistry {
    private val devices = ConcurrentHashMap<String, NearLinkDevice>()
    private val states = ConcurrentHashMap<String, NearLinkPairingState>()
    private val sessions = ConcurrentHashMap<String, NearLinkSession>()
    private val throughput = ArrayList<NearLinkThroughputSample>()
    private val lock = Any()

    /** Register (or replace) a device by device id. */
    fun register(d: NearLinkDevice) {
        devices[d.deviceId] = d
    }

    /** The device for [id], or null if unknown. */
    fun getDevice(id: String): NearLinkDevice? = devices[id]

    /** All registered devices, ordered by friendly name. */
    val allDevices: List<NearLinkDevice>
        get() = devices.values.sortedBy { it.friendlyName }

    /** Set the pairing state for [deviceId]. */
    fun setPairingState(deviceId: String, s: NearLinkPairingState) {
        states[deviceId] = s
    }

    /** The pairing state for [deviceId], or [NearLinkPairingState.Unpaired] if unset. */
    fun pairingState(deviceId: String): NearLinkPairingState =
        states[deviceId] ?: NearLinkPairingState.Unpaired

    /** Open (or replace) a session by session id. */
    fun openSession(s: NearLinkSession) {
        sessions[s.sessionId] = s
    }

    /** The session for [id], or null if unknown. */
    fun getSession(id: String): NearLinkSession? = sessions[id]

    /** Close the session with [id] (no-op if absent). */
    fun closeSession(id: String) {
        sessions.remove(id)
    }

    /** All currently-open sessions (unordered, mirroring C# `.Values.ToArray()`). */
    val activeSessions: List<NearLinkSession>
        get() = sessions.values.toList()

    /** Record a throughput sample. */
    fun recordThroughput(s: NearLinkThroughputSample) {
        synchronized(lock) { throughput.add(s) }
    }

    /** Mean RSSI (dBm) across all samples for [deviceId]; -127.0 when none. */
    fun avgRssi(deviceId: String): Double =
        synchronized(lock) {
            val vals = throughput.filter { it.deviceId == deviceId }.map { it.rssiDbm.toDouble() }
            if (vals.isEmpty()) -127.0 else vals.average()
        }

    /** Mean read throughput (kbps) across all samples for [deviceId]; 0.0 when none. */
    fun avgKbpsRead(deviceId: String): Double =
        synchronized(lock) {
            val vals = throughput.filter { it.deviceId == deviceId }.map { it.kbpsRead }
            if (vals.isEmpty()) 0.0 else vals.average()
        }

    /** Mean write throughput (kbps) across all samples for [deviceId]; 0.0 when none. */
    fun avgKbpsWrite(deviceId: String): Double =
        synchronized(lock) {
            val vals = throughput.filter { it.deviceId == deviceId }.map { it.kbpsWrite }
            if (vals.isEmpty()) 0.0 else vals.average()
        }

    /**
     * Remove a paired device: drops its device record and cached pairing state.
     * Open sessions are left untouched (close them explicitly via [closeSession]).
     * Returns true if a device record was actually removed.
     */
    fun unregister(deviceId: String): Boolean {
        if (deviceId.isEmpty()) return false
        val removed = devices.remove(deviceId) != null
        states.remove(deviceId)
        return removed
    }

    /** Active sessions belonging to a device, oldest-first by start time. */
    fun sessionsForDevice(deviceId: String): List<NearLinkSession> {
        if (deviceId.isEmpty()) return emptyList()
        return sessions.values
            .filter { it.deviceId == deviceId }
            .sortedBy { it.startedUtc }
    }
}

// ===========================================================================
// INearLinkAdapter  (NearLinkTransport.cs)
// ===========================================================================

/**
 * Platform-level NearLink / SLE operations. Implement using the Huawei DevEco
 * NearLink SDK on HarmonyOS, or the NearLink HAL on compatible Android devices.
 * [start] is handed the write-only [inbound] view so the adapter can push received
 * payloads back into the transport's receive loop (mirrors C#'s ChannelWriter).
 */
interface INearLinkAdapter {
    /** Whether the NearLink radio is present and usable. */
    val isAvailable: Boolean

    /** Bring the adapter up; push inbound payloads into [inbound]. */
    suspend fun start(inbound: SendChannel<NetworkPayload>)

    /** Tear the adapter down. */
    suspend fun stop()

    /** Send [payload] to the connected peer(s). */
    suspend fun send(payload: NetworkPayload)
}

// ===========================================================================
// NearLinkTransport  (NearLinkTransport.cs)
// ===========================================================================

/**
 * [INetworkTransport] for Huawei SLE / NearLink. Wires an injected
 * [INearLinkAdapter] to a channel-based receive loop. [start] hands the adapter the
 * write-only inbound channel; [stop] stops the adapter then completes the channel
 * (ending the [receive] flow); [send] delegates to the adapter's send.
 *
 * Availability tracks the injected adapter. No Aether required — works standalone on
 * HarmonyOS and compatible Android devices.
 */
class NearLinkTransport(
    private val adapter: INearLinkAdapter,
) : INetworkTransport {

    private val inbound = Channel<NetworkPayload>(Channel.UNLIMITED)

    override val kind: TransportKind get() = TransportKind.NearLink
    override val isAvailable: Boolean get() = adapter.isAvailable

    override suspend fun start() {
        adapter.start(inbound)
    }

    override suspend fun stop() {
        adapter.stop()
        inbound.close()
    }

    override suspend fun send(payload: NetworkPayload) {
        adapter.send(payload)
    }

    override fun receive(): Flow<NetworkPayload> = flow {
        for (p in inbound) emit(p)
    }
}
