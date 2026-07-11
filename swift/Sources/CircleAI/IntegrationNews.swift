// IntegrationNews.swift
//
// Port of the CircleAI.Integration.News vertical (collapsing the C# folder's
// four files into one):
//   • BlueskySource.cs  → BlueskyOptions + BlueskySource
//   • MastodonSource.cs → MastodonOptions + MastodonSource
//   • NewsApiSource.cs  → NewsApiOptions + NewsApiSource
//   • RssNewsSource.cs  → RssOptions + RssNewsSource
//
// All four are `INewsSource`s. The raw HTTP is the injected
// `IIntegrationHttpTransport`; every URL, header (X-Api-Key, User-Agent,
// Bearer), the HTML-strip regex, the 80-char title truncation, the AT-URI →
// bsky.app URL derivation, and the RSS/Atom XML parse are ported verbatim and
// asserted against `FakeIntegrationHttpTransport` (no real calls).

import Foundation

// MARK: - HTML strip (shared)

/// `Regex.Replace(html, "<[^>]+>", " ").Trim()` — the HTML-tag strip both the
/// Mastodon and RSS sources use.
enum IntegrationHtml {
    static func strip(_ html: String) -> String {
        let ns = html as NSString
        guard let rx = try? NSRegularExpression(pattern: "<[^>]+>", options: []) else {
            return html.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        let replaced = rx.stringByReplacingMatches(
            in: html, options: [], range: NSRange(location: 0, length: ns.length), withTemplate: " ")
        return replaced.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Truncate to 80 chars + "…" exactly like the C# `text.Length > 80 ?
    /// text[..80] + "…" : text`. Uses UTF-16 length + prefix to match .NET's
    /// `string.Length` / substring semantics.
    static func title80(_ text: String) -> String {
        let ns = text as NSString
        if ns.length > 80 {
            return ns.substring(to: 80) + "\u{2026}"
        }
        return text
    }
}

// MARK: - Bluesky

/// Bluesky source config. Port of the C# `BlueskyOptions` record.
public struct BlueskyOptions: Sendable, Equatable {
    /// Search query.
    public let query: String
    /// AppView host. Default the public API.
    public let host: String
    public init(query: String, host: String = "https://public.api.bsky.app") {
        self.query = query
        self.host = host
    }
}

/// Bluesky AT-protocol `app.bsky.feed.searchPosts` reader (`INewsSource`). Port
/// of the C# `BlueskySource`.
public final class BlueskySource: INewsSource, @unchecked Sendable {
    private let http: IIntegrationHttpTransport
    private let opts: BlueskyOptions

    public init(opts: BlueskyOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
    }

    public var sourceId: String { "bluesky:\(opts.query)" }
    public var isConfigured: Bool { !opts.query.isBlank }

    public func fetchLatest(max: Int) async throws -> [NewsItem] {
        if max <= 0 { throw IntegrationError.argumentOutOfRange("max") }
        let url = "\(opts.host)/xrpc/app.bsky.feed.searchPosts"
            + "?q=\(IntegrationUri.escapeDataString(opts.query))&limit=\(min(max, 100))&sort=latest"
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: url))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)

        var list: [NewsItem] = []
        guard let arr = IntegrationJson.array(doc, "posts") else { return list }
        for case let post as [String: Any] in arr {
            let uri = IntegrationJson.string(post, "uri") ?? ""
            let record = IntegrationJson.object(post, "record")
            let text = record.flatMap { IntegrationJson.string($0, "text") } ?? ""
            let ts = record.flatMap { IntegrationJson.string($0, "createdAt") }
            var author: String? = nil
            if let a = IntegrationJson.object(post, "author") { author = IntegrationJson.string(a, "handle") }
            var tags: [String] = []
            if let record, let facets = IntegrationJson.array(record, "facets") {
                for case let f as [String: Any] in facets {
                    if let feats = IntegrationJson.array(f, "features") {
                        for case let feat as [String: Any] in feats {
                            if let tag = IntegrationJson.string(feat, "tag") { tags.append(tag) }
                        }
                    }
                }
            }
            list.append(NewsItem(
                itemId: uri,
                sourceId: author ?? sourceId,
                title: IntegrationHtml.title80(text),
                summary: text,
                url: Self.buildPostUrl(handle: author, atUri: uri),
                publishedUtc: IntegrationDates.parseUtc(ts),
                tags: tags))
        }
        return list
    }

    /// at://did:plc:.../app.bsky.feed.post/<rkey> → https://bsky.app/profile/
    /// <handle>/post/<rkey>. Verbatim from the C# `BuildPostUrl`. Returns the
    /// URL string (the `NewsItem.url` field is a `String`), or "about:blank".
    static func buildPostUrl(handle: String?, atUri: String) -> String {
        guard let handle, !handle.isBlank, !atUri.isBlank else { return "about:blank" }
        let ns = atUri as NSString
        let idx = ns.range(of: "/", options: .backwards).location
        if idx == NSNotFound || idx == ns.length - 1 { return "about:blank" }
        let rkey = ns.substring(from: idx + 1)
        return "https://bsky.app/profile/\(handle)/post/\(rkey)"
    }
}

