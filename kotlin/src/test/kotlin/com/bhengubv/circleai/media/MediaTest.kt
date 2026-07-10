// MediaTest.kt
//
// Verifies the CircleAI.Media port against the C# reference semantics:
//   - Add stores/replaces by AssetId; blank AssetId rejected
//   - Get returns the asset or null
//   - ListByKind filters by kind and orders newest-first
//   - Search matches title substrings case-insensitively, newest-first, capped at topK
//   - Search rejects non-positive topK

package com.bhengubv.circleai.media

import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertSame

class MediaTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    private fun asset(
        id: String,
        title: String = "Track",
        kind: MediaKind = MediaKind.Audio,
        duration: Duration? = Duration.ofSeconds(180),
        bytes: Long = 1_024,
        mime: String = "audio/mpeg",
        at: Instant = t0,
    ) = MediaAsset(id, title, kind, duration, bytes, mime, at)

    @Test
    fun `add stores and get retrieves`() {
        val lib = InMemoryMediaLibrary()
        val a = asset("a1", title = "Hello World")
        lib.add(a)
        assertSame(a, lib.get("a1"))
        assertNull(lib.get("missing"))
    }

    @Test
    fun `add replaces on same id`() {
        val lib = InMemoryMediaLibrary()
        lib.add(asset("a1", title = "First"))
        lib.add(asset("a1", title = "Second"))
        assertEquals("Second", lib.get("a1")?.title)
    }

    @Test
    fun `add rejects blank assetId`() {
        val lib = InMemoryMediaLibrary()
        assertFailsWith<IllegalArgumentException> { lib.add(asset("   ")) }
        assertFailsWith<IllegalArgumentException> { lib.add(asset("")) }
    }

    @Test
    fun `listByKind filters and orders newest first`() {
        val lib = InMemoryMediaLibrary()
        lib.add(asset("old", kind = MediaKind.Audio, at = t0))
        lib.add(asset("new", kind = MediaKind.Audio, at = t0.plusSeconds(60)))
        lib.add(asset("vid", kind = MediaKind.Video, at = t0.plusSeconds(120)))
        lib.add(asset("img", kind = MediaKind.Image, duration = null, at = t0.plusSeconds(30)))

        val audio = lib.listByKind(MediaKind.Audio)
        assertEquals(listOf("new", "old"), audio.map { it.assetId })

        assertEquals(listOf("vid"), lib.listByKind(MediaKind.Video).map { it.assetId })
        assertEquals(listOf("img"), lib.listByKind(MediaKind.Image).map { it.assetId })
        assertNull(lib.listByKind(MediaKind.Image).single().duration)
    }

    @Test
    fun `search is case-insensitive substring, newest first, capped at topK`() {
        val lib = InMemoryMediaLibrary()
        lib.add(asset("1", title = "Sunset Boulevard", at = t0))
        lib.add(asset("2", title = "SUNSET rising", at = t0.plusSeconds(60)))
        lib.add(asset("3", title = "Midnight", at = t0.plusSeconds(120)))

        val hits = lib.search("sunset")
        assertEquals(listOf("2", "1"), hits.map { it.assetId })

        // topK caps the result set (still newest-first).
        assertEquals(listOf("2"), lib.search("sunset", topK = 1).map { it.assetId })

        // No match -> empty.
        assertEquals(emptyList(), lib.search("nope"))
    }

    @Test
    fun `search rejects non-positive topK`() {
        val lib = InMemoryMediaLibrary()
        assertFailsWith<IllegalArgumentException> { lib.search("x", topK = 0) }
        assertFailsWith<IllegalArgumentException> { lib.search("x", topK = -3) }
    }
}
