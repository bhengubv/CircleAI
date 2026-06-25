// OsrmRoutingProvider.cs
//
// (Phase B4) Open Source Routing Machine (OSRM) HTTP client. Default
// host is the public OSRM demo server; production hosts should run
// their own instance.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Integration;

namespace CircleAI.Integration.Geo;

public sealed record OsrmOptions(string Host = "https://router.project-osrm.org");

public sealed class OsrmRoutingProvider : IRoutingProvider
{
    private readonly HttpClient _http;
    private readonly OsrmOptions _opts;

    public OsrmRoutingProvider() : this(new OsrmOptions(), new HttpClient()) { }
    public OsrmRoutingProvider(OsrmOptions opts) : this(opts, new HttpClient()) { }
    public OsrmRoutingProvider(OsrmOptions opts, HttpClient http)
    {
        _opts = opts ?? throw new ArgumentNullException(nameof(opts));
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public string ProviderId => "osrm";

    public async ValueTask<RouteEstimate> RouteAsync(
        double fromLat, double fromLon, double toLat, double toLon,
        string mode = "car", CancellationToken ct = default)
    {
        var profile = mode switch
        {
            "bike" or "bicycle" => "bike",
            "foot" or "walk"    => "foot",
            _                   => "driving",
        };
        var url = $"{_opts.Host.TrimEnd('/')}/route/v1/{profile}/"
                + $"{fromLon.ToString(CultureInfo.InvariantCulture)},{fromLat.ToString(CultureInfo.InvariantCulture)};"
                + $"{toLon.ToString(CultureInfo.InvariantCulture)},{toLat.ToString(CultureInfo.InvariantCulture)}"
                + "?overview=full&geometries=geojson";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var code = doc.RootElement.GetProperty("code").GetString();
        if (!string.Equals(code, "Ok", StringComparison.Ordinal))
            throw new InvalidOperationException($"OSRM returned code={code}");

        var route = doc.RootElement.GetProperty("routes")[0];
        var dist = route.GetProperty("distance").GetDouble();      // metres
        var dur  = route.GetProperty("duration").GetDouble();      // seconds
        var poly = new List<(double Lat, double Lon)>();
        if (route.TryGetProperty("geometry", out var geom)
            && geom.TryGetProperty("coordinates", out var coords)
            && coords.ValueKind == JsonValueKind.Array)
        {
            foreach (var pt in coords.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
                poly.Add((pt[1].GetDouble(), pt[0].GetDouble()));
            }
        }
        return new RouteEstimate(DistanceKm: dist / 1000.0, Duration: TimeSpan.FromSeconds(dur), Polyline: poly);
    }
}
