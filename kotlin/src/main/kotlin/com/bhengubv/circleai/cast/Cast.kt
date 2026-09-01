// Cast.kt
//
// Kotlin port of CircleAI.Cast — the C# reference is the EXACT spec.
//
// Throwing what is on the phone onto the television: DLNA / UPnP discovery,
// control and metadata.
//
// SCOPE: the deterministic half ports in full - SSDP request building and
// response parsing, device-description XML, the SOAP envelope, DIDL-Lite, the
// clock formats and the transport-state map. Sockets, HTTP and the TCP media
// host stay behind interfaces, the same shape the C# uses.

package com.bhengubv.circleai.cast

import javax.xml.parsers.DocumentBuilderFactory
import org.w3c.dom.Element
import org.w3c.dom.Node

/** XML escaping for the five predefined entities, matching the C# wire form. */
internal object XmlText {
    fun escape(s: String): String {
        val out = StringBuilder(s.length)
        for (c in s) {
            when (c.code) {
                38 -> out.append("&amp;")
                60 -> out.append("&lt;")
                62 -> out.append("&gt;")
                34 -> out.append("&quot;")
                39 -> out.append("&apos;")
                else -> out.append(c)
            }
        }
        return out.toString()
    }
}

enum class CastProtocolKind { DLNA }

enum class CastContentKind { IMAGE, AUDIO, VIDEO, SLIDE_SHOW }

enum class CastPlaybackState { UNKNOWN, IDLE, BUFFERING, PLAYING, PAUSED, STOPPED, ERROR }

data class CastTargetId(val value: String) {
    override fun toString() = value
}

/**
 * Where the media is. A URL the renderer can already reach, or something local
 * that has to be published over the LAN before a television can pull it.
 */
sealed interface CastMediaSource {
    data class Url(val address: String) : CastMediaSource
    data class LocalFile(val path: String) : CastMediaSource
    data class Bytes(val data: ByteArray) : CastMediaSource {
        override fun equals(other: Any?) =
            this === other || (other is Bytes && data.contentEquals(other.data))
        override fun hashCode() = data.contentHashCode()
    }
}

data class CastMedia(
    val source: CastMediaSource,
    val mimeType: String,
    val kind: CastContentKind,
    val title: String = "",
    val durationSeconds: Double? = null,
) {
    companion object {
        fun video(src: CastMediaSource, mime: String = "video/mp4", title: String = "",
                  durationSeconds: Double? = null) =
            CastMedia(src, mime, CastContentKind.VIDEO, title, durationSeconds)

        fun image(src: CastMediaSource, mime: String = "image/jpeg", title: String = "") =
            CastMedia(src, mime, CastContentKind.IMAGE, title)

        fun audio(src: CastMediaSource, mime: String = "audio/mpeg", title: String = "",
                  durationSeconds: Double? = null) =
            CastMedia(src, mime, CastContentKind.AUDIO, title, durationSeconds)
    }
}

data class CastStatus(
    val state: CastPlaybackState,
    val positionSeconds: Double,
    val durationSeconds: Double,
    val currentUri: String?,
)

class CastControlException(message: String) : CastException(message)

class NoMediaHostException : CastException(
    "Byte/file media requires a local media host so the renderer can pull it over the LAN. " +
        "Construct the session with a host."
)

// ── SSDP ────────────────────────────────────────────────────────────────────

data class SsdpResponse(val location: String, val searchTarget: String, val uniqueServiceName: String)

object SsdpClient {
    const val MULTICAST_ADDRESS = "239.255.255.250"
    const val PORT = 1900
    const val MEDIA_RENDERER_TARGET = "urn:schemas-upnp-org:device:MediaRenderer:1"

    private val CRLF = "" + Char(13) + Char(10)
    private val QUOTE = Char(34)

