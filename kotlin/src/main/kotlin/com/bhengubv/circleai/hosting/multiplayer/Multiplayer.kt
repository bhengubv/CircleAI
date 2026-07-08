// Multiplayer.kt
//
// Kotlin port of CircleAI.Hosting.Multiplayer — the C# reference is the EXACT
// spec (Contracts.cs, MultiplayerHub.cs). Peer-identity surface + the live
// collaboration hub (per-document group, LWW-by-rev edits, cursors, presence).
//
// The C# hub subclasses SignalR's `Hub`, driving broadcasts through
// `Clients.OthersInGroup(...).SendAsync(...)`. Kotlin/JVM has no SignalR
// dependency in this portable core, so the transport is abstracted behind
// [IMultiplayerBroadcaster]: the hub owns all the group / rev / presence logic
// exactly as C# does, and the injected broadcaster is the seam a host wires to
// its real transport (SignalR, WebSocket, AetherNet, …). An in-memory
// recording broadcaster ships for tests + headless use.

package com.bhengubv.circleai.hosting.multiplayer

import java.time.Instant
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/**
 * Resolves the human-visible identity of the peer making a hub call.
 * Implementations typically pull from the active auth context or return a
 * guest record. Mirrors C# `IMultiplayerPeerIdentity`.
 */
interface IMultiplayerPeerIdentity {
    /** Stable id (used to derive a colour). */
    val peerId: String

    /** Human-readable display name. */
    val displayName: String
}

/**
 * Anonymous guest identity. Hosts can register this directly if no auth is
 * configured. Mirrors C# `GuestPeerIdentity` — a null [peerId] gets a random
 * 32-hex id and a null [displayName] becomes "Guest".
 */
class GuestPeerIdentity(
    peerId: String? = null,
    displayName: String? = null,
) : IMultiplayerPeerIdentity {
    override val peerId: String = peerId ?: UUID.randomUUID().toString().replace("-", "")
    override val displayName: String = displayName ?: "Guest"
}

/**
 * Snapshot of one peer's connection state. Mirrors C# nested
 * `MultiplayerHub.PeerState` record.
 */
data class PeerState(
    val connectionId: String,
    val displayName: String,
    val color: String,
    val docId: String?,
)

/**
 * The transport seam. The hub calls [sendToOthersInGroup] to fan an event out
 * to every peer in a document group except the sender. In C# this is
 * `Clients.OthersInGroup(group).SendAsync(eventName, args…)`; here the host
 * supplies the implementation. [addToGroup] / [removeFromGroup] mirror
 * SignalR's `Groups.AddToGroupAsync` / `RemoveFromGroupAsync`.
 */
interface IMultiplayerBroadcaster {
    suspend fun addToGroup(connectionId: String, group: String)
    suspend fun removeFromGroup(connectionId: String, group: String)
    suspend fun sendToOthersInGroup(
        group: String,
        senderConnectionId: String,
        eventName: String,
        args: List<Any?>,
    )
}

/** One event delivered to peers — captured by [RecordingBroadcaster]. */
data class MultiplayerEvent(
    val group: String,
    val senderConnectionId: String,
    val eventName: String,
    val args: List<Any?>,
)

/**
 * In-memory [IMultiplayerBroadcaster] for tests + headless hosts. Records
 * group membership and every broadcast so callers can assert on them.
 */
class RecordingBroadcaster : IMultiplayerBroadcaster {
    private val gate = Any()
    val events = ArrayList<MultiplayerEvent>()
    private val groups = HashMap<String, MutableSet<String>>()

    override suspend fun addToGroup(connectionId: String, group: String) {
        synchronized(gate) { groups.getOrPut(group) { LinkedHashSet() }.add(connectionId) }
    }

    override suspend fun removeFromGroup(connectionId: String, group: String) {
        synchronized(gate) { groups[group]?.remove(connectionId) }
    }

    override suspend fun sendToOthersInGroup(
        group: String,
        senderConnectionId: String,
        eventName: String,
        args: List<Any?>,
    ) {
        synchronized(gate) { events.add(MultiplayerEvent(group, senderConnectionId, eventName, args)) }
    }

    /** Connection ids currently in [group]. */
    fun membersOf(group: String): List<String> =
        synchronized(gate) { groups[group]?.toList() ?: emptyList() }

    /** Test helper — wipe all recorded state. */
    fun clear() {
        synchronized(gate) {
            events.clear()
            groups.clear()
        }
    }
}

// =====================================================================
// MultiplayerHub (MultiplayerHub.cs)
// =====================================================================

/**
 * Multiplayer collaboration hub. Per-document group, LWW-by-rev edits, live
 * cursors, presence. Mirrors C# `MultiplayerHub` — the SignalR
 * `Clients.OthersInGroup(...).SendAsync` calls become
 * [IMultiplayerBroadcaster.sendToOthersInGroup] calls, and `Context.ConnectionId`
 * is supplied explicitly to each handler (the caller owns the connection id).
 *
 * Static rev/peer maps mirror C# `RevByDoc` / `PeerByConn` and are shared
 * across every hub instance so presence and rev survive per-request hub churn.
 */
