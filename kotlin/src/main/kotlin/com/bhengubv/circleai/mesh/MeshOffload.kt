// MeshOffload.kt
//
// Kotlin port of CircleAI.Mesh — the C# reference is the EXACT spec.
//
// Borrowing a nearby phone to think with: route a turn to a peer that has the
// model loaded and spare KV budget, fall back to this device when none does.
//
// Fidelity notes:
//   * C# `record` -> `data class`; `TimeSpan` -> seconds as `Double`.
//   * The transport pump and the DI wiring are host concerns and stay out; the
//     peer choice and the envelopes are what is worth having in another
//     language, and they are here in full.
//   * `DeviceTier` is an Int here so this module does not depend on Core.

package com.bhengubv.circleai.mesh

import java.time.Instant
import java.util.UUID
import kotlin.math.ceil
import kotlin.math.max

/** Who actually answered. */
enum class OffloadServedBy { REMOTE_PEER, LOCAL_FALLBACK, NONE }

/** One turn to be answered, here or elsewhere. */
data class OffloadTurn(
    val modelId: String,
    val prompt: String,
    val maxOutputTokens: Int,
    val temperature: Float,
    val topP: Float,
    val stopSequences: List<String>,
    val correlationId: String,
    val createdAtUtc: Instant,
) {
    companion object {
        fun create(
            modelId: String,
            prompt: String,
            maxOutputTokens: Int = 256,
            temperature: Float = 0.7f,
            topP: Float = 0.95f,
            stopSequences: List<String> = emptyList(),
            correlationId: String = UUID.randomUUID().toString().replace("-", ""),
            now: Instant = Instant.now(),
        ): OffloadTurn? {
            if (modelId.isBlank()) return null
            return OffloadTurn(modelId, prompt, maxOutputTokens, temperature, topP,
                stopSequences, correlationId, now)
        }
    }
}

data class OffloadResult(
    val success: Boolean,
    val outputText: String,
    val servedBy: OffloadServedBy,
    val servingPeerId: String?,
    val outputTokenCount: Int,
    val elapsedMilliseconds: Double,
    val failureReason: String?,
    val reasoningText: String? = null,
) {
    companion object {
        fun fail(
            reason: String,
            servedBy: OffloadServedBy = OffloadServedBy.NONE,
            elapsedMilliseconds: Double = 0.0,
        ) = OffloadResult(false, "", servedBy, null, 0, elapsedMilliseconds, reason)
    }
}

/** What a peer advertises about itself. */
data class MeshCapabilityAdvertisement(
    val peerId: String,
    val modelId: String,
    val freeKvTokens: Int,
    /** Device tier as an ordinal: wearable 0, phone 1, tablet 2, desktop 3, workstation 4. */
    val tier: Int,
    val contextWindowTokens: Int,
    val advertisedAtUtc: Instant,
    val latencyHintMs: Int? = null,
)

interface MeshCapabilityRegistry {
    fun upsert(ad: MeshCapabilityAdvertisement)
    fun remove(peerId: String): Boolean
    fun list(staleAfterSeconds: Double? = null, now: Instant = Instant.now()): List<MeshCapabilityAdvertisement>
    fun find(
        modelId: String,
        minFreeKvTokens: Int = 0,
        staleAfterSeconds: Double? = null,
        now: Instant = Instant.now(),
    ): List<MeshCapabilityAdvertisement>
}

class InMemoryMeshCapabilityRegistry : MeshCapabilityRegistry {
    private val lock = Any()
    private val peers = mutableMapOf<String, MeshCapabilityAdvertisement>()

    override fun upsert(ad: MeshCapabilityAdvertisement) {
        synchronized(lock) { peers[ad.peerId] = ad }
    }

    override fun remove(peerId: String): Boolean =
        synchronized(lock) { peers.remove(peerId) != null }

    override fun list(staleAfterSeconds: Double?, now: Instant): List<MeshCapabilityAdvertisement> {
        val all = synchronized(lock) { peers.values.toList() }
        if (staleAfterSeconds == null) return all
        return all.filter { (now.toEpochMilli() - it.advertisedAtUtc.toEpochMilli()) / 1000.0 <= staleAfterSeconds }
    }

    /** Most-capable peer first: sorted by spare budget descending. */
    override fun find(
        modelId: String,
        minFreeKvTokens: Int,
        staleAfterSeconds: Double?,
        now: Instant,
    ): List<MeshCapabilityAdvertisement> =
        list(staleAfterSeconds, now)
            .filter { it.modelId == modelId && it.freeKvTokens >= minFreeKvTokens }
            .sortedByDescending { it.freeKvTokens }
}

// ── Seams ───────────────────────────────────────────────────────────────────

interface OffloadRouter {
    suspend fun route(turn: OffloadTurn): OffloadResult
}

interface LocalInferenceFallback {
    suspend fun complete(turn: OffloadTurn): OffloadResult
}

/**
 * This node can borrow a brain but has none of its own to lend - which is a
 * real configuration on a small phone, not an error.
 */
object NullLocalInferenceFallback : LocalInferenceFallback {
    override suspend fun complete(turn: OffloadTurn) = OffloadResult.fail(
        "No local inference fallback is registered; this node can borrow a peer brain " +
            "but cannot serve locally.",
        OffloadServedBy.NONE,
    )
}

