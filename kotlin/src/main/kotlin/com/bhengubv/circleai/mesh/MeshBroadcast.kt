// MeshBroadcast.kt
//
// Telling the mesh what this node can do, on a cadence.
//
// WE DO NOT DISCOVER PEERS. Zero-infrastructure BLE and Wi-Fi Direct discovery
// is AetherNet's job, in its own repository. This publishes over a transport the
// host already wired.
//
// Ported from src/CircleAI.Mesh/AetherMeshCapabilityBroadcaster.cs.

package com.bhengubv.circleai.mesh


import com.bhengubv.circleai.aethernet.IMeshCapabilityBroadcaster
import com.bhengubv.circleai.device.DeviceTier
import com.bhengubv.circleai.aethernet.MeshCapabilityAdvertisement as AetherAdvertisement
import java.time.Instant
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Sends our advertisement, destination-less, so every reachable peer's ingest
 * loop folds it into their registry.
 */
class AetherMeshCapabilityBroadcaster(
    private val localNodeId: String,
    private val staleAfterMillis: Long,
    private val transportAvailable: () -> Boolean,
    private val send: suspend (AetherAdvertisement, ttlMillis: Long) -> Unit,
    private val now: () -> Instant = { Instant.now() },
    private val log: ((String) -> Unit)? = null
) : IMeshCapabilityBroadcaster {

    override suspend fun broadcast(ad: AetherAdvertisement) {
        if (!transportAvailable()) {
            log?.invoke("mesh advert: transport unavailable; skipping broadcast")
            return
        }

        // STAMPED HERE, not by the caller. Peers dedupe on peer id and measure
        // staleness from the moment we actually SENT it — a timestamp taken when
        // the advertisement was built makes us look stale before we have said
        // anything.
        val stamped = ad.copy(peerId = localNodeId, advertisedAtUtc = now())

        try {
            // TTL IS THE FRESHNESS WINDOW. A peer that stops hearing us expires
            // us anyway, so a packet outliving that window is only ever noise.
            send(stamped, staleAfterMillis)
            log?.invoke("mesh advert: broadcast ${stamped.modelId}")
        } catch (t: Throwable) {
            // A failed broadcast is not worth failing a caller over: the next
            // beacon tick is seconds away and nothing downstream is waiting.
            log?.invoke("mesh advert: broadcast failed: $t")
        }
    }
}

/**
 * Re-broadcasts on a cadence, so this node never ages out of a peer's freshness
 * window.
 */
class MeshAdvertisementBeacon(
    private val broadcaster: IMeshCapabilityBroadcaster,
    /**
     * What to advertise RIGHT NOW, or null. A function rather than a stored
     * value because free KV budget changes minute to minute, and an
     * advertisement captured at startup advertises a phone that no longer
     * exists.
     */
    private val advertisement: () -> MeshCapabilityAdvertisement?,
    private val log: ((String) -> Unit)? = null
) {
    private val running = AtomicBoolean(false)

    val isRunning: Boolean get() = running.get()

    fun start() { running.set(true) }
    fun stop() { running.set(false) }

    /** One beacon tick. Public so a host drives the cadence and a test does not
     *  have to wait for it. */
    suspend fun tick() {
        // A node that advertises nothing BORROWS ONLY. That is a legitimate
        // configuration — a cheap phone with no model to share — and it must not
        // look like a failure.
        val ad = advertisement() ?: return
        try {
            broadcaster.broadcast(ad.toAether())
        } catch (t: Throwable) {
            log?.invoke("mesh advert beacon: tick failed: $t")
        }
    }
}


/**
 * The mesh advert as the AetherNet interface wants it.
 *
 * The two declarations differ only in `tier` - an ordinal here, a `DeviceTier`
 * there - and the ordinal on the wire IS the enum's ordinal. An out-of-range
 * value maps to the LOWEST tier rather than throwing: a peer advertising a tier
 * this build has not heard of should be treated as the least capable thing it
 * could be, not dropped and not trusted with more than it can do.
 */
private fun MeshCapabilityAdvertisement.toAether(): AetherAdvertisement =
    AetherAdvertisement(
        peerId = peerId,
        modelId = modelId,
        freeKvTokens = freeKvTokens,
        tier = DeviceTier.entries.getOrElse(tier) { DeviceTier.WEARABLE },
        contextWindowTokens = contextWindowTokens,
        advertisedAtUtc = advertisedAtUtc,
        latencyHintMs = latencyHintMs,
    )