// MARK: - Mastodon

/// Mastodon source config. Port of the C# `MastodonOptions` record.
public struct MastodonOptions: Sendable, Equatable {
    /// Instance base URL (e.g. "https://mastodon.social").
    public let instance: String
    /// Optional hashtag (public timeline when nil/empty).
    public let hashtag: String?
    /// Optional access token (Bearer) for authenticated reads.
    public let accessToken: String?
    public init(instance: String, hashtag: String? = nil, accessToken: String? = nil) {
        self.instance = instance
        self.hashtag = hashtag
        self.accessToken = accessToken
    }
}

/// Mastodon public/hashtag timeline reader (`INewsSource`). Port of the C#
/// `MastodonSource`.
public final class MastodonSource: INewsSource, @unchecked Sendable {
    private let http: IIntegrationHttpTransport
    private let opts: MastodonOptions

    public init(opts: MastodonOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
    }

    public var sourceId: String {
        (opts.hashtag ?? "").isEmpty
            ? "mastodon:\(opts.instance):public"
            : "mastodon:\(opts.instance):#\(opts.hashtag!)"
    }
    public var isConfigured: Bool { !opts.instance.isBlank }

    public func fetchLatest(max: Int) async throws -> [NewsItem] {
        if max <= 0 { throw IntegrationError.argumentOutOfRange("max") }
        let hashtag = opts.hashtag ?? ""
        let path = hashtag.isEmpty
            ? "/api/v1/timelines/public?limit=\(min(max, 40))"
            : "/api/v1/timelines/tag/\(IntegrationUri.escapeDataString(hashtag))?limit=\(min(max, 40))"
        var instance = opts.instance
        while instance.hasSuffix("/") { instance.removeLast() } // TrimEnd('/')

        var headers = ["User-Agent": "CircleAI/1.0 (MastodonSource)"]
        if let token = opts.accessToken, !token.isBlank { headers["Authorization"] = "Bearer \(token)" }

        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: instance + path, headers: headers))
        try resp.ensureSuccess()
        let arr = try IntegrationJson.parseArray(resp.body)

        var list: [NewsItem] = []
        for case let s as [String: Any] in arr {
            let url = IntegrationJson.string(s, "url") ?? ""
            let contentHtml = IntegrationJson.string(s, "content") ?? ""
            let pub = IntegrationJson.string(s, "created_at")
            var tags: [String] = []
            if let tagsArr = IntegrationJson.array(s, "tags") {
                for case let tg as [String: Any] in tagsArr {
                    if let tn = IntegrationJson.string(tg, "name") { tags.append(tn) }
                }
            }
            var acct: String? = nil
            if let a = IntegrationJson.object(s, "account") { acct = IntegrationJson.string(a, "acct") }
            let text = IntegrationHtml.strip(contentHtml)
            list.append(NewsItem(
                itemId: url,
                sourceId: acct ?? sourceId,
                title: IntegrationHtml.title80(text),
                summary: text,
                url: IntegrationUri.absoluteOrBlankString(url),
                publishedUtc: IntegrationDates.parseUtc(pub),
                tags: tags))
        }
        return list
    }
}

// MARK: - NewsAPI / GNews

/// NewsAPI source config. Port of the C# `NewsApiOptions` record.
public struct NewsApiOptions: Sendable, Equatable {
    /// API key.
    public let apiKey: String
    /// Search query.
    public let query: String
    /// Endpoint. Default newsapi.org /v2/everything.
    public let endpoint: String
    public init(apiKey: String, query: String, endpoint: String = "https://newsapi.org/v2/everything") {
        self.apiKey = apiKey
        self.query = query
        self.endpoint = endpoint
    }
}

