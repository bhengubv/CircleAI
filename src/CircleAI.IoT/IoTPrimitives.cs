// IoTPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.IoT;

public sealed record IoTDevice(string DeviceId, string Name, string Kind, string FirmwareVersion, DateTimeOffset LastSeenUtc);
public sealed record IoTTelemetry(string DeviceId, string Metric, double Value, DateTimeOffset AtUtc);
public sealed record IoTCommand(string CommandId, string DeviceId, string Action, string ArgumentsJson, DateTimeOffset SentUtc);

public interface IIoTBoard
{
    void Register(IoTDevice d);
    IoTDevice? GetDevice(string id);
    IReadOnlyList<IoTDevice> Devices { get; }
    void RecordTelemetry(IoTTelemetry t);
    double LatestValue(string deviceId, string metric);
    IReadOnlyList<IoTTelemetry> History(string deviceId, string metric, int limit = 100);
    void SendCommand(IoTCommand c);
    IReadOnlyList<IoTCommand> CommandsFor(string deviceId);
}

public sealed class InMemoryIoTBoard : IIoTBoard
{
    private readonly ConcurrentDictionary<string, IoTDevice> _devices = new(StringComparer.Ordinal);
    private readonly List<IoTTelemetry> _telemetry = new();
    private readonly List<IoTCommand> _commands = new();
    private readonly object _lock = new();

    public void Register(IoTDevice d) { ArgumentNullException.ThrowIfNull(d); _devices[d.DeviceId] = d; }
    public IoTDevice? GetDevice(string id) => _devices.GetValueOrDefault(id);
    public IReadOnlyList<IoTDevice> Devices => _devices.Values.OrderBy(d => d.Name).ToArray();
    public void RecordTelemetry(IoTTelemetry t) { ArgumentNullException.ThrowIfNull(t); lock (_lock) _telemetry.Add(t); }
    public double LatestValue(string deviceId, string metric)
    {
        lock (_lock)
        {
            var hit = _telemetry.Where(t => t.DeviceId == deviceId && t.Metric == metric).OrderByDescending(t => t.AtUtc).FirstOrDefault();
            return hit?.Value ?? double.NaN;
        }
    }
    public IReadOnlyList<IoTTelemetry> History(string deviceId, string metric, int limit = 100)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_lock) return _telemetry.Where(t => t.DeviceId == deviceId && t.Metric == metric).OrderByDescending(t => t.AtUtc).Take(limit).ToArray();
    }
    public void SendCommand(IoTCommand c) { ArgumentNullException.ThrowIfNull(c); lock (_lock) _commands.Add(c); }
    public IReadOnlyList<IoTCommand> CommandsFor(string deviceId)
    { lock (_lock) return _commands.Where(c => c.DeviceId == deviceId).OrderByDescending(c => c.SentUtc).ToArray(); }
}
