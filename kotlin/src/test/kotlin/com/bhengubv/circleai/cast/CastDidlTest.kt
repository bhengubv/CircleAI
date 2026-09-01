package com.bhengubv.circleai.cast

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** DIDL-Lite metadata and the address rules. */
class CastDidlTest {

    private val Q = Char(34)
    private val url = "http://192.168.1.9:8200/media/1.mp4"

    @Test fun `each content kind gets its upnp class`() {
        fun cls(k: CastContentKind) = DidlLite.forMedia(
            CastMedia(CastMediaSource.Url(url), "x", k), url, "p")
        assertTrue(cls(CastContentKind.VIDEO).contains("object.item.videoItem"))
        assertTrue(cls(CastContentKind.AUDIO).contains("object.item.audioItem.musicTrack"))
        assertTrue(cls(CastContentKind.IMAGE).contains("object.item.imageItem.photo"))
        assertTrue(cls(CastContentKind.SLIDE_SHOW).contains("object.item.imageItem.photo"))
    }

    @Test fun `protocol info is the http-get form`() {
        assertEquals("http-get:*:video/mp4:*", DidlLite.protocolInfo("video/mp4"))
    }

    // A track title with an ampersand must not break the document.
    @Test fun `a title with markup in it is escaped`() {
        val m = CastMedia.video(CastMediaSource.Url(url), title = "Tom & Jerry <1955>")
        val didl = DidlLite.forMedia(m, url, DidlLite.protocolInfo("video/mp4"))
        assertTrue(didl.contains("<dc:title>Tom &amp; Jerry &lt;1955&gt;</dc:title>"))
    }

    @Test fun `an untitled item still gets a name`() {
        val didl = DidlLite.forMedia(CastMedia.video(CastMediaSource.Url(url)), url, "p")
        assertTrue(didl.contains("<dc:title>CircleAI</dc:title>"))
    }

    @Test fun `the resource element carries the url and protocol info`() {
        val didl = DidlLite.forMedia(
            CastMedia.video(CastMediaSource.Url(url)), url, DidlLite.protocolInfo("video/mp4"))
        assertTrue(didl.contains("protocolInfo=" + Q + "http-get:*:video/mp4:*" + Q))
        assertTrue(didl.contains(">" + url + "</res>"))
    }

    @Test fun `the three private ranges are recognised`() {
        assertTrue(LocalAddress.isPrivateV4(listOf(10, 0, 0, 1)))
        assertTrue(LocalAddress.isPrivateV4(listOf(172, 16, 0, 1)))
        assertTrue(LocalAddress.isPrivateV4(listOf(172, 31, 255, 254)))
        assertTrue(LocalAddress.isPrivateV4(listOf(192, 168, 1, 50)))
    }

    // 172.15 and 172.32 are OUTSIDE the /12 - the classic off-by-one here.
    @Test fun `the edges of the one seventy two range are right`() {
        assertFalse(LocalAddress.isPrivateV4(listOf(172, 15, 0, 1)))
        assertFalse(LocalAddress.isPrivateV4(listOf(172, 32, 0, 1)))
    }

    @Test fun `public addresses are not private`() {
        assertFalse(LocalAddress.isPrivateV4(listOf(8, 8, 8, 8)))
        assertFalse(LocalAddress.isPrivateV4(listOf(203, 0, 113, 5)))
    }

    // APIPA means DHCP never answered; nothing on the LAN can reach it.
    @Test fun `link local and loopback are identified`() {
        assertTrue(LocalAddress.isLinkLocalV4(listOf(169, 254, 1, 1)))
        assertFalse(LocalAddress.isLinkLocalV4(listOf(169, 253, 1, 1)))
        assertTrue(LocalAddress.isLoopbackV4(listOf(127, 0, 0, 1)))
        assertFalse(LocalAddress.isLoopbackV4(listOf(10, 0, 0, 1)))
    }

    // The two that look fine on the phone and are unreachable from the TV.
    @Test fun `only a routable lan address is castable`() {
        assertTrue(LocalAddress.isCastable("192.168.1.50"))
        assertTrue(LocalAddress.isCastable("10.1.2.3"))
        assertFalse(LocalAddress.isCastable("127.0.0.1"))
        assertFalse(LocalAddress.isCastable("169.254.7.7"))
        assertFalse(LocalAddress.isCastable("8.8.8.8"))
        assertFalse(LocalAddress.isCastable("not-an-address"))
        assertFalse(LocalAddress.isCastable("999.1.1.1"))
    }

    @Test fun `a non positive slide interval falls back rather than refusing`() {
        assertEquals(8.0, CastDefaults.perImage(0.0))
        assertEquals(8.0, CastDefaults.perImage(-3.0))
        assertEquals(2.0, CastDefaults.perImage(2.0))
    }

    @Test fun `the helpers build the kind they name`() {
        val src = CastMediaSource.Url(url)
        assertEquals(CastContentKind.VIDEO, CastMedia.video(src).kind)
        assertEquals("video/mp4", CastMedia.video(src).mimeType)
        assertEquals("audio/mpeg", CastMedia.audio(src).mimeType)
        assertEquals("image/jpeg", CastMedia.image(src).mimeType)
        assertEquals(CastContentKind.IMAGE, CastMedia.image(src).kind)
    }
}
