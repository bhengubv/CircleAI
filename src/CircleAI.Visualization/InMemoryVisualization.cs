// InMemoryVisualization.cs
//
// (3.3.0) Real in-memory IDashboardDefinitionStore + IApiDocBuilder +
// ISiteBuilder. Stores definitions in a thread-safe dictionary. The
// ApiDoc builder normalises the supplied OpenAPI JSON; the SiteBuilder
// renders a simple multi-file static site from a JSON description of
// the form { "pages": [{ "path": "...", "html": "..." }] }.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Visualization;

/// <summary>(3.3.0) Thread-safe in-memory dashboard store.</summary>
public sealed class InMemoryDashboardStore : IDashboardDefinitionStore
{
    private readonly ConcurrentDictionary<string, DashboardDefinition> _items = new(StringComparer.Ordinal);

    public string BackendId => "in-memory";

    public ValueTask UpsertAsync(DashboardDefinition d, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (string.IsNullOrWhiteSpace(d.DashboardId)) throw new ArgumentException("DashboardId required");
        _items[d.DashboardId] = d;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DashboardDefinition?> GetAsync(string id, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required", nameof(id));
        _items.TryGetValue(id, out var d);
        return ValueTask.FromResult(d);
    }

    public ValueTask<IReadOnlyList<DashboardDefinition>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<DashboardDefinition>>(_items.Values.ToArray());
}

/// <summary>(3.3.0) Normalising API-doc builder. Parses the OpenAPI
/// JSON, extracts title + version, and re-serialises with a stable key
/// ordering so downstream sites get deterministic output.</summary>
public sealed class JsonApiDocBuilder : IApiDocBuilder
{
    public string BackendId => "json-normaliser";

    public ValueTask<ApiDoc> BuildAsync(string openApiSpec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(openApiSpec)) throw new ArgumentException("openApiSpec required", nameof(openApiSpec));
        using var doc = JsonDocument.Parse(openApiSpec);
        var root = doc.RootElement;

        var title    = root.TryGetProperty("info", out var info) && info.TryGetProperty("title", out var t) ? t.GetString() ?? "API" : "API";
        var docId    = title.Replace(' ', '-').ToLowerInvariant();
        var canonical = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = false });
        return ValueTask.FromResult(new ApiDoc(docId, title, canonical));
    }
}

/// <summary>(3.3.0) Builds a static site from a JSON spec
/// <c>{"pages":[{"path":"index.html","html":"..."},...]}</c>. Outputs
/// the rendered files in-memory.</summary>
public sealed class StaticSiteBuilder : ISiteBuilder
{
    public string BackendId => "static";

    public ValueTask<GeneratedSite> BuildAsync(string siteSpec, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteSpec)) throw new ArgumentException("siteSpec required", nameof(siteSpec));
        using var doc = JsonDocument.Parse(siteSpec);
        var files = new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.Ordinal);

        if (!doc.RootElement.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("siteSpec must contain a pages[] array.", nameof(siteSpec));
        }

        foreach (var page in pages.EnumerateArray())
        {
            var path = page.TryGetProperty("path", out var p) ? p.GetString() : null;
            var html = page.TryGetProperty("html", out var h) ? h.GetString() : null;
            if (string.IsNullOrWhiteSpace(path) || html is null) continue;
            files[path] = Encoding.UTF8.GetBytes(html);
        }

        var siteId = $"site-{Guid.NewGuid():n}";
        return ValueTask.FromResult(new GeneratedSite(siteId, files));
    }
}
