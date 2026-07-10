// Bluetooth.kt
//
// Kotlin port of CircleAI.Networking.Bluetooth (src/CircleAI.Networking.Bluetooth/*.cs
// is the EXACT spec). An [INetworkTransport] over BLE GATT. Platform adapters
// (Windows.Devices.Bluetooth, CoreBluetooth, Android BluetoothGatt via MAUI, BlueZ
// DBus on Linux) implement [IBleGattAdapter]; this transport wires the adapter to a
// channel-based receive loop. No real radios here — the adapter is injected.
//
// Covers (C# → Kotlin):
//   BluetoothTransportCommons.cs → BluetoothConnectionState (enum),
//                                  BluetoothEndpointDescriptor,
//                                  BluetoothCapabilityProfile,
//                                  BluetoothThroughputSample (records → data classes),
//                                  BluetoothCapabilityProfiles (static → object),
//                                  InMemoryBluetoothTransportRegistry
//   BluetoothNetworkTransport.cs → BluetoothNetworkTransport (INetworkTransport),
//                                  IBleGattAdapter (injected platform contract)
//
// C# → Kotlin conventions:
//   record                         → data class
//   IReadOnlyList                   → List
//   ChannelWriter<NetworkPayload>   → SendChannel<NetworkPayload>
//   ConcurrentDictionary + lock     → ConcurrentHashMap + synchronized
//   Task / IAsyncEnumerable<T>      → suspend fun / Flow<T>
//   static class                    → object
//
// CONCURRENCY: the inbound channel is UNBOUNDED so the adapter's writes never
// block; stop() first stops the adapter, then completes the channel so the
// receive() flow ends. The adapter is handed the channel's SendChannel view
// (write-only), mirroring C#'s ChannelWriter.
package com.bhengubv.circleai.networking.bluetooth

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
// BluetoothConnectionState  (BluetoothTransportCommons.cs)
// ===========================================================================

/** Lifecycle state of a BLE endpoint connection. */
enum class BluetoothConnectionState { Disconnected, Discovering, Connecting, Connected, Failed }

// ===========================================================================
// Records  (BluetoothTransportCommons.cs)
// ===========================================================================

/** A discovered BLE endpoint and the GATT services it advertises. */
data class BluetoothEndpointDescriptor(
    val deviceId: String,
    val name: String,
    val macAddress: String,
    val advertisedServices: List<String>,
)

/** Capability profile of a BLE link (MTU, secure/high-speed support, profiles). */
data class BluetoothCapabilityProfile(
    val maxMtuBytes: Int,
    val supportsSecureConnections: Boolean,
    val supportsHighSpeed: Boolean,
    val compatibleProfiles: List<String>,
)

/** One throughput sample (read/write kbps) for a device. */
data class BluetoothThroughputSample(
    val deviceId: String,
    val kbpsRead: Double,
    val kbpsWrite: Double,
    val atUtc: Instant,
)

// ===========================================================================
// BluetoothCapabilityProfiles  (BluetoothTransportCommons.cs)
// ===========================================================================

/** The three canonical BLE/Classic capability profiles, matching the C# statics. */
object BluetoothCapabilityProfiles {
    /** Bluetooth LE 5: 247-byte MTU, secure connections, high-speed, GATT + L2CAP. */
    val Le5: BluetoothCapabilityProfile =
        BluetoothCapabilityProfile(247, supportsSecureConnections = true, supportsHighSpeed = true, listOf("GATT", "L2CAP"))

    /** Bluetooth LE 4: 23-byte MTU, secure connections, no high-speed, GATT only. */
    val Le4: BluetoothCapabilityProfile =
        BluetoothCapabilityProfile(23, supportsSecureConnections = true, supportsHighSpeed = false, listOf("GATT"))

    /** Bluetooth Classic: 1024-byte MTU, secure connections, SPP + RFCOMM. */
    val Classic: BluetoothCapabilityProfile =
        BluetoothCapabilityProfile(1024, supportsSecureConnections = true, supportsHighSpeed = false, listOf("SPP", "RFCOMM"))
}

