// MemorySync.kt
//
// Kotlin port of the CircleAI.Memory.Sync companion-state sync layer — the C#
// reference is the EXACT spec. Covers:
//
//   HybridLogicalClock          — monotonic, globally-unique version stamps
//   SyncableEntry               — the wire unit
//   SyncEnvelope / *Kind        — the convergence-protocol message
//   StateVectorEntry            — per-type high-watermark
//   RequestItem                 — reply-side "send me newer than X"
//   ISyncableEntryStore         — the seat the engine reads/writes
//   InMemorySyncableEntryStore  — in-memory store with the apply rules
//   ICompanionStateChannel      — transport seam
//   InProcessSyncHub /
//   InProcessCompanionStateChannel — loopback transport for tests + sim
//   ICompanionStateSyncEngine   — the orchestrator contract
//   CompanionStateSyncEngine    — default engine (Announce/Request/Push)
//   PersonaStateSyncBridge      — bridges IPersonaStore <-> engine
//   LoraAdapterSyncBridge       — bridges trained LoRA adapter bytes
//   CompanionConversationSyncBridge — bridges live conversation deltas
//
// Wire/byte formats (HLC composition, SHA-256-of-payload content hash, apply
// tiebreakers) are matched exactly to the C# reference so every language port
// converges on identical bytes.

package com.bhengubv.circleai.memory.sync

import com.bhengubv.circleai.memory.IPersonaStore
import com.bhengubv.circleai.memory.PersonaState
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import java.io.File
import java.security.MessageDigest
import java.time.Instant
import java.util.Base64
import java.util.concurrent.ConcurrentHashMap

// ===========================================================================
// HybridLogicalClock  (HybridLogicalClock.cs)
// ===========================================================================

/**
 * Hybrid Logical Clock — produces monotonic, globally-unique version stamps
 * for syncable entries. Thread-safe.
 *
 * Layout of the 64-bit version:
 *   high 48 bits — physical time in milliseconds (Unix epoch)
 *   mid  10 bits — logical counter (resets when physical advances)
 *   low   6 bits — node short ID (0..63)
 *
 * @param nodeShortId 0..63 — packs into the low 6 bits of every version. Each
 *   device a user has should pick a stable distinct value.
 * @param physicalNowMs source of physical time in milliseconds; override in
 *   tests for determinism. Defaults to system time.
 */
class HybridLogicalClock(
    nodeShortId: Long,
    private val physicalNowMs: () -> Long = { System.currentTimeMillis() },
) {
    private val nodeShortId: Long
    private var lastPhysical: Long
    private var logical: Long = 0
    private val lock = Any()

    init {
        require(nodeShortId in 0..63) { "nodeShortId must be in 0..63" }
        this.nodeShortId = nodeShortId
        this.lastPhysical = physicalNowMs()
    }

    /** Produces the next outgoing version (for a write we originated). */
    fun tick(): Long = synchronized(lock) {
        val now = physicalNowMs()
        if (now > lastPhysical) {
            lastPhysical = now
            logical = 0
        } else {
            logical++
            if (logical >= 1024) {
                // Logical counter overflowed within the same ms — bump physical.
                lastPhysical++
                logical = 0
            }
        }
        compose(lastPhysical, logical, nodeShortId)
    }

    /**
     * Updates the clock from a received version (must be called on every
     * inbound apply so subsequent local ticks remain monotonic w.r.t. peers).
     */
    fun observe(incoming: Long): Long = synchronized(lock) {
        val incomingPhysical = decompose(incoming).physicalMs
        val now = physicalNowMs()
        val maxPhysical = maxOf(maxOf(lastPhysical, incomingPhysical), now)

        when {
            maxPhysical == lastPhysical && maxPhysical == incomingPhysical -> logical++
            maxPhysical == lastPhysical -> logical++
            maxPhysical == incomingPhysical -> logical = decompose(incoming).logical + 1
            else -> logical = 0
        }

        lastPhysical = maxPhysical
        compose(lastPhysical, logical, nodeShortId)
    }

    companion object {
        /** Three components of a decomposed HLC version. */
        data class Components(val physicalMs: Long, val logical: Long, val nodeShortId: Long)

        /** Composes the three components into a 64-bit version. */
        fun compose(physicalMs: Long, logical: Long, nodeShortId: Long): Long =
            (physicalMs shl 16) or ((logical and 0x3FF) shl 6) or (nodeShortId and 0x3F)

        /** Decomposes a version into its three components. */
        fun decompose(version: Long): Components =
            Components(version shr 16, (version shr 6) and 0x3FF, version and 0x3F)
    }
}

