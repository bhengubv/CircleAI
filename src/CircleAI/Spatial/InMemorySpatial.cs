// InMemorySpatial.cs
//
// (3.3.0) Real-but-deterministic spatial sources for tests and host
// fallbacks. The tile source returns a tiny PNG header (so format
// detection works) and place-search by registered name; radar/sky
// produce deterministic computed values; the 3D-scene renderer
// produces a minimal-but-valid GLTF JSON document.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Spatial;

public sealed class InMemoryGeoTileSource : IGeoTileSource
{
    private readonly ConcurrentDictionary<string, LatLon> _places = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryGeoTileSource()
    {
        Register("Johannesburg",   new LatLon(-26.2041,  28.0473));
        Register("Cape Town",       new LatLon(-33.9249,  18.4241));
        Register("Pretoria",        new LatLon(-25.7479,  28.2293));
        Register("Durban",          new LatLon(-29.8587,  31.0218));
        Register("Lagos",           new LatLon(  6.5244,   3.3792));
        Register("Nairobi",         new LatLon( -1.2921,  36.8219));
        Register("London",          new LatLon( 51.5074,  -0.1278));
        Register("New York",        new LatLon( 40.7128, -74.0060));
    }

    public string BackendId => "in-memory";

    public void Register(string name, LatLon at)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required");
        _places[name] = at;
    }

    public ValueTask<GeoTile> GetTileAsync(int z, int x, int y, CancellationToken ct = default)
    {
        if (z < 0 || x < 0 || y < 0) throw new ArgumentOutOfRangeException(nameof(z));
        // 1x1 transparent PNG.
        var pngBytes = new byte[]
        {
            0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A, 0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
            0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01, 0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,
            0x89,0x00,0x00,0x00,0x0D,0x49,0x44,0x41, 0x54,0x78,0x9C,0x63,0x00,0x01,0x00,0x00,
            0x05,0x00,0x01,0x0D,0x0A,0x2D,0xB4,0x00, 0x00,0x00,0x00,0x49,0x45,0x4E,0x44,0xAE,
            0x42,0x60,0x82
        };
        return ValueTask.FromResult(new GeoTile(z, x, y, pngBytes, "image/png"));
    }

    public ValueTask<IReadOnlyList<LatLon>> SearchPlacesAsync(string query, int topK = 5, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        var hits = _places
            .Where(kv => kv.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key)
            .Take(topK)
            .Select(kv => kv.Value)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<LatLon>>(hits);
    }
}

public sealed class SyntheticRadarReadout : IRadarReadout
{
    public string BackendId => "synthetic";

    public ValueTask<RadarReading> GetCurrentReadingAsync(LatLon at, double rangeKm = 50, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(at);
        if (rangeKm <= 0) throw new ArgumentOutOfRangeException(nameof(rangeKm));

        // Deterministic radar pattern based on coordinates so tests can assert against it.
        var seed   = (long)(at.Latitude * 1000) + (long)(at.Longitude * 1000) + (long)(rangeKm * 10);
        var rng    = new Random((int)(seed ^ (seed >> 32)));
        var count  = 3 + rng.Next(0, 5);
        var rets   = new RadarReturn[count];
        for (var i = 0; i < count; i++)
        {
            var d   = rng.NextDouble() * rangeKm * 0.9;
            var ang = rng.NextDouble() * Math.PI * 2;
            var lat = at.Latitude  + (Math.Cos(ang) * d) / 111.0;
            var lon = at.Longitude + (Math.Sin(ang) * d) / 111.0;
            rets[i] = new RadarReturn(new LatLon(lat, lon), rng.NextDouble() * 60 - 30, rng.NextDouble() * 60);
        }
        return ValueTask.FromResult(new RadarReading(at, rangeKm, rets));
    }
}

public sealed class SyntheticSkyTracker : ISkyTracker
{
    private static readonly (string Name, double Azimuth, double Altitude, double Mag)[] BaseObjects =
    {
        ("Sirius",      102.7, 35.0, -1.46),
        ("Polaris",       0.0, 51.5,  1.97),
        ("Vega",         88.0, 70.0,  0.03),
        ("Mars",        135.4, 22.0,  0.5),
        ("Jupiter",     180.5, 40.0, -2.0),
        ("Saturn",      210.0, 30.0,  0.4),
    };

    public string BackendId => "synthetic";

    public ValueTask<IReadOnlyList<SkyObject>> VisibleAsync(LatLon at, DateTimeOffset utc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(at);
        // Visibility filter: roughly, only those with altitude > 0 after a daily-rotation offset.
        var hours = utc.UtcDateTime.TimeOfDay.TotalHours;
        var rot   = hours * 15.0;  // earth rotation degrees-per-hour
        var hits  = new List<SkyObject>(BaseObjects.Length);
        foreach (var (n, az, alt, mag) in BaseObjects)
        {
            var az2 = (az - rot + 360) % 360;
            if (alt - Math.Abs(at.Latitude) > 0)
                hits.Add(new SkyObject(n, az2, alt, mag));
        }
        return ValueTask.FromResult<IReadOnlyList<SkyObject>>(hits);
    }
}

public sealed class JsonScene3DRenderer : I3DSceneRenderer
{
    public string BackendId => "json";

    public ValueTask<Scene3D> RenderAsync(string sceneScript, string format = "gltf", CancellationToken ct = default)
    {
        if (sceneScript is null) throw new ArgumentNullException(nameof(sceneScript));
        if (string.IsNullOrWhiteSpace(format)) format = "gltf";

        // Minimal valid GLTF 2.0 JSON wrapping the script as an extras blob.
        var sceneId = Guid.NewGuid().ToString("n");
        var json = $"{{\"asset\":{{\"version\":\"2.0\",\"generator\":\"CircleAI.Spatial.JsonScene3DRenderer\"}},\"scenes\":[{{\"nodes\":[]}}],\"scene\":0,\"extras\":{{\"script\":{System.Text.Json.JsonSerializer.Serialize(sceneScript)}}}}}";
        return ValueTask.FromResult(new Scene3D(sceneId, Encoding.UTF8.GetBytes(json), format));
    }
}
