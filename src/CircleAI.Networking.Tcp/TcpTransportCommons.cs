// TcpTransportCommons.cs
//
// (3.3.0) Shared types + helpers for the TCP network transport:
// endpoint descriptor, connection state, throughput sample.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Networking.Tcp;

public enum TcpConnectionState { Disconnected, Connecting, Connected, Closing, Failed }

public sealed record TcpEndpointDescriptor(string Host, int Port, bool NoDelay, bool KeepAlive, TimeSpan ConnectTimeout);
public sealed record TcpThroughputSample(string EndpointId, long BytesSent, long BytesReceived, DateTimeOffset AtUtc);

public static class TcpKnownPorts
{
    public const int Http     = 80;
    public const int Https    = 443;
    public const int Ssh      = 22;
    public const int Smtp     = 25;
    public const int Imap     = 143;
    public const int ImapSsl  = 993;
    public const int Pop3     = 110;
    public const int Pop3Ssl  = 995;
    public const int Mqtt     = 1883;
    public const int MqttSsl  = 8883;
}

public sealed class InMemoryTcpConnectionRegistry
{
    private readonly ConcurrentDictionary<string, TcpEndpointDescriptor> _endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TcpConnectionState> _states = new(StringComparer.Ordinal);
    private readonly List<TcpThroughputSample> _throughput = new();
    private readonly object _lock = new();

    public void Register(string id, TcpEndpointDescriptor d) { ArgumentNullException.ThrowIfNull(d); _endpoints[id] = d; }
    public TcpEndpointDescriptor? Get(string id) => _endpoints.GetValueOrDefault(id);
    public void SetState(string id, TcpConnectionState s) => _states[id] = s;
    public TcpConnectionState State(string id) => _states.TryGetValue(id, out var s) ? s : TcpConnectionState.Disconnected;
    public void RecordSample(TcpThroughputSample s) { ArgumentNullException.ThrowIfNull(s); lock (_lock) _throughput.Add(s); }
    public long TotalBytesSent(string id) { lock (_lock) return _throughput.Where(t => t.EndpointId == id).Sum(t => t.BytesSent); }
}
