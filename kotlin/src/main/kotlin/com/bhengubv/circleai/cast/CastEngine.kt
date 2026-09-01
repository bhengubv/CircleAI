// CastEngine.kt
//
// The I/O half of CircleAI.Cast: the target and session contracts, the LAN
// media host a renderer pulls from, the UPnP control point, and the engine that
// wires them together.
//
// The deterministic half - SSDP framing, the SOAP envelope, DIDL-Lite, the
// device description, the address rules - is in Cast.kt. This file is the part
// that touches a socket.

package com.bhengubv.circleai.cast

import java.io.File
import java.io.RandomAccessFile
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.URI
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicReference
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** Anything that went wrong casting, named. */
open class CastException(message: String, cause: Throwable? = null) : Exception(message, cause)

/** One discovered renderer. */
interface ICastTarget {
    val id: CastTargetId
    val friendlyName: String
    val manufacturer: String
    val model: String
    val protocol: CastProtocolKind
    val location: URI
    val iconUri: URI?
    suspend fun connect(): ICastSession
}

interface ICastDiscovery {
    val backendId: String

    /** Emits targets as they answer, for the length of the search window. */
    fun discover(searchWindowMs: Long): Flow<ICastTarget>
}

interface ICastSession {
    val target: ICastTarget
    suspend fun load(media: CastMedia)
    suspend fun play()
    suspend fun pause()
    suspend fun stop()
    suspend fun seek(positionSeconds: Double)
    suspend fun status(): CastStatus

    /** Cycles a sequence of images on a timer - the real "cast a deck" leg. */
    suspend fun showSlideShow(images: List<CastMedia>, perImageSeconds: Double)

    /**
     * Releases what the session published.
     *
     * SUSPEND rather than AutoCloseable, matching the C# IAsyncDisposable:
     * un-publishing talks to the media host, and a close() that had to block on
     * that would either deadlock a coroutine dispatcher or silently skip it.
     */
    suspend fun dispose()
}

/**
 * Serves published assets over LAN HTTP so a renderer can PULL them.
 *
 * The pull model is not a choice: a DLNA renderer will not accept a push, it
 * fetches the URL you hand it. That is why byte and file media need a host at
 * all, and why a session without one has to refuse them rather than pretend.
 */
interface ILocalMediaHost : AutoCloseable {
    val backendId: String
    val isRunning: Boolean
    val baseUrl: URI?
    suspend fun start()
    suspend fun publish(source: CastMediaSource, mimeType: String): URI
    suspend fun unpublish(url: URI)
}

data class CastDocument(val title: String, val source: CastMediaSource, val mimeType: String)

/**
 * Turns a document or a deck into castable page images.
 *
 * An HONEST SEAM: rasterising a PDF needs a page renderer that is not pure
 * managed code, so the contract is defined and the null implementation refuses
 * rather than returning an empty deck that looks like success.
 */
interface IDocumentCastAdapter {
    val backendId: String
    suspend fun toCastable(document: CastDocument): List<CastMedia>
}

interface ICastEngine {
    val backendId: String
    fun discover(searchWindowMs: Long): Flow<ICastTarget>
    suspend fun cast(target: ICastTarget, media: CastMedia): ICastSession
}

// ------------------------------------------------------ The media host

/**
 * A minimal HTTP/1.1 server that serves each published asset at its own URL,
 * with Range support so a renderer can seek.
 *
 * Range is not optional in practice: a television that cannot ask for a byte
 * range restarts the file from the beginning every time somebody scrubs, and
 * some refuse to play at all without an Accept-Ranges header.
 */
class TcpMediaHost(private val bind: InetAddress) : ILocalMediaHost {

    private data class Resource(
        val mime: String,
        val length: Long,
        val bytes: ByteArray?,
        val filePath: String?,
    )

    private val resources = ConcurrentHashMap<String, Resource>()
    private val gate = Any()
    private val listener = AtomicReference<ServerSocket?>(null)
    private var scope: CoroutineScope? = null
    private var port = 0

    override val backendId: String get() = "tcp-http"

    override val isRunning: Boolean get() = listener.get() != null

    override val baseUrl: URI?
        get() = if (isRunning) URI("http://" + bind.hostAddress + ":" + port + "/") else null

