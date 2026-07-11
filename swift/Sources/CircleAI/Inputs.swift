// Inputs.swift
//
// Port of src/CircleAI.Inputs/ — input adapters normalising URLs / files /
// casts into model-ready text:
//   • Contracts.cs              — ScrapedPage, VideoIngestResult, McpScrapeJob,
//                                 TerminalCastSegment, TerminalCast; IWebScraper,
//                                 IStealthHttpClient, IVideoIngest, IMcpWebScrape,
//                                 ITerminalCast
//   • InMemoryInputs.cs         — HttpHtmlScraper (HTML→text), StealthHttpClient
//                                 (rotating fingerprint headers), DefaultMcpWebScrape
//                                 (wraps a scraper), AsciinemaTerminalCast (v2 cast parser)
//   • NullImplementations.cs    — Null* backends (incl. NullVideoIngest — the only
//                                 IVideoIngest impl, since ingest needs host ffmpeg)
//
// Porting notes:
//   • `Uri` → `URL`. `record` → `struct: Sendable`. `TimeSpan` → `TimeInterval`.
//   • `HttpClient` → `URLSession`; the network dependency is injected via an
//     `HttpFetching` protocol so tests can supply a fake without real sockets
//     (mirrors the C# `HttpClient` ctor injection).
//   • `Regex` → NSRegularExpression; `WebUtility.HtmlDecode` → a small entity decoder.
//   • `Interlocked.Increment` → NSLock-guarded counter.
//   • asciinema v2: first line = header JSON (width/height), each subsequent line
//     = `[time, type, data]`; only "o" (output) events become segments.
//   • Guards → `InputsError`.

import Foundation

// MARK: - Records

/// One scraped page: URL, extracted text, optional title/metadata/links.
public struct ScrapedPage: Sendable, Equatable {
    public let url: URL
    public let text: String
    public let title: String?
    public let metadata: [String: String]?
    public let resolvedLinks: [URL]?
    public init(url: URL, text: String, title: String? = nil, metadata: [String: String]? = nil, resolvedLinks: [URL]? = nil) {
        self.url = url
        self.text = text
        self.title = title
        self.metadata = metadata
        self.resolvedLinks = resolvedLinks
    }
}

/// A video-ingest result (transcript + shots + duration + frame count).
public struct VideoIngestResult: Sendable, Equatable {
    public let transcript: String
    public let shots: [String]
    public let duration: TimeInterval
    public let frameCount: Int
    public init(transcript: String, shots: [String], duration: TimeInterval, frameCount: Int) {
        self.transcript = transcript
        self.shots = shots
        self.duration = duration
        self.frameCount = frameCount
    }
}

/// An MCP-side scrape job (URL + optional headers).
public struct McpScrapeJob: Sendable, Equatable {
    public let url: String
    public let headers: [String: String]?
    public init(url: String, headers: [String: String]? = nil) {
        self.url = url
        self.headers = headers
    }
}

/// One terminal-cast output segment (time offset + emitted text).
public struct TerminalCastSegment: Sendable, Equatable {
    public let offset: TimeInterval
    public let text: String
    public init(offset: TimeInterval, text: String) {
        self.offset = offset
        self.text = text
    }
}

/// A parsed terminal cast (segments + terminal dimensions).
public struct TerminalCast: Sendable, Equatable {
    public let segments: [TerminalCastSegment]
    public let width: Int
    public let height: Int
    public init(segments: [TerminalCastSegment], width: Int, height: Int) {
        self.segments = segments
        self.width = width
        self.height = height
    }
}

// MARK: - Errors

public enum InputsError: Error, Equatable, CustomStringConvertible {
    case filePathRequired
    case fileNotFound(String)
    case emptyCastFile
    case invalidURL(String)
    case httpStatus(Int)

    public var description: String {
        switch self {
        case .filePathRequired: return "filePath required"
        case .fileNotFound(let p): return "cast file not found: \(p)"
        case .emptyCastFile: return "empty cast file"
        case .invalidURL(let u): return "invalid URL: \(u)"
        case .httpStatus(let c): return "HTTP status \(c)"
        }
    }
}