    /**
     * The M-SEARCH datagram. CRLF line endings and the QUOTED MAN header are
     * not style - renderers that see anything else simply do not answer.
     */
    fun searchRequest(target: String, windowSeconds: Double): String {
        val mx = maxOf(1, minOf(5, windowSeconds.toInt()))
        return "M-SEARCH * HTTP/1.1" + CRLF +
            "HOST: " + MULTICAST_ADDRESS + ":" + PORT + CRLF +
            "MAN: " + QUOTE + "ssdp:discover" + QUOTE + CRLF +
            "MX: " + mx + CRLF +
            "ST: " + target + CRLF +
            CRLF
    }

    /**
     * Parses one datagram. Header names are case-insensitive on the wire and
     * devices disagree about capitalisation, so matching is folded.
     */
    fun parseResponse(text: String): SsdpResponse? {
        if (!text.lowercase().startsWith("http/1.1")) return null

        var location: String? = null
        var st: String? = null
        var usn: String? = null

        for (raw in text.split(CRLF)) {
            val colon = raw.indexOf(Char(58))
            if (colon <= 0) continue
            val key = raw.substring(0, colon).trim().uppercase()
            val value = raw.substring(colon + 1).trim()
            when (key) {
                "LOCATION" -> location = value
                "ST" -> st = value
                "USN" -> usn = value
            }
        }

        if (location.isNullOrEmpty()) return null
        if (!location.contains("://")) return null
        return SsdpResponse(location, st ?: "", usn ?: "")
    }
}

// ── Device description ──────────────────────────────────────────────────────

data class RendererDescription(
    val udn: String,
    val friendlyName: String,
    val manufacturer: String,
    val modelName: String,
    val location: String,
    val avTransportControlUrl: String,
    val iconUrl: String?,
)

object DeviceDescription {

    /**
     * A renderer WITHOUT an AVTransport service cannot be controlled, so it is
     * not a cast target and this returns null rather than a half-usable one.
     */
    fun parse(xml: String, location: String): RendererDescription? {
        val doc = try {
            val f = DocumentBuilderFactory.newInstance()
            f.isNamespaceAware = false      // match on LOCAL name, like the C#
            f.newDocumentBuilder().parse(xml.byteInputStream())
        } catch (_: Exception) {
            return null
        }

        fun firstValue(tag: String): String {
            val nodes = doc.getElementsByTagName(tag)
            return if (nodes.length == 0) "" else nodes.item(0).textContent.trim()
        }

        // URLBase, when present, wins over the description URL.
        var baseUrl = location
        val urlBase = firstValue("URLBase")
        if (urlBase.isNotEmpty() && urlBase.contains("://")) baseUrl = urlBase

        val services = doc.getElementsByTagName("service")
        var controlPath: String? = null
        for (i in 0 until services.length) {
            val el = services.item(i) as? Element ?: continue
            val types = el.getElementsByTagName("serviceType")
            val type = if (types.length == 0) "" else types.item(0).textContent
            if (!type.contains("AVTransport", ignoreCase = true)) continue
            val controls = el.getElementsByTagName("controlURL")
            if (controls.length > 0) controlPath = controls.item(0).textContent.trim()
            break
        }
        if (controlPath.isNullOrEmpty()) return null

        val controlUrl = resolve(controlPath, baseUrl) ?: return null

        var iconUrl: String? = null
        val icons = doc.getElementsByTagName("icon")
        if (icons.length > 0) {
            val el = icons.item(0) as? Element
            val urls = el?.getElementsByTagName("url")
            if (urls != null && urls.length > 0) {
                val p = urls.item(0).textContent.trim()
                if (p.isNotEmpty()) iconUrl = resolve(p, baseUrl)
            }
        }

        val udn = firstValue("UDN")
        val friendly = firstValue("friendlyName")

        return RendererDescription(
            udn = if (udn.isEmpty()) location else udn,
            friendlyName = if (friendly.isEmpty()) "DLNA Renderer" else friendly,
            manufacturer = firstValue("manufacturer"),
            modelName = firstValue("modelName"),
            location = location,
            avTransportControlUrl = controlUrl,
            iconUrl = iconUrl,
        )
    }