// ===========================================================================
// SyncableEntry  (SyncableEntry.cs)
// ===========================================================================

/**
 * A single syncable item — the smallest unit the engine moves between peers.
 *
 * @param entityType Logical type — e.g. "PersonaState", "CoreMemory".
 * @param entityId Identifier within the type — e.g. a user ID.
 * @param version HLC-produced monotonic version stamp.
 * @param isTombstone True when this entry represents a deletion.
 * @param contentHash SHA-256 hex of [payload] — content tiebreaker.
 * @param payload Opaque payload — type-specific JSON or any string.
 * @param sourceNodeId Identifier of the node that authored this version.
 * @param authoredAt UTC wall-clock when authored — display only, not ordering.
 */
data class SyncableEntry(
    val entityType: String,
    val entityId: String,
    val version: Long,
    val isTombstone: Boolean,
    val contentHash: String,
    val payload: String,
    val sourceNodeId: String,
    val authoredAt: Instant,
)

// ===========================================================================
// SyncEnvelope  (SyncEnvelope.cs)
// ===========================================================================

/** Kind of sync envelope. */
enum class SyncEnvelopeKind {
    /** Broadcast of the sender's per-entity-type high-watermark versions. */
    Announce,

    /** Reply to an Announce asking for entries newer than a known version. */
    Request,

    /** Unsolicited or replied delivery of syncable entries. */
    Push,
}

/** Per-entity-type high-watermark — used in Announce/Request payloads. */
data class StateVectorEntry(val entityType: String, val maxKnownVersion: Long)

/**
 * Reply-side request item — "send me entries of [entityType] strictly newer
 * than [sinceVersion]".
 */
data class RequestItem(val entityType: String, val sinceVersion: Long)

/** A sync envelope — the message unit that crosses the channel. */
data class SyncEnvelope(
    val kind: SyncEnvelopeKind,
    val fromNodeId: String,
    val stateVector: List<StateVectorEntry>?,
    val requests: List<RequestItem>?,
    val entries: List<SyncableEntry>?,
)

// ===========================================================================
// ISyncableEntryStore  (ISyncableEntryStore.cs)
// ===========================================================================

/**
 * The seat the sync engine reads from and writes to. Implementations track the
 * local view of all known syncable entries plus their version stamps.
 *
 * Apply rules — implementations MUST enforce these for convergence:
 *   - Higher [SyncableEntry.version] wins
 *   - On tie, higher [SyncableEntry.contentHash] (ordinal string compare) wins
 *   - Tombstones replace any non-tombstone of equal-or-lower version
 */
interface ISyncableEntryStore {
    /**
     * Applies an incoming entry. Returns true when local state was actually
     * updated (incoming was strictly newer / preferred). Returns false when the
     * local entry was already at or beyond the incoming version.
     */
    suspend fun apply(entry: SyncableEntry): Boolean

    /**
     * Returns the current entry for the given (type, id), or null when not
     * known locally. Tombstones ARE returned.
     */
    suspend fun get(entityType: String, entityId: String): SyncableEntry?

    /**
     * Returns every entry of the given type whose version is strictly greater
     * than [sinceVersion], ordered ascending by version.
     */
    suspend fun getSince(entityType: String, sinceVersion: Long): List<SyncableEntry>