// MARK: - HTTP injection seam

/// Minimal HTTP fetch seam so scrapers can be tested without real sockets.
/// The production `URLSessionHttpFetcher` uses `URLSession`.
public protocol HttpFetching: Sendable {
    /// Performs a GET and returns the decoded body + HTTP status code.
    func get(url: URL, headers: [String: String]?) async throws -> (body: String, status: Int)
}

/// `URLSession`-backed HTTP fetcher.
public struct URLSessionHttpFetcher: HttpFetching {
    private let session: URLSession
    public init(session: URLSession = .shared) { self.session = session }

    public func get(url: URL, headers: [String: String]?) async throws -> (body: String, status: Int) {
        var req = URLRequest(url: url)
        req.httpMethod = "GET"
        if let headers {
            for (k, v) in headers { req.setValue(v, forHTTPHeaderField: k) }
        }
        let (data, response) = try await session.data(for: req)
        let status = (response as? HTTPURLResponse)?.statusCode ?? 200
        return (String(decoding: data, as: UTF8.self), status)
    }
}

// MARK: - Contracts

public protocol IWebScraper: Sendable {
    var backendId: String { get }
    func fetch(url: URL) async throws -> ScrapedPage
}

public protocol IStealthHttpClient: Sendable {
    var backendId: String { get }
    func get(url: URL, headers: [String: String]?) async throws -> ScrapedPage
}

public protocol IVideoIngest: Sendable {
    var backendId: String { get }
    func ingest(filePath: String) async throws -> VideoIngestResult
}

public protocol IMcpWebScrape: Sendable {
    var backendId: String { get }
    func scrape(job: McpScrapeJob) async throws -> ScrapedPage
}

public protocol ITerminalCast: Sendable {
    var backendId: String { get }
    func load(filePath: String) async throws -> TerminalCast
    func renderTranscript(cast: TerminalCast) async throws -> String
}

// MARK: - HTML scraper

/// HTTP scraper that strips HTML to text and resolves links.
public struct HttpHtmlScraper: IWebScraper {
    private let http: any HttpFetching
    public init(http: any HttpFetching = URLSessionHttpFetcher()) { self.http = http }
    public var backendId: String { "http-html" }

    private static let titleRx = try! NSRegularExpression(pattern: "<title>(.*?)</title>", options: [.caseInsensitive, .dotMatchesLineSeparators])
    private static let scriptRx = try! NSRegularExpression(pattern: "<(script|style)[^>]*>.*?</\\1>", options: [.caseInsensitive, .dotMatchesLineSeparators])
    private static let tagRx = try! NSRegularExpression(pattern: "<[^>]+>")
    private static let hrefRx = try! NSRegularExpression(pattern: "href\\s*=\\s*[\"']([^\"'#]+)[\"']", options: [.caseInsensitive])
    private static let wsRx = try! NSRegularExpression(pattern: "\\s+")

    public func fetch(url: URL) async throws -> ScrapedPage {
        let (html, _) = try await http.get(url: url, headers: nil)

        var title = HtmlText.firstGroup(HttpHtmlScraper.titleRx, in: html)
        if !title.isEmpty { title = HtmlText.decodeEntities(title.trimmingCharacters(in: .whitespacesAndNewlines)) }

        let stripped = HtmlText.replace(HttpHtmlScraper.scriptRx, in: html, with: " ")
        let noTags = HtmlText.replace(HttpHtmlScraper.tagRx, in: stripped, with: " ")
        var text = HtmlText.replace(HttpHtmlScraper.wsRx, in: noTags, with: " ").trimmingCharacters(in: .whitespacesAndNewlines)
        text = HtmlText.decodeEntities(text)

        var links: [URL] = []
        let ns = html as NSString
        for m in HttpHtmlScraper.hrefRx.matches(in: html, range: NSRange(location: 0, length: ns.length)) {
            let raw = ns.substring(with: m.range(at: 1))
            if let abs = URL(string: raw, relativeTo: url)?.absoluteURL { links.append(abs) }
        }

        return ScrapedPage(url: url, text: text, title: title.isEmpty ? nil : title, metadata: nil, resolvedLinks: links)
    }
}

