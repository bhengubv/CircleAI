// ServerCounters.cs
//
// Coarse-grain server-wide counters surfaced by /v1/diagnostics. The
// fine-grain story lives in CircleAI.Core.Diagnostics.CircleAIDiagnostics
// (ActivitySource + Meter), which feeds OpenTelemetry. These are the
// human-readable "at a glance" numbers.

namespace CircleAI.Inference.Server.Models;

/// <summary>Thread-safe counters for diagnostics rendering.</summary>
public sealed class ServerCounters
{
    private long _total;
    private long _rejected;
    private long _failed;
    private int  _active;

    /// <summary>UTC time the server process started.</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Total requests accepted (including those that subsequently failed).</summary>
    public long TotalRequests => Interlocked.Read(ref _total);

    /// <summary>Requests rejected at admission (e.g. concurrency cap, auth fail).</summary>
    public long RejectedRequests => Interlocked.Read(ref _rejected);

    /// <summary>Requests that admitted but failed downstream (timeout, model error).</summary>
    public long FailedRequests => Interlocked.Read(ref _failed);

    /// <summary>Requests currently in flight (incremented at admission, decremented on completion).</summary>
    public int ActiveRequests => Volatile.Read(ref _active);

    /// <summary>Mark a request as accepted (admission passed).</summary>
    public void AccountAdmitted()
    {
        Interlocked.Increment(ref _total);
        Interlocked.Increment(ref _active);
    }

    /// <summary>Mark a request as completed (admission was previously counted).</summary>
    public void AccountCompleted() => Interlocked.Decrement(ref _active);

    /// <summary>Mark a request as rejected at admission (not counted in total).</summary>
    public void AccountRejected() => Interlocked.Increment(ref _rejected);

    /// <summary>Mark a request as failed downstream.</summary>
    public void AccountFailed() => Interlocked.Increment(ref _failed);
}