    /**
     * Returns the highest known version per entity type — the local node's
     * state vector. Types with no entries are omitted.
     */
    suspend fun getStateVector(): List<StateVectorEntry>
}

// ===========================================================================
// InMemorySyncableEntryStore  (InMemorySyncableEntryStore.cs)
// ===========================================================================

/** In-memory [ISyncableEntryStore]. */
class InMemorySyncableEntryStore : ISyncableEntryStore {
    private data class Key(val type: String, val id: String)

    // Keyed by (type, id) so writes are O(1).
    private val entries = ConcurrentHashMap<Key, SyncableEntry>()
    private val vectorLock = Any()
    private val maxVersionByType = HashMap<String, Long>()

    override suspend fun apply(entry: SyncableEntry): Boolean {
        val key = Key(entry.entityType, entry.entityId)

        var applied = false
        entries.compute(key) { _, existing ->
            if (existing == null) {
                applied = true
                entry
            } else if (shouldApply(existing, entry)) {
                applied = true
                entry
            } else {
                existing
            }
        }

        if (applied) {
            synchronized(vectorLock) {
                val current = maxVersionByType[entry.entityType] ?: 0L
                if (entry.version > current) maxVersionByType[entry.entityType] = entry.version
            }
        }
        return applied
    }

    override suspend fun get(entityType: String, entityId: String): SyncableEntry? =
        entries[Key(entityType, entityId)]

    override suspend fun getSince(entityType: String, sinceVersion: Long): List<SyncableEntry> =
        entries.values
            .filter { it.entityType == entityType && it.version > sinceVersion }
            .sortedBy { it.version }

    override suspend fun getStateVector(): List<StateVectorEntry> =
        synchronized(vectorLock) {
            maxVersionByType
                .map { (type, version) -> StateVectorEntry(type, version) }
                .sortedBy { it.entityType }
        }

    private companion object {
        /**
         * Apply rule: higher version wins; on tie, higher content hash
         * (ordinal string compare) wins; tombstone replaces a non-tombstone of
         * equal version.
         */
        fun shouldApply(existing: SyncableEntry, incoming: SyncableEntry): Boolean {
            if (incoming.version > existing.version) return true
            if (incoming.version < existing.version) return false
            // Equal versions — tombstone-of-non-tombstone wins.
            if (incoming.isTombstone && !existing.isTombstone) return true
            if (!incoming.isTombstone && existing.isTombstone) return false
            // Same tombstone state, same version — content hash tiebreaker.
            return incoming.contentHash.compareTo(existing.contentHash) > 0
        }
    }
}

// ===========================================================================
// ICompanionStateChannel  (ICompanionStateChannel.cs)
// ===========================================================================

/** Transport that moves [SyncEnvelope] messages between peers. */
interface ICompanionStateChannel {
    /**
     * Stable identifier of THIS node on this channel. Stamped onto every
     * envelope as [SyncEnvelope.fromNodeId].
     */
    val localNodeId: String

    /**
     * Sends an envelope to peers. Channel decides whether this is broadcast or
     * routed. For v0.1 every channel implements broadcast semantics.
     */
    suspend fun send(envelope: SyncEnvelope)

    /** Subscribe to inbound envelopes. The returned handle unsubscribes. */
    fun subscribe(handler: suspend (SyncEnvelope) -> Unit): AutoCloseable
}

// ===========================================================================
// InProcessSyncHub + InProcessCompanionStateChannel
//   (InProcessCompanionStateChannel.cs)
// ===========================================================================

/**
 * Routes envelopes between every [InProcessCompanionStateChannel] that has
 * joined the hub. One hub per simulated "mesh".
 */
class InProcessSyncHub {
    private val channels = ConcurrentHashMap<String, InProcessCompanionStateChannel>()

    internal fun join(channel: InProcessCompanionStateChannel) {
        channels[channel.localNodeId] = channel
    }

    internal fun leave(nodeId: String) {
        channels.remove(nodeId)
    }

