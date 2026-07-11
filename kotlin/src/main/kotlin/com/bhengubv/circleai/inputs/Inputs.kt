// Inputs.kt
//
// Kotlin port of CircleAI.Inputs (Contracts.cs + InMemoryInputs.cs +
// NullImplementations.cs) — the C# reference is the EXACT spec. Input adapters:
// an HTML scraper, a stealth HTTP client, an MCP-side scrape wrapper, and an
// asciinema terminal-cast parser. Video ingest ships as a null default only
// (real ingest needs ffmpeg on the host — matches C#, which has no in-memory
// video impl).
//
// Fidelity notes:
//   * C# `record` -> Kotlin `data class`.
//   * C# `Uri` -> `java.net.URI`; C# `TimeSpan` -> `java.time.Duration`.
//   * C# `ValueTask` async members -> `suspend fun`.
//   * The C# scrapers take an injectable `HttpClient`; the Kotlin ports take an
//     injectable [HttpFetcher] seam (default backed by java.net.http.HttpClient)
//     so the module stays testable and free of a network dependency in tests.
//   * HTML→text stripping, title extraction, href resolution, and the rotating
//     stealth headers mirror the C# regexes / user-agent set exactly.
//   * The asciinema parser reads a header line + line-delimited [time,type,data]
//     events, keeping only "o" (output) events.

package com.bhengubv.circleai.inputs

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonPrimitive
import java.io.File
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.time.Duration
import java.util.concurrent.atomic.AtomicInteger

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/** One scraped page. Mirrors C# `ScrapedPage`. */
data class ScrapedPage(
    val url: URI,
    val text: String,
    val title: String? = null,
    val metadata: Map<String, String>? = null,
    val resolvedLinks: List<URI>? = null,
)

/** Convert a URL into markdown/text (ConvertX pattern). Mirrors C# `IWebScraper`. */
interface IWebScraper {
    val backendId: String
    suspend fun fetchAsync(url: URI): ScrapedPage
}

/** Fingerprint-avoiding HTTPS client (Scrapling pattern). Mirrors C# `IStealthHttpClient`. */
interface IStealthHttpClient {
    val backendId: String
    suspend fun getAsync(url: URI, headers: Map<String, String>? = null): ScrapedPage
}

/** Video ingest result. Mirrors C# `VideoIngestResult`. */
data class VideoIngestResult(
    val transcript: String,
    val shots: List<String>,
    val duration: Duration,
    val frameCount: Int,
)

/** Bring a video into a model-ready text stream (openvid). Mirrors C# `IVideoIngest`. */
interface IVideoIngest {
    val backendId: String
    suspend fun ingestAsync(filePath: String): VideoIngestResult
}

/** One MCP scrape job. Mirrors C# `McpScrapeJob`. */
data class McpScrapeJob(val url: String, val headers: Map<String, String>? = null)

/** MCP-side delegated scraping (mcp-web-scrape pattern). Mirrors C# `IMcpWebScrape`. */
interface IMcpWebScrape {
    val backendId: String
    suspend fun scrapeAsync(job: McpScrapeJob): ScrapedPage
}

/** One terminal-cast segment. Mirrors C# `TerminalCastSegment`. */
data class TerminalCastSegment(val offset: Duration, val text: String)

/** A parsed terminal cast. Mirrors C# `TerminalCast`. */
data class TerminalCast(val segments: List<TerminalCastSegment>, val width: Int, val height: Int)

/** Parse / replay asciinema casts (ASCILINE pattern). Mirrors C# `ITerminalCast`. */
interface ITerminalCast {
    val backendId: String
    suspend fun loadAsync(filePath: String): TerminalCast
    suspend fun renderTranscriptAsync(cast: TerminalCast): String
}

// =====================================================================
// HTTP seam (was C# injectable HttpClient)
// =====================================================================

/**
 * The HTTP-fetch seam. Production hosts back this with a real client; tests
 * inject a fake. Returns the response body as text.
 */
fun interface HttpFetcher {
    /** Fetch [url] with optional extra [headers], returning the body text. */
    suspend fun get(url: URI, headers: Map<String, String>?): String
}

