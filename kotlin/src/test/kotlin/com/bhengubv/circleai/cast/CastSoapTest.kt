package com.bhengubv.circleai.cast

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

/** The SOAP envelope and the clock formats. */
class CastSoapTest {

    private val Q = Char(34)
    private val url = "http://192.168.1.9:8200/media/1.mp4"

    @Test fun `the envelope names the action and the service`() {
        val e = UpnpAvTransport.envelope("Play", UpnpAvTransport.PLAY_BODY)
        assertTrue(e.contains("<u:Play xmlns:u="))
        assertTrue(e.contains("urn:schemas-upnp-org:service:AVTransport:1"))
        assertTrue(e.contains("</u:Play>"))
        assertTrue(e.contains("<Speed>1</Speed>"))
    }

    // The header value is QUOTED - renderers reject it bare.
    @Test fun `the soap action header is quoted`() {
        assertEquals(Q + "urn:schemas-upnp-org:service:AVTransport:1#Play" + Q,
            UpnpAvTransport.soapActionHeader("Play"))
    }

    // The metadata is XML inside an XML element, so it is escaped TWICE - once
    // by DIDL for the URL, once here. Miss this and the envelope is malformed.
    @Test fun `the metadata is escaped into the envelope`() {
        val body = UpnpAvTransport.setAvTransportUriBody(
            url, "<DIDL-Lite><item id=" + Q + "0" + Q + "/></DIDL-Lite>")
        assertTrue(body.contains("&lt;DIDL-Lite&gt;"))
        assertTrue(body.contains("&quot;0&quot;"))
        assertFalse(body.contains("<DIDL-Lite>"))
    }

    @Test fun `a url with a query string is escaped`() {
        val body = UpnpAvTransport.setAvTransportUriBody("http://10.0.0.5/v?a=1&b=2", "")
        assertTrue(body.contains("a=1&amp;b=2"))
    }

    @Test fun `seek targets are zero padded hours minutes seconds`() {
        assertEquals("00:00:00", UpnpAvTransport.formatClock(0.0))
        assertEquals("00:01:05", UpnpAvTransport.formatClock(65.0))
        assertEquals("01:01:01", UpnpAvTransport.formatClock(3661.0))
        assertEquals("10:00:00", UpnpAvTransport.formatClock(36000.0))
    }

    @Test fun `a negative seek clamps to zero rather than printing nonsense`() {
        assertEquals("00:00:00", UpnpAvTransport.formatClock(-5.0))
    }

    @Test fun `the seek body uses relative time`() {
        val b = UpnpAvTransport.seekBody(90.0)
        assertTrue(b.contains("<Unit>REL_TIME</Unit>"))
        assertTrue(b.contains("<Target>00:01:30</Target>"))
    }

    // Renderers send several shapes, and NOT_IMPLEMENTED is a real answer.
    @Test fun `clocks are parsed in every shape renderers send`() {
        assertEquals(65.0, UpnpAvTransport.parseClock("00:01:05"))
        assertEquals(3661.0, UpnpAvTransport.parseClock("1:01:01"))
        assertEquals(10.0, UpnpAvTransport.parseClock("00:00:10.500"))
        assertEquals(0.0, UpnpAvTransport.parseClock("NOT_IMPLEMENTED"))
        assertEquals(0.0, UpnpAvTransport.parseClock(null))
        assertEquals(0.0, UpnpAvTransport.parseClock("  "))
        assertEquals(0.0, UpnpAvTransport.parseClock("99:99:99"))
    }

    @Test fun `the transport state is read out of the soap reply`() {
        val xml = "<?xml version=" + Q + "1.0" + Q + "?><s:Envelope><s:Body>" +
            "<u:GetTransportInfoResponse>" +
            "<CurrentTransportState>PLAYING</CurrentTransportState>" +
            "<CurrentTransportStatus>OK</CurrentTransportStatus>" +
            "</u:GetTransportInfoResponse></s:Body></s:Envelope>"
        assertEquals("PLAYING", UpnpAvTransport.transportState(xml))
        assertEquals("UNKNOWN", UpnpAvTransport.transportState("<broken"))
    }

    @Test fun `position and duration are read together`() {
        val xml = "<?xml version=" + Q + "1.0" + Q + "?><s:Envelope><s:Body>" +
            "<u:GetPositionInfoResponse>" +
            "<TrackDuration>00:03:20</TrackDuration><RelTime>00:00:45</RelTime>" +
            "</u:GetPositionInfoResponse></s:Body></s:Envelope>"
        val pair = UpnpAvTransport.positionInfo(xml)
        assertEquals(45.0, pair.first)
        assertEquals(200.0, pair.second)
    }

    @Test fun `all the states renderers actually report`() {
        assertEquals(CastPlaybackState.PLAYING, UpnpAvTransport.mapState("PLAYING"))
        assertEquals(CastPlaybackState.PAUSED, UpnpAvTransport.mapState("PAUSED_PLAYBACK"))
        assertEquals(CastPlaybackState.PAUSED, UpnpAvTransport.mapState("PAUSED"))
        assertEquals(CastPlaybackState.STOPPED, UpnpAvTransport.mapState("STOPPED"))
        assertEquals(CastPlaybackState.BUFFERING, UpnpAvTransport.mapState("TRANSITIONING"))
        assertEquals(CastPlaybackState.IDLE, UpnpAvTransport.mapState("NO_MEDIA_PRESENT"))
        assertEquals(CastPlaybackState.PLAYING, UpnpAvTransport.mapState("playing"))
        assertEquals(CastPlaybackState.UNKNOWN, UpnpAvTransport.mapState("SOMETHING_NEW"))
    }
}
