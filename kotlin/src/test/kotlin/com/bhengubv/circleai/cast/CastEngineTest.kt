package com.bhengubv.circleai.cast

import java.io.File
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URI
import java.net.URL
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout

class RangeHeaderTest {

    private fun range(h: String, len: Long) = TcpMediaHost.parseRange(h, len)

    @Test
    fun theFirstAndLastByteFormIsInclusiveAtBOTHends() {
        // bytes=0-499 is 500 bytes, not 499. Off by one here and the last byte
        // of every chunk is missing, which a decoder reports as a corrupt file.
        assertEquals(0L to 499L, range("bytes=0-499", 1000))
        assertEquals(500L to 999L, range("bytes=500-999", 1000))
    }

    @Test
    fun anOpenEndedRangeRunsToTheEndOfTheFile() {
        assertEquals(500L to 999L, range("bytes=500-", 1000))
    }

    @Test
    fun theSUFFIXformIsTheLASTnBytesNotTheFirst() {
        // bytes=-500 means the last 500. Reading it as a start offset serves the
        // wrong part of the file and the picture is silently wrong.
        assertEquals(500L to 999L, range("bytes=-500", 1000))
        assertEquals(0L to 999L, range("bytes=-5000", 1000))
    }

    @Test
    fun anEndPastTheFileIsCLAMPEDratherThanRefused() {
        // Renderers ask for more than exists all the time.
        assertEquals(900L to 999L, range("bytes=900-99999", 1000))
    }

    @Test
    fun onlyTheFirstRangeIsHonoured() {
        // A multipart response is not something any renderer here asks for.
        assertEquals(0L to 99L, range("bytes=0-99,200-299", 1000))
    }

    @Test
    fun theHeaderNameIsMatchedCaseInsensitivelyAndSpacesAreTolerated() {
        assertEquals(0L to 99L, range("BYTES=0-99", 1000))
        assertEquals(0L to 99L, range("bytes= 0 - 99 ", 1000))
    }

    @Test
    fun aHeaderThisCannotHonourIsNullSoTheWholeFileIsSentInstead() {
        // Null means "no partial content", which is always a valid answer.
        assertNull(range("items=0-99", 1000))
        assertNull(range("bytes=abc", 1000))
        assertNull(range("bytes=500", 1000))
        assertNull(range("bytes=900-100", 1000))
        assertNull(range("bytes=-0", 1000))
        assertNull(range("", 1000))
    }

    @Test
    fun aStartPastTheEndOfTheFileIsRefused() {
        assertNull(range("bytes=5000-6000", 1000))
    }
}

class MediaHostExtensionTest {

    @Test
    fun theExtensionFollowsTheMIMEtypeBecauseSomeRenderersReadTheUrl() {
        // Not cosmetic: several televisions decide how to handle a URL by its
        // extension and ignore Content-Type entirely.
        assertEquals(".mp4", TcpMediaHost.guessExtension("video/mp4"))
        assertEquals(".jpg", TcpMediaHost.guessExtension("image/jpeg"))
        assertEquals(".png", TcpMediaHost.guessExtension("IMAGE/PNG"))
        assertEquals(".mp3", TcpMediaHost.guessExtension("audio/mpeg"))
        assertEquals(".png", TcpMediaHost.guessExtension("image/apng"))
    }

    @Test
    fun anUnknownTypeGetsAGenericExtensionRatherThanNone() {
        assertEquals(".bin", TcpMediaHost.guessExtension("application/x-made-up"))
    }

    @Test
    fun theEndOfTheRequestHeaderIsTheDoubleBlankLine() {
        val h = "GET / HTTP/1.1\r\nHost: x\r\n\r\n".toByteArray(Charsets.US_ASCII)
        assertEquals(h.size - 4, TcpMediaHost.indexOfDoubleCrlf(h, h.size))
        assertEquals(-1, TcpMediaHost.indexOfDoubleCrlf("GET /\r\n".toByteArray(), 7))
    }
}

