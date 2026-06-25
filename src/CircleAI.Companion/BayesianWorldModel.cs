// BayesianWorldModel.cs
//
// (Phase E3) Replaces FrequencyWorldModel with a real probabilistic
// graphical model: an online-learning Naive Bayes classifier over
// (observations → outcome) pairs.
//
// At predict time we evaluate, for every previously-seen outcome:
//   P(outcome | obs) ∝ P(outcome) · ∏ P(obs_i | outcome)
// using Laplace smoothing so unseen pairs don't zero-out.
//
// The model is a real probabilistic graphical model — small but honest.
// Hosts that need richer structure (e.g. continuous variables, causal
// interventions) can swap in Microsoft.ML.Probabilistic without changing
// the IWorldModel contract.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion.HerJarvis;

namespace CircleAI.Companion;

public sealed class BayesianWorldModel : IWorldModel
{
    private readonly ConcurrentDictionary<string, long> _outcomeCounts = new(StringComparer.OrdinalIgnoreCase);
    // (outcome, observation) -> count
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _condCounts
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _vocab = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _vocabLock = new();
    private long _totalObservations;
    private readonly double _alpha; // Laplace smoothing strength

    public BayesianWorldModel(double laplaceAlpha = 1.0)
    {
        if (laplaceAlpha <= 0) throw new ArgumentOutOfRangeException(nameof(laplaceAlpha));
        _alpha = laplaceAlpha;
    }

    /// <summary>(Phase E3) Update the model with one (observations → outcome) example.</summary>
    public void Observe(IEnumerable<string> observations, string outcome)
    {
        ArgumentNullException.ThrowIfNull(observations);
        if (string.IsNullOrWhiteSpace(outcome)) throw new ArgumentException("outcome required");

        _outcomeCounts.AddOrUpdate(outcome, 1, (_, v) => v + 1);
        Interlocked.Increment(ref _totalObservations);

        var cond = _condCounts.GetOrAdd(outcome, _ => new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase));
        foreach (var obs in observations)
        {
            if (string.IsNullOrWhiteSpace(obs)) continue;
            cond.AddOrUpdate(obs, 1, (_, v) => v + 1);
            lock (_vocabLock) _vocab.Add(obs);
        }
    }

    public ValueTask<CausalPrediction> PredictAsync(string scenarioJson, CancellationToken ct = default)
    {
        var observations = ExtractObservations(scenarioJson);
        if (observations.Count == 0 || _outcomeCounts.IsEmpty)
            return ValueTask.FromResult(new CausalPrediction("unknown", 0.5, Array.Empty<string>()));

        var vocabSize = Math.Max(1, _vocab.Count);
        var totalEx   = Math.Max(1, _totalObservations);

        var scored = new List<(string Outcome, double LogPosterior)>();
        foreach (var (outcome, outcomeCount) in _outcomeCounts)
        {
            ct.ThrowIfCancellationRequested();
            // Log P(outcome) — Laplace-smoothed prior.
            var logPrior = Math.Log((outcomeCount + _alpha) / (totalEx + _alpha * _outcomeCounts.Count));

            var cond = _condCounts.TryGetValue(outcome, out var inner)
                ? inner : new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var totalForOutcome = cond.Values.Sum();
            var logLikelihood = 0.0;
            foreach (var obs in observations)
            {
                cond.TryGetValue(obs, out var n);
                var p = (n + _alpha) / (totalForOutcome + _alpha * vocabSize);
                logLikelihood += Math.Log(p);
            }
            scored.Add((outcome, logPrior + logLikelihood));
        }

        // Softmax over log-posteriors for normalised probability.
        var maxLogPost = scored.Max(s => s.LogPosterior);
        var expSum     = scored.Sum(s => Math.Exp(s.LogPosterior - maxLogPost));
        var top        = scored.OrderByDescending(s => s.LogPosterior).First();
        var prob       = Math.Exp(top.LogPosterior - maxLogPost) / expSum;
        return ValueTask.FromResult(new CausalPrediction(top.Outcome, prob, observations));
    }

    private static IReadOnlyList<string> ExtractObservations(string scenarioJson)
    {
        if (string.IsNullOrWhiteSpace(scenarioJson)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(scenarioJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
            var hits = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                hits.Add(prop.Name + "=" + prop.Value.ToString());
            }
            return hits;
        }
        catch (JsonException) { return Array.Empty<string>(); }
    }
}