    /**
     * An ABSOLUTE path resolves against the origin; a RELATIVE one against the
     * directory - which is what a browser does and what renderers expect.
     */
    internal fun resolve(path: String, base: String): String? {
        if (path.contains("://")) return path
        return try {
            java.net.URI(base).resolve(path).toString()
        } catch (_: Exception) {
            null
        }
    }
}

// ── AVTransport (SOAP) ──────────────────────────────────────────────────────

object UpnpAvTransport {
    const val SERVICE_TYPE = "urn:schemas-upnp-org:service:AVTransport:1"
    private val Q = Char(34)

    /** The full SOAP envelope for one action. */
    fun envelope(action: String, innerXml: String): String =
        "<?xml version=" + Q + "1.0" + Q + " encoding=" + Q + "utf-8" + Q + "?>" +
            "<s:Envelope xmlns:s=" + Q + "http://schemas.xmlsoap.org/soap/envelope/" + Q + " " +
            "s:encodingStyle=" + Q + "http://schemas.xmlsoap.org/soap/encoding/" + Q + ">" +
            "<s:Body>" +
            "<u:" + action + " xmlns:u=" + Q + SERVICE_TYPE + Q + ">" + innerXml + "</u:" + action + ">" +
            "</s:Body></s:Envelope>"

    /** The SOAPACTION header value, QUOTES INCLUDED - renderers reject it bare. */
    fun soapActionHeader(action: String): String = Q + SERVICE_TYPE + "#" + action + Q

    fun setAvTransportUriBody(mediaUrl: String, didlMetadata: String): String =
        "<InstanceID>0</InstanceID>" +
            "<CurrentURI>" + XmlText.escape(mediaUrl) + "</CurrentURI>" +
            "<CurrentURIMetaData>" + XmlText.escape(didlMetadata) + "</CurrentURIMetaData>"

    const val PLAY_BODY = "<InstanceID>0</InstanceID><Speed>1</Speed>"
    const val PAUSE_BODY = "<InstanceID>0</InstanceID>"
    const val STOP_BODY = "<InstanceID>0</InstanceID>"

    fun seekBody(positionSeconds: Double): String =
        "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>" +
            formatClock(positionSeconds) + "</Target>"

    /** hh:mm:ss, zero padded, no fraction - the only form renderers accept. */
    fun formatClock(seconds: Double): String {
        val total = maxOf(0, seconds.toInt())
        return String.format("%02d:%02d:%02d", total / 3600, (total % 3600) / 60, total % 60)
    }

    /**
     * Renderers send h:mm:ss, hh:mm:ss, sometimes hh:mm:ss.mmm, and sometimes
     * the literal NOT_IMPLEMENTED. Anything unreadable is ZERO, not a crash.
     */
    fun parseClock(text: String?): Double {
        val t = text?.trim() ?: return 0.0
        if (t.isEmpty()) return 0.0

        val parts = t.split(Char(58))
        if (parts.size != 3) return 0.0
        val h = parts[0].toIntOrNull() ?: return 0.0
        val m = parts[1].toIntOrNull() ?: return 0.0
        // Seconds may carry a fraction; take the whole part.
        val s = parts[2].split(Char(46))[0].toIntOrNull() ?: return 0.0
        if (h < 0 || m < 0 || m >= 60 || s < 0 || s >= 60) return 0.0
        return (h * 3600 + m * 60 + s).toDouble()
    }

    fun transportState(soapXml: String): String = firstTag(soapXml, "CurrentTransportState") ?: "UNKNOWN"

    fun positionInfo(soapXml: String): Pair<Double, Double> =
        Pair(parseClock(firstTag(soapXml, "RelTime")), parseClock(firstTag(soapXml, "TrackDuration")))

