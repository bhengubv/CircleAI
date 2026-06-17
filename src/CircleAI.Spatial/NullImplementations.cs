// NullImplementations.cs
//
// (2.5.0) Fail-safe defaults for the Spatial pack.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Spatial;

public sealed class NullGeoTileSource : IGeoTileSource
{
    public static readonly NullGeoTileSource Instance = new();
    public string BackendId => "null";
    public ValueTask<GeoTile> GetTileAsync(int z, int x, int y, CancellationToken ct = default)
        => ValueTask.FromResult(new GeoTile(z, x, y, ReadOnlyMemory<byte>.Empty, "image/png"));
    public ValueTask<IReadOnlyList<LatLon>> SearchPlacesAsync(string q, int topK = 5, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<LatLon>>(Array.Empty<LatLon>());
}

public sealed class NullRadarReadout : IRadarReadout
{
    public static readonly NullRadarReadout Instance = new();
    public string BackendId => "null";
    public ValueTask<RadarReading> GetCurrentReadingAsync(LatLon at, double rangeKm = 50, CancellationToken ct = default)
        => ValueTask.FromResult(new RadarReading(at, rangeKm, Array.Empty<RadarReturn>()));
}

public sealed class NullSkyTracker : ISkyTracker
{
    public static readonly NullSkyTracker Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<SkyObject>> VisibleAsync(LatLon at, DateTimeOffset utc, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<SkyObject>>(Array.Empty<SkyObject>());
}

public sealed class Null3DSceneRenderer : I3DSceneRenderer
{
    public static readonly Null3DSceneRenderer Instance = new();
    public string BackendId => "null";
    public ValueTask<Scene3D> RenderAsync(string scene, string format = "gltf", CancellationToken ct = default)
        => ValueTask.FromResult(new Scene3D(Guid.Empty.ToString(), ReadOnlyMemory<byte>.Empty, format));
}
