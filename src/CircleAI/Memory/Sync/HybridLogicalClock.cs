// HybridLogicalClock.cs
//
// Hybrid Logical Clock (HLC) — monotonic version stamps that survive small
// clock skew between peers WITHOUT needing NTP. Composes a physical
// millisecond timestamp with a logical counter and the node's short ID so
// every emitted version is globally unique and monotonically increasing.
//
// Why HLC and not simple Lamport / vector clocks?
//   • Lamport is unique but loses wall-clock correlation, making debugging
//     and natural ordering awkward.
//   • Vector clocks scale with node count — wrong for a mesh that may have
//     a dozen+ devices per user.
//   • HLC is wall-clock-ish (within skew bounds), unique, and 64-bit small.
//
// Layout of the version:
//   high 48 bits — physical time in milliseconds (Unix epoch)
//   mid  10 bits — logical counter (resets when physical advances)
//   low   6 bits — node short ID (0..63)
// Total: 64 bits.

using System;
using System.Threading;

namespace CircleAI.Memory.Sync;

/// <summary>
/// Hybrid Logical Clock — produces monotonic, globally-unique version
/// stamps for syncable entries. Thread-safe.
/// </summary>
public sealed class HybridLogicalClock
{
    private readonly Func<long> _physicalNowMs;
    private readonly long _nodeShortId;
    private long _lastPhysical;
    private long _logical;
    private readonly object _lock = new();

    /// <param name="nodeShortId">
    /// 0..63 — packs into the low 6 bits of every version. Each device a user
    /// has should pick a stable distinct value (any deterministic hash works).
    /// </param>
    /// <param name="physicalNowMs">
    /// Source of physical time in milliseconds. Defaults to system time;
    /// override in tests for determinism.
    /// </param>
    public HybridLogicalClock(long nodeShortId, Func<long>? physicalNowMs = null)
    {
        if (nodeShortId is < 0 or > 63)
            throw new ArgumentOutOfRangeException(nameof(nodeShortId), "nodeShortId must be in 0..63");
        _nodeShortId = nodeShortId;
        _physicalNowMs = physicalNowMs ?? DefaultNow;
        _lastPhysical = _physicalNowMs();
        _logical = 0;
    }

    /// <summary>Produces the next outgoing version (for a write we originated).</summary>
    public long Tick()
    {
        lock (_lock)
        {
            var now = _physicalNowMs();
            if (now > _lastPhysical)
            {
                _lastPhysical = now;
                _logical = 0;
            }
            else
            {
                _logical++;
                if (_logical >= 1024)
                {
                    // Logical counter overflowed within the same ms — bump physical.
                    _lastPhysical++;
                    _logical = 0;
                }
            }
            return Compose(_lastPhysical, _logical, _nodeShortId);
        }
    }

    /// <summary>
    /// Updates the clock from a received version (must be called on every
    /// inbound apply so subsequent local ticks remain monotonic w.r.t. peers).
    /// </summary>
    public long Observe(long incoming)
    {
        lock (_lock)
        {
            var (incomingPhysical, _, _) = Decompose(incoming);
            var now = _physicalNowMs();
            var maxPhysical = Math.Max(Math.Max(_lastPhysical, incomingPhysical), now);

            if (maxPhysical == _lastPhysical && maxPhysical == incomingPhysical) _logical++;
            else if (maxPhysical == _lastPhysical) _logical++;
            else if (maxPhysical == incomingPhysical) _logical = Decompose(incoming).Logical + 1;
            else _logical = 0;

            _lastPhysical = maxPhysical;
            return Compose(_lastPhysical, _logical, _nodeShortId);
        }
    }

    /// <summary>Composes the three components into a 64-bit version.</summary>
    public static long Compose(long physicalMs, long logical, long nodeShortId) =>
        (physicalMs << 16) | ((logical & 0x3FF) << 6) | (nodeShortId & 0x3F);

    /// <summary>Decomposes a version into its three components.</summary>
    public static (long PhysicalMs, long Logical, long NodeShortId) Decompose(long version) =>
        (version >> 16, (version >> 6) & 0x3FF, version & 0x3F);

    private static long DefaultNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
