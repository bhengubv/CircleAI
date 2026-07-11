// IntegrationNews.kt
//
// Kotlin port of CircleAI.Integration.News (RssNewsSource.cs + BlueskySource.cs
// + MastodonSource.cs + NewsApiSource.cs) — the C# reference is the EXACT spec.
// Four [INewsSource] implementations:
//   * RSS 2.0 / Atom 1.0 reader (one feed = one source).
//   * Bluesky AT-protocol searchPosts reader.
//   * Mastodon public / hashtag timeline reader.
//   * newsapi.org / gnews.io "articles" adapter.
//
// Fidelity notes:
//   * The network is injected via [HttpTransport]; URL/verb/header composition
//     and the response parsers mirror the C# code exactly.
//   * RSS parsing uses the JDK DOM (mirrors `XDocument`): <item> for RSS and
//     Atom <entry>; HTML stripped via the same "<[^>]+>" -> " " regex; missing
//     absolute links -> about:blank; guid falls back to link.
//   * Bluesky: title = text truncated to 80 chars + "…"; sourceId = author
//     handle else "bluesky:{query}"; post URL rebuilt from at:// rkey.
//   * Mastodon: content HTML stripped; title truncation; sourceId = acct.
//   * NewsApi: requires an API key; description as summary; tags empty.

package com.bhengubv.circleai.integrationnews

import com.bhengubv.circleai.integration.HttpRequest
import com.bhengubv.circleai.integration.HttpTransport
import com.bhengubv.circleai.integration.HttpVerb
import com.bhengubv.circleai.integration.INewsSource
import com.bhengubv.circleai.integration.NewsItem
import com.bhengubv.circleai.integration.ensureSuccess
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import org.w3c.dom.Element
import org.w3c.dom.Node
import java.io.ByteArrayInputStream
import java.net.URI
import java.net.URLEncoder
import java.time.Instant
import java.time.OffsetDateTime
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import javax.xml.parsers.DocumentBuilderFactory
import kotlin.math.min

internal val NEWS_JSON = Json { ignoreUnknownKeys = true; isLenient = true }
internal val ABOUT_BLANK: URI = URI.create("about:blank")
private val RX_HTML = Regex("<[^>]+>")

internal fun newsEsc(s: String): String =
    URLEncoder.encode(s, Charsets.UTF_8).replace("+", "%20")

internal fun stripHtml(html: String): String = RX_HTML.replace(html, " ").trim()

/** Parse a date as UTC; empty/unparseable -> Instant.MIN (mirrors the C# default). */
internal fun parseNewsDate(s: String?): Instant {
    if (s.isNullOrBlank()) return Instant.MIN
    runCatching { return OffsetDateTime.parse(s).toInstant() }
    runCatching { return ZonedDateTime.parse(s).toInstant() }
    // RFC 1123 (RSS pubDate), e.g. "Wed, 02 Oct 2024 13:00:00 GMT".
    runCatching { return ZonedDateTime.parse(s, DateTimeFormatter.RFC_1123_DATE_TIME).toInstant() }
    runCatching { return java.time.LocalDateTime.parse(s).atOffset(java.time.ZoneOffset.UTC).toInstant() }
    return Instant.MIN
}

internal fun toUriOrBlank(link: String): URI =
    runCatching { val u = URI(link); if (u.isAbsolute) u else ABOUT_BLANK }.getOrDefault(ABOUT_BLANK)

internal fun JsonObject.textOrNull(key: String): String? = (this[key] as? JsonPrimitive)?.content

// =====================================================================
// RSS / Atom (RssNewsSource.cs)
// =====================================================================

/** RSS/Atom source config. Mirrors C# `RssOptions`. */
data class RssOptions(val feedUrl: URI, val sourceId: String? = null)

