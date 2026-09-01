// CastSession.kt
//
// The UPnP control point, the DLNA target and session over it, the discovery
// that mints targets, the engine that wires a media host to each one, and the
// fail-closed nulls.

package com.bhengubv.circleai.cast

import java.net.InetAddress
import java.net.URI
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.flow

/** Posts one SOAP action to a renderer control URL. */
fun interface SoapTransport {
    /**
     * The response body. Implementations post [body] to [controlUrl] with the
     * SOAPACTION header [soapAction] and content type text/xml.
     */
    suspend fun post(controlUrl: String, soapAction: String, body: String): String
}

/**
 * Drives one renderer AVTransport service.
 *
 * The envelope, the SOAPACTION quoting and the clock formats live in
 * UpnpAvTransport and are tested without a network; this class is the thin
 * layer that puts them on the wire.
 */
class UpnpControlPoint(
    private val controlUrl: String,
    private val transport: SoapTransport,
) {
    private suspend fun invoke(action: String, body: String): String = transport.post(
        controlUrl,
        UpnpAvTransport.soapActionHeader(action),
        UpnpAvTransport.envelope(action, body),
    )

    suspend fun setAvTransportUri(mediaUrl: String, didlMetadata: String) {
        invoke("SetAVTransportURI", UpnpAvTransport.setAvTransportUriBody(mediaUrl, didlMetadata))
    }

    suspend fun play() { invoke("Play", UpnpAvTransport.PLAY_BODY) }

    suspend fun pause() { invoke("Pause", UpnpAvTransport.PAUSE_BODY) }

    suspend fun stop() { invoke("Stop", UpnpAvTransport.STOP_BODY) }

    suspend fun seek(positionSeconds: Double) {
        invoke("Seek", UpnpAvTransport.seekBody(positionSeconds))
    }

    suspend fun transportState(): String =
        UpnpAvTransport.transportState(invoke("GetTransportInfo", "<InstanceID>0</InstanceID>"))

    /** Position and duration, in seconds. */
    suspend fun position(): Pair<Double, Double> =
        UpnpAvTransport.positionInfo(invoke("GetPositionInfo", "<InstanceID>0</InstanceID>"))
}

/**
 * ICastTarget over a resolved UPnP MediaRenderer.
 *
 * The session is minted by a factory rather than constructed here, so the
 * target itself stays free of HTTP and media-host wiring and can be compared,
 * listed and shown on a screen without any of it.
 */
class DlnaCastTarget(
    val description: RendererDescription,
    private val sessionFactory: (DlnaCastTarget) -> ICastSession,
) : ICastTarget {
    override val id: CastTargetId get() = CastTargetId(description.udn)
    override val friendlyName: String get() = description.friendlyName
    override val manufacturer: String get() = description.manufacturer
    override val model: String get() = description.modelName
    override val protocol: CastProtocolKind get() = CastProtocolKind.DLNA
    override val location: URI get() = URI(description.location)
    override val iconUri: URI? get() = description.iconUrl?.let { URI(it) }

    override suspend fun connect(): ICastSession = sessionFactory(this)
}

/**
 * ICastSession over UPnP AVTransport.
 *
 * Byte and file media are published through the media host FIRST, because a
 * renderer pulls: it is handed a URL and fetches it. There is no push.
 */
class DlnaCastSession(
    override val target: ICastTarget,
    private val control: UpnpControlPoint,
    private val host: ILocalMediaHost?,
) : ICastSession {

    private val published = mutableListOf<URI>()
    private var currentUrl: URI? = null

    override suspend fun load(media: CastMedia) {
        val url = resolveUrl(media)
        val protocolInfo = DidlLite.protocolInfo(media.mimeType)
        val didl = DidlLite.forMedia(media, url.toString(), protocolInfo)
        control.setAvTransportUri(url.toString(), didl)
        currentUrl = url
    }

    override suspend fun play() = control.play()
    override suspend fun pause() = control.pause()
    override suspend fun stop() = control.stop()
    override suspend fun seek(positionSeconds: Double) = control.seek(positionSeconds)

    override suspend fun status(): CastStatus {
        val state = control.transportState()
        val (pos, dur) = control.position()
        return CastStatus(UpnpAvTransport.mapState(state), pos, dur, currentUrl?.toString())
    }

    /**
     * A slideshow is SetAVTransportURI in a loop. There is no DLNA slideshow
     * action; a deck is cast by handing the renderer one image after another.
     */
    override suspend fun showSlideShow(images: List<CastMedia>, perImageSeconds: Double) {
        // A non-positive interval would advance instantly and show nothing.
        val per = (CastDefaults.perImage(perImageSeconds) * 1000).toLong()

        for (image in images) {
            load(image)
            play()
            try {
                delay(per)
            } catch (e: CancellationException) {
                // Cancelling a slideshow stops it where it is; not an error.
                break
            }
        }
    }

    private suspend fun resolveUrl(media: CastMedia): URI {
        val source = media.source
        if (source is CastMediaSource.Url) return URI(source.address)

        val h = host ?: throw NoMediaHostException()
        val url = h.publish(source, media.mimeType)
        published.add(url)
        return url
    }

    /**
     * Un-publishes what this session put on the host, and leaves the HOST
     * ALONE: it is shared per bind address and owned by the engine. Closing it
     * here would take down every other session pointed at the same interface.
     */
    override suspend fun dispose() {
        val h = host
        if (h != null) {
            for (url in published) {
                // A renderer that has already gone off the network must not stop
                // the app tidying up after itself.
                try { h.unpublish(url) } catch (e: Exception) { }
            }
        }
        published.clear()
    }
}

