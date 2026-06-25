// NewsApiSource.cs
//
// (Phase B3) Adapter for newsapi.org / gnews.io style REST endpoints
// (both follow the "articles" array shape).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.News;

public sealed record NewsApiOptions(string ApiKey, string Query, string Endpoint = "https://newsapi.org/v2/everything");

public sealed class NewsApiSource : INewsSource
{
    private readonly HttpClient _http;
    private readonly NewsApiOptions _opts;

    public NewsApiSource(NewsApiOptions opts) : this(opts, new HttpClient()) { }
    public NewsApiSource(NewsApiOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public string SourceId   => $"newsapi:{_opts.Query}";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_opts.ApiKey);

    public async ValueTask<IReadOnlyList<NewsItem>> FetchLatestAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        if (!IsConfigured) throw new InvalidOperationException("NewsAPI key not configured.");
        var url = $"{_opts.Endpoint}?q={Uri.EscapeDataString(_opts.Query)}&pageSize={Math.Min(max, 100)}&sortBy=publishedAt&language=en";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", _opts.ApiKey);
        req.Headers.UserAgent.ParseAdd("CircleAI/1.0 (NewsApiSource)");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var list = new List<NewsItem>();
        if (doc.RootElement.TryGetProperty("articles", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var title = a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var desc  = a.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                var url2  = a.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var pub   = a.TryGetProperty("publishedAt", out var p) ? p.GetString() : null;
                var src   = a.TryGetProperty("source", out var s) && s.TryGetProperty("name", out var sn)
                                ? sn.GetString() : null;
                list.Add(new NewsItem(
                    ItemId:       url2,
                    SourceId:     src ?? SourceId,
                    Title:        title,
                    Summary:      desc,
                    Url:          Uri.TryCreate(url2, UriKind.Absolute, out var ux) ? ux : new Uri("about:blank"),
                    PublishedUtc: DateTimeOffset.TryParse(pub, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
                                    ? dto.ToUniversalTime() : DateTimeOffset.MinValue,
                    Tags:         Array.Empty<string>()));
            }
        }
        return list;
    }
}
