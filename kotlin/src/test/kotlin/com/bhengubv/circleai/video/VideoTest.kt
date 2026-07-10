// VideoTest.kt
//
// Verifies the CircleAI.Video Kotlin port against the C# reference: the
// NullVideoGenerator empty-video contract (echoing the requested resolution),
// the NullStyleScript pass-through, the InMemoryStyleReference register/get/list
// with OrdinalIgnoreCase key semantics + defensive-copy list, the StyleId /
// VideoResolution value shapes, and value equality over byte-array fields.

package com.bhengubv.circleai.video

import kotlinx.coroutines.runBlocking
import org.junit.jupiter.api.Test
import java.time.Duration
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class VideoTest {

    private fun style(id: String, name: String = id) = StyleReference(
        id = StyleId(id),
        displayName = name,
        shortDescription = "desc-$id",
        attribution = StyleAttribution(source = "src", license = "PD"),
        voicePersonaId = "voice-$id",
        frames = listOf(StyleReferenceFrame(byteArrayOf(1, 2), "image/png", "cap")),
    )

    // ── StyleId / VideoResolution ──────────────────────────────────────

    @Test
    fun `style id stringifies to its value`() {
        val id = StyleId("pooh-1926")
        assertEquals("pooh-1926", id.toString())
        assertEquals("pooh-1926", StyleId.asString(id))
    }

    @Test
    fun `video resolution presets`() {
        assertEquals(VideoResolution(720, 480), VideoResolution.P480)
        assertEquals(VideoResolution(1280, 720), VideoResolution.P720)
        assertEquals(VideoResolution(1920, 1080), VideoResolution.P1080)
    }

    // ── NullVideoGenerator ─────────────────────────────────────────────

    @Test
    fun `null video generator returns an empty video echoing the resolution`() = runBlocking {
        val g = NullVideoGenerator.Instance
        assertEquals("null", g.backendId)
        val req = VideoGenerationRequest(
            prompt = "hello",
            duration = Duration.ofSeconds(5),
            resolution = VideoResolution.P720,
        )
        val result = g.generateAsync(req)
        assertEquals(0, result.videoBytes.size)
        assertEquals("video/mp4", result.mimeType)
        assertEquals(Duration.ZERO, result.duration)
        assertEquals(0, result.frameCount)
        assertEquals(VideoResolution.P720, result.resolution) // echoes request
        assertEquals("null", result.backendId)
    }

    // ── NullStyleScript ────────────────────────────────────────────────

    @Test
    fun `null style script passes the message through unchanged`() = runBlocking {
        val s = NullStyleScript.Instance
        assertEquals("null", s.backendId)
        val req = StyleScriptRequest(sourceMessage = "call me back", style = StyleId("noir"))
        val r = s.rewriteAsync(req)
        assertEquals("call me back", r.rewrittenText)
        assertEquals(StyleId("noir"), r.style)
        assertNull(r.voicePersonaId)
        assertEquals(Duration.ZERO, r.estimatedSpokenDuration)
    }

    // ── InMemoryStyleReference ─────────────────────────────────────────

    @Test
    fun `in-memory catalogue registers and looks up styles`() = runBlocking {
        val cat = InMemoryStyleReference()
        assertEquals("in-memory", cat.backendId)
        assertTrue(cat.listAsync().isEmpty())
        assertNull(cat.getAsync(StyleId("nope")))

        cat.registerAsync(style("noir-detective", "Noir"))
        cat.registerAsync(style("space-opera", "Space"))

        assertEquals("Noir", cat.getAsync(StyleId("noir-detective"))?.displayName)
        assertEquals("Space", cat.getAsync(StyleId("space-opera"))?.displayName)
        assertEquals(2, cat.listAsync().size)
    }

    @Test
    fun `catalogue lookup is case-insensitive and last-write-wins`() = runBlocking {
        val cat = InMemoryStyleReference()
        cat.registerAsync(style("Pooh-1926", "First"))
        // Same id in a different case overwrites (OrdinalIgnoreCase semantics).
        cat.registerAsync(style("pooh-1926", "Second"))

        assertEquals(1, cat.listAsync().size)
        assertEquals("Second", cat.getAsync(StyleId("POOH-1926"))?.displayName)
        assertEquals("Second", cat.getAsync(StyleId("pooh-1926"))?.displayName)
    }

    @Test
    fun `list returns a defensive copy that does not mutate the catalogue`() = runBlocking {
        val cat = InMemoryStyleReference()
        cat.registerAsync(style("a"))
        val snapshot = cat.listAsync().toMutableList()
        snapshot.clear()
        // Catalogue is unaffected by mutating the returned list.
        assertEquals(1, cat.listAsync().size)
    }

    // ── Records ────────────────────────────────────────────────────────

    @Test
    fun `style reference frame value equality respects byte contents`() {
        val a = StyleReferenceFrame(byteArrayOf(1, 2, 3), "image/png", "c")
        val b = StyleReferenceFrame(byteArrayOf(1, 2, 3), "image/png", "c")
        assertEquals(a, b)
        assertEquals(a.hashCode(), b.hashCode())
        assertFalse(a == StyleReferenceFrame(byteArrayOf(9), "image/png", "c"))
    }

    @Test
    fun `video generation result value equality respects byte contents`() {
        val r1 = VideoGenerationResult(byteArrayOf(1, 2), "video/mp4", Duration.ofSeconds(2), 48, VideoResolution.P480, "cogvideox-2b")
        val r2 = VideoGenerationResult(byteArrayOf(1, 2), "video/mp4", Duration.ofSeconds(2), 48, VideoResolution.P480, "cogvideox-2b")
        assertEquals(r1, r2)
        assertEquals(r1.hashCode(), r2.hashCode())
    }

    @Test
    fun `request carries optional style reference audio and seed`() {
        val req = VideoGenerationRequest(
            prompt = "p",
            duration = Duration.ofSeconds(3),
            resolution = VideoResolution.P1080,
            frameRate = 30,
            styleId = StyleId("anime"),
            referenceImage = StyleReferenceFrame(byteArrayOf(5), "image/jpeg"),
            audioTrack = AudioTrack(byteArrayOf(0, 0), 16_000, Duration.ofSeconds(3)),
            seed = 42L,
        )
        assertEquals(StyleId("anime"), req.styleId)
        assertEquals(30, req.frameRate)
        assertEquals(42L, req.seed)
        assertEquals(16_000, req.audioTrack?.sampleRateHz)
    }
}