    override suspend fun start() {
        synchronized(gate) {
            if (listener.get() != null) return
            // Port 0 asks the OS for a free one, and the assigned port is read
            // back off the socket - hard-coding one collides with whatever else
            // the phone is running.
            val s = ServerSocket(0, 50, bind)
            port = s.localPort
            listener.set(s)
            val sc = CoroutineScope(SupervisorJob() + Dispatchers.IO)
            scope = sc
            sc.launch { acceptLoop(s) }
        }
    }

    override suspend fun publish(source: CastMediaSource, mimeType: String): URI {
        if (mimeType.isBlank()) throw CastException("mimeType is required to publish media.")
        if (!isRunning) start()

        val path = "/" + UUID.randomUUID().toString().replace("-", "") + guessExtension(mimeType)
        val res = when (source) {
            is CastMediaSource.Bytes -> Resource(mimeType, source.data.size.toLong(), source.data, null)
            is CastMediaSource.LocalFile -> Resource(mimeType, File(source.path).length(), null, source.path)
            is CastMediaSource.Url -> throw CastException(
                "URL sources are already reachable; publish is only for bytes and file media.",
            )
        }

        resources[path] = res
        return URI("http://" + bind.hostAddress + ":" + port + path)
    }

    override suspend fun unpublish(url: URI) {
        resources.remove(url.path)
    }

    override fun close() {
        synchronized(gate) {
            scope?.cancel()
            scope = null
            // Shutdown races here are expected: the accept loop is blocked in
            // accept() and closing the socket is how it is woken.
            try { listener.getAndSet(null)?.close() } catch (e: Exception) { }
        }
        resources.clear()
    }

    private suspend fun acceptLoop(server: ServerSocket) {
        while (scope?.isActive == true) {
            val client = try {
                withContext(Dispatchers.IO) { server.accept() }
            } catch (e: Exception) {
                return
            }
            scope?.launch {
                try { handleClient(client) } catch (e: Exception) { } finally {
                    try { client.close() } catch (e: Exception) { }
                }
            }
        }
    }

    private fun handleClient(client: Socket) {
        client.soTimeout = 10_000
        val input = client.getInputStream()
        val output = client.getOutputStream()

        val request = readRequest(input) ?: return
        serve(output, request.first, request.second, request.third)
        output.flush()
    }

    /** Method, path and the Range header, or null if the request never completed. */
    private fun readRequest(input: java.io.InputStream): Triple<String, String, String?>? {
        val header = ByteArray(8192)
        var len = 0
        while (len < header.size) {
            val n = input.read(header, len, header.size - len)
            if (n <= 0) break
            len += n
            if (indexOfDoubleCrlf(header, len) >= 0) break
        }
        val end = indexOfDoubleCrlf(header, len)
        if (end < 0) return null

        val text = String(header, 0, end, Charsets.US_ASCII)
        val lines = text.split("\r\n")
        if (lines.isEmpty()) return null

        val parts = lines[0].split(Char(32))
        if (parts.size < 2) return null

        var range: String? = null
        for (i in 1 until lines.size) {
            val c = lines[i].indexOf(':')
            if (c <= 0) continue
            if (lines[i].substring(0, c).trim().equals("Range", ignoreCase = true)) {
                range = lines[i].substring(c + 1).trim()
            }
        }
        return Triple(parts[0], parts[1], range)
    }