    internal suspend fun broadcast(envelope: SyncEnvelope, senderNodeId: String) {
        val peers = channels.values.filter { it.localNodeId != senderNodeId }
        for (peer in peers) {
            peer.deliver(envelope)
        }
    }

    /** Channels currently on this hub. */
    val connectedNodeIds: Collection<String>
        get() = channels.keys.toList()
}

/**
 * In-process [ICompanionStateChannel]. Broadcasts via an [InProcessSyncHub].
 */
class InProcessCompanionStateChannel(
    private val hub: InProcessSyncHub,
    override val localNodeId: String,
) : ICompanionStateChannel, AutoCloseable {

    private val handlers = ArrayList<suspend (SyncEnvelope) -> Unit>()
    private val lock = Any()
    @Volatile private var disposed = false

    init {
        require(localNodeId.isNotBlank()) { "localNodeId required" }
        hub.join(this)
    }

    override suspend fun send(envelope: SyncEnvelope) {
        check(!disposed) { "InProcessCompanionStateChannel is disposed" }
        hub.broadcast(envelope, localNodeId)
    }

    override fun subscribe(handler: suspend (SyncEnvelope) -> Unit): AutoCloseable {
        check(!disposed) { "InProcessCompanionStateChannel is disposed" }
        synchronized(lock) { handlers.add(handler) }
        return Subscription(this, handler)
    }

    internal suspend fun deliver(envelope: SyncEnvelope) {
        val snapshot = synchronized(lock) { handlers.toList() }
        for (h in snapshot) {
            h(envelope)
        }
    }

    /** Unregisters from the hub. */
    override fun close() {
        if (disposed) return
        disposed = true
        hub.leave(localNodeId)
        synchronized(lock) { handlers.clear() }
    }

    private class Subscription(
        private val owner: InProcessCompanionStateChannel,
        private val handler: suspend (SyncEnvelope) -> Unit,
    ) : AutoCloseable {
        override fun close() {
            synchronized(owner.lock) { owner.handlers.remove(handler) }
        }
    }
}

// ===========================================================================
// ICompanionStateSyncEngine  (ICompanionStateSyncEngine.cs)
// ===========================================================================

/**
 * Engine that broadcasts local state vectors, fulfils peer Requests, and
 * applies inbound Push entries. Hosts call [start] once at startup, then either
 * rely on event-driven sync or trigger [syncNow] after notable local writes.
 *
 * Implements [AutoCloseable]; [close] unsubscribes from the channel.
 */
interface ICompanionStateSyncEngine : AutoCloseable {
    /** Subscribes the engine to channel envelopes. */
    suspend fun start()

    /** Broadcasts the local state vector to all peers immediately. */
    suspend fun syncNow()

    /**
     * Convenience to apply a locally-authored entry: stamps it with a fresh HLC
     * version, persists it to the local store, and (if started) broadcasts it
     * via Push. Returns the resulting entry with its assigned version.
     */
    suspend fun writeLocal(
        entityType: String,
        entityId: String,
        payload: String,
        isTombstone: Boolean = false,
    ): SyncableEntry
}

// ===========================================================================
// CompanionStateSyncEngine  (CompanionStateSyncEngine.cs)
// ===========================================================================

/**
 * Default [ICompanionStateSyncEngine].
 *
 * Protocol — convergent in <= 2 round-trips per peer pair:
 *   1. syncNow        -> broadcast Announce(localStateVector)
 *   2. Peer receives Announce -> diff -> reply Request(missing)
 *   3. We receive Request -> gather via store.getSince -> Push
 *   4. Peer receives Push -> apply for each entry
 *   5. Peer re-announces if anything applied — converges.
 */