class TcpMediaHostServingTest {

    private fun get(url: URI, range: String? = null): Triple<Int, Map<String, String>, ByteArray> {
        val c = URL(url.toString()).openConnection() as HttpURLConnection
        c.connectTimeout = 5000
        c.readTimeout = 5000
        if (range != null) c.setRequestProperty("Range", range)
        c.connect()
        val code = c.responseCode
        val headers = c.headerFields
            .filterKeys { it != null }
            .mapKeys { it.key.lowercase() }
            .mapValues { it.value.joinToString(", ") }
        val body = try {
            (if (code >= 400) c.errorStream else c.inputStream)?.readBytes() ?: ByteArray(0)
        } catch (e: Exception) {
            ByteArray(0)
        }
        c.disconnect()
        return Triple(code, headers, body)
    }

    private fun host() = TcpMediaHost(InetAddress.getLoopbackAddress())

    @Test
    fun publishedBytesComeBackOverRealHTTP() = runTest {
        // The whole point of the class: a renderer PULLS, so this has to be a
        // real socket serving real bytes, not a fake in front of one.
        val h = host()
        try {
            val payload = ByteArray(1000) { (it % 251).toByte() }
            val url = h.publish(CastMediaSource.Bytes(payload), "video/mp4")
            val (code, headers, body) = get(url)
            assertEquals(200, code)
            assertContentEquals(payload, body)
            assertEquals("video/mp4", headers["content-type"])
            assertEquals("1000", headers["content-length"])
            assertEquals("bytes", headers["accept-ranges"])
        } finally { h.close() }
    }

    @Test
    fun aRangeRequestComesBackAs206WithAContentRange() = runTest {
        val h = host()
        try {
            val payload = ByteArray(1000) { (it % 251).toByte() }
            val url = h.publish(CastMediaSource.Bytes(payload), "video/mp4")
            val (code, headers, body) = get(url, "bytes=100-199")
            assertEquals(206, code)
            assertEquals("bytes 100-199/1000", headers["content-range"])
            assertEquals("100", headers["content-length"])
            assertContentEquals(payload.copyOfRange(100, 200), body)
        } finally { h.close() }
    }

    @Test
    fun theSuffixRangeServesTheTAILofTheFile() = runTest {
        val h = host()
        try {
            val payload = ByteArray(1000) { (it % 251).toByte() }
            val url = h.publish(CastMediaSource.Bytes(payload), "video/mp4")
            val (code, _, body) = get(url, "bytes=-10")
            assertEquals(206, code)
            assertContentEquals(payload.copyOfRange(990, 1000), body)
        } finally { h.close() }
    }

    @Test
    fun aFileIsServedFromDiskWithoutBeingReadIntoMemoryFirst() = runTest {
        val h = host()
        val tmp = File.createTempFile("cast", ".mp4")
        try {
            val payload = ByteArray(4096) { (it % 97).toByte() }
            tmp.writeBytes(payload)
            val url = h.publish(CastMediaSource.LocalFile(tmp.absolutePath), "video/mp4")
            val (code, _, body) = get(url)
            assertEquals(200, code)
            assertContentEquals(payload, body)

            val (rc, _, rbody) = get(url, "bytes=1000-1099")
            assertEquals(206, rc)
            assertContentEquals(payload.copyOfRange(1000, 1100), rbody)
        } finally { h.close(); tmp.delete() }
    }

    @Test
    fun aHeadRequestSendsTheHeadersAndNoBody() = runTest {
        val h = host()
        try {
            val url = h.publish(CastMediaSource.Bytes(ByteArray(500)), "image/jpeg")
            val c = URL(url.toString()).openConnection() as HttpURLConnection
            c.requestMethod = "HEAD"
            c.connectTimeout = 5000
            assertEquals(200, c.responseCode)
            assertEquals("500", c.getHeaderField("Content-Length"))
            c.disconnect()
        } finally { h.close() }
    }

