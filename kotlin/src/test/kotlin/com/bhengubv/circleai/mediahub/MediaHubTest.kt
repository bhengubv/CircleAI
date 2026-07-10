// MediaHubTest.kt
//
// Verifies the CircleAI.MediaHub port against the C# reference semantics:
//   - InMemoryMediaLibrary: GetAsync (blank id rejected), SearchAsync matches
//     case-insensitively, orders by title ASCENDING, caps at topK, rejects topK<=0
//   - InMemorySyncedPlayback: join validates args; subscribe delivers broadcasts;
//     dispose unsubscribes; a throwing subscriber does not break fan-out;
//     broadcasting an unknown session is a no-op
//   - Null implementations are inert

package com.bhengubv.circleai.mediahub

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue

class MediaHubTest {

    private fun item(id: String, title: String, kind: String = "audio") =
        MediaItem(id, title, kind, Duration.ofSeconds(200), "audio/mpeg")

    // ── Library ──────────────────────────────────────────────────────────────

    @Test
    fun `library get returns item or null and rejects blank id`() = runTest {
        val lib = InMemoryMediaLibrary()
        val it = item("i1", "Hello")
        lib.add(it)
        assertEquals(it, lib.getAsync("i1"))
        assertNull(lib.getAsync("missing"))
        assertEquals("in-memory", lib.backendId)
        assertFailsWith<IllegalArgumentException> { lib.getAsync("  ") }
    }

    @Test
    fun `library search is case-insensitive, ordered by title ascending, capped`() = runTest {
        val lib = InMemoryMediaLibrary()
        lib.add(item("1", "Zebra song"))
        lib.add(item("2", "apple tune"))
        lib.add(item("3", "Banana beat"))
        lib.add(item("4", "cherry"))

        // Match everything containing a common letter to test ordering; use "a".
        val hits = lib.searchAsync("a")
        // Titles containing 'a' (case-insensitive): "Zebra song", "apple tune",
        // "Banana beat"; ordered ascending ignoring case -> apple, Banana, Zebra.
        assertEquals(listOf("apple tune", "Banana beat", "Zebra song"), hits.map { it.title })

        // topK caps.
        assertEquals(listOf("apple tune"), lib.searchAsync("a", topK = 1).map { it.title })

        // Case-insensitivity of the query itself.
        assertEquals(listOf("apple tune"), lib.searchAsync("APPLE").map { it.title })

        assertFailsWith<IllegalArgumentException> { lib.searchAsync("x", topK = 0) }
    }

    // ── Synced playback ──────────────────────────────────────────────────────

    @Test
    fun `join validates args`() = runTest {
        val hub = InMemorySyncedPlayback()
        assertEquals("in-memory", hub.backendId)
        assertFailsWith<IllegalArgumentException> { hub.joinSessionAsync("", "u1") }
        assertFailsWith<IllegalArgumentException> { hub.joinSessionAsync("s1", " ") }
        // Valid join is accepted (idempotent).
        hub.joinSessionAsync("s1", "u1")
        hub.joinSessionAsync("s1", "u1")
    }

    @Test
    fun `subscribe receives broadcasts and dispose unsubscribes`() = runTest {
        val hub = InMemorySyncedPlayback()
        val got = ArrayList<PlaybackPosition>()
        val token = hub.subscribe("s1") { got.add(it) }

        val p1 = PlaybackPosition("track", Duration.ofSeconds(5), Instant.parse("2026-07-10T00:00:00Z"))
        hub.broadcastPositionAsync("s1", p1)
        assertEquals(listOf(p1), got)

        // After dispose, further broadcasts are not delivered.
        token.close()
        hub.broadcastPositionAsync("s1", p1.copy(position = Duration.ofSeconds(10)))
        assertEquals(listOf(p1), got)
    }

    @Test
    fun `multiple subscribers all receive and a throwing subscriber does not break fan-out`() = runTest {
        val hub = InMemorySyncedPlayback()
        val a = ArrayList<PlaybackPosition>()
        val c = ArrayList<PlaybackPosition>()
        hub.subscribe("s1") { a.add(it) }
        hub.subscribe("s1") { throw RuntimeException("boom") } // must be swallowed
        hub.subscribe("s1") { c.add(it) }

        val p = PlaybackPosition("t", Duration.ZERO, Instant.EPOCH)
        hub.broadcastPositionAsync("s1", p)

        assertEquals(listOf(p), a)
        assertEquals(listOf(p), c)
    }

    @Test
    fun `broadcasting an unknown session is a no-op`() = runTest {
        val hub = InMemorySyncedPlayback()
        // No subscribers, unknown session — must not throw.
        hub.broadcastPositionAsync("ghost", PlaybackPosition("t", Duration.ZERO, Instant.EPOCH))
    }

    // ── Null implementations ─────────────────────────────────────────────────

    @Test
    fun `null implementations are inert`() = runTest {
        assertEquals("null", NullMediaLibrary.Instance.backendId)
        assertNull(NullMediaLibrary.Instance.getAsync("x"))
        assertTrue(NullMediaLibrary.Instance.searchAsync("x").isEmpty())

        val sp = NullSyncedPlayback.Instance
        assertEquals("null", sp.backendId)
        sp.joinSessionAsync("s", "u")
        var called = false
        val tok = sp.subscribe("s") { called = true }
        sp.broadcastPositionAsync("s", PlaybackPosition("t", Duration.ZERO, Instant.EPOCH))
        tok.close()
        assertTrue(!called) // Null playback never delivers
    }
}
