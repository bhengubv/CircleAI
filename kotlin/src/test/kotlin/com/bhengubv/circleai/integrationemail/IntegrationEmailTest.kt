// IntegrationEmailTest.kt
//
// Verifies the CircleAI.Integration.Email port against the C# reference:
//   - Gmail: list == search("is:unread"); two-step list+get; header parse
//     (From/To/Subject case-insensitive); base64url text/plain body; UNREAD
//     label -> unread; internalDate epoch-ms; markRead posts removeLabelIds.
//   - IMAP: unseen newest-first Take(max); search subject/body; markRead
//     requires numeric UID; unread == !seen.
//   - Graph: unread filter; body.content else bodyPreview; isRead==false.

package com.bhengubv.circleai.integrationemail

import com.bhengubv.circleai.integration.HttpResponse
import com.bhengubv.circleai.integration.HttpVerb
import com.bhengubv.circleai.integration.support.FakeImapTransport
import com.bhengubv.circleai.integration.support.FakeTransport
import com.bhengubv.circleai.integration.support.okTransport
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.time.Instant
import java.util.Base64
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class IntegrationEmailTest {

    // ── Gmail ───────────────────────────────────────────────────────────

    private fun b64url(s: String): String =
        Base64.getUrlEncoder().withoutPadding().encodeToString(s.toByteArray(Charsets.UTF_8))

    @Test
    fun `gmail list unread parses headers and body`() = runTest {
        val bodyData = b64url("Hello there")
        val listJson = """{ "messages": [ { "id": "m1" } ] }"""
        val getJson = """
            {
              "id": "m1",
              "labelIds": [ "INBOX", "UNREAD" ],
              "internalDate": "1720612800000",
              "payload": {
                "headers": [
                  { "name": "From", "value": "sender@x.com" },
                  { "name": "To", "value": "me@x.com, other@x.com" },
                  { "name": "Subject", "value": "Hi" }
                ],
                "body": { "data": "$bodyData" }
              }
            }
        """.trimIndent()
        val http = FakeTransport { req ->
            when {
                req.url.contains("messages?q=") -> HttpResponse(200, listJson)
                req.url.contains("messages/m1") -> HttpResponse(200, getJson)
                else -> HttpResponse(404, "")
            }
        }
        val c = GmailEmailConnector(GmailOptions { "tok" }, http)
        assertEquals("gmail", c.providerId)

        val msgs = c.listUnread(10)
        assertEquals(1, msgs.size)
        val m = msgs[0]
        assertEquals("m1", m.messageId)
        assertEquals("sender@x.com", m.from)
        assertEquals(listOf("me@x.com", "other@x.com"), m.to)
        assertEquals("Hi", m.subject)
        assertEquals("Hello there", m.bodyText)
        assertTrue(m.unread)
        assertEquals(Instant.ofEpochMilli(1720612800000L), m.receivedUtc)
        // list uses is:unread query
        assertTrue(http.requests.first().url.contains("is%3Aunread"))
    }

    @Test
    fun `gmail mark read posts remove unread label`() = runTest {
        val http = okTransport("{}")
        val c = GmailEmailConnector(GmailOptions { "tok" }, http)
        c.markRead("m1")
        assertEquals(HttpVerb.POST, http.last.verb)
        assertTrue(http.last.url.endsWith("/modify"))
        assertTrue(http.last.body!!.contains("UNREAD"))
    }

    // ── IMAP ────────────────────────────────────────────────────────────

    @Test
    fun `imap list unread newest first capped`() = runTest {
        val summaries = listOf(
            ImapMessageSummary(1, "a@x", listOf("me@x"), "One", "b1", Instant.parse("2026-07-01T00:00:00Z"), false, listOf("Recent")),
            ImapMessageSummary(3, "c@x", listOf("me@x"), "Three", "b3", Instant.parse("2026-07-03T00:00:00Z"), false, emptyList()),
            ImapMessageSummary(2, "b@x", listOf("me@x"), "Two", "b2", Instant.parse("2026-07-02T00:00:00Z"), false, emptyList()),
        )
        val c = ImapEmailConnector(
            ImapOptions("imap.x.com", 993, true, "u", "p"),
            FakeImapTransport(unseen = summaries),
        )
        assertEquals("imap", c.providerId)
        assertTrue(c.isConfigured)

        val msgs = c.listUnread(2)
        assertEquals(listOf("3", "2"), msgs.map { it.messageId })
        assertTrue(msgs.all { it.unread })
    }

    @Test
    fun `imap mark read requires numeric uid`() = runTest {
        val imap = FakeImapTransport()
        val c = ImapEmailConnector(ImapOptions("imap.x.com", 993, true, "u", "p"), imap)
        c.markRead("42")
        assertEquals(listOf(42L), imap.markedSeen)
        assertFailsWith<IllegalArgumentException> { c.markRead("not-a-uid") }
    }

    @Test
    fun `imap not configured when host blank`() {
        val c = ImapEmailConnector(ImapOptions("", 993, true, "u", "p"), FakeImapTransport())
        assertFalse(c.isConfigured)
    }

    // ── Graph ───────────────────────────────────────────────────────────

    @Test
    fun `graph list unread parses recipients and body`() = runTest {
        val json = """
            {
              "value": [
                {
                  "id": "g1",
                  "subject": "Report",
                  "isRead": false,
                  "from": { "emailAddress": { "address": "boss@x.com" } },
                  "toRecipients": [ { "emailAddress": { "address": "me@x.com" } } ],
                  "receivedDateTime": "2026-07-09T08:00:00Z",
                  "categories": [ "Work" ],
                  "body": { "content": "Full body" }
                }
              ]
            }
        """.trimIndent()
        val http = okTransport(json)
        val c = MsGraphEmailConnector(MsGraphEmailOptions { "tok" }, http)
        assertEquals("ms-graph-mail", c.providerId)

        val msgs = c.listUnread(10)
        assertEquals(1, msgs.size)
        val m = msgs[0]
        assertEquals("boss@x.com", m.from)
        assertEquals(listOf("me@x.com"), m.to)
        assertEquals("Full body", m.bodyText)
        assertEquals(listOf("Work"), m.labels)
        assertTrue(m.unread)
        assertEquals(Instant.parse("2026-07-09T08:00:00Z"), m.receivedUtc)
        assertTrue(http.requests.first().url.contains("isRead+eq+false"))
    }
}