/** Default [HttpFetcher] backed by java.net.http.HttpClient. */
class JdkHttpFetcher(private val client: HttpClient = HttpClient.newHttpClient()) : HttpFetcher {
    override suspend fun get(url: URI, headers: Map<String, String>?): String {
        val builder = HttpRequest.newBuilder(url).GET()
        headers?.forEach { (k, v) -> builder.header(k, v) }
        val rsp = client.send(builder.build(), HttpResponse.BodyHandlers.ofString())
        if (rsp.statusCode() / 100 != 2) {
            throw java.io.IOException("HTTP ${rsp.statusCode()} for $url")
        }
        return rsp.body()
    }
}

// =====================================================================
// In-memory / real implementations (InMemoryInputs.cs)
// =====================================================================

/** HTML scraper using an [HttpFetcher] + text extraction. Mirrors C# `HttpHtmlScraper`. */
class HttpHtmlScraper(private val fetcher: HttpFetcher = JdkHttpFetcher()) : IWebScraper {
    override val backendId: String get() = "http-html"

    override suspend fun fetchAsync(url: URI): ScrapedPage {
        val html = fetcher.get(url, null)
        var title = TITLE_RX.find(html)?.groupValues?.getOrNull(1) ?: ""
        if (title.isNotEmpty()) title = htmlDecode(title.trim())

        val stripped = SCRIPT_RX.replace(html, " ")
        var text = WS_RX.replace(TAG_RX.replace(stripped, " "), " ").trim()
        text = htmlDecode(text)

        val links = ArrayList<URI>()
        for (m in HREF_RX.findAll(html)) {
            runCatching { url.resolve(m.groupValues[1]) }.getOrNull()?.let { links.add(it) }
        }

        return ScrapedPage(url, text, title.ifEmpty { null }, null, links)
    }

    private companion object {
        val TITLE_RX = Regex("""<title>(.*?)</title>""", setOf(RegexOption.IGNORE_CASE, RegexOption.DOT_MATCHES_ALL))
        val SCRIPT_RX = Regex("""<(script|style)[^>]*>.*?</\1>""", setOf(RegexOption.IGNORE_CASE, RegexOption.DOT_MATCHES_ALL))
        val TAG_RX = Regex("""<[^>]+>""")
        val HREF_RX = Regex("""href\s*=\s*["']([^"'#]+)["']""", RegexOption.IGNORE_CASE)
        val WS_RX = Regex("""\s+""")

        fun htmlDecode(s: String): String =
            s.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">")
                .replace("&quot;", "\"").replace("&#39;", "'").replace("&apos;", "'").replace("&nbsp;", " ")
    }
}

/** Stealth HTTP client — rotating headers per call. Mirrors C# `StealthHttpClient`. */
class StealthHttpClient(private val fetcher: HttpFetcher = JdkHttpFetcher()) : IStealthHttpClient {
    private val seq = AtomicInteger(0)

    override val backendId: String get() = "stealth-http"

    override suspend fun getAsync(url: URI, headers: Map<String, String>?): ScrapedPage {
        val s = seq.incrementAndGet()
        val merged = LinkedHashMap<String, String>()
        merged["User-Agent"] = USER_AGENTS[s % USER_AGENTS.size]
        merged["Accept-Language"] = ACCEPT_LANGUAGES[s % ACCEPT_LANGUAGES.size]
        merged["Accept"] = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
        merged["Accept-Encoding"] = "gzip, deflate, br"
        merged["Cache-Control"] = "no-cache"
        merged["Connection"] = "keep-alive"
        headers?.forEach { (k, v) -> merged[k] = v }

        val body = fetcher.get(url, merged)
        return ScrapedPage(url, body)
    }

    private companion object {
        val USER_AGENTS = arrayOf(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
            "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
        )
        val ACCEPT_LANGUAGES = arrayOf("en-US,en;q=0.9", "en-GB,en;q=0.9", "en-ZA,en;q=0.9")
    }
}

/** MCP-side scrape wrapper over an [IWebScraper]. Mirrors C# `DefaultMcpWebScrape`. */
class DefaultMcpWebScrape(private val inner: IWebScraper = HttpHtmlScraper()) : IMcpWebScrape {
    override val backendId: String get() = "mcp:${inner.backendId}"