// MARK: - Stealth HTTP client

/// Stealth HTTP client — rotates browser-fingerprint headers per call.
public final class StealthHttpClient: IStealthHttpClient, @unchecked Sendable {
    private static let userAgents = [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
    ]
    private static let acceptLanguages = ["en-US,en;q=0.9", "en-GB,en;q=0.9", "en-ZA,en;q=0.9"]

    private let http: any HttpFetching
    private let lock = NSLock()
    private var seq = 0

    public init(http: any HttpFetching = URLSessionHttpFetcher()) { self.http = http }
    public var backendId: String { "stealth-http" }

    public func get(url: URL, headers: [String: String]? = nil) async throws -> ScrapedPage {
        lock.lock()
        seq += 1
        let s = seq
        lock.unlock()

        var reqHeaders: [String: String] = [
            "User-Agent": StealthHttpClient.userAgents[s % StealthHttpClient.userAgents.count],
            "Accept-Language": StealthHttpClient.acceptLanguages[s % StealthHttpClient.acceptLanguages.count],
            "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
            "Accept-Encoding": "gzip, deflate, br",
            "Cache-Control": "no-cache",
            "Connection": "keep-alive",
        ]
        if let headers {
            for (k, v) in headers { reqHeaders[k] = v }
        }

        let (body, status) = try await http.get(url: url, headers: reqHeaders)
        if status < 200 || status >= 300 { throw InputsError.httpStatus(status) }
        return ScrapedPage(url: url, text: body)
    }
}

// MARK: - MCP web scrape

/// MCP-side scrape — wraps an inner `IWebScraper`.
public struct DefaultMcpWebScrape: IMcpWebScrape {
    private let inner: any IWebScraper
    public init(inner: any IWebScraper = HttpHtmlScraper()) { self.inner = inner }
    public var backendId: String { "mcp:\(inner.backendId)" }

    public func scrape(job: McpScrapeJob) async throws -> ScrapedPage {
        guard let url = URL(string: job.url) else { throw InputsError.invalidURL(job.url) }
        return try await inner.fetch(url: url)
    }
}

// MARK: - Asciinema cast

/// Parser for asciinema v2 cast files (header line + `[time, type, data]` events).
public struct AsciinemaTerminalCast: ITerminalCast {
    public init() {}
    public var backendId: String { "asciinema" }

    public func load(filePath: String) async throws -> TerminalCast {
        if filePath.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { throw InputsError.filePathRequired }
        guard FileManager.default.fileExists(atPath: filePath) else { throw InputsError.fileNotFound(filePath) }
        guard let raw = try? String(contentsOfFile: filePath, encoding: .utf8) else { throw InputsError.emptyCastFile }

        let allLines = raw.components(separatedBy: "\n")
        guard let first = allLines.first, !first.isEmpty else { throw InputsError.emptyCastFile }

        var width = 80, height = 24
        if let hdrData = first.data(using: .utf8),
           let hdr = try? JSONSerialization.jsonObject(with: hdrData) as? [String: Any] {
            if let w = hdr["width"] as? Int { width = w }
            if let h = hdr["height"] as? Int { height = h }
        }

        var segments: [TerminalCastSegment] = []
        for line in allLines.dropFirst() {
            if line.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { continue }
            guard let data = line.data(using: .utf8),
                  let arr = try? JSONSerialization.jsonObject(with: data) as? [Any],
                  arr.count >= 3 else { continue }
            guard let t = AsciinemaTerminalCast.asDouble(arr[0]) else { continue }
            let typ = arr[1] as? String
            let txt = arr[2] as? String ?? ""
            if typ == "o" { segments.append(TerminalCastSegment(offset: t, text: txt)) }
        }

        return TerminalCast(segments: segments, width: width, height: height)
    }

    public func renderTranscript(cast: TerminalCast) async throws -> String {
        cast.segments.map { $0.text }.joined()
    }

