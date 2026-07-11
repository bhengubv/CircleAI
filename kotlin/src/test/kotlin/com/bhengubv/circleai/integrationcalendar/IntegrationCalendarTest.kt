// IntegrationCalendarTest.kt
//
// Verifies the CircleAI.Integration.Calendar port against the C# reference:
//   - CalDAV: REPORT verb + Depth header + Basic auth; ICS parse of
//     UID/SUMMARY/DTSTART/DTEND; createEvent PUTs If-None-Match:*; providerId +
//     isConfigured semantics.
//   - Google: bearer auth, URL params, items walk, cancelled skip, attendees,
//     all-day date parse; createEvent returns the server id; token refresh throws.
//   - Graph: calendarView URL, value walk, isAllDay, attendee emailAddress.

package com.bhengubv.circleai.integrationcalendar

import com.bhengubv.circleai.integration.AccessTokenProvider
import com.bhengubv.circleai.integration.CalendarEvent
import com.bhengubv.circleai.integration.HttpResponse
import com.bhengubv.circleai.integration.HttpVerb
import com.bhengubv.circleai.integration.support.FakeTransport
import com.bhengubv.circleai.integration.support.okTransport
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.net.URI
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class IntegrationCalendarTest {

    private val from = Instant.parse("2026-07-10T00:00:00Z")
    private val to = Instant.parse("2026-07-11T00:00:00Z")

    // ── CalDAV ──────────────────────────────────────────────────────────

    private val caldavXml = """
        <?xml version="1.0"?>
        <D:multistatus xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
          <D:response>
            <D:propstat><D:prop>
              <C:calendar-data>BEGIN:VCALENDAR
        VERSION:2.0
        BEGIN:VEVENT
        UID:evt-1
        SUMMARY:Standup
        DESCRIPTION:Daily sync
        LOCATION:Room 4
        DTSTART:20260710T090000Z
        DTEND:20260710T093000Z
        END:VEVENT
        END:VCALENDAR</C:calendar-data>
            </D:prop></D:propstat>
          </D:response>
        </D:multistatus>
    """.trimIndent()

    @Test
    fun `caldav lists events via REPORT`() = runTest {
        val http = okTransport(caldavXml)
        val c = CalDavCalendarConnector(
            CalDavCalendarOptions(URI("https://cal.example.com/dav/personal/"), "alice", "pw"),
            http,
        )
        assertEquals("caldav", c.providerId)
        assertTrue(c.isConfigured)

        val events = c.listEvents(from, to)
        assertEquals(1, events.size)
        val e = events[0]
        assertEquals("evt-1", e.eventId)
        assertEquals("Standup", e.title)
        assertEquals("Daily sync", e.description)
        assertEquals("Room 4", e.location)
        assertEquals(Instant.parse("2026-07-10T09:00:00Z"), e.startUtc)
        assertEquals(Instant.parse("2026-07-10T09:30:00Z"), e.endUtc)
        assertFalse(e.isAllDay)

        // Request shape.
        assertEquals(HttpVerb.REPORT, http.last.verb)
        assertEquals("1", http.last.headers["Depth"])
        assertTrue(http.last.headers["Authorization"]!!.startsWith("Basic "))
    }

    @Test
    fun `caldav not configured when password blank`() {
        val c = CalDavCalendarConnector(
            CalDavCalendarOptions(URI("https://cal.example.com/dav/personal/"), "alice", ""),
            okTransport(""),
        )
        assertFalse(c.isConfigured)
    }

    @Test
    fun `caldav create puts ics with if-none-match`() = runTest {
        val http = FakeTransport { HttpResponse(201, "") }
        val c = CalDavCalendarConnector(
            CalDavCalendarOptions(URI("https://cal.example.com/dav/personal/"), "alice", "pw"),
            http,
        )
        val ev = CalendarEvent("e9", "cal", "Lunch", null, null, from, to, false, emptyList())
        val created = c.createEvent(ev)
        assertEquals("e9", created.eventId)
        assertEquals(HttpVerb.PUT, http.last.verb)
        assertEquals("*", http.last.headers["If-None-Match"])
        assertTrue(http.last.url.endsWith("/e9.ics"))
        assertTrue(http.last.body!!.contains("SUMMARY:Lunch"))
    }

    // ── Google ──────────────────────────────────────────────────────────

    private val googleJson = """
        {
          "items": [
            { "status": "cancelled", "id": "gone" },
            {
              "id": "g1", "summary": "Design review", "description": "Q3",
              "location": "HQ",
              "start": { "dateTime": "2026-07-10T14:00:00Z" },
              "end":   { "dateTime": "2026-07-10T15:00:00Z" },
              "attendees": [ { "email": "bob@x.com" }, { "email": "cara@x.com" } ]
            },
            {
              "id": "g2", "summary": "Holiday",
              "start": { "date": "2026-07-12" },
              "end":   { "date": "2026-07-13" }
            }
          ]
        }
    """.trimIndent()

    @Test
    fun `google lists events skipping cancelled`() = runTest {
        val http = okTransport(googleJson)
        val c = GoogleCalendarConnector(GoogleCalendarOptions({ "tok-123" }), http)
        assertEquals("google-calendar", c.providerId)

        val events = c.listEvents(from, to)
        assertEquals(2, events.size)
        assertEquals("g1", events[0].eventId)
        assertEquals("Design review", events[0].title)
        assertEquals(listOf("bob@x.com", "cara@x.com"), events[0].attendees)
        assertFalse(events[0].isAllDay)
        // all-day
        assertTrue(events[1].isAllDay)
        assertEquals(Instant.parse("2026-07-12T00:00:00Z"), events[1].startUtc)

        assertTrue(http.last.headers["Authorization"] == "Bearer tok-123")
        assertTrue(http.requests.first().url.contains("singleEvents=true"))
    }

    @Test
    fun `google create returns server id`() = runTest {
        val http = FakeTransport { req ->
            if (req.verb == HttpVerb.POST) HttpResponse(200, """{"id":"srv-77"}""") else HttpResponse(200, "{}")
        }
        val c = GoogleCalendarConnector(GoogleCalendarOptions({ "tok" }), http)
        val ev = CalendarEvent("", "primary", "New", "d", "L", from, to, false, listOf("z@x.com"))
        val created = c.createEvent(ev)
        assertEquals("srv-77", created.eventId)
        assertTrue(http.last.body!!.contains("\"summary\":\"New\""))
    }

    @Test
    fun `google throws when token unavailable`() = runTest {
        val c = GoogleCalendarConnector(GoogleCalendarOptions(AccessTokenProvider { null }), okTransport("{}"))
        assertFailsWith<IllegalStateException> { c.listEvents(from, to) }
    }

    // ── Graph ───────────────────────────────────────────────────────────

    private val graphJson = """
        {
          "value": [
            {
              "id": "m1", "subject": "1:1", "bodyPreview": "notes",
              "isAllDay": false,
              "location": { "displayName": "Cafe" },
              "start": { "dateTime": "2026-07-10T10:00:00.0000000", "timeZone": "UTC" },
              "end":   { "dateTime": "2026-07-10T10:30:00.0000000", "timeZone": "UTC" },
              "attendees": [ { "emailAddress": { "address": "d@x.com" } } ]
            }
          ]
        }
    """.trimIndent()

    @Test
    fun `graph lists events from calendarView`() = runTest {
        val http = okTransport(graphJson)
        val c = MsGraphCalendarConnector(MsGraphCalendarOptions({ "gtok" }), http)
        assertEquals("ms-graph-calendar", c.providerId)

        val events = c.listEvents(from, to)
        assertEquals(1, events.size)
        assertEquals("m1", events[0].eventId)
        assertEquals("1:1", events[0].title)
        assertEquals("Cafe", events[0].location)
        assertEquals(listOf("d@x.com"), events[0].attendees)
        assertEquals(Instant.parse("2026-07-10T10:00:00Z"), events[0].startUtc)
        assertTrue(http.requests.first().url.contains("calendarView"))
    }
}
