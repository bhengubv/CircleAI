package com.bhengubv.circleai.cast

import java.net.URI
import kotlin.test.Test
import kotlin.test.assertContains
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.test.runTest

/** Records every SOAP action and hands back canned XML. */
private class FakeSoap(
    private val transportState: String = "PLAYING",
    private val relTime: String = "00:01:30",
    private val duration: String = "00:05:00",
) : SoapTransport {
    val actions = mutableListOf<String>()
    val bodies = mutableListOf<String>()
    val urls = mutableListOf<String>()
    var failOn: String? = null

    override suspend fun post(controlUrl: String, soapAction: String, body: String): String {
        urls.add(controlUrl)
        actions.add(soapAction)
        bodies.add(body)
        if (failOn != null && soapAction.contains(failOn!!)) throw CastException("renderer said no")
        return when {
            soapAction.contains("GetTransportInfo") ->
                "<r><CurrentTransportState>" + transportState + "</CurrentTransportState></r>"
            soapAction.contains("GetPositionInfo") ->
                "<r><RelTime>" + relTime + "</RelTime><TrackDuration>" + duration + "</TrackDuration></r>"
            else -> "<r/>"
        }
    }
}

private class FakeHost : ILocalMediaHost {
    val published = mutableListOf<URI>()
    val unpublished = mutableListOf<URI>()
    var closed = 0
    override val backendId = "fake"
    override val isRunning = true
    override val baseUrl: URI? = URI("http://192.168.1.5:9000/")
    override suspend fun start() {}
    override suspend fun publish(source: CastMediaSource, mimeType: String): URI {
        val u = URI("http://192.168.1.5:9000/" + published.size + ".bin")
        published.add(u)
        return u
    }
    override suspend fun unpublish(url: URI) { unpublished.add(url) }
    override fun close() { closed++ }
}

private fun description(
    udn: String = "uuid:tv-1",
    control: String = "http://192.168.1.10:8080/AVTransport/control",
) = RendererDescription(
    udn = udn,
    friendlyName = "Living room TV",
    manufacturer = "Acme",
    modelName = "A1",
    location = "http://192.168.1.10:8080/desc.xml",
    avTransportControlUrl = control,
    iconUrl = "http://192.168.1.10:8080/icon.png",
)

private fun session(soap: FakeSoap, host: ILocalMediaHost? = null): DlnaCastSession {
    val d = description()
    val target = DlnaCastTarget(d) { t ->
        DlnaCastSession(t, UpnpControlPoint(d.avTransportControlUrl, soap), host)
    }
    return DlnaCastSession(target, UpnpControlPoint(d.avTransportControlUrl, soap), host)
}

class UpnpControlPointTest {

    @Test
    fun everyActionCarriesAQUOTEDsoapActionHeader() = runTest {
        // Unquoted, a renderer answers 401 or simply ignores the request, and
        // nothing about the symptom points at a pair of quotation marks.
        val soap = FakeSoap()
        val c = UpnpControlPoint("http://tv/control", soap)
        c.play()
        c.pause()
        c.stop()
        assertTrue(soap.actions.all { it.startsWith(Char(34).toString()) && it.endsWith(Char(34).toString()) })
        assertContains(soap.actions[0], "AVTransport:1#Play")
    }

    @Test
    fun seekIsSentAsAClockNotAsANumberOfSeconds() = runTest {
        val soap = FakeSoap()
        UpnpControlPoint("http://tv/control", soap).seek(90.0)
        assertContains(soap.bodies[0], "00:01:30")
    }

    @Test
    fun theTransportStateIsReadOutOfTheResponse() = runTest {
        val soap = FakeSoap(transportState = "PAUSED_PLAYBACK")
        assertEquals("PAUSED_PLAYBACK", UpnpControlPoint("http://tv/control", soap).transportState())
    }

    @Test
    fun positionAndDurationComeBackInSeconds() = runTest {
        val soap = FakeSoap(relTime = "00:02:03", duration = "01:00:00")
        val (pos, dur) = UpnpControlPoint("http://tv/control", soap).position()
        assertEquals(123.0, pos)
        assertEquals(3600.0, dur)
    }

    @Test
    fun everyActionGoesToTheControlUrlFromTheDescription() = runTest {
        val soap = FakeSoap()
        UpnpControlPoint("http://192.168.1.10:8080/AVTransport/control", soap).play()
        assertEquals("http://192.168.1.10:8080/AVTransport/control", soap.urls[0])
    }
}

class DlnaCastSessionTest {