/** Discovers renderers by SSDP and resolves each one description. */
class DlnaCastDiscovery(
    private val search: suspend (Long) -> List<SsdpResponse>,
    private val fetchDescription: suspend (String) -> String,
    private val hostForTarget: (ICastTarget) -> ILocalMediaHost?,
    private val transport: SoapTransport,
) : ICastDiscovery {

    override val backendId: String get() = "dlna"

    override fun discover(searchWindowMs: Long): Flow<ICastTarget> = flow {
        val seen = HashSet<String>()
        for (response in search(searchWindowMs)) {
            // The SAME renderer answers an M-SEARCH several times - that is the
            // protocol, not a fault. Emitting each answer would put a television
            // in the list four times.
            if (!seen.add(response.location)) continue

            val description = try {
                DeviceDescription.parse(fetchDescription(response.location), response.location)
            } catch (e: Exception) {
                // One unreachable or malformed device must not end the scan.
                null
            } ?: continue

            emit(
                DlnaCastTarget(description) { target ->
                    DlnaCastSession(
                        target,
                        UpnpControlPoint(description.avTransportControlUrl, transport),
                        hostForTarget(target),
                    )
                },
            )
        }
    }
}

/**
 * The one type most callers touch: find televisions, then fling something at
 * one. One media host per LAN bind address, created on first use and reused.
 */
class DlnaCastEngine(
    private val search: suspend (Long) -> List<SsdpResponse>,
    private val fetchDescription: suspend (String) -> String,
    private val transport: SoapTransport,
    private val makeHost: (InetAddress) -> ILocalMediaHost = { TcpMediaHost(it) },
    private val localAddresses: () -> List<String> = { emptyList() },
) : ICastEngine, AutoCloseable {

    private val hostsByBind = ConcurrentHashMap<String, ILocalMediaHost>()

    override val backendId: String get() = "dlna"

    private val discovery = DlnaCastDiscovery(search, fetchDescription, ::hostForTarget, transport)

    override fun discover(searchWindowMs: Long): Flow<ICastTarget> = discovery.discover(searchWindowMs)

    override suspend fun cast(target: ICastTarget, media: CastMedia): ICastSession {
        val session = target.connect()
        return try {
            session.load(media)
            session.play()
            session
        } catch (e: Throwable) {
            // A session that failed to start is disposed here rather than left
            // holding published bytes nobody will ever come back for.
            try { session.dispose() } catch (ignored: Exception) { }
            throw e
        }
    }

    internal fun hostForTarget(target: ICastTarget): ILocalMediaHost? {
        val bind = resolveBind(target)
        return hostsByBind.computeIfAbsent(bind.hostAddress) { makeHost(bind) }
    }

    private fun resolveBind(target: ICastTarget): InetAddress {
        val host = target.location.host ?: return InetAddress.getLoopbackAddress()
        // The address to bind is the one on the SAME network as the television.
        // Binding to the wrong interface produces a URL the renderer cannot
        // reach, and the symptom is a television that accepts the command and
        // then shows nothing.
        val candidates = localAddresses().filter { LocalAddress.isCastable(it) }
        val prefix = host.substringBeforeLast('.', "")
        val sameSubnet = candidates.firstOrNull { it.substringBeforeLast('.', "") == prefix }
        val chosen = sameSubnet ?: candidates.firstOrNull()
        return if (chosen != null) InetAddress.getByName(chosen) else InetAddress.getLoopbackAddress()
    }

    override fun close() {
        val hosts = hostsByBind.values.toList()
        hostsByBind.clear()
        for (h in hosts) {
            try { h.close() } catch (e: Exception) { }
        }
    }
}

// ------------------------------------------------------ Fail-closed

/** Finds nothing, and finds it without an error. */
class NullCastDiscovery : ICastDiscovery {
    override val backendId: String get() = "null"
    override fun discover(searchWindowMs: Long): Flow<ICastTarget> = emptyFlow()

    companion object { val instance = NullCastDiscovery() }
}

/**
 * REFUSES, and says what is missing.
 *
 * Rasterising a PDF or a deck needs a page renderer that is not pure managed
 * code. Returning an empty list would look like a document with no pages, which
 * is indistinguishable from success and is how somebody ends up casting a blank
 * screen to a room.
 */
class NullDocumentCastAdapter : IDocumentCastAdapter {
    override val backendId: String get() = "null"
    override suspend fun toCastable(document: CastDocument): List<CastMedia> = throw CastException(
        "Casting a document needs a page renderer wired through IDocumentCastAdapter. " +
            "Rasterising PDFs and decks is not pure managed code.",
    )

    companion object { val instance = NullDocumentCastAdapter() }
}

/** Accepts everything, does nothing, publishes nowhere. */
class NullLocalMediaHost : ILocalMediaHost {
    override val backendId: String get() = "null"
    override val isRunning: Boolean get() = false
    override val baseUrl: URI? get() = null
    override suspend fun start() {}
    override suspend fun publish(source: CastMediaSource, mimeType: String): URI = throw CastException(
        "No local media host is wired, so byte and file media cannot be reached by a renderer.",
    )
    override suspend fun unpublish(url: URI) {}
    override fun close() {}

    companion object { val instance = NullLocalMediaHost() }
}
