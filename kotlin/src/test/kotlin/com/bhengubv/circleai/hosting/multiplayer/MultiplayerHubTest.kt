// MultiplayerHubTest.kt
//
// Verifies MultiplayerHub against the C# reference: presence on connect/join,
// LWW-by-rev edit acceptance/rejection, cursor + join/leave broadcasts, the
// GuestPeerIdentity defaults, and the stable colour hash.

package com.bhengubv.circleai.hosting.multiplayer

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.BeforeEach
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

class MultiplayerHubTest {

    @BeforeEach
    fun reset() = MultiplayerHub.resetStateForTesting()

    private fun hub(peerId: String, name: String, bc: RecordingBroadcaster) =
        MultiplayerHub(GuestPeerIdentity(peerId, name), bc)

    @Test
    fun `guest identity defaults`() {
        val g = GuestPeerIdentity()
        assertEquals("Guest", g.displayName)
        assertEquals(32, g.peerId.length) // 32-hex, no dashes
        val g2 = GuestPeerIdentity("pid", "Alice")
        assertEquals("pid", g2.peerId)
        assertEquals("Alice", g2.displayName)
    }

    @Test
    fun `join announces PeerJoined and records presence`() = runTest {
        val bc = RecordingBroadcaster()
        val h = hub("p1", "Alice", bc)
        h.onConnected("c1")
        h.joinDocument("c1", "doc42")

        val peers = MultiplayerHub.peers("doc42")
        assertEquals(1, peers.size)
        assertEquals("Alice", peers[0].displayName)

        val joined = bc.events.single { it.eventName == "PeerJoined" }
        assertEquals("doc:doc42", joined.group)
        assertEquals("doc42", joined.args[0])
        assertEquals("c1", joined.args[1])
        assertEquals("Alice", joined.args[2])
    }

    @Test
    fun `edit accepted when rev is greater, rejected when stale`() = runTest {
        val bc = RecordingBroadcaster()
        val h = hub("p1", "Alice", bc)
        h.onConnected("c1")
        h.joinDocument("c1", "d")

        // First edit rev 5 accepted.
        assertEquals(5L, h.sendEdit("c1", "d", "hello", 5))
        assertEquals(5L, MultiplayerHub.currentRev("d"))
        assertEquals(1, bc.events.count { it.eventName == "EditApplied" })

        // Stale edit rev 3 rejected — returns current server rev (5), no new broadcast.
        assertEquals(5L, h.sendEdit("c1", "d", "old", 3))
        assertEquals(1, bc.events.count { it.eventName == "EditApplied" })

        // Newer edit rev 8 accepted.
        assertEquals(8L, h.sendEdit("c1", "d", "newer", 8))
        assertEquals(8L, MultiplayerHub.currentRev("d"))
        assertEquals(2, bc.events.count { it.eventName == "EditApplied" })
    }

    @Test
    fun `cursor and leave broadcast to others in group`() = runTest {
        val bc = RecordingBroadcaster()
        val h = hub("p1", "Alice", bc)
        h.onConnected("c1")
        h.joinDocument("c1", "d")
        bc.events.clear()

        h.sendCursor("c1", "d", 3, 7)
        val cursor = bc.events.single { it.eventName == "CursorChanged" }
        assertEquals(listOf<Any?>("c1", "Alice", cursor.args[2], 3, 7), cursor.args)

        h.leaveDocument("c1", "d")
        assertTrue(bc.events.any { it.eventName == "PeerLeft" })
        assertEquals(0, MultiplayerHub.peers("d").size)
    }

    @Test
    fun `disconnect announces PeerLeft when in a doc`() = runTest {
        val bc = RecordingBroadcaster()
        val h = hub("p1", "Alice", bc)
        h.onConnected("c1")
        h.joinDocument("c1", "d")
        bc.events.clear()

        h.onDisconnected("c1")
        assertTrue(bc.events.any { it.eventName == "PeerLeft" })
        assertEquals(0, MultiplayerHub.peers("d").size)
    }

    @Test
    fun `colourFor is stable and empty-id yields fallback`() {
        assertEquals("#5a4fcf", MultiplayerHub.colourFor(""))
        val a = MultiplayerHub.colourFor("peer-x")
        val b = MultiplayerHub.colourFor("peer-x")
        assertEquals(a, b)
        assertTrue(a.startsWith("hsl("))
        assertNotNull(a)
    }
}