class CompanionStateSyncEngine(
    private val channel: ICompanionStateChannel,
    private val store: ISyncableEntryStore,
    private val clock: HybridLogicalClock,
    private val wallClock: () -> Instant = { Instant.now() },
) : ICompanionStateSyncEngine {

    private var subscription: AutoCloseable? = null
    @Volatile private var disposed = false

    override suspend fun start() {
        throwIfDisposed()
        if (subscription == null) {
            subscription = channel.subscribe { envelope -> handleEnvelope(envelope) }
        }
    }

    override suspend fun syncNow() {
        throwIfDisposed()
        val vector = store.getStateVector()
        channel.send(
            SyncEnvelope(
                kind = SyncEnvelopeKind.Announce,
                fromNodeId = channel.localNodeId,
                stateVector = vector,
                requests = null,
                entries = null,
            )
        )
    }

    override suspend fun writeLocal(
        entityType: String,
        entityId: String,
        payload: String,
        isTombstone: Boolean,
    ): SyncableEntry {
        throwIfDisposed()
        require(entityType.isNotBlank()) { "entityType required" }
        require(entityId.isNotBlank()) { "entityId required" }

        val entry = SyncableEntry(
            entityType = entityType,
            entityId = entityId,
            version = clock.tick(),
            isTombstone = isTombstone,
            contentHash = computeHash(payload),
            payload = payload,
            sourceNodeId = channel.localNodeId,
            authoredAt = wallClock(),
        )

        store.apply(entry)

        if (subscription != null) {
            channel.send(
                SyncEnvelope(
                    kind = SyncEnvelopeKind.Push,
                    fromNodeId = channel.localNodeId,
                    stateVector = null,
                    requests = null,
                    entries = listOf(entry),
                )
            )
        }
        return entry
    }

    // -- Inbound envelope handling ------------------------------------------

    private suspend fun handleEnvelope(envelope: SyncEnvelope) {
        when (envelope.kind) {
            SyncEnvelopeKind.Announce -> handleAnnounce(envelope)
            SyncEnvelopeKind.Request -> handleRequest(envelope)
            SyncEnvelopeKind.Push -> handlePush(envelope)
        }
    }

    private suspend fun handleAnnounce(envelope: SyncEnvelope) {
        val peerVector = envelope.stateVector ?: return
        val local = store.getStateVector()
        val localMap = local.associate { it.entityType to it.maxKnownVersion }

        val requests = ArrayList<RequestItem>()
        for (peer in peerVector) {
            val ourMax = localMap[peer.entityType] ?: 0L
            if (peer.maxKnownVersion > ourMax) {
                requests.add(RequestItem(peer.entityType, ourMax))
            }
        }
        if (requests.isEmpty()) return

        channel.send(
            SyncEnvelope(
                kind = SyncEnvelopeKind.Request,
                fromNodeId = channel.localNodeId,
                stateVector = null,
                requests = requests,
                entries = null,
            )
        )
    }

    private suspend fun handleRequest(envelope: SyncEnvelope) {
        val requests = envelope.requests
        if (requests.isNullOrEmpty()) return
        val collected = ArrayList<SyncableEntry>()
        for (req in requests) {
            collected.addAll(store.getSince(req.entityType, req.sinceVersion))
        }
        if (collected.isEmpty()) return

        channel.send(
            SyncEnvelope(
                kind = SyncEnvelopeKind.Push,
                fromNodeId = channel.localNodeId,
                stateVector = null,
                requests = null,
                entries = collected,
            )
        )
    }

    private suspend fun handlePush(envelope: SyncEnvelope) {
        val entries = envelope.entries ?: return
        var anyApplied = false
        for (e in entries) {
            clock.observe(e.version)
            val applied = store.apply(e)
            anyApplied = anyApplied || applied
        }
        // If anything applied, re-announce so other peers can converge too.
        if (anyApplied) syncNow()
    }

    // -- AutoCloseable ------------------------------------------------------

    override fun close() {
        if (disposed) return
        disposed = true
        subscription?.close()
        subscription = null
    }

    private fun throwIfDisposed() {
        check(!disposed) { "CompanionStateSyncEngine is disposed" }
    }

    private companion object {
        fun computeHash(payload: String): String {
            val digest = MessageDigest.getInstance("SHA-256")
                .digest(payload.toByteArray(Charsets.UTF_8))
            val sb = StringBuilder(digest.size * 2)
            for (b in digest) {
                val v = b.toInt() and 0xFF
                sb.append(HEX[v ushr 4])
                sb.append(HEX[v and 0x0F])
            }
            return sb.toString()
        }

        private val HEX = "0123456789abcdef".toCharArray()
    }
}