    private static func asDouble(_ v: Any) -> Double? {
        if let d = v as? Double { return d }
        if let i = v as? Int { return Double(i) }
        if let n = v as? NSNumber { return n.doubleValue }
        return nil
    }
}

// MARK: - HTML text helpers

enum HtmlText {
    static func firstGroup(_ rx: NSRegularExpression, in text: String) -> String {
        let ns = text as NSString
        guard let m = rx.firstMatch(in: text, range: NSRange(location: 0, length: ns.length)), m.numberOfRanges >= 2 else { return "" }
        let r = m.range(at: 1)
        return r.location == NSNotFound ? "" : ns.substring(with: r)
    }

    static func replace(_ rx: NSRegularExpression, in text: String, with template: String) -> String {
        let ns = text as NSString
        return rx.stringByReplacingMatches(in: text, range: NSRange(location: 0, length: ns.length), withTemplate: template)
    }

    /// Decodes the common named + numeric HTML entities (mirrors WebUtility.HtmlDecode
    /// for the cases the scraper produces).
    static func decodeEntities(_ s: String) -> String {
        var out = s
        let named: [(String, String)] = [
            ("&amp;", "&"), ("&lt;", "<"), ("&gt;", ">"),
            ("&quot;", "\""), ("&#39;", "'"), ("&apos;", "'"), ("&nbsp;", "\u{00A0}"),
        ]
        for (e, r) in named { out = out.replacingOccurrences(of: e, with: r) }
        // Numeric entities &#NNN; and &#xHH;.
        out = HtmlText.decodeNumeric(out)
        return out
    }

    private static func decodeNumeric(_ s: String) -> String {
        guard let rx = try? NSRegularExpression(pattern: "&#(x?)([0-9A-Fa-f]+);") else { return s }
        let ns = s as NSString
        var result = ""
        var last = 0
        for m in rx.matches(in: s, range: NSRange(location: 0, length: ns.length)) {
            result += ns.substring(with: NSRange(location: last, length: m.range.location - last))
            let isHex = ns.substring(with: m.range(at: 1)) == "x"
            let digits = ns.substring(with: m.range(at: 2))
            if let code = UInt32(digits, radix: isHex ? 16 : 10), let scalar = Unicode.Scalar(code) {
                result += String(scalar)
            } else {
                result += ns.substring(with: m.range)
            }
            last = m.range.location + m.range.length
        }
        result += ns.substring(from: last)
        return result
    }
}

// MARK: - Null backends

public struct NullWebScraper: IWebScraper {
    public static let instance = NullWebScraper()
    public init() {}
    public var backendId: String { "null" }
    public func fetch(url: URL) async throws -> ScrapedPage { ScrapedPage(url: url, text: "") }
}

public struct NullStealthHttpClient: IStealthHttpClient {
    public static let instance = NullStealthHttpClient()
    public init() {}
    public var backendId: String { "null" }
    public func get(url: URL, headers: [String: String]? = nil) async throws -> ScrapedPage { ScrapedPage(url: url, text: "") }
}

public struct NullVideoIngest: IVideoIngest {
    public static let instance = NullVideoIngest()
    public init() {}
    public var backendId: String { "null" }
    public func ingest(filePath: String) async throws -> VideoIngestResult {
        VideoIngestResult(transcript: "", shots: [], duration: 0, frameCount: 0)
    }
}

public struct NullMcpWebScrape: IMcpWebScrape {
    public static let instance = NullMcpWebScrape()
    public init() {}
    public var backendId: String { "null" }
    public func scrape(job: McpScrapeJob) async throws -> ScrapedPage {
        guard let url = URL(string: job.url) else { throw InputsError.invalidURL(job.url) }
        return ScrapedPage(url: url, text: "")
    }
}

public struct NullTerminalCast: ITerminalCast {
    public static let instance = NullTerminalCast()
    public init() {}
    public var backendId: String { "null" }
    public func load(filePath: String) async throws -> TerminalCast { TerminalCast(segments: [], width: 80, height: 24) }
    public func renderTranscript(cast: TerminalCast) async throws -> String { "" }
}
