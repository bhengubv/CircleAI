// IntegrationNewsTests.swift
//
// Exercises the four news sources (Bluesky / Mastodon / NewsApi / Rss) against
// FakeIntegrationHttpTransport, plus the shared HTML-strip / 80-char title
// helpers and the Bluesky AT-URI → bsky.app URL derivation. Mirrors the C#
// under src/CircleAI.Integration.News/.

import XCTest
import Foundation
@testable import CircleAI

final class IntegrationNewsTests: XCTestCase {

    // ── Shared helpers ───────────────────────────────────────────────────────

    func testHtmlStrip() {
        XCTAssertEqual(IntegrationHtml.strip("<p>Hello <b>world</b></p>"), "Hello  world")
        XCTAssertEqual(IntegrationHtml.strip("  plain  "), "plain")
    }

    func testTitle80Truncation() {
        let short = "short title"
        XCTAssertEqual(IntegrationHtml.title80(short), short)
        let long = String(repeating: "a", count: 100)
        let t = IntegrationHtml.title80(long)
        XCTAssertEqual(t.count, 81) // 80 chars + ellipsis
        XCTAssertTrue(t.hasSuffix("\u{2026}"))
    }

    // ── Bluesky ──────────────────────────────────────────────────────────────

    func testBlueskySourceIdAndConfigured() {
        let s = BlueskySource(opts: BlueskyOptions(query: "climate"), http: FakeIntegrationHttpTransport())
        XCTAssertEqual(s.sourceId, "bluesky:climate")
        XCTAssertTrue(s.isConfigured)
        XCTAssertFalse(BlueskySource(opts: BlueskyOptions(query: "  "), http: FakeIntegrationHttpTransport()).isConfigured)
    }