// ===========================================================================
// PersonaStateSyncBridge  (PersonaStateSyncBridge.cs)
// ===========================================================================

/**
 * Bridges [IPersonaStore] <-> [ICompanionStateSyncEngine]. On [save], the
 * persona is JSON-serialised and pushed.
 *
 * The JSON shape matches the C# System.Text.Json serialisation of
 * PersonaState so the payload is interoperable with other language ports.
 */
class PersonaStateSyncBridge(
    private val store: IPersonaStore,
    private val engine: ICompanionStateSyncEngine,
) {
    /** Persists [persona] locally AND broadcasts it via sync. */
    suspend fun save(persona: PersonaState) {
        store.saveAsync(persona)
        val payload = encode(persona)
        engine.writeLocal(EntityType, persona.userId, payload, isTombstone = false)
    }

    companion object {
        /** EntityType used on the wire for PersonaState entries. */
        const val EntityType: String = "PersonaState"

        /**
         * Decodes a [SyncableEntry] back into a [PersonaState]. Returns null
         * for tombstones or mismatched entity types.
         */
        fun tryDecode(entry: SyncableEntry): PersonaState? {
            if (entry.isTombstone) return null
            if (entry.entityType != EntityType) return null
            return decode(entry.payload)
        }

        // -- JSON (matches C# PersonaState property set) --------------------

        internal fun encode(p: PersonaState): String {
            val sb = StringBuilder()
            sb.append('{')
            sb.append("\"UserId\":").append(jsonStr(p.userId)).append(',')
            sb.append("\"LastUpdatedUtc\":").append(jsonStr(p.lastUpdatedAt.toString())).append(',')
            sb.append("\"Verbosity\":").append(jsonStr(p.verbosity)).append(',')
            sb.append("\"Formality\":").append(jsonStr(p.formality)).append(',')
            sb.append("\"PreferredLocale\":")
                .append(p.preferredLocale?.let { jsonStr(it) } ?: "null").append(',')
            sb.append("\"TopicWeights\":{")
            var first = true
            for ((k, v) in p.topicWeights) {
                if (!first) sb.append(',')
                first = false
                sb.append(jsonStr(k)).append(':').append(floatJson(v))
            }
            sb.append("},")
            sb.append("\"DisfavouredTopics\":[")
            for ((i, t) in p.disfavouredTopics.withIndex()) {
                if (i > 0) sb.append(',')
                sb.append(jsonStr(t))
            }
            sb.append("],")
            sb.append("\"TotalInteractions\":").append(p.totalInteractions).append(',')
            sb.append("\"PositiveSignals\":").append(p.positiveSignals).append(',')
            sb.append("\"NegativeSignals\":").append(p.negativeSignals)
            sb.append('}')
            return sb.toString()
        }

        internal fun decode(payload: String): PersonaState? {
            return try {
                val obj = LENIENT_JSON.parseToJsonElement(payload).let {
                    (it as? kotlinx.serialization.json.JsonObject) ?: return null
                }
                fun str(key: String): String? =
                    (obj[key] as? kotlinx.serialization.json.JsonPrimitive)
                        ?.takeIf { it.isString }?.content
                fun intOf(key: String): Int =
                    (obj[key] as? kotlinx.serialization.json.JsonPrimitive)
                        ?.content?.toIntOrNull() ?: 0

                val userId = str("UserId") ?: "default"
                val persona = PersonaState(userId)
                str("LastUpdatedUtc")?.let {
                    runCatching { persona.lastUpdatedAt = Instant.parse(it) }
                }
                str("Verbosity")?.let { persona.verbosity = it }
                str("Formality")?.let { persona.formality = it }
                persona.preferredLocale = str("PreferredLocale")
                (obj["TopicWeights"] as? kotlinx.serialization.json.JsonObject)?.forEach { (k, v) ->
                    (v as? kotlinx.serialization.json.JsonPrimitive)?.content?.toFloatOrNull()
                        ?.let { persona.topicWeights[k] = it }
                }
                (obj["DisfavouredTopics"] as? kotlinx.serialization.json.JsonArray)?.forEach { el ->
                    (el as? kotlinx.serialization.json.JsonPrimitive)
                        ?.takeIf { it.isString }?.content?.let { persona.disfavouredTopics.add(it) }
                }
                persona.totalInteractions = intOf("TotalInteractions")
                persona.positiveSignals = intOf("PositiveSignals")
                persona.negativeSignals = intOf("NegativeSignals")
                persona
            } catch (_: Exception) {
                null
            }
        }
    }
}

