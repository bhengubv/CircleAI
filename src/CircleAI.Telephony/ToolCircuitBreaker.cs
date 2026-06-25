// ToolCircuitBreaker.cs
//
// (3.3.0) Per-tool circuit breaker + timeout wrapper around any
// IToolCallRegistry. Three states: Closed (normal), Open (failing —
// reject immediately), HalfOpen (one trial allowed). Each tool has
// its own breaker state — a broken billing API doesn't cut off the
// order-lookup API.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Per-tool timeout + breaker thresholds.</summary>
/// <param name="Timeout">Wall-clock ceiling for the call. Default 5 s.</param>
/// <param name="FailureThreshold">Consecutive failures that trip the breaker. Default 3.</param>
/// <param name="OpenDuration">How long the breaker stays open before half-opening. Default 30 s.</param>
public sealed record ToolCallPolicy(
    TimeSpan? Timeout          = null,
    int       FailureThreshold = 3,
    TimeSpan? OpenDuration     = null)
{
    public TimeSpan TimeoutOrDefault     => Timeout      ?? TimeSpan.FromSeconds(5);
    public TimeSpan OpenDurationOrDefault => OpenDuration ?? TimeSpan.FromSeconds(30);
}

/// <summary>(3.3.0) Breaker state.</summary>
public enum ToolBreakerState { Closed, Open, HalfOpen }

/// <summary>
/// (3.3.0) Decorates an <see cref="IToolCallRegistry"/> with per-tool
/// timeouts and a circuit breaker. Pass an <c>ITimeProvider</c>-style
/// clock for deterministic tests.
/// </summary>
public sealed class CircuitBreakerToolRegistry : IToolCallRegistry
{
    private readonly IToolCallRegistry _inner;
    private readonly ToolCallPolicy _defaultPolicy;
    private readonly ConcurrentDictionary<string, ToolCallPolicy> _policies = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, BreakerEntry> _breakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public CircuitBreakerToolRegistry(
        IToolCallRegistry         inner,
        ToolCallPolicy?           defaultPolicy = null,
        Func<DateTimeOffset>?     clock         = null)
    {
        _inner         = inner ?? throw new ArgumentNullException(nameof(inner));
        _defaultPolicy = defaultPolicy ?? new ToolCallPolicy();
        _clock         = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Override the policy for a specific tool.</summary>
    public void SetPolicy(string toolName, ToolCallPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policies[toolName] = policy;
    }

    /// <summary>Inspect the current breaker state for a tool.</summary>
    public ToolBreakerState GetState(string toolName)
        => _breakers.TryGetValue(toolName, out var entry) ? entry.CurrentState(_clock(), GetPolicy(toolName).OpenDurationOrDefault) : ToolBreakerState.Closed;

    public System.Collections.Generic.IReadOnlyList<ToolDefinition> Definitions => _inner.Definitions;

    public void RegisterLocal(ToolDefinition definition, LocalToolHandler handler)   => _inner.RegisterLocal(definition, handler);
    public void RegisterWebhook(ToolDefinition definition, Uri webhook)              => _inner.RegisterWebhook(definition, webhook);

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var policy = GetPolicy(invocation.ToolName);
        var entry  = _breakers.GetOrAdd(invocation.ToolName, _ => new BreakerEntry());

        var state = entry.CurrentState(_clock(), policy.OpenDurationOrDefault);
        if (state == ToolBreakerState.Open)
        {
            return new ToolResult(invocation.CallId, false, "{}",
                $"Tool '{invocation.ToolName}' is circuit-broken; retry after the breaker resets.");
        }

        using var timeout = new CancellationTokenSource(policy.TimeoutOrDefault);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        try
        {
            var result = await _inner.InvokeAsync(invocation, linked.Token).ConfigureAwait(false);
            if (result.Succeeded)
            {
                entry.RecordSuccess();
            }
            else
            {
                entry.RecordFailure(policy.FailureThreshold, _clock());
            }
            return result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            entry.RecordFailure(policy.FailureThreshold, _clock());
            return new ToolResult(invocation.CallId, false, "{}",
                $"Tool '{invocation.ToolName}' timed out after {policy.TimeoutOrDefault.TotalMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            entry.RecordFailure(policy.FailureThreshold, _clock());
            return new ToolResult(invocation.CallId, false, "{}", ex.Message);
        }
    }

    private ToolCallPolicy GetPolicy(string toolName)
        => _policies.TryGetValue(toolName, out var policy) ? policy : _defaultPolicy;

    private sealed class BreakerEntry
    {
        private int _consecutiveFailures;
        private DateTimeOffset _openedAt;
        private bool _isOpen;

        public ToolBreakerState CurrentState(DateTimeOffset now, TimeSpan openDuration)
        {
            if (!_isOpen) return ToolBreakerState.Closed;
            if (now - _openedAt >= openDuration) return ToolBreakerState.HalfOpen;
            return ToolBreakerState.Open;
        }

        public void RecordSuccess()
        {
            _consecutiveFailures = 0;
            _isOpen = false;
        }

        public void RecordFailure(int threshold, DateTimeOffset now)
        {
            if (Interlocked.Increment(ref _consecutiveFailures) >= threshold)
            {
                _isOpen = true;
                _openedAt = now;
            }
        }
    }
}