    @Test
    fun aUrlSourceNeedsNoMediaHostAtAll() = runTest {
        val soap = FakeSoap()
        val s = session(soap)
        s.load(CastMedia(CastMediaSource.Url("http://cdn/x.mp4"), "video/mp4", CastContentKind.VIDEO))
        assertContains(soap.bodies[0], "http://cdn/x.mp4")
    }

    @Test
    fun byteMediaIsPUBLISHEDfirstBecauseARendererPULLS() = runTest {
        // There is no push in DLNA. The renderer is handed a URL and fetches it,
        // so bytes have to become a URL before anything is sent.
        val soap = FakeSoap()
        val host = FakeHost()
        val s = session(soap, host)
        s.load(CastMedia(CastMediaSource.Bytes(ByteArray(10)), "image/png", CastContentKind.IMAGE))
        assertEquals(1, host.published.size)
        assertContains(soap.bodies[0], host.published[0].toString())
    }

    @Test
    fun byteMediaWithNOhostIsRefusedRatherThanSentAsNothing() = runTest {
        val s = session(FakeSoap(), null)
        assertFailsWith<NoMediaHostException> {
            s.load(CastMedia(CastMediaSource.Bytes(ByteArray(4)), "image/png", CastContentKind.IMAGE))
        }
    }

    @Test
    fun theDidlMetadataRidesAlongWithTheUrl() = runTest {
        val soap = FakeSoap()
        val s = session(soap)
        s.load(
            CastMedia(
                CastMediaSource.Url("http://cdn/x.mp4"), "video/mp4",
                CastContentKind.VIDEO, "Holiday clip",
            ),
        )
        // DIDL is escaped INTO the envelope, so the metadata appears twice-escaped.
        assertContains(soap.bodies[0], "DIDL-Lite")
        assertContains(soap.bodies[0], "Holiday clip")
    }

    @Test
    fun statusMapsTheRendererStateOntoOurs() = runTest {
        val s = session(FakeSoap(transportState = "TRANSITIONING"))
        assertEquals(CastPlaybackState.BUFFERING, s.status().state)

        val s2 = session(FakeSoap(transportState = "NO_MEDIA_PRESENT"))
        assertEquals(CastPlaybackState.IDLE, s2.status().state)

        val s3 = session(FakeSoap(transportState = "SOMETHING_NEW"))
        assertEquals(CastPlaybackState.UNKNOWN, s3.status().state)
    }

    @Test
    fun statusReportsWhatIsCurrentlyLoaded() = runTest {
        val soap = FakeSoap()
        val s = session(soap)
        assertNull(s.status().currentUri)
        s.load(CastMedia(CastMediaSource.Url("http://cdn/x.mp4"), "video/mp4", CastContentKind.VIDEO))
        assertEquals("http://cdn/x.mp4", s.status().currentUri)
    }

    @Test
    fun aSlideshowIsSetAvTransportUriInALOOP() = runTest {
        // There is no DLNA slideshow action; a deck is cast one image at a time.
        val soap = FakeSoap()
        val s = session(soap)
        val images = (1..3).map {
            CastMedia(CastMediaSource.Url("http://cdn/" + it + ".png"), "image/png", CastContentKind.IMAGE)
        }
        s.showSlideShow(images, 0.001)
        assertEquals(3, soap.actions.count { it.contains("SetAVTransportURI") })
        assertEquals(3, soap.actions.count { it.contains("#Play") })
    }

    @Test
    fun aNonPositiveIntervalFallsBackToTheDefaultRatherThanAdvancingInstantly() {
        assertEquals(CastDefaults.SLIDE_SHOW_PER_IMAGE_SECONDS, CastDefaults.perImage(0.0))
        assertEquals(CastDefaults.SLIDE_SHOW_PER_IMAGE_SECONDS, CastDefaults.perImage(-4.0))
        assertEquals(2.0, CastDefaults.perImage(2.0))
    }

    @Test
    fun disposeUnpublishesWhatItPublishedAndLEAVESTHEHOSTalone() = runTest {
        // The host is shared per bind address and owned by the engine. Closing
        // it here would take down every other session on the same interface.
        val host = FakeHost()
        val s = session(FakeSoap(), host)
        s.load(CastMedia(CastMediaSource.Bytes(ByteArray(4)), "image/png", CastContentKind.IMAGE))
        s.load(CastMedia(CastMediaSource.Bytes(ByteArray(4)), "image/png", CastContentKind.IMAGE))
        s.dispose()
        assertEquals(2, host.unpublished.size)
        assertEquals(0, host.closed, "the shared media host was closed by a session")
    }