// ===========================================================================
// LoraAdapterSyncBridge  (LoraAdapterSyncBridge.cs)
// ===========================================================================

/**
 * (Phase D4) Payload of a synced LoRA adapter snapshot.
 *
 * @param adapterId Stable id (typically "personal-{userId}").
 * @param base64Bytes Adapter file contents, base64-encoded.
 * @param trainedAtUtc When training that produced these bytes finished (ISO-8601).
 * @param stepCount Total training steps so far (monotonic).
 */
@Serializable
data class LoraAdapterSnapshot(
    val adapterId: String,
    val base64Bytes: String,
    val trainedAtUtc: String,
    val stepCount: Long,
)

/**
 * (Phase D4) Bridges trained LoRA adapter bytes across the user's devices
 * through the [ICompanionStateSyncEngine]. Adapter bytes are base64-encoded
 * into the payload; receiving devices decode and persist to disk.
 */
class LoraAdapterSyncBridge(
    private val engine: ICompanionStateSyncEngine,
) {
    /** Publish a trained adapter to peer devices. */
    suspend fun publish(adapterId: String, adapterPath: String, stepCount: Long) {
        require(adapterId.isNotBlank()) { "adapterId required" }
        require(adapterPath.isNotBlank()) { "adapterPath required" }
        val file = File(adapterPath)
        if (!file.exists()) throw java.io.FileNotFoundException("adapter file not found: $adapterPath")
        val bytes = file.readBytes()
        val snapshot = LoraAdapterSnapshot(
            adapterId = adapterId,
            base64Bytes = Base64.getEncoder().encodeToString(bytes),
            trainedAtUtc = Instant.now().toString(),
            stepCount = stepCount,
        )
        val payload = JSON.encodeToString(LoraAdapterSnapshot.serializer(), snapshot)
        engine.writeLocal(EntityType, adapterId, payload, isTombstone = false)
    }

    companion object {
        /** EntityType used on the wire. */
        const val EntityType: String = "LoraAdapter"

        private val JSON = Json { encodeDefaults = true; ignoreUnknownKeys = true }

        /**
         * Decode an inbound [SyncableEntry] and write the adapter to
         * [destinationPath]. Returns the decoded snapshot for caller-side
         * bookkeeping (e.g. trigger Apply), or null for tombstones / mismatched
         * types / undecodable payloads.
         */
        fun tryWrite(entry: SyncableEntry, destinationPath: String): LoraAdapterSnapshot? {
            if (entry.isTombstone) return null
            if (entry.entityType != EntityType) return null
            val snapshot = try {
                JSON.decodeFromString(LoraAdapterSnapshot.serializer(), entry.payload)
            } catch (ex: Exception) {
                System.err.println("[LoraAdapterSyncBridge] inbound payload decode failed: ${ex.message}")
                return null
            }
            if (snapshot.base64Bytes.isEmpty()) return snapshot
            try {
                val destFile = File(destinationPath)
                destFile.parentFile?.mkdirs()
                val bytes = Base64.getDecoder().decode(snapshot.base64Bytes)
                destFile.writeBytes(bytes)
            } catch (ex: Exception) {
                System.err.println("[LoraAdapterSyncBridge] write failed: ${ex.message}")
            }
            return snapshot
        }
    }
}

