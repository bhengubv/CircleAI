// AbBenchRunner.cs
//
// (Phase E7) A/B comparison: runs the same bench suite against a baseline
// and a candidate IAIService and produces a verdict (promote / reject).
// The verdict is gated by a RegressionGate which can refuse to promote
// even if the overall mean score went up — e.g. when any critical task
// regresses.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting;
using Microsoft.Extensions.Logging;

namespace CircleAI.SelfBench;

/// <summary>(Phase E7) Configuration for the regression gate.</summary>
public sealed record RegressionGateConfig(
    double MinMeanScoreImprovement  = 0.01,
    double MaxP95LatencyRegressionMs = 250.0,
    // Allow at most this many critical-task regressions before refusing.
    int    MaxCriticalRegressions    = 0);

/// <summary>(Phase E7) Verdict returned by <see cref="AbBenchRunner"/>.</summary>
public sealed record AbVerdict(
    bool                  ShouldPromote,
    BenchSummary          BaselineSummary,
    BenchSummary          CandidateSummary,
    double                MeanScoreDelta,
    double                P95LatencyDeltaMs,
    IReadOnlyList<string> CriticalRegressions,
    string                Reason);

public sealed class AbBenchRunner
{
    private readonly BenchRunner _runner;
    private readonly ILogger<AbBenchRunner>? _logger;

    public AbBenchRunner(BenchRunner runner, ILogger<AbBenchRunner>? logger = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _logger = logger;
    }

    public async Task<AbVerdict> CompareAsync(
        string suiteId,
        IReadOnlyList<BenchTask> tasks,
        IAIService baseline,
        IAIService candidate,
        RegressionGateConfig? gate = null,
        CancellationToken ct = default)
    {
        gate ??= new RegressionGateConfig();
        var baseSummary      = await _runner.RunAsync(suiteId + "@baseline", tasks, baseline, ct).ConfigureAwait(false);
        var candidateSummary = await _runner.RunAsync(suiteId + "@candidate", tasks, candidate, ct).ConfigureAwait(false);

        var meanDelta    = candidateSummary.MeanScore   - baseSummary.MeanScore;
        var p95Delta     = candidateSummary.P95LatencyMs - baseSummary.P95LatencyMs;
        var criticals    = tasks.Where(t => t.IsCritical).ToList();
        var criticalReg  = new List<string>();
        foreach (var crit in criticals)
        {
            var baseScore = baseSummary.PerTaskScore.GetValueOrDefault(crit.Id, 0.0);
            var candScore = candidateSummary.PerTaskScore.GetValueOrDefault(crit.Id, 0.0);
            if (candScore < baseScore - 1e-9) criticalReg.Add(crit.Id);
        }

        var promote =
            meanDelta >= gate.MinMeanScoreImprovement
         && p95Delta  <= gate.MaxP95LatencyRegressionMs
         && criticalReg.Count <= gate.MaxCriticalRegressions;

        var reason = promote
            ? $"+{meanDelta:F3} mean, p95 Δ {p95Delta:F0}ms, {criticalReg.Count} critical regressions"
            : BuildRejectionReason(meanDelta, p95Delta, criticalReg, gate);

        _logger?.LogInformation("[SelfBench] A/B verdict: promote={Promote} reason={Reason}", promote, reason);
        return new AbVerdict(promote, baseSummary, candidateSummary, meanDelta, p95Delta, criticalReg, reason);
    }

    private static string BuildRejectionReason(
        double meanDelta, double p95Delta, IReadOnlyList<string> criticals, RegressionGateConfig gate)
    {
        var reasons = new List<string>();
        if (meanDelta < gate.MinMeanScoreImprovement)
            reasons.Add($"mean score Δ {meanDelta:F3} below threshold {gate.MinMeanScoreImprovement:F3}");
        if (p95Delta > gate.MaxP95LatencyRegressionMs)
            reasons.Add($"p95 latency regression {p95Delta:F0}ms > {gate.MaxP95LatencyRegressionMs:F0}ms");
        if (criticals.Count > gate.MaxCriticalRegressions)
            reasons.Add($"{criticals.Count} critical regressions: {string.Join(',', criticals)}");
        return reasons.Count == 0 ? "rejected" : string.Join("; ", reasons);
    }
}
