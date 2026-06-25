// WearablePrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Wearable
// vertical: device descriptor, telemetry samples, in-memory store.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Wearable;

public enum WearableKind { Smartwatch, FitnessBand, ChestStrap, Patch, Headset }
public enum WearableTelemetryKind { HeartRate, Steps, Calories, SleepStage, SkinTempC, Stress, OxygenPct }

public sealed record WearableDevice(string DeviceId, WearableKind Kind, string Vendor, string FirmwareVersion, double BatteryPct);
public sealed record WearableSample(string DeviceId, WearableTelemetryKind Kind, double Value, DateTimeOffset AtUtc);

public interface IWearableBoard
{
    void Add(WearableDevice d);
    WearableDevice? GetDevice(string id);
    IReadOnlyList<WearableDevice> Devices { get; }
    void Record(WearableSample s);
    IReadOnlyList<WearableSample> ReadSince(string deviceId, WearableTelemetryKind kind, DateTimeOffset since);
    double? LatestValue(string deviceId, WearableTelemetryKind kind);
    double AverageValue(string deviceId, WearableTelemetryKind kind, DateTimeOffset since);
}

public sealed class InMemoryWearableBoard : IWearableBoard
{
    private readonly ConcurrentDictionary<string, WearableDevice> _devices = new(StringComparer.Ordinal);
    private readonly List<WearableSample> _samples = new();
    private readonly object _lock = new();

    public void Add(WearableDevice d) { ArgumentNullException.ThrowIfNull(d); _devices[d.DeviceId] = d; }
    public WearableDevice? GetDevice(string id) => _devices.GetValueOrDefault(id);
    public IReadOnlyList<WearableDevice> Devices => _devices.Values.OrderBy(d => d.Vendor).ToArray();

    public void Record(WearableSample s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!_devices.ContainsKey(s.DeviceId)) throw new InvalidOperationException($"Unknown device {s.DeviceId}");
        lock (_lock) _samples.Add(s);
    }

    public IReadOnlyList<WearableSample> ReadSince(string deviceId, WearableTelemetryKind kind, DateTimeOffset since)
    {
        lock (_lock) return _samples.Where(s => s.DeviceId == deviceId && s.Kind == kind && s.AtUtc >= since).OrderBy(s => s.AtUtc).ToArray();
    }

    public double? LatestValue(string deviceId, WearableTelemetryKind kind)
    {
        lock (_lock)
        {
            var hit = _samples.Where(s => s.DeviceId == deviceId && s.Kind == kind).OrderByDescending(s => s.AtUtc).FirstOrDefault();
            return hit?.Value;
        }
    }

    public double AverageValue(string deviceId, WearableTelemetryKind kind, DateTimeOffset since)
    {
        var items = ReadSince(deviceId, kind, since);
        return items.Count == 0 ? double.NaN : items.Average(s => s.Value);
    }
}