    @Test
    fun disposeDoesNotUnpublishAUrlItNeverPublished() = runTest {
        val host = FakeHost()
        val s = session(FakeSoap(), host)
        s.load(CastMedia(CastMediaSource.Url("http://cdn/x.mp4"), "video/mp4", CastContentKind.VIDEO))
        s.dispose()
        assertTrue(host.unpublished.isEmpty())
    }
}

class DlnaCastTargetTest {

    @Test
    fun theTargetReadsEverythingOffItsDescription() {
        val d = description()
        val t = DlnaCastTarget(d) { DlnaCastSession(it, UpnpControlPoint(d.avTransportControlUrl, FakeSoap()), null) }
        assertEquals(CastTargetId("uuid:tv-1"), t.id)
        assertEquals("Living room TV", t.friendlyName)
        assertEquals("Acme", t.manufacturer)
        assertEquals("A1", t.model)
        assertEquals(CastProtocolKind.DLNA, t.protocol)
        assertEquals(URI("http://192.168.1.10:8080/desc.xml"), t.location)
        assertEquals(URI("http://192.168.1.10:8080/icon.png"), t.iconUri)
    }

    @Test
    fun aRendererWithNoIconIsStillAValidTarget() {
        val d = description().copy(iconUrl = null)
        val t = DlnaCastTarget(d) { DlnaCastSession(it, UpnpControlPoint(d.avTransportControlUrl, FakeSoap()), null) }
        assertNull(t.iconUri)
    }
}

class DlnaCastDiscoveryTest {

    private val descXml = """
        <root xmlns="urn:schemas-upnp-org:device-1-0">
          <device>
            <UDN>uuid:tv-1</UDN>
            <friendlyName>Living room TV</friendlyName>
            <manufacturer>Acme</manufacturer>
            <modelName>A1</modelName>
            <serviceList>
              <service>
                <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
                <controlURL>/AVTransport/control</controlURL>
              </service>
            </serviceList>
          </device>
        </root>
    """.trimIndent()

    private fun discovery(
        responses: List<SsdpResponse>,
        xmlFor: (String) -> String = { descXml },
    ) = DlnaCastDiscovery(
        search = { responses },
        fetchDescription = { xmlFor(it) },
        hostForTarget = { null },
        transport = FakeSoap(),
    )

    private fun response(location: String) =
        SsdpResponse(location, "urn:schemas-upnp-org:device:MediaRenderer:1", "uuid:tv-1::x")

    @Test
    fun oneRendererBecomesOneTarget() = runTest {
        val found = discovery(listOf(response("http://192.168.1.10:8080/desc.xml"))).discover(2000).toList()
        assertEquals(1, found.size)
        assertEquals("Living room TV", found[0].friendlyName)
    }

    @Test
    fun theSAMErendererAnsweringSeveralTimesIsListedONCE() = runTest {
        // Answering an M-SEARCH repeatedly is the protocol, not a fault. Emitting
        // each answer puts one television in the list four times.
        val loc = "http://192.168.1.10:8080/desc.xml"
        val found = discovery(List(4) { response(loc) }).discover(2000).toList()
        assertEquals(1, found.size)
    }

    @Test
    fun oneUnreachableDeviceDoesNotEndTheSCAN() = runTest {
        // A television that is turned off mid-scan must not hide the ones that
        // are on.
        val bad = "http://192.168.1.99:8080/desc.xml"
        val good = "http://192.168.1.10:8080/desc.xml"
        val d = DlnaCastDiscovery(
            search = { listOf(response(bad), response(good)) },
            fetchDescription = { if (it == bad) throw CastException("unreachable") else descXml },
            hostForTarget = { null },
            transport = FakeSoap(),
        )
        val found = d.discover(2000).toList()
        assertEquals(1, found.size)
        assertEquals("Living room TV", found[0].friendlyName)
    }

    @Test
    fun aDeviceWithNoAvTransportIsNotACastTarget() = runTest {
        // It cannot be controlled, so listing it would offer somebody a
        // television that does nothing when they pick it.
        val noTransport = "<root><device><UDN>uuid:x</UDN><friendlyName>Printer</friendlyName></device></root>"
        val found = discovery(listOf(response("http://192.168.1.10/desc.xml")) ) { noTransport }
            .discover(2000).toList()
        assertTrue(found.isEmpty())
    }

