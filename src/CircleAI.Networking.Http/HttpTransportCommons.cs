// HttpTransportCommons.cs
//
// (3.3.0) Shared metadata + helpers for the HTTP network transport:
// request descriptor, response cache key, simple in-memory request
// counter for ops dashboards.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace CircleAI.Networking.Http;

public sealed record HttpEndpointDescriptor(string Method, string BaseUri, string Path, IReadOnlyDictionary<string, string>? DefaultHeaders);
public sealed record HttpRequestSummary(string EndpointId, int StatusCode, TimeSpan Latency, int ResponseBytes, DateTimeOffset AtUtc);
public sealed record HttpCacheKey(string Method, string FullUri, string AcceptHeader);

public static class HttpStatusFamily
{
    public static bool Is2xx(int s) => s >= 200 && s < 300;
    public static bool Is3xx(int s) => s >= 300 && s < 400;
    public static bool Is4xx(int s) => s >= 400 && s < 500;
    public static bool Is5xx(int s) => s >= 500 && s < 600;
    public static bool ShouldRetry(int s) => s == 408 || s == 425 || s == 429 || Is5xx(s);
}

public sealed class InMemoryHttpRequestMetrics
{
    private readonly ConcurrentDictionary<string, HttpEndpointDescriptor> _endpoints = new(StringComparer.Ordinal);
    private readonly List<HttpRequestSummary> _requests = new();
    private readonly object _lock = new();

    public void Register(string id, HttpEndpointDescriptor d) { ArgumentNullException.ThrowIfNull(d); _endpoints[id] = d; }
    public HttpEndpointDescriptor? GetEndpoint(string id) => _endpoints.GetValueOrDefault(id);
    public void Log(HttpRequestSummary s) { ArgumentNullException.ThrowIfNull(s); lock (_lock) _requests.Add(s); }
    public IReadOnlyList<HttpRequestSummary> RecentRequests(int limit = 100)
    { lock (_lock) return _requests.OrderByDescending(r => r.AtUtc).Take(limit).ToArray(); }
    public double Avg2xxLatencyMs(string endpointId)
    {
        lock (_lock)
        {
            var rows = _requests.Where(r => r.EndpointId == endpointId && HttpStatusFamily.Is2xx(r.StatusCode)).ToArray();
            if (rows.Length == 0) return 0.0;
            return rows.Average(r => r.Latency.TotalMilliseconds);
        }
    }
}
