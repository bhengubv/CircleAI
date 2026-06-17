// Contracts.cs
//
// (2.5.0) Spatial / geo contract surface. Lets an AI describe — and
// pull data about — places, tracks, skies, and radar surfaces.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Spatial;

public sealed record LatLon(double Latitude, double Longitude);

public sealed record GeoTile(int Z, int X, int Y, ReadOnlyMemory<byte> ImageBytes, string MimeType);

/// <summary>(2.5.0) Map-tile source (deck.gl / cesium pattern).</summary>
public interface IGeoTileSource
{
    string BackendId { get; }

    ValueTask<GeoTile> GetTileAsync(int z, int x, int y, CancellationToken ct = default);

    ValueTask<IReadOnlyList<LatLon>> SearchPlacesAsync(string query, int topK = 5, CancellationToken ct = default);
}

public sealed record RadarReading(LatLon Centre, double RangeKm, IReadOnlyList<RadarReturn> Returns);
public sealed record RadarReturn(LatLon Position, double DopplerKmh, double IntensityDbz);

/// <summary>(2.5.0) Weather / surveillance radar (RADAR pattern).</summary>
public interface IRadarReadout
{
    string BackendId { get; }

    ValueTask<RadarReading> GetCurrentReadingAsync(LatLon at, double rangeKm = 50, CancellationToken ct = default);
}

public sealed record SkyObject(string Name, double AzimuthDeg, double AltitudeDeg, double MagnitudeApparent);

/// <summary>(2.5.0) Visible-sky tracking (skylight pattern).</summary>
public interface ISkyTracker
{
    string BackendId { get; }

    ValueTask<IReadOnlyList<SkyObject>> VisibleAsync(LatLon at, DateTimeOffset utc, CancellationToken ct = default);
}

public sealed record Scene3D(string SceneId, ReadOnlyMemory<byte> Encoded, string Format);

/// <summary>(2.5.0) 3D-scene rendering hook (flame / anime pattern).</summary>
public interface I3DSceneRenderer
{
    string BackendId { get; }

    ValueTask<Scene3D> RenderAsync(string sceneScript, string format = "gltf", CancellationToken ct = default);
}