    private fun serve(out: java.io.OutputStream, method: String, rawPath: String, rangeHeader: String?) {
        var path = rawPath
        val q = path.indexOf('?')
        if (q >= 0) path = path.substring(0, q)

        val isGet = method.equals("GET", ignoreCase = true)
        val isHead = method.equals("HEAD", ignoreCase = true)
        if (!isGet && !isHead) { writeStatus(out, 405, "Method Not Allowed"); return }

        val res = resources[path] ?: run { writeStatus(out, 404, "Not Found"); return }

        val parsed = if (res.length > 0 && rangeHeader != null) parseRange(rangeHeader, res.length) else null
        val start = parsed?.first ?: 0L
        val end = parsed?.second ?: (res.length - 1)
        val partial = parsed != null
        val contentLength = if (res.length == 0L) 0L else end - start + 1

        val sb = StringBuilder(256)
        sb.append(if (partial) "HTTP/1.1 206 Partial Content\r\n" else "HTTP/1.1 200 OK\r\n")
        sb.append("Content-Type: ").append(res.mime).append("\r\n")
        sb.append("Content-Length: ").append(contentLength).append("\r\n")
        sb.append("Accept-Ranges: bytes\r\n")
        if (partial) {
            sb.append("Content-Range: bytes ").append(start).append('-').append(end)
                .append('/').append(res.length).append("\r\n")
        }
        // The two DLNA headers a renderer reads to decide how to fetch. Without
        // them some televisions download the whole file before showing anything,
        // and some refuse an image outright.
        sb.append("transferMode.dlna.org: ")
            .append(if (res.mime.startsWith("image/", ignoreCase = true)) "Interactive" else "Streaming")
            .append("\r\n")
        sb.append(
            "contentFeatures.dlna.org: DLNA.ORG_OP=01;DLNA.ORG_CI=0;" +
                "DLNA.ORG_FLAGS=01700000000000000000000000000000\r\n",
        )
        sb.append("Server: CircleAI.Cast/3.5\r\n")
        sb.append("Connection: close\r\n\r\n")

        out.write(sb.toString().toByteArray(Charsets.US_ASCII))
        if (isHead || contentLength == 0L) return

        if (res.bytes != null) {
            out.write(res.bytes, start.toInt(), contentLength.toInt())
        } else if (res.filePath != null) {
            RandomAccessFile(res.filePath, "r").use { raf ->
                raf.seek(start)
                val buffer = ByteArray(81920)
                var remaining = contentLength
                while (remaining > 0) {
                    val toRead = minOf(buffer.size.toLong(), remaining).toInt()
                    val n = raf.read(buffer, 0, toRead)
                    if (n <= 0) break
                    out.write(buffer, 0, n)
                    remaining -= n
                }
            }
        }
    }

    private fun writeStatus(out: java.io.OutputStream, code: Int, reason: String) {
        out.write(
            ("HTTP/1.1 " + code + " " + reason + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                .toByteArray(Charsets.US_ASCII),
        )
    }

    companion object {
        internal fun indexOfDoubleCrlf(b: ByteArray, len: Int): Int {
            for (i in 0..(len - 4)) {
                if (b[i] == 13.toByte() && b[i + 1] == 10.toByte() &&
                    b[i + 2] == 13.toByte() && b[i + 3] == 10.toByte()
                ) {
                    return i
                }
            }
            return -1
        }

        /**
         * The first byte and the last, inclusive, or null when the header is
         * one this cannot honour.
         *
         * Three forms, and all three turn up in the wild: bytes=0-499,
         * bytes=500- (to the end) and bytes=-500 (the LAST 500, not the first).
         * Reading the suffix form as a start offset serves the wrong part of the
         * file and the picture is silently corrupt.
         */
        internal fun parseRange(header: String, length: Long): Pair<Long, Long>? {
            val prefix = "bytes="
            if (!header.startsWith(prefix, ignoreCase = true)) return null

            var spec = header.substring(prefix.length)
            val dash = spec.indexOf('-')
            if (dash < 0) return null

            val startPart = spec.substring(0, dash).trim()
            var endPart = spec.substring(dash + 1).trim()
            // Only the FIRST range is honoured; a multipart response is not
            // something any renderer here asks for.
            val comma = endPart.indexOf(',')
            if (comma >= 0) endPart = endPart.substring(0, comma).trim()

            var start: Long
            var end: Long
            if (startPart.isEmpty()) {
                val suffix = endPart.toLongOrNull() ?: return null
                if (suffix <= 0) return null
                start = maxOf(0L, length - suffix)
                end = length - 1
            } else {
                start = startPart.toLongOrNull() ?: return null
                end = if (endPart.isEmpty()) length - 1 else (endPart.toLongOrNull() ?: return null)
            }

            if (start < 0 || end < start) return null
            if (end > length - 1) end = length - 1
            return if (start <= end) start to end else null
        }

        internal fun guessExtension(mime: String): String = when (mime.lowercase()) {
            "video/mp4" -> ".mp4"
            "video/x-matroska" -> ".mkv"
            "video/webm" -> ".webm"
            "audio/mpeg" -> ".mp3"
            "audio/mp4" -> ".m4a"
            "audio/wav", "audio/x-wav" -> ".wav"
            "image/jpeg" -> ".jpg"
            "image/png" -> ".png"
            "image/gif" -> ".gif"
            "image/apng" -> ".png"
            else -> ".bin"
        }
    }
}
