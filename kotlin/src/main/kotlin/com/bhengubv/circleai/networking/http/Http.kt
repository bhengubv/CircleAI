// Http.kt
//
// Kotlin port of CircleAI.Networking.Http (src/CircleAI.Networking.Http/*.cs is the
// EXACT spec). An [INetworkTransport] backed by an HTTP client — REST calls with
// retry + backoff. HTTP is request-response, so the pull-based receive is
// intentionally empty (use WebSocket/SSE for server push).
//
// The C# reference uses System.Net.Http.HttpClient (a real socket). Per the work
// unit ("in-memory, no real sockets; the socket is an injected interface"), the
// Kotlin port injects [IHttpMessageSender] — a deterministic POST hook that carries
// the body, content-type, and headers. Transient failures surface as
// [HttpTransientException] (the analogue of C#'s HttpRequestException) and drive the
// same 3-attempt exponential-backoff retry.
//
// Covers (C# → Kotlin):
//   HttpTransportCommons.cs → HttpEndpointDescriptor, HttpRequestSummary,
//                             HttpCacheKey (records → data classes),
//                             HttpStatusFamily (static → object),
//                             InMemoryHttpRequestMetrics
//   HttpNetworkTransport.cs → HttpNetworkTransport (INetworkTransport,
//                             AutoCloseable), IHttpMessageSender (injected socket
//                             contract, standing in for HttpClient), HttpResponse +
//                             HttpTransientException (supporting the send contract)
//
// C# → Kotlin conventions:
//   record                        → data class
//   IReadOnlyDictionary            → Map
//   TimeSpan                       → java.time.Duration
//   ConcurrentDictionary + lock    → ConcurrentHashMap + synchronized
//   IDisposable                    → AutoCloseable
//   Uri.EscapeDataString           → URLEncoder (RFC-3986 form; space→%20 not +)
//   HttpRequestException           → HttpTransientException
//   EnsureSuccessStatusCode        → HttpResponse.ensureSuccess (throws on non-2xx)
//   Task.Delay(2^attempt s)        → delay backoff via injectable sleep lambda
//   Task / IAsyncEnumerable<T>     → suspend fun / Flow<T>
//   static class                   → object
//
// DETERMINISM: C# awaits Task.Delay for the backoff. To keep tests fast +
// deterministic (RULES) the backoff is an injectable suspend lambda
// (sleep: (Duration) -> Unit) defaulting to kotlinx.coroutines.delay; tests pass a
// no-op recorder to assert the schedule without real waiting.
package com.bhengubv.circleai.networking.http

import com.bhengubv.circleai.networking.INetworkTransport
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import java.time.Duration
import java.time.Instant
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.pow

// ===========================================================================
// Records  (HttpTransportCommons.cs)
// ===========================================================================

/** Description of an HTTP endpoint: method, base URI, path, default headers. */
data class HttpEndpointDescriptor(
    val method: String,
    val baseUri: String,
    val path: String,
    val defaultHeaders: Map<String, String>?,
)

/** Summary of one completed HTTP request. */
data class HttpRequestSummary(
    val endpointId: String,
    val statusCode: Int,
    val latency: Duration,
    val responseBytes: Int,
    val atUtc: Instant,
)

/** Cache key for an HTTP response: method + full URI + Accept header. */
data class HttpCacheKey(
    val method: String,
    val fullUri: String,
    val acceptHeader: String,
)

// ===========================================================================
// HttpStatusFamily  (HttpTransportCommons.cs)
// ===========================================================================

/** HTTP status-code family predicates + a retryability rule. Matches the C# statics. */
object HttpStatusFamily {
    fun is2xx(s: Int): Boolean = s in 200..299
    fun is3xx(s: Int): Boolean = s in 300..399
    fun is4xx(s: Int): Boolean = s in 400..499
    fun is5xx(s: Int): Boolean = s in 500..599

    /** Retry on 408 (timeout), 425 (too early), 429 (rate-limited), or any 5xx. */
    fun shouldRetry(s: Int): Boolean = s == 408 || s == 425 || s == 429 || is5xx(s)
}

// ===========================================================================
// InMemoryHttpRequestMetrics  (HttpTransportCommons.cs)
// ===========================================================================

/**
 * Deterministic in-memory metrics for HTTP endpoints + requests. Mirrors the C#
 * [ConcurrentDictionary] endpoint map + `lock`ed request list. [recentRequests] is
 * newest first; [avg2xxLatencyMs] averages only 2xx responses for the endpoint and
 * returns 0.0 when there are none.
 */
class InMemoryHttpRequestMetrics {
    private val endpoints = ConcurrentHashMap<String, HttpEndpointDescriptor>()
    private val requests = ArrayList<HttpRequestSummary>()
    private val lock = Any()

    /** Register (or replace) an endpoint descriptor by id. */
    fun register(id: String, d: HttpEndpointDescriptor) {
        endpoints[id] = d
    }

    /** The endpoint descriptor for [id], or null if unknown. */
    fun getEndpoint(id: String): HttpEndpointDescriptor? = endpoints[id]

    /** Log a completed request. */
    fun log(s: HttpRequestSummary) {
        synchronized(lock) { requests.add(s) }
    }

    /** The [limit] most recent request summaries, newest first. */
    fun recentRequests(limit: Int = 100): List<HttpRequestSummary> =
        synchronized(lock) {
            requests.sortedByDescending { it.atUtc }.take(limit)
        }

    /** Mean latency (ms) across 2xx responses for [endpointId]; 0.0 when none. */
    fun avg2xxLatencyMs(endpointId: String): Double =
        synchronized(lock) {
            val rows = requests.filter { it.endpointId == endpointId && HttpStatusFamily.is2xx(it.statusCode) }
            if (rows.isEmpty()) 0.0 else rows.map { it.latency.toNanos() / 1_000_000.0 }.average()
        }
}

