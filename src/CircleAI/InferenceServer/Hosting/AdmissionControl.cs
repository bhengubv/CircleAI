// AdmissionControl.cs
//
// Fixed-cap admission gate. SemaphoreSlim provides the bounded counter;
// non-blocking TryWait failure is mapped to HTTP 503 with a Retry-After
// hint at the endpoint layer.

using Microsoft.Extensions.Options;
using CircleAI.Inference.Server.Models;
using CircleAI.Inference.Server.Options;

namespace CircleAI.Inference.Server.Hosting;

/// <summary>
/// Bounded admission gate — at most <see cref="InferenceServerOptions.MaxConcurrentRequests"/>
/// requests in flight at any time. Excess requests are rejected immediately
/// (no queueing) so callers can decide whether to back off or retry.
/// </summary>
public sealed class AdmissionControl : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly ServerCounters _counters;

    /// <summary>Maximum admitted-at-once requests.</summary>
    public int MaxConcurrentRequests { get; }

    public AdmissionControl(IOptions<InferenceServerOptions> options, ServerCounters counters)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(counters);
        MaxConcurrentRequests = Math.Max(1, options.Value.MaxConcurrentRequests);
        _gate = new SemaphoreSlim(MaxConcurrentRequests, MaxConcurrentRequests);
        _counters = counters;
    }

    /// <summary>
    /// Attempt to acquire one slot. When successful returns an
    /// <see cref="IDisposable"/> the caller MUST dispose to release the slot
    /// (use <c>using</c>). When the gate is saturated, returns <c>null</c> —
    /// the endpoint should respond with HTTP 503.
    /// </summary>
    public IDisposable? TryEnter()
    {
        if (_gate.Wait(0))
        {
            _counters.AccountAdmitted();
            return new Slot(_gate, _counters);
        }
        _counters.AccountRejected();
        return null;
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Slot : IDisposable
    {
        private readonly SemaphoreSlim _gate;
        private readonly ServerCounters _counters;
        private int _disposed;
        public Slot(SemaphoreSlim gate, ServerCounters counters)
        { _gate = gate; _counters = counters; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _gate.Release();
                _counters.AccountCompleted();
            }
        }
    }
}