    @Test
    fun findingNothingIsAnEmptyFlowNotAnError() = runTest {
        assertTrue(discovery(emptyList()).discover(2000).toList().isEmpty())
    }
}

class DlnaCastEngineTest {

    private val descXml = """
        <root><device>
          <UDN>uuid:tv-1</UDN><friendlyName>TV</friendlyName>
          <serviceList><service>
            <serviceType>urn:schemas-upnp-org:service:AVTransport:1</serviceType>
            <controlURL>/ctl</controlURL>
          </service></serviceList>
        </device></root>
    """.trimIndent()

    private fun engine(soap: FakeSoap, hosts: MutableList<FakeHost> = mutableListOf()) = DlnaCastEngine(
        search = {
            listOf(
                SsdpResponse(
                    "http://192.168.1.10:8080/desc.xml",
                    "urn:schemas-upnp-org:device:MediaRenderer:1",
                    "uuid:tv-1::x",
                ),
            )
        },
        fetchDescription = { descXml },
        transport = soap,
        makeHost = { FakeHost().also { hosts.add(it) } },
        localAddresses = { listOf("192.168.1.5") },
    )

    @Test
    fun castingLoadsThenPlaysInThatOrder() = runTest {
        val soap = FakeSoap()
        engine(soap).use { e ->
            val target = e.discover(2000).toList().single()
            e.cast(target, CastMedia(CastMediaSource.Url("http://cdn/x.mp4"), "video/mp4", CastContentKind.VIDEO))
            assertContains(soap.actions[0], "SetAVTransportURI")
            assertContains(soap.actions[1], "#Play")
        }
    }

    @Test
    fun aSessionThatFailsToSTARTisDisposedRatherThanLeaked() = runTest {
        // Otherwise it sits there holding published bytes nobody comes back for.
        val soap = FakeSoap()
        soap.failOn = "#Play"
        val hosts = mutableListOf<FakeHost>()
        engine(soap, hosts).use { e ->
            val target = e.discover(2000).toList().single()
            assertFailsWith<CastException> {
                e.cast(target, CastMedia(CastMediaSource.Bytes(ByteArray(4)), "image/png", CastContentKind.IMAGE))
            }
            assertEquals(1, hosts.size)
            assertEquals(1, hosts[0].published.size)
            assertEquals(1, hosts[0].unpublished.size, "the failed session left its bytes published")
        }
    }

    @Test
    fun oneMediaHostPerBindAddressIsCreatedOnceAndREUSED() = runTest {
        val hosts = mutableListOf<FakeHost>()
        engine(FakeSoap(), hosts).use { e ->
            val target = e.discover(2000).toList().single()
            e.hostForTarget(target)
            e.hostForTarget(target)
            e.hostForTarget(target)
            assertEquals(1, hosts.size, "a host was created per call instead of per address")
        }
    }

    @Test
    fun closingTheEngineClosesTheHostsItOwns() = runTest {
        val hosts = mutableListOf<FakeHost>()
        val e = engine(FakeSoap(), hosts)
        val target = e.discover(2000).toList().single()
        e.hostForTarget(target)
        e.close()
        assertEquals(1, hosts[0].closed)
    }
}

class CastNullsTest {

    @Test
    fun theNullDiscoveryFindsNothingWithoutAnError() = runTest {
        assertTrue(NullCastDiscovery.instance.discover(2000).toList().isEmpty())
        assertEquals("null", NullCastDiscovery.instance.backendId)
    }

    @Test
    fun theNullDocumentAdapterREFUSESratherThanReturningAnEmptyDeck() = runTest {
        // An empty list looks like a document with no pages, which is
        // indistinguishable from success and is how somebody ends up casting a
        // blank screen to a room full of people.
        val e = assertFailsWith<CastException> {
            NullDocumentCastAdapter.instance.toCastable(
                CastDocument("Deck", CastMediaSource.LocalFile("/tmp/x.pdf"), "application/pdf"),
            )
        }
        assertContains(e.message!!, "page renderer")
    }

    @Test
    fun theNullMediaHostRefusesToPublishAndSaysWhy() = runTest {
        val h = NullLocalMediaHost.instance
        assertFalse(h.isRunning)
        assertNull(h.baseUrl)
        h.start()
        val e = assertFailsWith<CastException> { h.publish(CastMediaSource.Bytes(ByteArray(1)), "image/png") }
        assertContains(e.message!!, "cannot be reached by a renderer")
        h.unpublish(URI("http://x/y"))
        h.close()
    }
}
