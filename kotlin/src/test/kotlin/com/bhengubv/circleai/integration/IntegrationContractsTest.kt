// IntegrationContractsTest.kt
//
// Verifies the CircleAI.Integration (Contracts.cs) port: record shapes,
// HttpResponse success semantics, ensureSuccess throwing, and the injected
// transport/token-provider abstractions.

package com.bhengubv.circleai.integration

import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.net.URI
import java.time.Duration
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class IntegrationContractsTest {

    @Test
    fun `records carry their fields`() {
        val ev = CalendarEvent("e", "c", "T", "d", "l", Instant.EPOCH, Instant.EPOCH, false, listOf("a"))
        assertEquals("T", ev.title)
        assertEquals(listOf("a"), ev.attendees)

        val mail = EmailMessage("m", "f", listOf("t"), "s", "b", Instant.EPOCH, true, listOf("L"))
        assertTrue(mail.unread)

        val news = NewsItem("i", "s", "t", "sum", URI("https://x"), Instant.EPOCH, listOf("tag"))
        assertEquals(URI("https://x"), news.url)

        val w = WeatherSample(Instant.EPOCH, 1.0, 2.0, 3.0, 4.0, 5, "clear")
        assertEquals("clear", w.condition)

        val route = RouteEstimate(1.0, Duration.ofMinutes(5), listOf(GeoPoint(1.0, 2.0)))
        assertEquals(1, route.polyline.size)
        assertEquals(2.0, route.polyline[0].lon, 1e-9)

        val ha = HaEntity("light.k", "Kitchen", "light", "on", mapOf("b" to "200"))
        assertEquals("light", ha.domain)
    }

    @Test
    fun `http response success semantics`() {
        assertTrue(HttpResponse(200, "").isSuccess)
        assertTrue(HttpResponse(204, "").isSuccess)
        assertFalse(HttpResponse(404, "").isSuccess)
        assertFalse(HttpResponse(500, "").isSuccess)
    }

    @Test
    fun `ensure success throws on failure`() {
        HttpResponse(201, "ok").ensureSuccess() // no throw
        val ex = assertFailsWith<IntegrationHttpException> { HttpResponse(403, "").ensureSuccess() }
        assertEquals(403, ex.status)
    }

    @Test
    fun `access token provider is a suspend fun interface`() = runTest {
        val p = AccessTokenProvider { "abc" }
        assertEquals("abc", p.getToken())
    }

    @Test
    fun `http transport records requests`() = runTest {
        var seen: HttpRequest? = null
        val t = object : HttpTransport {
            override suspend fun send(request: HttpRequest): HttpResponse {
                seen = request
                return HttpResponse(200, "body")
            }
        }
        val resp = t.send(HttpRequest(HttpVerb.GET, "https://x"))
        assertEquals("body", resp.body)
        assertEquals(HttpVerb.GET, seen!!.verb)
    }
}