interface MeshOffloadClient {
    val isReady: Boolean
    suspend fun request(peerId: String, turn: OffloadTurn, timeoutSeconds: Double): OffloadResult
}

// ── Options ─────────────────────────────────────────────────────────────────

/** Everything the router is allowed to decide, in one place. */
data class MeshOffloadOptions(
    val localNodeId: String = UUID.randomUUID().toString().replace("-", ""),
    val staleAfterSeconds: Double = 30.0,
    val requestTimeoutSeconds: Double = 30.0,
    val maxPeerAttempts: Int = 2,
    val kvHeadroomFactor: Double = 1.0,
    /**
     * Four characters to the token is the rough English ratio; the OUTPUT
     * budget is exact, because the caller asked for it.
     */
    val estimateKvTokens: (OffloadTurn) -> Int = { (it.prompt.length / 4) + it.maxOutputTokens },
    val selectPeer: (List<MeshCapabilityAdvertisement>) -> MeshCapabilityAdvertisement? =
        { defaultSelectPeer(it) },
    val serveInboundRequests: Boolean = true,
    val maxConcurrentServed: Int = 2,
    val startTransport: Boolean = true,
    val broadcastIntervalSeconds: Double = 15.0,
) {
    companion object {
        /**
         * Best tier first, then LOWEST latency, then most spare budget.
         *
         * A peer that reports NO latency hint sorts LAST on that key rather
         * than first - unknown is not fast, and treating it as zero hands
         * every turn to the peer that never measured itself.
         */
        fun defaultSelectPeer(candidates: List<MeshCapabilityAdvertisement>): MeshCapabilityAdvertisement? {
            var best: MeshCapabilityAdvertisement? = null
            for (c in candidates) {
                val b = best
                if (b == null) { best = c; continue }

                if (c.tier > b.tier) { best = c; continue }
                if (c.tier < b.tier) continue

                val cl = c.latencyHintMs ?: Int.MAX_VALUE
                val bl = b.latencyHintMs ?: Int.MAX_VALUE
                if (cl < bl) { best = c; continue }
                if (cl > bl) continue

                if (c.freeKvTokens > b.freeKvTokens) best = c
            }
            return best
        }
    }
}

// ── The router ──────────────────────────────────────────────────────────────

/**
 * Picks a peer, tries it, tries the next, and falls back to this device.
 *
 * EVERY PATH RETURNS A RESULT. A mesh that throws when no peer answers is a
 * mesh that takes the whole app down when somebody walks out of range.
 */
class MeshOffloadRouter(
    private val registry: MeshCapabilityRegistry,
    private val client: MeshOffloadClient,
    private val localFallback: LocalInferenceFallback,
    private val options: MeshOffloadOptions = MeshOffloadOptions(),
    private val now: () -> Instant = { Instant.now() },
) : OffloadRouter {

    override suspend fun route(turn: OffloadTurn): OffloadResult {
        val estimate = max(0, options.estimateKvTokens(turn))
        val minFreeKv = max(0, ceil(estimate * options.kvHeadroomFactor).toInt())

        val candidates = registry.find(turn.modelId, minFreeKv, options.staleAfterSeconds, now())
        if (candidates.isEmpty()) return fallBackLocal(turn, "No capable peer advertised.")

        val pool = candidates.toMutableList()
        val tried = mutableSetOf<String>()
        val reasons = mutableListOf<String>()

        repeat(max(1, options.maxPeerAttempts)) {
            if (pool.isEmpty()) return@repeat
            val pick = options.selectPeer(pool) ?: pool[0]
            // Removed whether or not it is tried, so a selector that keeps
            // returning the same peer cannot spin.
            pool.removeAll { it.peerId == pick.peerId }
            if (!tried.add(pick.peerId)) return@repeat

            val remote = try {
                client.request(pick.peerId, turn, options.requestTimeoutSeconds)
            } catch (e: Exception) {
                reasons.add(pick.peerId + ": " + (e.message ?: e::class.simpleName))
                null
            }
            if (remote != null) {
                if (remote.success) return remote
                reasons.add(pick.peerId + ": " + (remote.failureReason ?: "unknown"))
            }
        }

        return fallBackLocal(turn, "All peer attempts failed: " + reasons.joinToString("; "))
    }

    private suspend fun fallBackLocal(turn: OffloadTurn, why: String): OffloadResult {
        val started = now().toEpochMilli()
        return try {
            var local = localFallback.complete(turn)

            // A fallback that answered but did not say who served it gets
            // labelled here, so the caller can always tell where words came from.
            if (local.success && local.servedBy == OffloadServedBy.NONE) {
                local = local.copy(servedBy = OffloadServedBy.LOCAL_FALLBACK)
            }
            // And a bare failure inherits WHY the mesh gave up, which is the
            // part that explains itself to a person.
            if (!local.success && local.failureReason.isNullOrEmpty()) {
                local = local.copy(failureReason = why)
            }
            local
        } catch (e: Exception) {
            OffloadResult.fail(
                why + " Local fallback also failed: " + (e.message ?: e::class.simpleName),
                OffloadServedBy.NONE,
                (now().toEpochMilli() - started).toDouble(),
            )
        }
    }
}
