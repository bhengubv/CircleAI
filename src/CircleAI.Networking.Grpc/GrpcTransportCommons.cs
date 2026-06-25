// GrpcTransportCommons.cs
//
// (3.3.0) Shared metadata + helpers for the gRPC network transport:
// channel descriptor, retry policy record, in-memory call counter.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CircleAI.Networking.Grpc;

public enum GrpcChannelState { Idle, Connecting, Ready, TransientFailure, Shutdown }

public sealed record GrpcChannelDescriptor(string Target, bool UseTls, int MaxReceiveBytes, int MaxSendBytes, TimeSpan KeepAliveInterval);
public sealed record GrpcRetryPolicy(int MaxAttempts, TimeSpan InitialBackoff, TimeSpan MaxBackoff, double Multiplier, IReadOnlyList<string> RetryableStatusCodes);
public sealed record GrpcCallSummary(string Method, int Attempts, TimeSpan Latency, string StatusCode, DateTimeOffset AtUtc);

public static class GrpcRetryPolicies
{
    public static GrpcRetryPolicy Default { get; } = new(3, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2), 2.0, new[] { "UNAVAILABLE", "DEADLINE_EXCEEDED" });
    public static GrpcRetryPolicy Aggressive { get; } = new(6, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(5), 2.0, new[] { "UNAVAILABLE", "DEADLINE_EXCEEDED", "RESOURCE_EXHAUSTED" });
    public static GrpcRetryPolicy NoRetry { get; } = new(1, TimeSpan.Zero, TimeSpan.Zero, 1.0, Array.Empty<string>());
}

public sealed class InMemoryGrpcCallMetrics
{
    private readonly ConcurrentDictionary<string, GrpcChannelDescriptor> _channels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GrpcChannelState> _states = new(StringComparer.Ordinal);
    private readonly List<GrpcCallSummary> _calls = new();
    private readonly object _lock = new();
    private long _seq;

    public void RegisterChannel(string id, GrpcChannelDescriptor d) { ArgumentNullException.ThrowIfNull(d); _channels[id] = d; }
    public GrpcChannelDescriptor? GetChannel(string id) => _channels.GetValueOrDefault(id);
    public void SetState(string id, GrpcChannelState s) => _states[id] = s;
    public GrpcChannelState State(string id) => _states.TryGetValue(id, out var s) ? s : GrpcChannelState.Idle;
    public string LogCall(GrpcCallSummary c) { ArgumentNullException.ThrowIfNull(c); lock (_lock) _calls.Add(c); return $"grpc-{Interlocked.Increment(ref _seq)}"; }
    public IReadOnlyList<GrpcCallSummary> RecentCalls(int limit = 50)
    { lock (_lock) return _calls.OrderByDescending(c => c.AtUtc).Take(limit).ToArray(); }
}