// ===========================================================================
// CompanionConversationSyncBridge  (CompanionConversationSyncBridge.cs)
// ===========================================================================

/**
 * (Phase A2) Wire-format payload of an in-flight conversation turn. The
 * entityId is the [sessionId] so multiple sessions converge independently.
 *
 * @param sessionId Stable identifier the originating device uses.
 * @param userText The latest user utterance for this turn (may be partial).
 * @param assistantText Assistant reply so far — empty until tokens start.
 * @param isTurnComplete True once the turn finished; false during streaming.
 * @param startedAtUtc When the originating device started the turn (ISO-8601).
 * @param updatedAtUtc When this delta was authored (ISO-8601).
 */
@Serializable
data class ConversationStateDelta(
    val sessionId: String,
    val userText: String,
    val assistantText: String,
    val isTurnComplete: Boolean,
    val startedAtUtc: String,
    val updatedAtUtc: String,
)

/**
 * (Phase A2) Bridges live [ConversationStateDelta] snapshots to the
 * [ICompanionStateSyncEngine] wire so any peer device subscribing to the
 * "ConversationState" entity type can mirror or hand off the conversation.
 */
class CompanionConversationSyncBridge(
    private val engine: ICompanionStateSyncEngine,
) {
    /** Broadcast a conversation-state snapshot to peer devices. */
    suspend fun publish(delta: ConversationStateDelta) {
        require(delta.sessionId.isNotBlank()) { "SessionId required" }
        val payload = JSON.encodeToString(ConversationStateDelta.serializer(), delta)
        engine.writeLocal(EntityType, delta.sessionId, payload, isTombstone = false)
    }

    /**
     * Mark the session as ended so peers can clean up shadow state. Uses the
     * sync-layer tombstone primitive — peers receive an empty payload.
     */
    suspend fun terminate(sessionId: String) {
        require(sessionId.isNotBlank()) { "sessionId required" }
        engine.writeLocal(EntityType, sessionId, payload = "", isTombstone = true)
    }

    companion object {
        /** EntityType used on the wire for conversation-state entries. */
        const val EntityType: String = "ConversationState"

        private val JSON = Json { encodeDefaults = true; ignoreUnknownKeys = true }

        /** Decode a sync-layer entry back to a typed delta. */
        fun tryDecode(entry: SyncableEntry): ConversationStateDelta? {
            if (entry.isTombstone) return null
            if (entry.entityType != EntityType) return null
            return try {
                JSON.decodeFromString(ConversationStateDelta.serializer(), entry.payload)
            } catch (_: Exception) {
                null
            }
        }
    }
}

// ===========================================================================
// Shared JSON helpers for the hand-rolled PersonaState encoder
// ===========================================================================

private val LENIENT_JSON = Json { ignoreUnknownKeys = true; isLenient = true }

private fun jsonStr(s: String): String {
    val sb = StringBuilder(s.length + 2)
    sb.append('"')
    for (c in s) {
        when (c) {
            '"' -> sb.append("\\\"")
            '\\' -> sb.append("\\\\")
            '\n' -> sb.append("\\n")
            '\r' -> sb.append("\\r")
            '\t' -> sb.append("\\t")
            else -> if (c.code < 0x20) sb.append(String.format("\\u%04x", c.code)) else sb.append(c)
        }
    }
    sb.append('"')
    return sb.toString()
}

private fun floatJson(f: Float): String {
    // Emit integral floats without a trailing ".0" to stay compact; otherwise
    // the shortest round-trippable representation.
    return if (f == f.toLong().toFloat()) f.toLong().toString() else f.toString()
}