/** Generic RSS 2.0 / Atom 1.0 reader. Mirrors C# `RssNewsSource`. */
class RssNewsSource(
    private val opts: RssOptions,
    private val http: HttpTransport,
) : INewsSource {

    override val sourceId: String get() = opts.sourceId ?: (opts.feedUrl.host ?: "")
    override val isConfigured: Boolean get() = true

    override suspend fun fetchLatest(max: Int): List<NewsItem> {
        require(max > 0) { "max" }
        val resp = http.send(HttpRequest(HttpVerb.GET, opts.feedUrl.toString())).ensureSuccess()
        val doc = parseXml(resp.body)
        val items = parseRss(doc, sourceId) + parseAtom(doc, sourceId)
        return items.take(max)
    }

    private companion object {
        private const val ATOM_NS = "http://www.w3.org/2005/Atom"

        fun parseXml(xml: String): org.w3c.dom.Document {
            val factory = DocumentBuilderFactory.newInstance().apply {
                isNamespaceAware = true
                // Harden against XXE (best-effort; ignore on parsers lacking a feature).
                runCatching { setFeature("http://apache.org/xml/features/nonvalidating/load-external-dtd", false) }
                runCatching { setFeature("http://xml.org/sax/features/external-general-entities", false) }
                runCatching { setFeature("http://xml.org/sax/features/external-parameter-entities", false) }
            }
            return factory.newDocumentBuilder().parse(ByteArrayInputStream(xml.toByteArray(Charsets.UTF_8)))
        }

        fun parseRss(doc: org.w3c.dom.Document, sourceId: String): List<NewsItem> {
            val out = ArrayList<NewsItem>()
            val items = doc.getElementsByTagName("item")
            for (i in 0 until items.length) {
                val item = items.item(i) as? Element ?: continue
                val title = childText(item, "title")
                val link = childText(item, "link")
                val pub = childTextOrNull(item, "pubDate")
                val desc = childText(item, "description")
                val guid = childTextOrNull(item, "guid")?.ifEmpty { null } ?: link
                val tags = childTexts(item, "category")
                out += NewsItem(
                    itemId = guid,
                    sourceId = sourceId,
                    title = title,
                    summary = stripHtml(desc),
                    url = toUriOrBlank(link),
                    publishedUtc = parseNewsDate(pub),
                    tags = tags,
                )
            }
            return out
        }

        fun parseAtom(doc: org.w3c.dom.Document, sourceId: String): List<NewsItem> {
            val out = ArrayList<NewsItem>()
            val entries = doc.getElementsByTagNameNS(ATOM_NS, "entry")
            for (i in 0 until entries.length) {
                val entry = entries.item(i) as? Element ?: continue
                val title = childTextNS(entry, ATOM_NS, "title")
                val link = firstLinkHref(entry)
                val pub = childTextNSOrNull(entry, ATOM_NS, "updated")
                    ?: childTextNSOrNull(entry, ATOM_NS, "published")
                val desc = childTextNSOrNull(entry, ATOM_NS, "summary")
                    ?: childTextNSOrNull(entry, ATOM_NS, "content") ?: ""
                val guid = childTextNSOrNull(entry, ATOM_NS, "id")?.ifEmpty { null } ?: link
                val tags = categoryTerms(entry)
                out += NewsItem(
                    itemId = guid,
                    sourceId = sourceId,
                    title = title,
                    summary = stripHtml(desc),
                    url = toUriOrBlank(link),
                    publishedUtc = parseNewsDate(pub),
                    tags = tags,
                )
            }
            return out
        }

        private fun directChildren(parent: Element, ns: String?, local: String): List<Element> {
            val out = ArrayList<Element>()
            val kids = parent.childNodes
            for (i in 0 until kids.length) {
                val n = kids.item(i)
                if (n.nodeType != Node.ELEMENT_NODE) continue
                val e = n as Element
                if (ns == null) {
                    if (e.localName == local || e.nodeName == local) out += e
                } else if (ns == e.namespaceURI && e.localName == local) {
                    out += e
                }
            }
            return out
        }

        fun childText(parent: Element, name: String): String =
            directChildren(parent, null, name).firstOrNull()?.textContent?.trim() ?: ""

        fun childTextOrNull(parent: Element, name: String): String? =
            directChildren(parent, null, name).firstOrNull()?.textContent?.trim()

        fun childTexts(parent: Element, name: String): List<String> =
            directChildren(parent, null, name).map { it.textContent.trim() }

        fun childTextNS(parent: Element, ns: String, name: String): String =
            directChildren(parent, ns, name).firstOrNull()?.textContent?.trim() ?: ""

        fun childTextNSOrNull(parent: Element, ns: String, name: String): String? =
            directChildren(parent, ns, name).firstOrNull()?.textContent?.trim()

        fun firstLinkHref(entry: Element): String =
            directChildren(entry, ATOM_NS, "link").firstOrNull()?.getAttribute("href")?.trim() ?: ""

        fun categoryTerms(entry: Element): List<String> =
            directChildren(entry, ATOM_NS, "category")
                .map { it.getAttribute("term").trim() }
                .filter { it.isNotEmpty() }
    }
}