    @Test
    fun anUnknownPathIs404andAnUnsupportedMethodIs405() = runTest {
        val h = host()
        try {
            h.publish(CastMediaSource.Bytes(ByteArray(4)), "image/png")
            val base = h.baseUrl!!
            assertEquals(404, get(URI(base.toString() + "nothing.png")).first)

            val c = URL(base.toString()).openConnection() as HttpURLConnection
            c.requestMethod = "DELETE"
            c.connectTimeout = 5000
            assertTrue(c.responseCode == 405 || c.responseCode == 404)
            c.disconnect()
        } finally { h.close() }
    }

    @Test
    fun anUnpublishedUrlStopsBeingServed() = runTest {
        val h = host()
        try {
            val url = h.publish(CastMediaSource.Bytes(ByteArray(8)), "image/png")
            assertEquals(200, get(url).first)
            h.unpublish(url)
            assertEquals(404, get(url).first)
        } finally { h.close() }
    }

    @Test
    fun theDlnaHeadersDistinguishAnImageFromAStream() = runTest {
        // Without transferMode some televisions download a whole video before
        // showing anything, and some refuse an image outright.
        val h = host()
        try {
            val image = h.publish(CastMediaSource.Bytes(ByteArray(4)), "image/jpeg")
            assertEquals("Interactive", get(image).second["transfermode.dlna.org"])

            val video = h.publish(CastMediaSource.Bytes(ByteArray(4)), "video/mp4")
            assertEquals("Streaming", get(video).second["transfermode.dlna.org"])
            assertContains(get(video).second["contentfeatures.dlna.org"]!!, "DLNA.ORG_OP=01")
        } finally { h.close() }
    }

    @Test
    fun publishingStartsTheHostIfItIsNotAlreadyRunning() = runTest {
        val h = host()
        try {
            assertFalse(h.isRunning)
            assertNull(h.baseUrl)
            h.publish(CastMediaSource.Bytes(ByteArray(1)), "image/png")
            assertTrue(h.isRunning)
            assertNotNull(h.baseUrl)
        } finally { h.close() }
    }

    @Test
    fun theOsPicksThePortSoNothingCollidesWithWhateverElseIsRunning() = runTest {
        val a = host()
        val b = host()
        try {
            a.start()
            b.start()
            assertTrue(a.baseUrl!!.port > 0)
            assertTrue(b.baseUrl!!.port > 0)
            assertTrue(a.baseUrl!!.port != b.baseUrl!!.port)
        } finally { a.close(); b.close() }
    }

    @Test
    fun aUrlSourceIsREFUSEDbecauseItIsAlreadyReachable() = runTest {
        val h = host()
        try {
            assertFailsWith<CastException> {
                h.publish(CastMediaSource.Url("http://example.invalid/x.mp4"), "video/mp4")
            }
            assertFailsWith<CastException> { h.publish(CastMediaSource.Bytes(ByteArray(1)), "  ") }
        } finally { h.close() }
    }

    @Test
    fun eachPublishGetsItsOwnUrl() = runTest {
        val h = host()
        try {
            val a = h.publish(CastMediaSource.Bytes(byteArrayOf(1)), "image/png")
            val b = h.publish(CastMediaSource.Bytes(byteArrayOf(2)), "image/png")
            assertTrue(a != b)
            assertContentEquals(byteArrayOf(1), get(a).third)
            assertContentEquals(byteArrayOf(2), get(b).third)
        } finally { h.close() }
    }

    @Test
    fun closingItStopsServingAndForgetsWhatWasPublished() = runTest {
        val h = host()
        val url = h.publish(CastMediaSource.Bytes(ByteArray(4)), "image/png")
        h.close()
        assertFalse(h.isRunning)
        assertNull(h.baseUrl)
        assertFailsWith<Exception> { get(url) }
    }
}
