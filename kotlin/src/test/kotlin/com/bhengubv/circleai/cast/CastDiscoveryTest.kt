package com.bhengubv.circleai.cast

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/** SSDP and the device description. */
class CastDiscoveryTest {

    private val CRLF = "" + Char(13) + Char(10)
    private val Q = Char(34)
    private val location = "http://192.168.1.50:8080/dev/description.xml"

    @Test fun `the search request carries the headers renderers demand`() {
        val r = SsdpClient.searchRequest(SsdpClient.MEDIA_RENDERER_TARGET, 3.0)
        assertTrue(r.startsWith("M-SEARCH * HTTP/1.1" + CRLF))
        assertTrue(r.contains("HOST: 239.255.255.250:1900" + CRLF))
        assertTrue(r.contains("MX: 3" + CRLF))
        assertTrue(r.contains("ST: urn:schemas-upnp-org:device:MediaRenderer:1" + CRLF))
        assertTrue(r.endsWith(CRLF + CRLF))
    }

    // MAN must be QUOTED. Renderers that see it unquoted simply do not answer.
    @Test fun `the man header is quoted`() {
        assertTrue(SsdpClient.searchRequest("x", 3.0)
            .contains("MAN: " + Q + "ssdp:discover" + Q + CRLF))
    }

    // MX outside 1..5 is out of spec and gets clamped, not passed through.
    @Test fun `mx is clamped into the legal range`() {
        assertTrue(SsdpClient.searchRequest("x", 0.0).contains("MX: 1" + CRLF))
        assertTrue(SsdpClient.searchRequest("x", 60.0).contains("MX: 5" + CRLF))
        assertTrue(SsdpClient.searchRequest("x", 2.9).contains("MX: 2" + CRLF))
    }

    @Test fun `a well formed response is parsed`() {
        val raw = "HTTP/1.1 200 OK" + CRLF +
            "CACHE-CONTROL: max-age=1800" + CRLF +
            "LOCATION: http://192.168.1.50:8080/description.xml" + CRLF +
            "ST: urn:schemas-upnp-org:device:MediaRenderer:1" + CRLF +
            "USN: uuid:abc-123::urn:schemas-upnp-org:device:MediaRenderer:1" + CRLF + CRLF
        val r = SsdpClient.parseResponse(raw)!!
        assertEquals("http://192.168.1.50:8080/description.xml", r.location)
        assertEquals("urn:schemas-upnp-org:device:MediaRenderer:1", r.searchTarget)
        assertTrue(r.uniqueServiceName.contains("uuid:abc-123"))
    }

    // Devices disagree about capitalisation, so header matching is folded.
    @Test fun `header names are case insensitive`() {
        val raw = "HTTP/1.1 200 OK" + CRLF + "location: http://10.0.0.5/d.xml" + CRLF +
            "st: x" + CRLF + "Usn: y" + CRLF + CRLF
        val r = SsdpClient.parseResponse(raw)!!
        assertEquals("http://10.0.0.5/d.xml", r.location)
        assertEquals("x", r.searchTarget)
    }

    @Test fun `a notify or garbage is not a response`() {
        assertNull(SsdpClient.parseResponse("NOTIFY * HTTP/1.1" + CRLF + "LOCATION: http://x/d.xml" + CRLF))
        assertNull(SsdpClient.parseResponse("not http at all"))
        assertNull(SsdpClient.parseResponse(""))
    }

    // No LOCATION means nothing to fetch, so there is no device here.
    @Test fun `a response without a location is discarded`() {
        assertNull(SsdpClient.parseResponse(
            "HTTP/1.1 200 OK" + CRLF + "ST: x" + CRLF + "USN: y" + CRLF + CRLF))
    }

    @Test fun `missing st and usn become empty rather than failing`() {
        val r = SsdpClient.parseResponse(
            "HTTP/1.1 200 OK" + CRLF + "LOCATION: http://10.0.0.5/d.xml" + CRLF + CRLF)!!
        assertEquals("", r.searchTarget)
        assertEquals("", r.uniqueServiceName)
    }

    private fun doc(
        service: String = "urn:schemas-upnp-org:service:AVTransport:1",
        control: String = "/AVTransport/control",
        urlBase: String? = null,
        icon: String? = "/icon/sm.png",
    ): String {
        val base = urlBase?.let { "<URLBase>" + it + "</URLBase>" } ?: ""
        val iconXml = icon?.let { "<iconList><icon><url>" + it + "</url></icon></iconList>" } ?: ""
        return "<?xml version=" + Q + "1.0" + Q + "?><root><device>" +
            base +
            "<friendlyName>Lounge TV</friendlyName>" +
            "<manufacturer>Acme</manufacturer>" +
            "<modelName>SmartBox 400</modelName>" +
            "<UDN>uuid:abc-123</UDN>" + iconXml +
            "<serviceList>" +
            "<service><serviceType>urn:schemas-upnp-org:service:ConnectionManager:1</serviceType>" +
            "<controlURL>/CM/control</controlURL></service>" +
            "<service><serviceType>" + service + "</serviceType>" +
            "<controlURL>" + control + "</controlURL></service>" +
            "</serviceList></device></root>"
    }

    @Test fun `a full description is read`() {
        val d = DeviceDescription.parse(doc(), location)!!
        assertEquals("Lounge TV", d.friendlyName)
        assertEquals("Acme", d.manufacturer)
        assertEquals("SmartBox 400", d.modelName)
        assertEquals("uuid:abc-123", d.udn)
    }

    // The AVTransport service must be picked, not the first service listed.
    @Test fun `the av transport control url is the one chosen`() {
        assertEquals("http://192.168.1.50:8080/AVTransport/control",
            DeviceDescription.parse(doc(), location)!!.avTransportControlUrl)
    }

    // A renderer with no AVTransport cannot be controlled, so it is not a target.
    @Test fun `a device without av transport is not a target`() {
        assertNull(DeviceDescription.parse(
            doc(service = "urn:schemas-upnp-org:service:RenderingControl:1"), location))
    }

    @Test fun `an empty control url is refused`() {
        assertNull(DeviceDescription.parse(doc(control = "   "), location))
    }

    @Test fun `url base wins over the description location`() {
        assertEquals("http://10.0.0.9:2020/AVTransport/control",
            DeviceDescription.parse(doc(urlBase = "http://10.0.0.9:2020/"), location)!!
                .avTransportControlUrl)
    }

    // A relative control path resolves against the DIRECTORY of the description.
    @Test fun `a relative control path resolves against the description directory`() {
        assertEquals("http://192.168.1.50:8080/dev/control",
            DeviceDescription.parse(doc(control = "control"), location)!!.avTransportControlUrl)
    }

    @Test fun `an absolute control url is used as is`() {
        assertEquals("http://1.2.3.4/x",
            DeviceDescription.parse(doc(control = "http://1.2.3.4/x"), location)!!
                .avTransportControlUrl)
    }

    @Test fun `the icon is resolved when present and null when not`() {
        assertEquals("http://192.168.1.50:8080/icon/sm.png",
            DeviceDescription.parse(doc(), location)!!.iconUrl)
        assertNull(DeviceDescription.parse(doc(icon = null), location)!!.iconUrl)
    }

    // Broken XML is a device to SKIP, not a crash.
    @Test fun `malformed xml is null`() {
        assertNull(DeviceDescription.parse("<root><device>", location))
        assertNull(DeviceDescription.parse("", location))
    }
}