/// newsapi.org / gnews.io style `INewsSource` (both use the "articles" array
/// shape). Port of the C# `NewsApiSource`.
public final class NewsApiSource: INewsSource, @unchecked Sendable {
    private let http: IIntegrationHttpTransport
    private let opts: NewsApiOptions

    public init(opts: NewsApiOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
    }

    public var sourceId: String { "newsapi:\(opts.query)" }
    public var isConfigured: Bool { !opts.apiKey.isBlank }

    public func fetchLatest(max: Int) async throws -> [NewsItem] {
        if max <= 0 { throw IntegrationError.argumentOutOfRange("max") }
        if !isConfigured { throw IntegrationError.invalidOperation("NewsAPI key not configured.") }
        let url = "\(opts.endpoint)?q=\(IntegrationUri.escapeDataString(opts.query))&pageSize=\(min(max, 100))&sortBy=publishedAt&language=en"
        let headers = [
            "X-Api-Key": opts.apiKey,
            "User-Agent": "CircleAI/1.0 (NewsApiSource)",
        ]
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: url, headers: headers))
        try resp.ensureSuccess()
        let doc = try IntegrationJson.parseObject(resp.body)

        var list: [NewsItem] = []
        guard let arr = IntegrationJson.array(doc, "articles") else { return list }
        for case let a as [String: Any] in arr {
            let title = IntegrationJson.string(a, "title") ?? ""
            let desc = IntegrationJson.string(a, "description") ?? ""
            let url2 = IntegrationJson.string(a, "url") ?? ""
            let pub = IntegrationJson.string(a, "publishedAt")
            var src: String? = nil
            if let s = IntegrationJson.object(a, "source") { src = IntegrationJson.string(s, "name") }
            list.append(NewsItem(
                itemId: url2,
                sourceId: src ?? sourceId,
                title: title,
                summary: desc,
                url: IntegrationUri.absoluteOrBlankString(url2),
                publishedUtc: IntegrationDates.parseUtc(pub),
                tags: []))
        }
        return list
    }
}

// MARK: - RSS / Atom

/// RSS source config. Port of the C# `RssOptions` record.
public struct RssOptions: Sendable, Equatable {
    /// Feed URL.
    public let feedUrl: URL
    /// Optional source id override (defaults to the feed host).
    public let sourceId: String?
    public init(feedUrl: URL, sourceId: String? = nil) {
        self.feedUrl = feedUrl
        self.sourceId = sourceId
    }
}

/// Generic RSS 2.0 / Atom 1.0 reader (`INewsSource`). Port of the C#
/// `RssNewsSource`. One feed = one source. The XML parse is done with a small
/// event-driven `XMLParser` that reproduces the C# `XDocument.Descendants`
/// traversal for the `item` (RSS) and Atom `entry` shapes.
public final class RssNewsSource: INewsSource, @unchecked Sendable {
    private let http: IIntegrationHttpTransport
    private let opts: RssOptions

    public init(opts: RssOptions, http: IIntegrationHttpTransport) {
        self.opts = opts
        self.http = http
    }

    public var sourceId: String { opts.sourceId ?? (opts.feedUrl.host ?? "") }
    public var isConfigured: Bool { true }

    public func fetchLatest(max: Int) async throws -> [NewsItem] {
        if max <= 0 { throw IntegrationError.argumentOutOfRange("max") }
        let resp = try await http.send(IntegrationHttpRequest(method: .get, url: opts.feedUrl.absoluteString))
        try resp.ensureSuccess()
        let xml = resp.bodyString
        // C#: ParseRss(...).Concat(ParseAtom(...)).Take(max).
        let items = (Self.parseRss(xml, sourceId: sourceId) + Self.parseAtom(xml, sourceId: sourceId)).prefix(max)
        return Array(items)
    }

    static func parseRss(_ xml: String, sourceId: String) -> [NewsItem] {
        let parser = RssFeedXMLParser(mode: .rss)
        let nodes = parser.parse(xml)
        return nodes.map { node in
            let link = node["link"] ?? ""
            let guid = node["guid"] ?? link
            return NewsItem(
                itemId: guid,
                sourceId: sourceId,
                title: node["title"] ?? "",
                summary: IntegrationHtml.strip(node["description"] ?? ""),
                url: IntegrationUri.absoluteOrBlankString(link),
                publishedUtc: parseDate(node["pubDate"]),
                tags: node.categories)
        }
    }