class MultiplayerHub(
    private val peerIdentity: IMultiplayerPeerIdentity,
    private val broadcaster: IMultiplayerBroadcaster,
) {

    /** SignalR `OnConnectedAsync`. Registers the peer's initial state. */
    fun onConnected(connectionId: String) {
        peerByConn[connectionId] = PeerState(
            connectionId = connectionId,
            displayName = peerIdentity.displayName,
            color = colourFor(peerIdentity.peerId),
            docId = null,
        )
    }

    /** SignalR `OnDisconnectedAsync`. Announces PeerLeft to the peer's doc group. */
    suspend fun onDisconnected(connectionId: String) {
        val peer = peerByConn.remove(connectionId)
        if (peer != null && !peer.docId.isNullOrEmpty()) {
            broadcaster.sendToOthersInGroup(
                docGroup(peer.docId), connectionId, "PeerLeft",
                listOf(peer.docId, peer.connectionId, peer.displayName),
            )
        }
    }

    suspend fun joinDocument(connectionId: String, docId: String) {
        if (docId.isBlank()) return
        broadcaster.addToGroup(connectionId, docGroup(docId))
        val peer = peerByConn[connectionId] ?: return
        val updated = peer.copy(docId = docId)
        peerByConn[connectionId] = updated
        broadcaster.sendToOthersInGroup(
            docGroup(docId), connectionId, "PeerJoined",
            listOf(docId, updated.connectionId, updated.displayName, updated.color),
        )
    }

    suspend fun leaveDocument(connectionId: String, docId: String) {
        if (docId.isBlank()) return
        broadcaster.removeFromGroup(connectionId, docGroup(docId))
        val peer = peerByConn[connectionId] ?: return
        val updated = peer.copy(docId = null)
        peerByConn[connectionId] = updated
        broadcaster.sendToOthersInGroup(
            docGroup(docId), connectionId, "PeerLeft",
            listOf(docId, updated.connectionId, updated.displayName),
        )
    }

    suspend fun sendCursor(connectionId: String, docId: String, line: Int, ch: Int) {
        val peer = peerByConn[connectionId] ?: return
        broadcaster.sendToOthersInGroup(
            docGroup(docId), connectionId, "CursorChanged",
            listOf(peer.connectionId, peer.displayName, peer.color, line, ch),
        )
    }

    /**
     * Apply an edit if its rev is greater than the server's current rev.
     * Returns the new rev (or the server's current rev if the client's rev was
     * stale). Mirrors the C# `RevByDoc.AddOrUpdate` LWW logic exactly.
     */
    suspend fun sendEdit(connectionId: String, docId: String, content: String, rev: Long): Long {
        val newRev = revByDoc.compute(docId) { _, prev ->
            when {
                prev == null -> DocRevState(maxOf(rev, 1L), Instant.now())
                rev <= prev.rev -> prev
                else -> DocRevState(rev, Instant.now())
            }
        }!!

        if (newRev.rev != rev) {
            // Rejected — client gets current rev back and can rebase.
            return newRev.rev
        }

        broadcaster.sendToOthersInGroup(
            docGroup(docId), connectionId, "EditApplied",
            listOf(docId, content, rev, connectionId),
        )
        return rev
    }

    companion object {
        // Static shared state — mirrors C# `RevByDoc` / `PeerByConn`.
        private val revByDoc = ConcurrentHashMap<String, DocRevState>()
        private val peerByConn = ConcurrentHashMap<String, PeerState>()

        /** Snapshot of who is currently in a document. */
        fun peers(docId: String): List<PeerState> =
            peerByConn.values.filter { it.docId == docId }

        /** Current server-known rev for a document (0 if never touched). */
        fun currentRev(docId: String): Long = revByDoc[docId]?.rev ?: 0L

        /** Test/admin hook — wipes static state. Do NOT call in production. */
        fun resetStateForTesting() {
            revByDoc.clear()
            peerByConn.clear()
        }

        private fun docGroup(docId: String): String = "doc:$docId"

        /**
         * Stable hash → HSL hue, so each peer lands on a different cursor colour
         * without a database column. Saturation + lightness fixed so the colour
         * reads on both dark and light themes. Byte-identical to the C# `unchecked`
         * int-overflow hash (32-bit wrapping via Int arithmetic).
         */
        fun colourFor(peerId: String): String {
            if (peerId.isEmpty()) return "#5a4fcf"
            var h = 0
            for (c in peerId) h = h * 31 + c.code // Int wraps at 32 bits, matching C# unchecked
            val hue = ((h % 360) + 360) % 360
            return "hsl($hue, 70%, 55%)"
        }
    }

    private data class DocRevState(val rev: Long, val updatedAt: Instant)
}