// ===========================================================================
// IHttpMessageSender  (injected socket contract for HttpNetworkTransport)
// ===========================================================================

/**
 * The injected stand-in for HttpClient. Performs a single POST of [body] to [url]
 * with the given [contentType] and [headers], returning the [HttpResponse]. A
 * transport-level failure (connection refused, reset, timeout) must be signalled by
 * throwing [HttpTransientException] — the analogue of C#'s HttpRequestException — so
 * the transport's retry loop engages.
 */
interface IHttpMessageSender : AutoCloseable {
    /** POST [body] to [url]; may throw [HttpTransientException] on a transport failure. */
    suspend fun post(
        url: String,
        body: ByteArray,
        contentType: String,
        headers: Map<String, String>,
    ): HttpResponse

    override fun close() {}
}

/**
 * The outcome of an HTTP POST. [ensureSuccess] throws [HttpTransientException] on a
 * non-2xx status (mirrors C# `HttpResponseMessage.EnsureSuccessStatusCode`, whose
 * thrown HttpRequestException is what the send loop treats as retryable).
 */
data class HttpResponse(val statusCode: Int, val body: ByteArray = ByteArray(0)) {
    val isSuccess: Boolean get() = HttpStatusFamily.is2xx(statusCode)

    /** Throw [HttpTransientException] if the status is not 2xx. */
    fun ensureSuccess() {
        if (!isSuccess) {
            throw HttpTransientException("Response status code does not indicate success: $statusCode.")
        }
    }

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is HttpResponse) return false
        return statusCode == other.statusCode && body.contentEquals(other.body)
    }

    override fun hashCode(): Int = 31 * statusCode + body.contentHashCode()
}

/**
 * Signals a transient HTTP failure that the transport retries (analogue of C#'s
 * System.Net.Http.HttpRequestException). Both a transport-level fault and a non-2xx
 * response (via [HttpResponse.ensureSuccess]) raise this.
 */
class HttpTransientException(message: String) : Exception(message)

// ===========================================================================
// HttpNetworkTransport  (HttpNetworkTransport.cs)
// ===========================================================================

/**
 * [INetworkTransport] backed by an HTTP client. Supports REST calls with retry +
 * backoff. Always reports available once configured (`isAvailable == true`).
 *
 * [send] POSTs the payload data to `{baseUrl}/messages/{destinationId}` (or
 * `{baseUrl}/messages` when there is no destination), retrying up to 3 times with
 * exponential backoff (2^attempt seconds) on a transient failure — exactly the C#
 * loop: the first two failures back off and retry, the third propagates. [receive]
 * yields nothing (HTTP is request-response; use WebSocket/SSE for push). [close]
 * disposes the sender.
 *
 * @param sender injected POST hook (stand-in for HttpClient).
 * @param baseUrl trailing slashes are trimmed (matches C# `TrimEnd('/')`).
 * @param sleep injected backoff delay (defaults to kotlinx.coroutines.delay) — kept
 *   injectable so tests exercise the retry schedule without real waiting.
 */
class HttpNetworkTransport(
    private val sender: IHttpMessageSender,
    baseUrl: String,
    private val sleep: suspend (Duration) -> Unit = { delay(it.toMillis()) },
) : INetworkTransport, AutoCloseable {

    private val baseUrl: String
    @Volatile private var running = false

    init {
        require(baseUrl.isNotBlank()) { "baseUrl is required." }
        this.baseUrl = baseUrl.trimEnd('/')
    }

    override val kind: TransportKind get() = TransportKind.Http

    /** Assume HTTP is always available once configured (matches C#). */
    override val isAvailable: Boolean get() = true

    override suspend fun start() {
        running = true
    }

    override suspend fun stop() {
        running = false
    }

    /**
     * POST the payload data to `{baseUrl}/messages/{destinationId}`. Retries up to 3
     * times with exponential backoff on transient failures (the first two attempts
     * back off then retry; the third rethrows).
     */
    override suspend fun send(payload: NetworkPayload) {
        val dest = payload.destinationId
        val url = if (!dest.isNullOrEmpty()) {
            "$baseUrl/messages/${escapeDataString(dest)}"
        } else {
            "$baseUrl/messages"
        }

        val headers = mapOf(
            "X-Payload-Id" to payload.id,
            "X-Payload-Priority" to payload.priority.name,
        )

        var attempt = 0
        while (attempt < 3) {
            try {
                val resp = sender.post(url, payload.data, payload.contentType, headers)
                resp.ensureSuccess()
                return
            } catch (e: HttpTransientException) {
                // Mirror C#: catch only when attempt < 2, else let it propagate.
                if (attempt < 2) {
                    sleep(Duration.ofSeconds(2.0.pow(attempt).toLong()))
                } else {
                    throw e
                }
            }
            attempt++
        }
    }

    override fun receive(): Flow<NetworkPayload> = emptyFlow()

    override fun close() {
        sender.close()
    }

    private companion object {
        /**
         * RFC-3986 percent-encoding matching .NET's Uri.EscapeDataString: encode
         * everything except the unreserved set (A–Z a–z 0–9 - _ . ~). URLEncoder is
         * application/x-www-form-urlencoded (space→'+', and it leaves '*' unescaped
         * while escaping '~'), so post-process to reconcile the two.
         */
        fun escapeDataString(value: String): String =
            URLEncoder.encode(value, StandardCharsets.UTF_8)
                .replace("+", "%20")
                .replace("*", "%2A")
                .replace("%7E", "~")
    }
}