// ===========================================================================
// InMemoryBluetoothTransportRegistry  (BluetoothTransportCommons.cs)
// ===========================================================================

/**
 * Deterministic in-memory store of BLE endpoints + per-device connection state +
 * throughput samples. Mirrors the C# [ConcurrentDictionary] maps + `lock`ed
 * throughput list. [allEndpoints] is ordered by name; [avgKbpsRead] returns 0.0
 * when there are no samples (matching C# `DefaultIfEmpty(0.0).Average()`).
 */
class InMemoryBluetoothTransportRegistry {
    private val endpoints = ConcurrentHashMap<String, BluetoothEndpointDescriptor>()
    private val states = ConcurrentHashMap<String, BluetoothConnectionState>()
    private val throughput = ArrayList<BluetoothThroughputSample>()
    private val lock = Any()

    /** Register (or replace) an endpoint by device id. */
    fun register(e: BluetoothEndpointDescriptor) {
        endpoints[e.deviceId] = e
    }

    /** The endpoint for [deviceId], or null if unknown. */
    fun getEndpoint(deviceId: String): BluetoothEndpointDescriptor? = endpoints[deviceId]

    /** All registered endpoints, ordered by name. */
    val allEndpoints: List<BluetoothEndpointDescriptor>
        get() = endpoints.values.sortedBy { it.name }

    /** Set the connection state for [deviceId]. */
    fun setState(deviceId: String, s: BluetoothConnectionState) {
        states[deviceId] = s
    }

    /** The connection state for [deviceId], or [BluetoothConnectionState.Disconnected] if unset. */
    fun state(deviceId: String): BluetoothConnectionState =
        states[deviceId] ?: BluetoothConnectionState.Disconnected

    /** Record a throughput sample. */
    fun recordThroughput(s: BluetoothThroughputSample) {
        synchronized(lock) { throughput.add(s) }
    }

    /** Mean read throughput (kbps) across all samples for [deviceId]; 0.0 when none. */
    fun avgKbpsRead(deviceId: String): Double =
        synchronized(lock) {
            val vals = throughput.filter { it.deviceId == deviceId }.map { it.kbpsRead }
            if (vals.isEmpty()) 0.0 else vals.average()
        }
}

// ===========================================================================
// IBleGattAdapter  (BluetoothNetworkTransport.cs)
// ===========================================================================

/**
 * Platform-specific BLE GATT operations. Implement per platform (MAUI, Windows,
 * Linux). [start] is handed the write-only [inbound] view so the adapter can push
 * received payloads back into the transport's receive loop.
 */
interface IBleGattAdapter {
    /** Whether the BLE radio is present and usable. */
    val isAvailable: Boolean

    /** Bring the adapter up; push inbound payloads into [inbound]. */
    suspend fun start(inbound: SendChannel<NetworkPayload>)

    /** Tear the adapter down. */
    suspend fun stop()

    /** Write [payload] to the connected peer(s). */
    suspend fun write(payload: NetworkPayload)
}

// ===========================================================================
// BluetoothNetworkTransport  (BluetoothNetworkTransport.cs)
// ===========================================================================

/**
 * [INetworkTransport] over BLE GATT. Wires an injected [IBleGattAdapter] to a
 * channel-based receive loop. [start] hands the adapter the write-only inbound
 * channel; [stop] stops the adapter then completes the channel (ending the
 * [receive] flow); [send] delegates to the adapter's write.
 */
class BluetoothNetworkTransport(
    private val adapter: IBleGattAdapter,
) : INetworkTransport {

    private val inbound = Channel<NetworkPayload>(Channel.UNLIMITED)

    override val kind: TransportKind get() = TransportKind.Bluetooth
    override val isAvailable: Boolean get() = adapter.isAvailable

    override suspend fun start() {
        adapter.start(inbound)
    }

    override suspend fun stop() {
        adapter.stop()
        inbound.close()
    }

    override suspend fun send(payload: NetworkPayload) {
        adapter.write(payload)
    }

    override fun receive(): Flow<NetworkPayload> = flow {
        for (p in inbound) emit(p)
    }
}
