// ChildSafetyPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Child Safety
// vertical: trusted-adult ring, geofences, check-in events.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Safety.Child;

public sealed record TrustedAdult(string AdultId, string Name, string Phone, string Relationship, int RingPriority);
public sealed record Geofence(string FenceId, string Name, double CentreLat, double CentreLon, double RadiusMeters);
public sealed record CheckIn(string ChildId, string Status, double? Lat, double? Lon, DateTimeOffset AtUtc);

public interface IChildSafetyBoard
{
    void AddAdult(TrustedAdult a);
    IReadOnlyList<TrustedAdult> RingOrdered { get; }
    void DefineGeofence(Geofence g);
    Geofence? GetGeofence(string id);
    bool IsInsideAnyFence(double lat, double lon);
    void RecordCheckIn(CheckIn c);
    IReadOnlyList<CheckIn> RecentCheckIns(string childId, int limit = 20);
}

public sealed class InMemoryChildSafetyBoard : IChildSafetyBoard
{
    private readonly ConcurrentDictionary<string, TrustedAdult> _adults = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Geofence> _fences = new(StringComparer.Ordinal);
    private readonly List<CheckIn> _checkIns = new();
    private readonly object _lock = new();

    public void AddAdult(TrustedAdult a) { ArgumentNullException.ThrowIfNull(a); _adults[a.AdultId] = a; }
    public IReadOnlyList<TrustedAdult> RingOrdered => _adults.Values.OrderBy(a => a.RingPriority).ToArray();

    public void DefineGeofence(Geofence g) { ArgumentNullException.ThrowIfNull(g); _fences[g.FenceId] = g; }
    public Geofence? GetGeofence(string id) => _fences.GetValueOrDefault(id);

    public bool IsInsideAnyFence(double lat, double lon)
    {
        foreach (var g in _fences.Values)
        {
            if (HaversineMeters(g.CentreLat, g.CentreLon, lat, lon) <= g.RadiusMeters) return true;
        }
        return false;
    }

    public void RecordCheckIn(CheckIn c) { ArgumentNullException.ThrowIfNull(c); lock (_lock) _checkIns.Add(c); }

    public IReadOnlyList<CheckIn> RecentCheckIns(string childId, int limit = 20)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_lock) return _checkIns.Where(c => c.ChildId == childId).OrderByDescending(c => c.AtUtc).Take(limit).ToArray();
    }

    private static double HaversineMeters(double aLat, double aLon, double bLat, double bLon)
    {
        const double R = 6_371_000;
        double DegToRad(double d) => d * Math.PI / 180.0;
        var dLat = DegToRad(bLat - aLat);
        var dLon = DegToRad(bLon - aLon);
        var s1 = Math.Sin(dLat / 2);
        var s2 = Math.Sin(dLon / 2);
        var a = s1 * s1 + Math.Cos(DegToRad(aLat)) * Math.Cos(DegToRad(bLat)) * s2 * s2;
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return R * c;
    }
}
