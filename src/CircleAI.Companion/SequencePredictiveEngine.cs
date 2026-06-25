// SequencePredictiveEngine.cs
//
// (Phase E4) Replaces HistogramPredictiveEngine with a real online
// sequence model: a variable-order Markov chain (3-gram) over the user's
// observed event timeline. Predicts the next likely events by sampling
// forward from the current context and aggregating probable arrivals
// inside the requested horizon.
//
// Hosts that want a neural sequence model (LSTM / small Transformer)
// can swap one in behind the same IPredictiveEngine contract.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion.HerJarvis;

namespace CircleAI.Companion;

public sealed class SequencePredictiveEngine : IPredictiveEngine
{
    // (previous-n-events tuple) -> { next event -> count }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _transitions =
        new(StringComparer.Ordinal);
    // Per-event mean interval (s) for forecasting.
    private readonly ConcurrentDictionary<string, (long Count, double SumSeconds)> _interArrivals =
        new(StringComparer.Ordinal);
    private readonly List<(string Event, DateTimeOffset AtUtc)> _history = new();
    private readonly int _order;
    private readonly object _historyLock = new();

    public SequencePredictiveEngine(int order = 3)
    {
        if (order < 1 || order > 6) throw new ArgumentOutOfRangeException(nameof(order));
        _order = order;
    }

    /// <summary>(Phase E4) Add one event to the user timeline.</summary>
    public void Observe(string @event, DateTimeOffset atUtc)
    {
        if (string.IsNullOrWhiteSpace(@event)) throw new ArgumentException("event required");
        lock (_historyLock)
        {
            _history.Add((@event, atUtc));
            // Build n-gram contexts up to _order.
            for (var k = 1; k <= _order && _history.Count > k; k++)
            {
                var contextStart = _history.Count - 1 - k;
                if (contextStart < 0) break;
                var contextItems = _history.GetRange(contextStart, k).Select(e => e.Event);
                var key = string.Join("|", contextItems);
                var bucket = _transitions.GetOrAdd(key, _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal));
                bucket.AddOrUpdate(@event, 1, (_, v) => v + 1);
            }
            // Track inter-arrival time for this event.
            if (_history.Count >= 2)
            {
                var last = _history[^2];
                if (last.Event == @event)
                {
                    var gap = (atUtc - last.AtUtc).TotalSeconds;
                    _interArrivals.AddOrUpdate(@event,
                        (1, gap),
                        (_, prev) => (prev.Count + 1, prev.SumSeconds + gap));
                }
            }
        }
    }

    public ValueTask<IReadOnlyList<AnticipatedNeed>> AnticipateAsync(int horizonMinutes, CancellationToken ct = default)
    {
        if (horizonMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(horizonMinutes));

        List<(string Event, DateTimeOffset AtUtc)> snapshot;
        lock (_historyLock) snapshot = _history.ToList();
        if (snapshot.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<AnticipatedNeed>>(Array.Empty<AnticipatedNeed>());

        // Take the most recent _order events as the prediction context.
        var contextLen = Math.Min(_order, snapshot.Count);
        var context = snapshot.GetRange(snapshot.Count - contextLen, contextLen).Select(e => e.Event).ToList();

        var totalScore = new Dictionary<string, double>(StringComparer.Ordinal);
        // Walk down from longest context to shortest (back-off), weighting longer contexts higher.
        for (var k = context.Count; k >= 1; k--)
        {
            var key = string.Join("|", context.GetRange(context.Count - k, k));
            if (!_transitions.TryGetValue(key, out var bucket)) continue;
            var totalForCtx = bucket.Values.Sum();
            if (totalForCtx == 0) continue;
            var weight = Math.Pow(2, k);
            foreach (var (next, count) in bucket)
            {
                var prob = (double)count / totalForCtx;
                totalScore[next] = totalScore.TryGetValue(next, out var prev) ? prev + weight * prob : weight * prob;
            }
        }

        if (totalScore.Count == 0)
            return ValueTask.FromResult<IReadOnlyList<AnticipatedNeed>>(Array.Empty<AnticipatedNeed>());

        var totalWeight = totalScore.Values.Sum();
        var horizonSec  = horizonMinutes * 60.0;
        var now         = DateTimeOffset.UtcNow;
        var anticipated = new List<AnticipatedNeed>();
        foreach (var (ev, raw) in totalScore.OrderByDescending(kv => kv.Value))
        {
            var prob = raw / totalWeight;
            if (prob <= 0) continue;
            // Use the event's mean inter-arrival to estimate when it'll happen.
            var (cnt, sumSec) = _interArrivals.GetValueOrDefault(ev);
            var meanInterval = cnt > 0 ? sumSec / cnt : horizonSec * 0.5;
            if (meanInterval > horizonSec) continue;  // not expected within window
            anticipated.Add(new AnticipatedNeed(
                Description:    ev,
                ExpectedByUtc:  now.AddSeconds(meanInterval),
                Probability:    prob));
        }
        return ValueTask.FromResult<IReadOnlyList<AnticipatedNeed>>(anticipated);
    }
}
