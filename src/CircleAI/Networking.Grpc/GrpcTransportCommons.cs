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

/// <summary>
/// Lifecycle state of a managed gRPC connection, mirroring the connectivity
/// states a channel steps through as reconnection is driven.
/// </summary>
public enum GrpcConnectionState { Idle, Connecting, Ready, TransientFailure, Shutdown }

/// <summary>
/// Reconnection strategy for a managed gRPC channel: how many attempts to make
/// and how to grow the backoff between them. Fulfils the channel-lifecycle and
/// reconnection promise of <c>GrpcNetworkTransport</c> without any transport deps.
/// </summary>
public sealed record GrpcReconnectPolicy(int MaxAttempts, TimeSpan InitialBackoff, double BackoffMultiplier, TimeSpan MaxBackoff)
{
    /// <summary>A sane default: 5 attempts, 200ms growing ×2 up to a 30s ceiling.</summary>
    public static GrpcReconnectPolicy Default { get; } =
        new(5, TimeSpan.FromMilliseconds(200), 2.0, TimeSpan.FromSeconds(30));

    /// <summary>
    /// Backoff before a given 1-based attempt: <c>InitialBackoff × Multiplier^(attempt-1)</c>,
    /// capped at <see cref="MaxBackoff"/>. Attempt 1 returns <see cref="InitialBackoff"/>.
    /// </summary>
    public TimeSpan BackoffFor(int attempt)
    {
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), "attempt is 1-based");
        var scaled = InitialBackoff.TotalMilliseconds * Math.Pow(BackoffMultiplier, attempt - 1);
        var capMs = MaxBackoff.TotalMilliseconds;
        if (double.IsInfinity(scaled) || scaled > capMs) return MaxBackoff;
        return TimeSpan.FromMilliseconds(scaled);
    }

    /// <summary>True when the 1-based attempt number is still within the retry budget.</summary>
    public bool ShouldRetry(int attempt) => attempt < MaxAttempts;
}

/// <summary>
/// Deadline math for gRPC calls: turns a relative timeout into the absolute UTC
/// instant a call must complete by, and reports remaining time against a clock.
/// </summary>
public static class GrpcDeadline
{
    /// <summary>Absolute deadline for a call started at <paramref name="nowUtc"/> with the given timeout.</summary>
    public static DateTime FromTimeout(TimeSpan timeout, DateTime nowUtc)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        return nowUtc + timeout;
    }

    /// <summary>Time left before <paramref name="deadlineUtc"/>, clamped to zero once passed.</summary>
    public static TimeSpan Remaining(DateTime deadlineUtc, DateTime nowUtc)
    {
        var left = deadlineUtc - nowUtc;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>True once <paramref name="nowUtc"/> has reached or passed the deadline.</summary>
    public static bool IsExpired(DateTime deadlineUtc, DateTime nowUtc) => nowUtc >= deadlineUtc;
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