    static func parseAtom(_ xml: String, sourceId: String) -> [NewsItem] {
        let parser = RssFeedXMLParser(mode: .atom)
        let nodes = parser.parse(xml)
        return nodes.map { node in
            let link = node["link"] ?? ""
            let guid = node["id"] ?? link
            // updated ?? published for the date.
            let pub = node["updated"] ?? node["published"]
            // summary ?? content for the description.
            let desc = node["summary"] ?? node["content"] ?? ""
            return NewsItem(
                itemId: guid,
                sourceId: sourceId,
                title: node["title"] ?? "",
                summary: IntegrationHtml.strip(desc),
                url: IntegrationUri.absoluteOrBlankString(link),
                publishedUtc: parseDate(pub),
                tags: node.categories)
        }
    }

    /// `DateTimeOffset.TryParse(... AssumeUniversal).ToUniversalTime()` else
    /// MinValue. Handles RFC-1123 (RSS pubDate) and ISO-8601 (Atom updated).
    static func parseDate(_ s: String?) -> Date {
        IntegrationDates.parseUtc(s)
    }
}

// MARK: - RSS/Atom XML parsing

/// A single parsed feed node (one `<item>` or one Atom `<entry>`): scalar child
/// elements by local name, plus the collected category values.
struct RssFeedNode {
    var fields: [String: String] = [:]
    var categories: [String] = []
    subscript(_ key: String) -> String? { fields[key] }
}

/// Minimal event-driven RSS/Atom parser over `XMLParser`, reproducing the C#
/// `XDocument.Descendants("item")` / `Descendants(atom+"entry")` traversal for
/// the fields the source reads. Namespace prefixes are ignored (local names are
/// keyed), which matches the C# element-name lookups for well-formed feeds.
final class RssFeedXMLParser: NSObject, XMLParserDelegate {
    enum Mode { case rss, atom }

    private let mode: Mode
    private var nodes: [RssFeedNode] = []
    private var inItem = false
    private var current = RssFeedNode()
    private var text = ""
    private var currentElement = ""
    private var atomLinkCaptured = false

    init(mode: Mode) {
        self.mode = mode
    }

    func parse(_ xml: String) -> [RssFeedNode] {
        guard let data = xml.data(using: .utf8) else { return [] }
        let parser = XMLParser(data: data)
        parser.delegate = self
        parser.shouldProcessNamespaces = false
        _ = parser.parse()
        return nodes
    }

    private func localName(_ name: String) -> String {
        if let idx = name.firstIndex(of: ":") {
            return String(name[name.index(after: idx)...])
        }
        return name
    }

    private var itemElement: String { mode == .rss ? "item" : "entry" }

    func parser(_ parser: XMLParser, didStartElement elementName: String, namespaceURI: String?,
                qualifiedName qName: String?, attributes attributeDict: [String: String]) {
        let local = localName(elementName)
        if local == itemElement {
            inItem = true
            current = RssFeedNode()
            atomLinkCaptured = false
            return
        }
        guard inItem else { return }
        currentElement = local
        text = ""
        if mode == .atom, local == "link" {
            // Atom <link href="..."/> — capture the FIRST link's href (C#
            // `entry.Elements(atom+"link").FirstOrDefault()?.Attribute("href")`).
            if !atomLinkCaptured, let href = attributeDict["href"] {
                current.fields["link"] = href
                atomLinkCaptured = true
            }
        }
        if mode == .atom, local == "category" {
            // Atom category term attribute.
            if let term = attributeDict["term"], !term.isEmpty { current.categories.append(term) }
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        if inItem { text += string }
    }

    func parser(_ parser: XMLParser, foundCDATA CDATABlock: Data) {
        if inItem, let s = String(data: CDATABlock, encoding: .utf8) { text += s }
    }

    func parser(_ parser: XMLParser, didEndElement elementName: String, namespaceURI: String?,
                qualifiedName qName: String?) {
        let local = localName(elementName)
        if local == itemElement {
            nodes.append(current)
            inItem = false
            return
        }
        guard inItem else { return }
        let value = text
        switch mode {
        case .rss:
            if local == "category" {
                current.categories.append(value)
            } else if ["title", "link", "pubDate", "description", "guid"].contains(local) {
                // First occurrence wins (Element(...) returns the first).
                if current.fields[local] == nil { current.fields[local] = value }
            }
        case .atom:
            if ["title", "updated", "published", "summary", "content", "id"].contains(local) {
                if current.fields[local] == nil { current.fields[local] = value }
            }
            // link + category handled at start (attribute-driven).
        }
        currentElement = ""
    }
}
