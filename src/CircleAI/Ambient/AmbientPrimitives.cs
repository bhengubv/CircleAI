// AmbientPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Ambient;

public sealed record AmbientReading(string DeviceId, double TemperatureC, double Humidity, double LuxLight, double DbNoise, DateTimeOffset AtUtc);
public sealed record AmbientPreference(string Location, double TargetTempC, double TargetHumidity, double MaxNoiseDb);

public interface IAmbientBoard
{
    void Record(AmbientReading r);
    AmbientReading? Latest(string deviceId);
    IReadOnlyList<AmbientReading> History(string deviceId, int limit = 50);
    void SetPreference(AmbientPreference p);
    AmbientPreference? GetPreference(string location);
    bool IsComfortable(string deviceId, string location);
}

public sealed class InMemoryAmbientBoard : IAmbientBoard
{
    private readonly List<AmbientReading> _readings = new();
    private readonly ConcurrentDictionary<string, AmbientPreference> _prefs = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Record(AmbientReading r) { ArgumentNullException.ThrowIfNull(r); lock (_lock) _readings.Add(r); }
    public AmbientReading? Latest(string deviceId)
    { lock (_lock) return _readings.Where(r => r.DeviceId == deviceId).OrderByDescending(r => r.AtUtc).FirstOrDefault(); }
    public IReadOnlyList<AmbientReading> History(string deviceId, int limit = 50)
    { lock (_lock) return _readings.Where(r => r.DeviceId == deviceId).OrderByDescending(r => r.AtUtc).Take(limit).ToArray(); }
    public void SetPreference(AmbientPreference p) { ArgumentNullException.ThrowIfNull(p); _prefs[p.Location] = p; }
    public AmbientPreference? GetPreference(string location) => _prefs.GetValueOrDefault(location);
    public bool IsComfortable(string deviceId, string location)
    {
        var pref = GetPreference(location);
        var last = Latest(deviceId);
        if (pref is null || last is null) return false;
        return Math.Abs(last.TemperatureC - pref.TargetTempC) <= 2
            && Math.Abs(last.Humidity      - pref.TargetHumidity) <= 10
            && last.DbNoise <= pref.MaxNoiseDb;
    }
}
