// HttpTest.kt
//
// Verifies the CircleAI.Networking.Http port:
//   - HttpStatusFamily predicates + shouldRetry (408/425/429/5xx)
//   - InMemoryHttpRequestMetrics: register/getEndpoint, recentRequests newest-first,
//     avg2xxLatencyMs (only 2xx, 0.0 when none)
//   - HttpNetworkTransport: kind, isAvailable always true, start/stop flip running,
//     send POSTs to {base}/messages/{escaped dest} (or /messages), sets the
//     X-Payload-Id + X-Payload-Priority headers + content-type, trims the base url,
//     retries up to 3x with 1s then 2s backoff on transient failure then rethrows,
//     succeeds on a later attempt, receive is empty, close disposes the sender

package com.bhengubv.circleai.networking.http

import com.bhengubv.circleai.networking.MessagePriority
import com.bhengubv.circleai.networking.NetworkPayload
import com.bhengubv.circleai.networking.TransportKind
import kotlinx.coroutines.flow.count
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class HttpTest {

    private val t0: Instant = Instant.parse("2026-07-10T00:00:00Z")

    /**
     * Fake sender: records each POST (url/body/contentType/headers). Fails the first
     * [failFirst] attempts with a transient exception, then returns [status]. Backoffs
     * are captured via [backoffs] passed to the transport's injected sleep.
     */
    private class FakeSender(
        val failFirst: Int = 0,
        val status: Int = 200,
    ) : IHttpMessageSender {
        data class Call(val url: String, val body: ByteArray, val contentType: String, val headers: Map<String, String>)
        val calls = mutableListOf<Call>()
        var closed = false
        private var attempt = 0

        override suspend fun post(url: String, body: ByteArray, contentType: String, headers: Map<String, String>): HttpResponse {
            calls.add(Call(url, body, contentType, headers))
            val i = attempt++
            if (i < failFirst) throw HttpTransientException("transient at attempt $i")
            return HttpResponse(status)
        }

        override fun close() { closed = true }
    }

    private fun payload(dest: String? = "devB", data: ByteArray = byteArrayOf(1, 2, 3)) =
        NetworkPayload.create(
            data = data,
            destinationId = dest,
            priority = MessagePriority.High,
            contentType = "application/dtn-bundle",
            now = { t0 },
        )

    // -----------------------------------------------------------------------
    // HttpStatusFamily
    // -----------------------------------------------------------------------

    @Test
    fun `status family predicates + shouldRetry match C#`() {
        assertTrue(HttpStatusFamily.is2xx(204))
        assertTrue(HttpStatusFamily.is3xx(301))
        assertTrue(HttpStatusFamily.is4xx(404))
        assertTrue(HttpStatusFamily.is5xx(503))
        assertFalse(HttpStatusFamily.is2xx(404))

        for (s in listOf(408, 425, 429, 500, 502, 599)) assertTrue(HttpStatusFamily.shouldRetry(s), "should retry $s")
        for (s in listOf(200, 400, 401, 404)) assertFalse(HttpStatusFamily.shouldRetry(s), "should not retry $s")
    }

    // -----------------------------------------------------------------------
    // InMemoryHttpRequestMetrics
    // -----------------------------------------------------------------------

    @Test
    fun `metrics register + getEndpoint round-trips`() {
        val m = InMemoryHttpRequestMetrics()
        val d = HttpEndpointDescriptor("POST", "https://h", "/x", mapOf("A" to "B"))
        m.register("e1", d)
        assertEquals(d, m.getEndpoint("e1"))
        assertNull(m.getEndpoint("absent"))
    }

    @Test
    fun `recentRequests is newest-first`() {
        val m = InMemoryHttpRequestMetrics()
        m.log(HttpRequestSummary("e1", 200, Duration.ofMillis(10), 100, t0))
        m.log(HttpRequestSummary("e1", 200, Duration.ofMillis(20), 100, t0.plusSeconds(5)))
        m.log(HttpRequestSummary("e1", 200, Duration.ofMillis(30), 100, t0.plusSeconds(10)))
        assertEquals(
            listOf(t0.plusSeconds(10), t0.plusSeconds(5), t0),
            m.recentRequests().map { it.atUtc },
        )
    }

    @Test
    fun `avg2xxLatencyMs averages only 2xx and is 0 when none`() {
        val m = InMemoryHttpRequestMetrics()
        assertEquals(0.0, m.avg2xxLatencyMs("e1"), 1e-9)
        m.log(HttpRequestSummary("e1", 200, Duration.ofMillis(10), 1, t0))
        m.log(HttpRequestSummary("e1", 204, Duration.ofMillis(30), 1, t0))
        m.log(HttpRequestSummary("e1", 500, Duration.ofMillis(999), 1, t0)) // excluded
        m.log(HttpRequestSummary("other", 200, Duration.ofMillis(1), 1, t0)) // excluded
        assertEquals(20.0, m.avg2xxLatencyMs("e1"), 1e-9)
    }

    // -----------------------------------------------------------------------
    // HttpNetworkTransport
    // -----------------------------------------------------------------------

    @Test
    fun `transport kind + always-available + start-stop`() = runTest {
        val transport = HttpNetworkTransport(FakeSender(), "https://h")
        assertEquals(TransportKind.Http, transport.kind)
        assertTrue(transport.isAvailable)
        transport.start()
        transport.stop()
        assertTrue(transport.isAvailable) // HTTP is always available once configured
    }

    @Test
    fun `send posts to messages-destination with headers + content type`() = runTest {
        val sender = FakeSender()
        val transport = HttpNetworkTransport(sender, "https://h/")  // trailing slash trimmed
        val p = payload(dest = "devB")
        transport.send(p)

        val call = sender.calls.single()
        assertEquals("https://h/messages/devB", call.url)
        assertEquals("application/dtn-bundle", call.contentType)
        assertEquals(p.id, call.headers["X-Payload-Id"])
        assertEquals("High", call.headers["X-Payload-Priority"])
        assertTrue(byteArrayOf(1, 2, 3).contentEquals(call.body))
    }

    @Test
    fun `send with no destination posts to bare messages endpoint`() = runTest {
        val sender = FakeSender()
        val transport = HttpNetworkTransport(sender, "https://h")
        transport.send(payload(dest = null))
        assertEquals("https://h/messages", sender.calls.single().url)
    }

    @Test
    fun `send percent-encodes the destination id`() = runTest {
        val sender = FakeSender()
        val transport = HttpNetworkTransport(sender, "https://h")
        transport.send(payload(dest = "a b/c#d"))
        // space->%20, '/'->%2F, '#'->%23 (RFC-3986 data-string escaping)
        assertEquals("https://h/messages/a%20b%2Fc%23d", sender.calls.single().url)
    }

    @Test
    fun `send retries with 1s then 2s backoff and succeeds on the third attempt`() = runTest {
        val sender = FakeSender(failFirst = 2, status = 200)
        val backoffs = mutableListOf<Duration>()
        val transport = HttpNetworkTransport(sender, "https://h", sleep = { backoffs.add(it) })

        transport.send(payload())

        assertEquals(3, sender.calls.size) // 2 failures + 1 success
        assertEquals(listOf(Duration.ofSeconds(1), Duration.ofSeconds(2)), backoffs)
    }

    @Test
    fun `send rethrows after exhausting all three attempts`() = runTest {
        val sender = FakeSender(failFirst = 3, status = 200)
        val backoffs = mutableListOf<Duration>()
        val transport = HttpNetworkTransport(sender, "https://h", sleep = { backoffs.add(it) })

        assertFailsWith<HttpTransientException> { transport.send(payload()) }

        assertEquals(3, sender.calls.size)                 // three attempts made
        assertEquals(2, backoffs.size)                     // backoff only after attempts 0 and 1
    }

    @Test
    fun `non-2xx response is treated as retryable then rethrows`() = runTest {
        val sender = FakeSender(failFirst = 0, status = 500) // always 500 -> ensureSuccess throws each time
        val backoffs = mutableListOf<Duration>()
        val transport = HttpNetworkTransport(sender, "https://h", sleep = { backoffs.add(it) })

        assertFailsWith<HttpTransientException> { transport.send(payload()) }
        assertEquals(3, sender.calls.size)
    }

    @Test
    fun `send succeeds immediately on 2xx with no backoff`() = runTest {
        val sender = FakeSender(failFirst = 0, status = 200)
        val backoffs = mutableListOf<Duration>()
        val transport = HttpNetworkTransport(sender, "https://h", sleep = { backoffs.add(it) })
        transport.send(payload())
        assertEquals(1, sender.calls.size)
        assertTrue(backoffs.isEmpty())
    }

    @Test
    fun `receive is empty and close disposes the sender`() = runTest {
        val sender = FakeSender()
        val transport = HttpNetworkTransport(sender, "https://h")
        assertEquals(0, transport.receive().count())
        transport.close()
        assertTrue(sender.closed)
    }
}
