// WebSocketTransportCommons.cs
//
// (3.3.0) Shared types + helpers for the WebSocket network transport:
// endpoint descriptor, state machine, message-type enum.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Networking.WebSocket;

public enum WebSocketLinkState { Closed, Connecting, Open, CloseSent, CloseReceived, Closed_Error }
public enum WebSocketMessageType { Text, Binary, Ping, Pong, Close }

public sealed record WebSocketEndpointDescriptor(Uri Uri, IReadOnlyDictionary<string, string>? Headers, TimeSpan PingInterval, IReadOnlyList<string> Subprotocols);
public sealed record WebSocketFrameSummary(string SessionId, WebSocketMessageType Type, int Bytes, DateTimeOffset AtUtc);

public sealed class InMemoryWebSocketSessionRegistry
{
    private readonly ConcurrentDictionary<string, WebSocketEndpointDescriptor> _endpoints = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WebSocketLinkState> _states = new(StringComparer.Ordinal);
    private readonly List<WebSocketFrameSummary> _frames = new();
    private readonly object _lock = new();

    public void Register(string sessionId, WebSocketEndpointDescriptor d) { ArgumentNullException.ThrowIfNull(d); _endpoints[sessionId] = d; }
    public WebSocketEndpointDescriptor? Get(string sessionId) => _endpoints.GetValueOrDefault(sessionId);
    public void SetState(string sessionId, WebSocketLinkState s) => _states[sessionId] = s;
    public WebSocketLinkState State(string sessionId) => _states.TryGetValue(sessionId, out var s) ? s : WebSocketLinkState.Closed;
    public void RecordFrame(WebSocketFrameSummary f) { ArgumentNullException.ThrowIfNull(f); lock (_lock) _frames.Add(f); }
    public long TotalBytes(string sessionId)
    { lock (_lock) return _frames.Where(f => f.SessionId == sessionId).Sum(f => (long)f.Bytes); }
    public int FrameCount(string sessionId, WebSocketMessageType type)
    { lock (_lock) return _frames.Count(f => f.SessionId == sessionId && f.Type == type); }
}
