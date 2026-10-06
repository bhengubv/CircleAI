// BlueskySource.cs
//
// (Phase B3) Bluesky AT-protocol "app.bsky.feed.searchPosts" endpoint
// reader. Pulls posts matching a user-supplied query from the public
// AppView. Anonymous reads work for keyword search; following a user's
// feed requires session auth (left to the host).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.News;

public sealed record BlueskyOptions(string Query, string Host = "https://public.api.bsky.app");

public sealed class BlueskySource : INewsSource
{
    private readonly HttpClient _http;
    private readonly BlueskyOptions _opts;

    public BlueskySource(BlueskyOptions opts) : this(opts, new HttpClient()) { }
    public BlueskySource(BlueskyOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public string SourceId   => $"bluesky:{_opts.Query}";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_opts.Query);

    public async ValueTask<IReadOnlyList<NewsItem>> FetchLatestAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) throw new ArgumentOutOfRangeException(nameof(max));
        var url = $"{_opts.Host}/xrpc/app.bsky.feed.searchPosts"
                + $"?q={Uri.EscapeDataString(_opts.Query)}&limit={Math.Min(max, 100)}&sort=latest";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var list = new List<NewsItem>();
        if (!doc.RootElement.TryGetProperty("posts", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var post in arr.EnumerateArray())
        {
            var uri    = post.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
            var record = post.TryGetProperty("record", out var r) ? r : default;
            var text   = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var ts     = record.ValueKind == JsonValueKind.Object && record.TryGetProperty("createdAt", out var c)
                            ? c.GetString() : null;
            var author = post.TryGetProperty("author", out var a) && a.TryGetProperty("handle", out var h)
                            ? h.GetString() : null;
            var tags   = new List<string>();
            if (record.ValueKind == JsonValueKind.Object
                && record.TryGetProperty("facets", out var facets) && facets.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in facets.EnumerateArray())
                    if (f.TryGetProperty("features", out var feats) && feats.ValueKind == JsonValueKind.Array)
                        foreach (var feat in feats.EnumerateArray())
                            if (feat.TryGetProperty("tag", out var tag)) tags.Add(tag.GetString() ?? "");
            }
            var publishedUtc = DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)
                                ? dto.ToUniversalTime() : DateTimeOffset.MinValue;
            list.Add(new NewsItem(
                ItemId:       uri,
                SourceId:     author ?? SourceId,
                Title:        text.Length > 80 ? text[..80] + "…" : text,
                Summary:      text,
                Url:          BuildPostUrl(author, uri),
                PublishedUtc: publishedUtc,
                Tags:         tags));
        }
        return list;
    }

    private static Uri BuildPostUrl(string? handle, string atUri)
    {
        if (string.IsNullOrWhiteSpace(handle) || string.IsNullOrWhiteSpace(atUri)) return new Uri("about:blank");
        // at://did:plc:.../app.bsky.feed.post/<rkey>  → https://bsky.app/profile/<handle>/post/<rkey>
        var idx = atUri.LastIndexOf('/');
        if (idx < 0 || idx == atUri.Length - 1) return new Uri("about:blank");
        var rkey = atUri[(idx + 1)..];
        return new Uri($"https://bsky.app/profile/{handle}/post/{rkey}");
    }
}
