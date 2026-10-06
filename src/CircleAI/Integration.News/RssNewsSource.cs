// RssNewsSource.cs
//
// (Phase B3) Generic RSS 2.0 / Atom 1.0 reader. One feed = one source.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using CircleAI.Integration;

namespace CircleAI.Integration.News;

public sealed record RssOptions(Uri FeedUrl, string? SourceId = null);

public sealed class RssNewsSource : INewsSource
{
    private readonly HttpClient _http;
    private readonly RssOptions _opts;

    public RssNewsSource(RssOptions opts) : this(opts, new HttpClient()) { }
    public RssNewsSource(RssOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public string SourceId   => _opts.SourceId ?? _opts.FeedUrl.Host;
    public bool   IsConfigured => true;

    public async ValueTask<IReadOnlyList<NewsItem>> FetchLatestAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        using var resp = await _http.GetAsync(_opts.FeedUrl, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var xml = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        var items = ParseRss(doc, SourceId).Concat(ParseAtom(doc, SourceId)).Take(max).ToArray();
        return items;
    }

    private static IEnumerable<NewsItem> ParseRss(XDocument doc, string sourceId)
    {
        foreach (var item in doc.Descendants("item"))
        {
            var title  = item.Element("title")?.Value ?? "";
            var link   = item.Element("link")?.Value  ?? "";
            var pub    = item.Element("pubDate")?.Value;
            var desc   = item.Element("description")?.Value ?? "";
            var guid   = item.Element("guid")?.Value ?? link;
            var tags   = item.Elements("category").Select(c => c.Value).ToArray();
            yield return new NewsItem(
                ItemId:       guid,
                SourceId:     sourceId,
                Title:        title,
                Summary:      Strip(desc),
                Url:          Uri.TryCreate(link, UriKind.Absolute, out var u) ? u : new Uri("about:blank"),
                PublishedUtc: ParseDate(pub),
                Tags:         tags);
        }
    }

    private static IEnumerable<NewsItem> ParseAtom(XDocument doc, string sourceId)
    {
        XNamespace atom = "http://www.w3.org/2005/Atom";
        foreach (var entry in doc.Descendants(atom + "entry"))
        {
            var title  = entry.Element(atom + "title")?.Value ?? "";
            var link   = entry.Elements(atom + "link").FirstOrDefault()?.Attribute("href")?.Value ?? "";
            var pub    = entry.Element(atom + "updated")?.Value ?? entry.Element(atom + "published")?.Value;
            var desc   = entry.Element(atom + "summary")?.Value ?? entry.Element(atom + "content")?.Value ?? "";
            var guid   = entry.Element(atom + "id")?.Value ?? link;
            var tags   = entry.Elements(atom + "category").Select(c => c.Attribute("term")?.Value ?? "").Where(t => !string.IsNullOrEmpty(t)).ToArray();
            yield return new NewsItem(
                ItemId:       guid,
                SourceId:     sourceId,
                Title:        title,
                Summary:      Strip(desc),
                Url:          Uri.TryCreate(link, UriKind.Absolute, out var u) ? u : new Uri("about:blank"),
                PublishedUtc: ParseDate(pub),
                Tags:         tags);
        }
    }

    private static DateTimeOffset ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return DateTimeOffset.MinValue;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            return dto.ToUniversalTime();
        return DateTimeOffset.MinValue;
    }

    private static string Strip(string html)
        => System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ").Trim();
}
