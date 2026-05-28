// BiosignalAggregator.cs
//
// Sliding-window aggregator. Pulls samples from an IBiosignalSource and
// computes per-kind min/max/mean/count over a configurable time window.

namespace Circle.AI.Wearable.Biosignals;

/// <summary>
/// Per-kind aggregate statistics over a sliding window.
/// </summary>
/// <param name="SampleCount">Number of samples included in the aggregate.</param>
/// <param name="Min">Minimum value observed.</param>
/// <param name="Max">Maximum value observed.</param>
/// <param name="Mean">Arithmetic mean of observed values.</param>
public sealed record BiosignalStats(int SampleCount, float Min, float Max, float Mean);

/// <summary>
/// A snapshot of biosignal aggregates across all observed kinds at a point in time.
/// </summary>
/// <param name="Stats">Per-kind statistics. Kinds with no samples in the window are absent.</param>
/// <param name="GeneratedAt">UTC timestamp at which the snapshot was generated.</param>
public sealed record BiosignalSnapshot(
    IReadOnlyDictionary<BiosignalKind, BiosignalStats> Stats,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Sliding-window aggregator over an <see cref="IBiosignalSource"/>.
/// </summary>
public sealed class BiosignalAggregator
{
    private readonly IBiosignalSource _source;

    /// <summary>
    /// Creates an aggregator wrapping the given source.
    /// </summary>
    /// <param name="source">Underlying biosignal source.</param>
    public BiosignalAggregator(IBiosignalSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>
    /// Consumes samples from the source until either the source completes or the
    /// total elapsed time exceeds <paramref name="window"/>, then returns a snapshot
    /// over the samples that fell within the window (relative to UTC now at call time).
    /// </summary>
    /// <remarks>
    /// This is a single-shot snapshot, not a continuous aggregator. For continuous
    /// aggregation, invoke this method repeatedly on the same source.
    /// </remarks>
    /// <param name="window">Time window relative to UTC now.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="BiosignalSnapshot"/>.</returns>
    public async Task<BiosignalSnapshot> SnapshotAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window must be positive.");
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var cutoff      = generatedAt - window;
        var deadline    = generatedAt + window;
        var accumulator = new Dictionary<BiosignalKind, Accumulator>();

        // Time-bound the read so a never-completing source still yields a snapshot.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(window);

        try
        {
            await foreach (var sample in _source.StreamAsync(cts.Token).ConfigureAwait(false))
            {
                if (sample.MeasuredAt < cutoff) continue;

                if (!accumulator.TryGetValue(sample.Kind, out var acc))
                {
                    acc = new Accumulator();
                    accumulator[sample.Kind] = acc;
                }
                acc.Add(sample.Value);

                if (DateTimeOffset.UtcNow >= deadline) break;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Window elapsed before the source completed — expected; fall through.
        }

        var stats = new Dictionary<BiosignalKind, BiosignalStats>(accumulator.Count);
        foreach (var (kind, acc) in accumulator)
        {
            stats[kind] = acc.ToStats();
        }

        return new BiosignalSnapshot(stats, generatedAt);
    }

    private sealed class Accumulator
    {
        private int _count;
        private float _min = float.PositiveInfinity;
        private float _max = float.NegativeInfinity;
        private double _sum;

        public void Add(float v)
        {
            _count++;
            if (v < _min) _min = v;
            if (v > _max) _max = v;
            _sum += v;
        }

        public BiosignalStats ToStats() =>
            new(_count, _min, _max, _count == 0 ? 0f : (float)(_sum / _count));
    }
}
