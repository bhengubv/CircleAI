// AetherNetTransportCommons.cs
//
// (3.3.0) Shared types + helpers for the AetherNet mesh transport:
// peer descriptor, hop telemetry, packet-summary records.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Networking.AetherNet;

public enum AetherPeerKind { Phone, Tablet, Laptop, Desktop, Edge, Vehicle, Iot }

public sealed record AetherPeer(string PeerId, AetherPeerKind Kind, string? FriendlyName, IReadOnlyList<string> AdvertisedCapabilities);
public sealed record AetherHopTelemetry(string PeerId, int HopCount, double RoundTripMs, DateTimeOffset AtUtc);
public sealed record AetherPacketSummary(string PacketId, string FromPeer, string ToPeer, int Bytes, string PacketKind, DateTimeOffset AtUtc);

public sealed class InMemoryAetherNetRegistry
{
    private readonly ConcurrentDictionary<string, AetherPeer> _peers = new(StringComparer.Ordinal);
    private readonly List<AetherHopTelemetry> _telemetry = new();
    private readonly List<AetherPacketSummary> _packets = new();
    private readonly object _lock = new();

    public void Register(AetherPeer p) { ArgumentNullException.ThrowIfNull(p); _peers[p.PeerId] = p; }
    public AetherPeer? GetPeer(string id) => _peers.GetValueOrDefault(id);
    public IReadOnlyList<AetherPeer> Peers => _peers.Values.OrderBy(p => p.PeerId).ToArray();
    public void RecordHop(AetherHopTelemetry t) { ArgumentNullException.ThrowIfNull(t); lock (_lock) _telemetry.Add(t); }
    public void RecordPacket(AetherPacketSummary p) { ArgumentNullException.ThrowIfNull(p); lock (_lock) _packets.Add(p); }
    public IReadOnlyList<AetherPacketSummary> RecentPackets(int limit = 100)
    { lock (_lock) return _packets.OrderByDescending(p => p.AtUtc).Take(limit).ToArray(); }
    public double AvgRoundTripMs(string peerId)
    { lock (_lock) return _telemetry.Where(t => t.PeerId == peerId).Select(t => t.RoundTripMs).DefaultIfEmpty(0).Average(); }
    public int TotalBytesBetween(string fromPeer, string toPeer)
    { lock (_lock) return _packets.Where(p => p.FromPeer == fromPeer && p.ToPeer == toPeer).Sum(p => p.Bytes); }
}