// =====================================================================
// Bluesky (BlueskySource.cs)
// =====================================================================

/** Bluesky source config. Mirrors C# `BlueskyOptions`. */
data class BlueskyOptions(val query: String, val host: String = "https://public.api.bsky.app")

/** Bluesky AT-protocol searchPosts reader. Mirrors C# `BlueskySource`. */
class BlueskySource(
    private val opts: BlueskyOptions,
    private val http: HttpTransport,
) : INewsSource {

    override val sourceId: String get() = "bluesky:${opts.query}"
    override val isConfigured: Boolean get() = opts.query.isNotBlank()

    override suspend fun fetchLatest(max: Int): List<NewsItem> {
        require(max > 0) { "max" }
        val url = "${opts.host}/xrpc/app.bsky.feed.searchPosts" +
            "?q=${newsEsc(opts.query)}&limit=${min(max, 100)}&sort=latest"
        val resp = http.send(HttpRequest(HttpVerb.GET, url)).ensureSuccess()
        val root = NEWS_JSON.parseToJsonElement(resp.body) as? JsonObject ?: return emptyList()

        val list = ArrayList<NewsItem>()
        val arr = root["posts"] as? JsonArray ?: return list
        for (p in arr) {
            val post = p as? JsonObject ?: continue
            val uri = post.textOrNull("uri") ?: ""
            val record = post["record"] as? JsonObject
            val text = record?.textOrNull("text") ?: ""
            val ts = record?.textOrNull("createdAt")
            val author = (post["author"] as? JsonObject)?.textOrNull("handle")
            val tags = ArrayList<String>()
            (record?.get("facets") as? JsonArray)?.forEach { f ->
                ((f as? JsonObject)?.get("features") as? JsonArray)?.forEach { feat ->
                    (feat as? JsonObject)?.textOrNull("tag")?.let { tags += it }
                }
            }
            list += NewsItem(
                itemId = uri,
                sourceId = author ?: sourceId,
                title = truncate80(text),
                summary = text,
                url = buildPostUrl(author, uri),
                publishedUtc = parseNewsDate(ts),
                tags = tags,
            )
        }
        return list
    }

    private companion object {
        fun buildPostUrl(handle: String?, atUri: String): URI {
            if (handle.isNullOrBlank() || atUri.isBlank()) return ABOUT_BLANK
            val idx = atUri.lastIndexOf('/')
            if (idx < 0 || idx == atUri.length - 1) return ABOUT_BLANK
            val rkey = atUri.substring(idx + 1)
            return runCatching { URI("https://bsky.app/profile/$handle/post/$rkey") }.getOrDefault(ABOUT_BLANK)
        }
    }
}

// =====================================================================
// Mastodon (MastodonSource.cs)
// =====================================================================

/** Mastodon source config. Mirrors C# `MastodonOptions`. */
data class MastodonOptions(val instance: String, val hashtag: String? = null, val accessToken: String? = null)