    func testBlueskyFetchParsesPosts() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "app.bsky.feed.searchPosts", json: """
        {"posts":[
          {"uri":"at://did:plc:abc/app.bsky.feed.post/xyz123",
           "author":{"handle":"alice.bsky.social"},
           "record":{"text":"Breaking news about the climate","createdAt":"2024-10-02T12:00:00Z",
             "facets":[{"features":[{"tag":"climate"}]}]}}
        ]}
        """)
        let items = try await BlueskySource(opts: BlueskyOptions(query: "climate"), http: http).fetchLatest(max: 10)
        XCTAssertEqual(items.count, 1)
        let i = items[0]
        XCTAssertEqual(i.itemId, "at://did:plc:abc/app.bsky.feed.post/xyz123")
        XCTAssertEqual(i.sourceId, "alice.bsky.social")
        XCTAssertEqual(i.summary, "Breaking news about the climate")
        XCTAssertEqual(i.tags, ["climate"])
        XCTAssertEqual(i.url, "https://bsky.app/profile/alice.bsky.social/post/xyz123")
        XCTAssertNotEqual(i.publishedUtc, Date.distantPast)
        XCTAssertTrue(http.lastRequest?.url.contains("sort=latest") ?? false)
    }

    func testBlueskyFetchValidatesMax() async {
        let s = BlueskySource(opts: BlueskyOptions(query: "x"), http: FakeIntegrationHttpTransport())
        do { _ = try await s.fetchLatest(max: 0); XCTFail() }
        catch IntegrationError.argumentOutOfRange {} catch { XCTFail("wrong \(error)") }
    }

    func testBlueskyBuildPostUrl() {
        XCTAssertEqual(
            BlueskySource.buildPostUrl(handle: "bob.test", atUri: "at://did/app.bsky.feed.post/rk"),
            "https://bsky.app/profile/bob.test/post/rk")
        XCTAssertEqual(BlueskySource.buildPostUrl(handle: nil, atUri: "at://x/y"), "about:blank")
        XCTAssertEqual(BlueskySource.buildPostUrl(handle: "h", atUri: ""), "about:blank")
    }

    // ── Mastodon ─────────────────────────────────────────────────────────────

    func testMastodonSourceIdPublicVsHashtag() {
        let pub = MastodonSource(opts: MastodonOptions(instance: "https://mastodon.social"), http: FakeIntegrationHttpTransport())
        XCTAssertEqual(pub.sourceId, "mastodon:https://mastodon.social:public")
        let tag = MastodonSource(opts: MastodonOptions(instance: "https://mastodon.social", hashtag: "ai"), http: FakeIntegrationHttpTransport())
        XCTAssertEqual(tag.sourceId, "mastodon:https://mastodon.social:#ai")
    }

    func testMastodonFetchStripsHtmlAndReadsFields() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/api/v1/timelines/public", json: """
        [
          {"url":"https://mastodon.social/@bob/1","content":"<p>Hello <b>fediverse</b></p>",
           "created_at":"2024-10-02T12:00:00Z","account":{"acct":"bob"},
           "tags":[{"name":"intro"}]}
        ]
        """)
        let items = try await MastodonSource(opts: MastodonOptions(instance: "https://mastodon.social/"), http: http)
            .fetchLatest(max: 10)
        XCTAssertEqual(items.count, 1)
        XCTAssertEqual(items[0].summary, "Hello  fediverse") // HTML stripped
        XCTAssertEqual(items[0].sourceId, "bob")
        XCTAssertEqual(items[0].tags, ["intro"])
        XCTAssertEqual(items[0].url, "https://mastodon.social/@bob/1")
        // trailing slash trimmed → no // before /api
        XCTAssertFalse(http.lastRequest?.url.contains(".social//api") ?? true)
        XCTAssertEqual(http.lastRequest?.headers["User-Agent"], "CircleAI/1.0 (MastodonSource)")
    }

    func testMastodonHashtagPathAndBearer() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/api/v1/timelines/tag/ai", json: "[]")
        _ = try await MastodonSource(opts: MastodonOptions(instance: "https://m.social", hashtag: "ai", accessToken: "tok"), http: http)
            .fetchLatest(max: 5)
        let req = try XCTUnwrap(http.lastRequest)
        XCTAssertTrue(req.url.contains("/api/v1/timelines/tag/ai"))
        XCTAssertEqual(req.headers["Authorization"], "Bearer tok")
    }

    // ── NewsAPI ──────────────────────────────────────────────────────────────

    func testNewsApiSourceIdAndConfigured() {
        let s = NewsApiSource(opts: NewsApiOptions(apiKey: "k", query: "tech"), http: FakeIntegrationHttpTransport())
        XCTAssertEqual(s.sourceId, "newsapi:tech")
        XCTAssertTrue(s.isConfigured)
        XCTAssertFalse(NewsApiSource(opts: NewsApiOptions(apiKey: "", query: "t"), http: FakeIntegrationHttpTransport()).isConfigured)
    }

    func testNewsApiFetchParsesArticles() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "newsapi.org/v2/everything", json: """
        {"articles":[
          {"title":"AI advances","description":"A summary","url":"https://news.example.com/ai",
           "publishedAt":"2024-10-02T12:00:00Z","source":{"name":"Example News"}}
        ]}
        """)
        let items = try await NewsApiSource(opts: NewsApiOptions(apiKey: "secret", query: "ai"), http: http)
            .fetchLatest(max: 10)
        XCTAssertEqual(items.count, 1)
        XCTAssertEqual(items[0].title, "AI advances")
        XCTAssertEqual(items[0].summary, "A summary")
        XCTAssertEqual(items[0].sourceId, "Example News")
        XCTAssertEqual(items[0].url, "https://news.example.com/ai")
        let req = try XCTUnwrap(http.lastRequest)
        XCTAssertEqual(req.headers["X-Api-Key"], "secret")
        XCTAssertTrue(req.url.contains("sortBy=publishedAt"))
    }

    func testNewsApiThrowsWhenNotConfigured() async {
        let s = NewsApiSource(opts: NewsApiOptions(apiKey: "", query: "x"), http: FakeIntegrationHttpTransport())
        do { _ = try await s.fetchLatest(max: 5); XCTFail() }
        catch IntegrationError.invalidOperation {} catch { XCTFail("wrong \(error)") }
    }

    // ── RSS / Atom ───────────────────────────────────────────────────────────

    func testRssSourceIdDefaultsToHost() {
        let s = RssNewsSource(opts: RssOptions(feedUrl: URL(string: "https://blog.example.com/feed.xml")!), http: FakeIntegrationHttpTransport())
        XCTAssertEqual(s.sourceId, "blog.example.com")
        XCTAssertTrue(s.isConfigured)
        let named = RssNewsSource(opts: RssOptions(feedUrl: URL(string: "https://x/f")!, sourceId: "MyFeed"), http: FakeIntegrationHttpTransport())
        XCTAssertEqual(named.sourceId, "MyFeed")
    }

    func testRss20Parse() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/feed.xml", text: """
        <?xml version="1.0"?>
        <rss version="2.0"><channel>
          <item>
            <title>First Post</title>
            <link>https://blog.example.com/1</link>
            <guid>guid-1</guid>
            <pubDate>Wed, 02 Oct 2024 13:00:00 GMT</pubDate>
            <description>&lt;p&gt;Body one&lt;/p&gt;</description>
            <category>tech</category>
            <category>news</category>
          </item>
          <item>
            <title>Second Post</title>
            <link>https://blog.example.com/2</link>
            <description>Body two</description>
          </item>
        </channel></rss>
        """)
        let items = try await RssNewsSource(opts: RssOptions(feedUrl: URL(string: "https://blog.example.com/feed.xml")!), http: http)
            .fetchLatest(max: 10)
        XCTAssertEqual(items.count, 2)
        XCTAssertEqual(items[0].title, "First Post")
        XCTAssertEqual(items[0].itemId, "guid-1")
        XCTAssertEqual(items[0].summary, "Body one") // HTML entities decoded then stripped
        XCTAssertEqual(items[0].tags, ["tech", "news"])
        XCTAssertEqual(items[0].url, "https://blog.example.com/1")
        XCTAssertNotEqual(items[0].publishedUtc, Date.distantPast)
        // Second item: no guid → itemId falls back to link.
        XCTAssertEqual(items[1].itemId, "https://blog.example.com/2")
    }

    func testAtomParse() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/atom", text: """
        <?xml version="1.0"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <title>Atom Entry</title>
            <link href="https://site.example.com/post"/>
            <id>urn:uuid:1234</id>
            <updated>2024-10-02T13:00:00Z</updated>
            <summary>An atom summary</summary>
            <category term="release"/>
          </entry>
        </feed>
        """)
        let items = try await RssNewsSource(opts: RssOptions(feedUrl: URL(string: "https://site.example.com/atom")!), http: http)
            .fetchLatest(max: 10)
        XCTAssertEqual(items.count, 1)
        XCTAssertEqual(items[0].title, "Atom Entry")
        XCTAssertEqual(items[0].itemId, "urn:uuid:1234")
        XCTAssertEqual(items[0].summary, "An atom summary")
        XCTAssertEqual(items[0].url, "https://site.example.com/post")
        XCTAssertEqual(items[0].tags, ["release"])
        XCTAssertNotEqual(items[0].publishedUtc, Date.distantPast)
    }

    func testRssRespectsMaxLimit() async throws {
        let http = FakeIntegrationHttpTransport()
        http.on(.get, urlContains: "/feed", text: """
        <?xml version="1.0"?><rss version="2.0"><channel>
          <item><title>A</title><link>https://x/a</link></item>
          <item><title>B</title><link>https://x/b</link></item>
          <item><title>C</title><link>https://x/c</link></item>
        </channel></rss>
        """)
        let items = try await RssNewsSource(opts: RssOptions(feedUrl: URL(string: "https://x/feed")!), http: http)
            .fetchLatest(max: 2)
        XCTAssertEqual(items.count, 2)
        XCTAssertEqual(items.map { $0.title }, ["A", "B"])
    }

    func testRssValidatesMax() async {
        let s = RssNewsSource(opts: RssOptions(feedUrl: URL(string: "https://x/f")!), http: FakeIntegrationHttpTransport())
        do { _ = try await s.fetchLatest(max: 0); XCTFail() }
        catch IntegrationError.argumentOutOfRange {} catch { XCTFail("wrong \(error)") }
    }
}