    /** The names renderers ACTUALLY report, mapped onto our states. */
    fun mapState(s: String): CastPlaybackState = when (s.uppercase()) {
        "PLAYING" -> CastPlaybackState.PLAYING
        "PAUSED_PLAYBACK", "PAUSED" -> CastPlaybackState.PAUSED
        "STOPPED" -> CastPlaybackState.STOPPED
        "TRANSITIONING" -> CastPlaybackState.BUFFERING
        "NO_MEDIA_PRESENT" -> CastPlaybackState.IDLE
        else -> CastPlaybackState.UNKNOWN
    }

    private fun firstTag(xml: String, tag: String): String? = try {
        val f = DocumentBuilderFactory.newInstance()
        f.isNamespaceAware = false
        val doc = f.newDocumentBuilder().parse(xml.byteInputStream())
        val nodes = doc.getElementsByTagName(tag)
        if (nodes.length == 0) null else nodes.item(0).textContent.trim()
    } catch (_: Exception) {
        null
    }
}

// ── DIDL-Lite ───────────────────────────────────────────────────────────────

object DidlLite {
    private val Q = Char(34)

    fun protocolInfo(mime: String): String = "http-get:*:" + mime + ":*"

    /**
     * The metadata blob that rides alongside the URL. Televisions that ignore
     * it still play; the ones that do not, will not play without it.
     */
    fun forMedia(media: CastMedia, url: String, protocolInfo: String): String {
        val upnpClass = when (media.kind) {
            CastContentKind.IMAGE, CastContentKind.SLIDE_SHOW -> "object.item.imageItem.photo"
            CastContentKind.AUDIO -> "object.item.audioItem.musicTrack"
            CastContentKind.VIDEO -> "object.item.videoItem"
        }

        val title = XmlText.escape(if (media.title.isEmpty()) "CircleAI" else media.title)
        val res = XmlText.escape(url)
        val pInfo = XmlText.escape(protocolInfo)

        return "<DIDL-Lite xmlns=" + Q + "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" + Q + " " +
            "xmlns:dc=" + Q + "http://purl.org/dc/elements/1.1/" + Q + " " +
            "xmlns:upnp=" + Q + "urn:schemas-upnp-org:metadata-1-0/upnp/" + Q + ">" +
            "<item id=" + Q + "0" + Q + " parentID=" + Q + "-1" + Q + " restricted=" + Q + "1" + Q + ">" +
            "<dc:title>" + title + "</dc:title>" +
            "<upnp:class>" + upnpClass + "</upnp:class>" +
            "<res protocolInfo=" + Q + pInfo + Q + ">" + res + "</res>" +
            "</item></DIDL-Lite>"
    }
}

// ── Local addresses ─────────────────────────────────────────────────────────
//
// A television PULLS the media from the phone, so the URL handed to it must
// carry an address the television can route to. Loopback and link-local are the
// two that look fine on the phone and are unreachable from the television.

object LocalAddress {

    /** RFC 1918 only: 10/8, 172.16/12, 192.168/16. */
    fun isPrivateV4(b: List<Int>): Boolean {
        if (b.size != 4) return false
        if (b[0] == 10) return true
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true
        return b[0] == 192 && b[1] == 168
    }

    /** APIPA - DHCP never answered, and nothing on the LAN can reach it. */
    fun isLinkLocalV4(b: List<Int>): Boolean = b.size == 4 && b[0] == 169 && b[1] == 254

    fun isLoopbackV4(b: List<Int>): Boolean = b.size == 4 && b[0] == 127

    /** Whether a television on the same LAN could actually fetch from this. */
    fun isCastable(text: String): Boolean {
        val parts = text.split(Char(46))
        if (parts.size != 4) return false
        val b = parts.map { it.toIntOrNull() ?: return false }
        if (b.any { it < 0 || it > 255 }) return false
        return !isLoopbackV4(b) && !isLinkLocalV4(b) && isPrivateV4(b)
    }
}

/** The default slide-show interval, used when a caller passes a non-positive one. */
object CastDefaults {
    const val SLIDE_SHOW_PER_IMAGE_SECONDS = 8.0

    fun perImage(requested: Double): Double =
        if (requested <= 0) SLIDE_SHOW_PER_IMAGE_SECONDS else requested
}
