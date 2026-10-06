// MastodonSource.cs
//
// (Phase B3) Mastodon public-timeline / hashtag-timeline reader.
// Anonymous access works for public timelines on most instances.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.News;

public sealed record MastodonOptions(string Instance, string? Hashtag = null, string? AccessToken = null);

public sealed class MastodonSource : INewsSource
{
    private readonly HttpClient _http;
    private readonly MastodonOptions _opts;

    public MastodonSource(MastodonOptions opts) : this(opts, new HttpClient()) { }
    public MastodonSource(MastodonOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public string SourceId   => string.IsNullOrEmpty(_opts.Hashtag)
                                    ? $"mastodon:{_opts.Instance}:public"
                                    : $"mastodon:{_opts.Instance}:#{_opts.Hashtag}";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_opts.Instance);

    public async ValueTask<IReadOnlyList<NewsItem>> FetchLatestAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        var path = string.IsNullOrEmpty(_opts.Hashtag)
            ? $"/api/v1/timelines/public?limit={Math.Min(max, 40)}"
            : $"/api/v1/timelines/tag/{Uri.EscapeDataString(_opts.Hashtag)}?limit={Math.Min(max, 40)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, _opts.Instance.TrimEnd('/') + path);
        if (!string.IsNullOrWhiteSpace(_opts.AccessToken))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _opts.AccessToken);
        req.Headers.UserAgent.ParseAdd("CircleAI/1.0 (MastodonSource)");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<NewsItem>();
        var list = new List<NewsItem>();
        foreach (var s in doc.RootElement.EnumerateArray())
        {
            var url    = s.TryGetProperty("url",     out var u) ? u.GetString() ?? "" : "";
            var contentHtml = s.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var pub    = s.TryGetProperty("created_at", out var p) ? p.GetString() : null;
            var tags   = new List<string>();
            if (s.TryGetProperty("tags", out var tagsArr) && tagsArr.ValueKind == JsonValueKind.Array)
                foreach (var tg in tagsArr.EnumerateArray())
                    if (tg.TryGetProperty("name", out var tn)) tags.Add(tn.GetString() ?? "");
            var acct   = s.TryGetProperty("account", out var a) && a.TryGetProperty("acct", out var ac) ? ac.GetString() : null;
            var text   = Regex.Replace(contentHtml, "<[^>]+>", " ").Trim();
            list.Add(new NewsItem(
                ItemId:       url,
                SourceId:     acct ?? SourceId,
                Title:        text.Length > 80 ? text[..80] + "…" : text,
                Summary:      text,
                Url:          Uri.TryCreate(url, UriKind.Absolute, out var ux) ? ux : new Uri("about:blank"),
                PublishedUtc: DateTimeOffset.TryParse(pub, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
                                ? dto.ToUniversalTime() : DateTimeOffset.MinValue,
                Tags:         tags));
        }
        return list;
    }
}
