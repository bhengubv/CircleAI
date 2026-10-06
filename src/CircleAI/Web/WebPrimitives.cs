// WebPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Web vertical:
// HTTP routes, page metadata, simple in-memory cache.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Web;

public sealed record RouteDescriptor(string Path, string Method, string HandlerName, IReadOnlyList<string> Tags);
public sealed record PageMetadata(string Path, string Title, string? Description, IReadOnlyList<string> Keywords);
public sealed record CachedResponse(string Key, byte[] Body, string Mime, DateTimeOffset ExpiresUtc);

public interface IWebBoard
{
    void Register(RouteDescriptor r);
    IReadOnlyList<RouteDescriptor> RoutesByMethod(string method);
    void SetMetadata(PageMetadata m);
    PageMetadata? GetMetadata(string path);
    void Cache(CachedResponse c);
    CachedResponse? Lookup(string key);
}

public sealed class InMemoryWebBoard : IWebBoard
{
    private readonly ConcurrentDictionary<string, RouteDescriptor> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PageMetadata> _meta = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedResponse> _cache = new(StringComparer.Ordinal);

    public void Register(RouteDescriptor r)
    {
        ArgumentNullException.ThrowIfNull(r);
        _routes[$"{r.Method.ToUpperInvariant()} {r.Path}"] = r;
    }

    public IReadOnlyList<RouteDescriptor> RoutesByMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("method required", nameof(method));
        return _routes.Values.Where(r => string.Equals(r.Method, method, StringComparison.OrdinalIgnoreCase))
                             .OrderBy(r => r.Path).ToArray();
    }

    public void SetMetadata(PageMetadata m) { ArgumentNullException.ThrowIfNull(m); _meta[m.Path] = m; }
    public PageMetadata? GetMetadata(string path) => _meta.GetValueOrDefault(path);

    public void Cache(CachedResponse c)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.ExpiresUtc <= DateTimeOffset.UtcNow) return;  // already expired; skip
        _cache[c.Key] = c;
    }

    public CachedResponse? Lookup(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key required", nameof(key));
        if (!_cache.TryGetValue(key, out var c)) return null;
        if (c.ExpiresUtc <= DateTimeOffset.UtcNow) { _cache.TryRemove(key, out _); return null; }
        return c;
    }
}
