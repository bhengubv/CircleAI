// InMemoryInputs.cs
//
// (3.3.0) Real input adapters that work offline: scraper that strips
// HTML to text (so it works without an external converter), a stealth
// http client whose default-headers swap browser fingerprints, and a
// fast asciinema cast parser (line-delimited JSON of [time, type,
// data]). Video ingest still needs ffmpeg on the host; we throw a
// clean NotSupported with how to enable.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inputs;

/// <summary>(3.3.0) HTML scraper using HttpClient + simple text extraction.</summary>
public sealed class HttpHtmlScraper : IWebScraper, IDisposable
{
    private static readonly Regex TitleRx = new(@"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ScriptRx = new(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRx    = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex HrefRx   = new(@"href\s*=\s*[""']([^""'#]+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WsRx     = new(@"\s+", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly bool _owned;

    public HttpHtmlScraper() : this(new HttpClient(), owned: true) { }
    public HttpHtmlScraper(HttpClient http, bool owned = false)
    {
        _http  = http ?? throw new ArgumentNullException(nameof(http));
        _owned = owned;
    }

    public string BackendId => "http-html";

    public async ValueTask<ScrapedPage> FetchAsync(Uri url, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        var html  = await _http.GetStringAsync(url, ct).ConfigureAwait(false);
        var title = TitleRx.Match(html).Groups[1].Value;
        if (!string.IsNullOrEmpty(title)) title = WebUtilityDecode(title.Trim());

        var stripped = ScriptRx.Replace(html, " ");
        var text     = WsRx.Replace(TagRx.Replace(stripped, " "), " ").Trim();
        text         = WebUtilityDecode(text);

        var links = new List<Uri>();
        foreach (Match m in HrefRx.Matches(html))
        {
            if (Uri.TryCreate(url, m.Groups[1].Value, out var abs)) links.Add(abs);
        }

        return new ScrapedPage(url, text, string.IsNullOrEmpty(title) ? null : title, null, links);
    }

    private static string WebUtilityDecode(string s) => System.Net.WebUtility.HtmlDecode(s);

    public void Dispose() { if (_owned) _http.Dispose(); }
}

/// <summary>(3.3.0) Stealth HTTP client — picks a rotating set of headers per call.</summary>
public sealed class StealthHttpClient : IStealthHttpClient, IDisposable
{
    private static readonly string[] UserAgents =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 14_2) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.2 Safari/605.1.15",
        "Mozilla/5.0 (X11; Linux x86_64; rv:121.0) Gecko/20100101 Firefox/121.0",
    };
    private static readonly string[] AcceptLanguages = { "en-US,en;q=0.9", "en-GB,en;q=0.9", "en-ZA,en;q=0.9" };

    private readonly HttpClient _http;
    private readonly bool _owned;
    private int _seq;

    public StealthHttpClient() : this(new HttpClient(), owned: true) { }
    public StealthHttpClient(HttpClient http, bool owned = false)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _owned = owned;
    }

    public string BackendId => "stealth-http";

    public async ValueTask<ScrapedPage> GetAsync(Uri url, IReadOnlyDictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var seq = Interlocked.Increment(ref _seq);
        req.Headers.UserAgent.ParseAdd(UserAgents[seq % UserAgents.Length]);
        req.Headers.AcceptLanguage.ParseAdd(AcceptLanguages[seq % AcceptLanguages.Length]);
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.AcceptEncoding.ParseAdd("gzip, deflate, br");
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        req.Headers.Connection.Add("keep-alive");

        if (headers is not null)
        {
            foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        using var rsp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        rsp.EnsureSuccessStatusCode();
        var body = await rsp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new ScrapedPage(url, body);
    }

    public void Dispose() { if (_owned) _http.Dispose(); }
}

/// <summary>(3.3.0) MCP-side scrape implementation — wraps a real scraper.
/// Default ctor wires HttpHtmlScraper; the configurable ctor lets a host
/// inject a different one (e.g. StealthHttpClient-backed).</summary>
public sealed class DefaultMcpWebScrape : IMcpWebScrape, IDisposable
{
    private readonly IWebScraper _inner;
    private readonly bool _owned;

    public DefaultMcpWebScrape() : this(new HttpHtmlScraper(), owned: true) { }
    public DefaultMcpWebScrape(IWebScraper inner, bool owned = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _owned = owned;
    }

    public string BackendId => $"mcp:{_inner.BackendId}";

    public ValueTask<ScrapedPage> ScrapeAsync(McpScrapeJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        return _inner.FetchAsync(new Uri(job.Url), ct);
    }

    public void Dispose() { if (_owned && _inner is IDisposable d) d.Dispose(); }
}


/// <summary>(3.3.0) Parser for asciinema v2 cast files — header line + array events.</summary>
public sealed class AsciinemaTerminalCast : ITerminalCast
{
    public string BackendId => "asciinema";

    public async ValueTask<TerminalCast> LoadAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("filePath required", nameof(filePath));
        if (!File.Exists(filePath)) throw new FileNotFoundException("cast file not found", filePath);

        var width = 80; var height = 24;
        var segments = new List<TerminalCastSegment>();

        using var fs = File.OpenRead(filePath);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        var first = await sr.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new InvalidDataException("empty cast file");
        try
        {
            using var hdr = JsonDocument.Parse(first);
            if (hdr.RootElement.TryGetProperty("width",  out var w)) width  = w.GetInt32();
            if (hdr.RootElement.TryGetProperty("height", out var h)) height = h.GetInt32();
        }
        catch (JsonException) { /* header optional / non-standard cast */ }

        string? line;
        while ((line = await sr.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var ev = JsonDocument.Parse(line);
                var arr = ev.RootElement;
                if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 3) continue;
                var t   = arr[0].GetDouble();
                var typ = arr[1].GetString();
                var txt = arr[2].GetString() ?? "";
                if (typ == "o") segments.Add(new TerminalCastSegment(TimeSpan.FromSeconds(t), txt));
            }
            catch (JsonException) { /* skip malformed event */ }
        }

        return new TerminalCast(segments, width, height);
    }

    public ValueTask<string> RenderTranscriptAsync(TerminalCast cast, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cast);
        var sb = new StringBuilder();
        foreach (var s in cast.Segments) sb.Append(s.Text);
        return ValueTask.FromResult(sb.ToString());
    }
}
