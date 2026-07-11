// IntegrationNewsTest.kt
//
// Verifies the CircleAI.Integration.News port against the C# reference:
//   - RSS: <item> parse (title/link/description/guid/category); HTML stripped;
//     pubDate parsed; Take(max). Atom <entry> parse (link href, id, category term).
//   - Bluesky: posts walk; 80-char title truncation; sourceId = author handle;
//     post URL rebuilt from at:// rkey; facet tags.
//   - Mastodon: content HTML stripped; sourceId = acct; sourceId property shape.
//   - NewsApi: articles walk; requires API key; source name -> sourceId.

package com.bhengubv.circleai.integrationnews

import com.bhengubv.circleai.integration.support.okTransport
import kotlinx.coroutines.test.runTest
import org.junit.jupiter.api.Test
import java.net.URI
import java.time.Instant
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class IntegrationNewsTest {

    // ── RSS / Atom ──────────────────────────────────────────────────────

    @Test
    fun `rss parses items and strips html`() = runTest {
        val xml = """
            <?xml version="1.0"?>
            <rss version="2.0"><channel>
              <item>
                <title>Headline One</title>
                <link>https://news.example.com/1</link>
                <guid>guid-1</guid>
                <pubDate>Wed, 02 Oct 2024 13:00:00 GMT</pubDate>
                <description>&lt;p&gt;Body &lt;b&gt;bold&lt;/b&gt;&lt;/p&gt;</description>
                <category>tech</category>
                <category>ai</category>
              </item>
            </channel></rss>
        """.trimIndent()
        val src = RssNewsSource(RssOptions(URI("https://news.example.com/feed"), "myfeed"), okTransport(xml))
        assertEquals("myfeed", src.sourceId)
        assertTrue(src.isConfigured)

        val items = src.fetchLatest(10)
        assertEquals(1, items.size)
        val it = items[0]
        assertEquals("guid-1", it.itemId)
        assertEquals("Headline One", it.title)
        assertEquals(URI("https://news.example.com/1"), it.url)
        assertEquals(listOf("tech", "ai"), it.tags)
        assertTrue(it.summary.contains("Body"))
        assertTrue(it.summary.contains("bold"))
        assertFalse(it.summary.contains("<"))
        assertEquals(Instant.parse("2024-10-02T13:00:00Z"), it.publishedUtc)
    }

    @Test
    fun `rss defaults source id to host`() = runTest {
        val xml = """<?xml version="1.0"?><rss version="2.0"><channel></channel></rss>"""
        val src = RssNewsSource(RssOptions(URI("https://feeds.bbci.co.uk/news/rss.xml")), okTransport(xml))
        assertEquals("feeds.bbci.co.uk", src.sourceId)
    }

    @Test
    fun `atom parses entries`() = runTest {
        val xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <title>Atom Title</title>
                <link href="https://a.example.com/x"/>
                <id>atom-id-1</id>
                <updated>2026-07-10T09:00:00Z</updated>
                <summary>An atom summary</summary>
                <category term="world"/>
              </entry>
            </feed>
        """.trimIndent()
        val src = RssNewsSource(RssOptions(URI("https://a.example.com/atom"), "atomsrc"), okTransport(xml))
        val items = src.fetchLatest(5)
        assertEquals(1, items.size)
        assertEquals("atom-id-1", items[0].itemId)
        assertEquals("Atom Title", items[0].title)
        assertEquals(URI("https://a.example.com/x"), items[0].url)
        assertEquals(listOf("world"), items[0].tags)
        assertEquals(Instant.parse("2026-07-10T09:00:00Z"), items[0].publishedUtc)
    }

    // ── Bluesky ─────────────────────────────────────────────────────────

    @Test
    fun `bluesky truncates title and rebuilds url`() = runTest {
        val longText = "x".repeat(100)
        val json = """
            {
              "posts": [
                {
                  "uri": "at://did:plc:abc/app.bsky.feed.post/rkey123",
                  "author": { "handle": "alice.bsky.social" },
                  "record": {
                    "text": "$longText",
                    "createdAt": "2026-07-10T12:00:00Z",
                    "facets": [ { "features": [ { "tag": "news" } ] } ]
                  }
                }
              ]
            }
        """.trimIndent()
        val src = BlueskySource(BlueskyOptions("kotlin"), okTransport(json))
        assertEquals("bluesky:kotlin", src.sourceId)
        assertTrue(src.isConfigured)

        val items = src.fetchLatest(10)
        assertEquals(1, items.size)
        val it = items[0]
        assertEquals("alice.bsky.social", it.sourceId)
        assertEquals(81, it.title.length) // 80 + ellipsis
        assertTrue(it.title.endsWith("…"))
        assertEquals(longText, it.summary)
        assertEquals(URI("https://bsky.app/profile/alice.bsky.social/post/rkey123"), it.url)
        assertEquals(listOf("news"), it.tags)
    }

    // ── Mastodon ────────────────────────────────────────────────────────

    @Test
    fun `mastodon strips html and uses acct`() = runTest {
        val json = """
            [
              {
                "url": "https://mastodon.social/@bob/1",
                "content": "<p>Toot content</p>",
                "created_at": "2026-07-10T08:00:00.000Z",
                "tags": [ { "name": "tech" } ],
                "account": { "acct": "bob@mastodon.social" }
              }
            ]
        """.trimIndent()
        val src = MastodonSource(MastodonOptions("https://mastodon.social", "tech"), okTransport(json))
        assertEquals("mastodon:https://mastodon.social:#tech", src.sourceId)
        assertTrue(src.isConfigured)

        val items = src.fetchLatest(10)
        assertEquals(1, items.size)
        assertEquals("bob@mastodon.social", items[0].sourceId)
        assertEquals("Toot content", items[0].summary)
        assertEquals(listOf("tech"), items[0].tags)
        assertEquals(URI("https://mastodon.social/@bob/1"), items[0].url)
    }

    @Test
    fun `mastodon public timeline source id`() {
        val src = MastodonSource(MastodonOptions("https://m.example.com"), okTransport("[]"))
        assertEquals("mastodon:https://m.example.com:public", src.sourceId)
    }

    // ── NewsApi ─────────────────────────────────────────────────────────

    @Test
    fun `newsapi parses articles`() = runTest {
        val json = """
            {
              "articles": [
                {
                  "title": "Big News",
                  "description": "Summary text",
                  "url": "https://n.example.com/a",
                  "publishedAt": "2026-07-10T06:00:00Z",
                  "source": { "name": "The Example" }
                }
              ]
            }
        """.trimIndent()
        val src = NewsApiSource(NewsApiOptions("key-1", "climate"), okTransport(json))
        assertEquals("newsapi:climate", src.sourceId)
        assertTrue(src.isConfigured)

        val items = src.fetchLatest(10)
        assertEquals(1, items.size)
        assertEquals("The Example", items[0].sourceId)
        assertEquals("Big News", items[0].title)
        assertEquals("Summary text", items[0].summary)
        assertEquals(URI("https://n.example.com/a"), items[0].url)
        assertTrue(items[0].tags.isEmpty())
    }

    @Test
    fun `newsapi requires api key`() = runTest {
        val src = NewsApiSource(NewsApiOptions("", "q"), okTransport("{}"))
        assertFalse(src.isConfigured)
        assertFailsWith<IllegalStateException> { src.fetchLatest(5) }
    }
}
