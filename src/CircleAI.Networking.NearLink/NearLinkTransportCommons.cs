// NearLinkTransportCommons.cs
//
// (3.3.0) Shared types + helpers for the NearLink network transport:
// device pairing record, link state, throughput sample.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Networking.NearLink;

public enum NearLinkPairingState { Unpaired, Pairing, Paired, PairingFailed }
public enum NearLinkPowerProfile { LowEnergy, Balanced, HighThroughput }

public sealed record NearLinkDevice(string DeviceId, string FriendlyName, string ManufacturerId, string FirmwareVersion);
public sealed record NearLinkSession(string SessionId, string DeviceId, NearLinkPowerProfile PowerProfile, DateTimeOffset StartedUtc);
public sealed record NearLinkThroughputSample(string DeviceId, double KbpsRead, double KbpsWrite, int RssiDbm, DateTimeOffset AtUtc);

public sealed class InMemoryNearLinkRegistry
{
    private readonly ConcurrentDictionary<string, NearLinkDevice> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NearLinkPairingState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NearLinkSession> _sessions = new(StringComparer.Ordinal);
    private readonly List<NearLinkThroughputSample> _throughput = new();
    private readonly object _lock = new();

    public void Register(NearLinkDevice d) { ArgumentNullException.ThrowIfNull(d); _devices[d.DeviceId] = d; }
    public NearLinkDevice? GetDevice(string id) => _devices.GetValueOrDefault(id);
    public IReadOnlyList<NearLinkDevice> Devices => _devices.Values.OrderBy(d => d.FriendlyName).ToArray();
    public void SetPairingState(string deviceId, NearLinkPairingState s) => _states[deviceId] = s;
    public NearLinkPairingState PairingState(string deviceId) => _states.TryGetValue(deviceId, out var s) ? s : NearLinkPairingState.Unpaired;
    public void OpenSession(NearLinkSession s) { ArgumentNullException.ThrowIfNull(s); _sessions[s.SessionId] = s; }
    public NearLinkSession? GetSession(string id) => _sessions.GetValueOrDefault(id);
    public void CloseSession(string id) => _sessions.TryRemove(id, out _);
    public IReadOnlyList<NearLinkSession> ActiveSessions => _sessions.Values.ToArray();
    public void RecordThroughput(NearLinkThroughputSample s) { ArgumentNullException.ThrowIfNull(s); lock (_lock) _throughput.Add(s); }
    public double AvgRssi(string deviceId)
    { lock (_lock) return _throughput.Where(t => t.DeviceId == deviceId).Select(t => (double)t.RssiDbm).DefaultIfEmpty(-127).Average(); }
}