    override suspend fun scrapeAsync(job: McpScrapeJob): ScrapedPage =
        inner.fetchAsync(URI(job.url))
}

/** Asciinema v2 cast parser. Mirrors C# `AsciinemaTerminalCast`. */
class AsciinemaTerminalCast : ITerminalCast {
    override val backendId: String get() = "asciinema"

    override suspend fun loadAsync(filePath: String): TerminalCast {
        require(filePath.isNotBlank()) { "filePath required" }
        val f = File(filePath)
        if (!f.exists()) throw java.io.FileNotFoundException("cast file not found: $filePath")

        var width = 80
        var height = 24
        val segments = ArrayList<TerminalCastSegment>()

        val lines = f.readLines()
        if (lines.isEmpty()) throw java.io.IOException("empty cast file")

        // Header (optional / non-standard tolerated).
        runCatching {
            val hdr = CAST_JSON.parseToJsonElement(lines[0]) as? JsonObject
            hdr?.get("width")?.jsonPrimitive?.intOrNull?.let { width = it }
            hdr?.get("height")?.jsonPrimitive?.intOrNull?.let { height = it }
        }

        for (i in 1 until lines.size) {
            val line = lines[i]
            if (line.isBlank()) continue
            runCatching {
                val arr = CAST_JSON.parseToJsonElement(line) as? JsonArray ?: return@runCatching
                if (arr.size < 3) return@runCatching
                val t = arr[0].jsonPrimitive.doubleOrNull ?: return@runCatching
                val typ = arr[1].jsonPrimitive.contentOrNull
                val txt = arr[2].jsonPrimitive.contentOrNull ?: ""
                if (typ == "o") {
                    segments.add(TerminalCastSegment(Duration.ofNanos((t * 1_000_000_000L).toLong()), txt))
                }
            }
        }

        return TerminalCast(segments, width, height)
    }

    override suspend fun renderTranscriptAsync(cast: TerminalCast): String {
        val sb = StringBuilder()
        for (s in cast.segments) sb.append(s.text)
        return sb.toString()
    }

    private companion object {
        val CAST_JSON = Json { ignoreUnknownKeys = true }
    }
}

// =====================================================================
// Null implementations (NullImplementations.cs)
// =====================================================================

/** No-op [IWebScraper]. Mirrors C# `NullWebScraper`. */
class NullWebScraper private constructor() : IWebScraper {
    override val backendId: String get() = "null"
    override suspend fun fetchAsync(url: URI): ScrapedPage = ScrapedPage(url, "")

    companion object {
        val Instance = NullWebScraper()
    }
}

/** No-op [IStealthHttpClient]. Mirrors C# `NullStealthHttpClient`. */
class NullStealthHttpClient private constructor() : IStealthHttpClient {
    override val backendId: String get() = "null"
    override suspend fun getAsync(url: URI, headers: Map<String, String>?): ScrapedPage = ScrapedPage(url, "")

    companion object {
        val Instance = NullStealthHttpClient()
    }
}

/** No-op [IVideoIngest]. Mirrors C# `NullVideoIngest`. */
class NullVideoIngest private constructor() : IVideoIngest {
    override val backendId: String get() = "null"
    override suspend fun ingestAsync(filePath: String): VideoIngestResult =
        VideoIngestResult("", emptyList(), Duration.ZERO, 0)

    companion object {
        val Instance = NullVideoIngest()
    }
}

/** No-op [IMcpWebScrape]. Mirrors C# `NullMcpWebScrape`. */
class NullMcpWebScrape private constructor() : IMcpWebScrape {
    override val backendId: String get() = "null"
    override suspend fun scrapeAsync(job: McpScrapeJob): ScrapedPage = ScrapedPage(URI(job.url), "")

    companion object {
        val Instance = NullMcpWebScrape()
    }
}

/** No-op [ITerminalCast]. Mirrors C# `NullTerminalCast`. */
class NullTerminalCast private constructor() : ITerminalCast {
    override val backendId: String get() = "null"
    override suspend fun loadAsync(filePath: String): TerminalCast = TerminalCast(emptyList(), 80, 24)
    override suspend fun renderTranscriptAsync(cast: TerminalCast): String = ""

    companion object {
        val Instance = NullTerminalCast()
    }
}