/** Mastodon public / hashtag timeline reader. Mirrors C# `MastodonSource`. */
class MastodonSource(
    private val opts: MastodonOptions,
    private val http: HttpTransport,
) : INewsSource {

    override val sourceId: String
        get() = if (opts.hashtag.isNullOrEmpty()) {
            "mastodon:${opts.instance}:public"
        } else {
            "mastodon:${opts.instance}:#${opts.hashtag}"
        }
    override val isConfigured: Boolean get() = opts.instance.isNotBlank()

    override suspend fun fetchLatest(max: Int): List<NewsItem> {
        require(max > 0) { "max" }
        val path = if (opts.hashtag.isNullOrEmpty()) {
            "/api/v1/timelines/public?limit=${min(max, 40)}"
        } else {
            "/api/v1/timelines/tag/${newsEsc(opts.hashtag)}?limit=${min(max, 40)}"
        }
        val headers = HashMap<String, String>()
        if (!opts.accessToken.isNullOrBlank()) headers["Authorization"] = "Bearer ${opts.accessToken}"
        headers["User-Agent"] = "CircleAI/1.0 (MastodonSource)"
        val resp = http.send(HttpRequest(HttpVerb.GET, opts.instance.trimEnd('/') + path, headers)).ensureSuccess()
        val root = NEWS_JSON.parseToJsonElement(resp.body) as? JsonArray ?: return emptyList()

        val list = ArrayList<NewsItem>()
        for (s in root) {
            val o = s as? JsonObject ?: continue
            val url = o.textOrNull("url") ?: ""
            val contentHtml = o.textOrNull("content") ?: ""
            val pub = o.textOrNull("created_at")
            val tags = ArrayList<String>()
            (o["tags"] as? JsonArray)?.forEach { tg -> (tg as? JsonObject)?.textOrNull("name")?.let { tags += it } }
            val acct = (o["account"] as? JsonObject)?.textOrNull("acct")
            val text = stripHtml(contentHtml)
            list += NewsItem(
                itemId = url,
                sourceId = acct ?: sourceId,
                title = truncate80(text),
                summary = text,
                url = toUriOrBlank(url),
                publishedUtc = parseNewsDate(pub),
                tags = tags,
            )
        }
        return list
    }
}

// =====================================================================
// NewsAPI (NewsApiSource.cs)
// =====================================================================

/** NewsAPI source config. Mirrors C# `NewsApiOptions`. */
data class NewsApiOptions(
    val apiKey: String,
    val query: String,
    val endpoint: String = "https://newsapi.org/v2/everything",
)

/** newsapi.org / gnews.io "articles" adapter. Mirrors C# `NewsApiSource`. */
class NewsApiSource(
    private val opts: NewsApiOptions,
    private val http: HttpTransport,
) : INewsSource {

    override val sourceId: String get() = "newsapi:${opts.query}"
    override val isConfigured: Boolean get() = opts.apiKey.isNotBlank()

    override suspend fun fetchLatest(max: Int): List<NewsItem> {
        require(max > 0) { "max" }
        if (!isConfigured) error("NewsAPI key not configured.")
        val url = "${opts.endpoint}?q=${newsEsc(opts.query)}&pageSize=${min(max, 100)}&sortBy=publishedAt&language=en"
        val headers = mapOf("X-Api-Key" to opts.apiKey, "User-Agent" to "CircleAI/1.0 (NewsApiSource)")
        val resp = http.send(HttpRequest(HttpVerb.GET, url, headers)).ensureSuccess()
        val root = NEWS_JSON.parseToJsonElement(resp.body) as? JsonObject ?: return emptyList()

        val list = ArrayList<NewsItem>()
        val arr = root["articles"] as? JsonArray ?: return list
        for (a in arr) {
            val o = a as? JsonObject ?: continue
            val title = o.textOrNull("title") ?: ""
            val desc = o.textOrNull("description") ?: ""
            val url2 = o.textOrNull("url") ?: ""
            val pub = o.textOrNull("publishedAt")
            val src = (o["source"] as? JsonObject)?.textOrNull("name")
            list += NewsItem(
                itemId = url2,
                sourceId = src ?: sourceId,
                title = title,
                summary = desc,
                url = toUriOrBlank(url2),
                publishedUtc = parseNewsDate(pub),
                tags = emptyList(),
            )
        }
        return list
    }
}

// ── shared ─────────────────────────────────────────────────────────────────

/** Truncate to 80 chars + "…", matching the C# `text[..80] + "…"`. */
internal fun truncate80(text: String): String =
    if (text.length > 80) text.substring(0, 80) + "…" else text
