// BluetoothTransportCommons.cs
//
// (3.3.0) Shared metadata + helpers for the Bluetooth network
// transport: connection-state record, capability descriptor, simple
// in-memory transport registry.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Networking.Bluetooth;

public enum BluetoothConnectionState { Disconnected, Discovering, Connecting, Connected, Failed }

public sealed record BluetoothEndpointDescriptor(string DeviceId, string Name, string MacAddress, IReadOnlyList<string> AdvertisedServices);
public sealed record BluetoothCapabilityProfile(int MaxMtuBytes, bool SupportsSecureConnections, bool SupportsHighSpeed, IReadOnlyList<string> CompatibleProfiles);
public sealed record BluetoothThroughputSample(string DeviceId, double KbpsRead, double KbpsWrite, DateTimeOffset AtUtc);

public static class BluetoothCapabilityProfiles
{
    public static BluetoothCapabilityProfile Le5 { get; } = new(247, true, true,  new[] { "GATT", "L2CAP" });
    public static BluetoothCapabilityProfile Le4 { get; } = new(23,  true, false, new[] { "GATT" });
    public static BluetoothCapabilityProfile Classic { get; } = new(1024, true, false, new[] { "SPP", "RFCOMM" });
}

public sealed class InMemoryBluetoothTransportRegistry
{
    private readonly ConcurrentDictionary<string, BluetoothEndpointDescriptor> _endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BluetoothConnectionState> _states = new(StringComparer.Ordinal);
    private readonly List<BluetoothThroughputSample> _throughput = new();
    private readonly object _lock = new();

    public void Register(BluetoothEndpointDescriptor e) { ArgumentNullException.ThrowIfNull(e); _endpoints[e.DeviceId] = e; }
    public BluetoothEndpointDescriptor? GetEndpoint(string deviceId) => _endpoints.GetValueOrDefault(deviceId);
    public IReadOnlyList<BluetoothEndpointDescriptor> AllEndpoints => _endpoints.Values.OrderBy(e => e.Name).ToArray();
    public void SetState(string deviceId, BluetoothConnectionState s) => _states[deviceId] = s;
    public BluetoothConnectionState State(string deviceId) => _states.TryGetValue(deviceId, out var s) ? s : BluetoothConnectionState.Disconnected;
    public void RecordThroughput(BluetoothThroughputSample s) { ArgumentNullException.ThrowIfNull(s); lock (_lock) _throughput.Add(s); }
    public double AvgKbpsRead(string deviceId)
    { lock (_lock) return _throughput.Where(t => t.DeviceId == deviceId).Select(t => t.KbpsRead).DefaultIfEmpty(0.0).Average(); }
}
