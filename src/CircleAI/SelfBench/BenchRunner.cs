// BenchRunner.cs
//
// (Phase E7) Runs a bench suite end-to-end against an IAIService. Times
// each task, applies the scoring strategy, aggregates pass-count + mean
// score + p50/p95 latency.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting;
using Microsoft.Extensions.Logging;

namespace CircleAI.SelfBench;

public sealed class BenchRunner
{
    private readonly Dictionary<string, IBenchScorer> _scorers;
    private readonly ILogger<BenchRunner>? _logger;

    public BenchRunner(IEnumerable<IBenchScorer>? extraScorers = null, ILogger<BenchRunner>? logger = null)
    {
        _logger  = logger;
        _scorers = new Dictionary<string, IBenchScorer>(StringComparer.OrdinalIgnoreCase)
        {
            ["exact"]             = new BuiltInScorers.ExactMatchScorer(),
            ["substring"]         = new BuiltInScorers.SubstringScorer(),
            ["regex"]             = new BuiltInScorers.RegexScorer(),
            ["numeric-tolerance"] = new BuiltInScorers.NumericToleranceScorer(),
        };
        if (extraScorers is not null)
            foreach (var s in extraScorers) _scorers[s.Name] = s;
    }

    public async Task<BenchSummary> RunAsync(
        string suiteId,
        IReadOnlyList<BenchTask> tasks,
        IAIService ai,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(ai);
        if (!ai.IsReady) await ai.StartAsync(ct).ConfigureAwait(false);

        var runId   = $"run-{suiteId}-{Guid.NewGuid():N}";
        var results = new ConcurrentBag<BenchResult>();
        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            var result = await RunOneAsync(task, ai, ct).ConfigureAwait(false);
            results.Add(result);
            _logger?.LogInformation("[SelfBench] {Suite}/{Task} score={Score:F2} pass={Passed} latency={Latency:F0}ms",
                task.Suite, task.Id, result.Score, result.Passed, result.LatencyMs);
        }

        var ordered = results.ToList();
        var perTaskScore = ordered.ToDictionary(r => r.TaskId, r => r.Score, StringComparer.Ordinal);
        var passCount  = ordered.Count(r => r.Passed);
        var meanScore  = ordered.Count > 0 ? ordered.Average(r => r.Score) : 0;
        var latencies  = ordered.Select(r => r.LatencyMs).OrderBy(x => x).ToArray();
        var p50        = Percentile(latencies, 0.50);
        var p95        = Percentile(latencies, 0.95);

        return new BenchSummary(
            runId, suiteId, ordered.Count, passCount, meanScore,
            p50, p95, perTaskScore, DateTimeOffset.UtcNow);
    }

    private async Task<BenchResult> RunOneAsync(BenchTask task, IAIService ai, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string actual;
        try
        {
            using var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            taskCts.CancelAfter(TimeSpan.FromMilliseconds(task.MaxLatencyMs));
            actual = await ai.AskAsync(task.Prompt, taskCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new BenchResult(task.Id, task.Suite, string.Empty, 0, sw.Elapsed.TotalMilliseconds,
                Passed: false, FailureReason: ex.GetType().Name + ": " + ex.Message);
        }
        sw.Stop();

        var scorer = ResolveScorer(task);
        var score  = scorer.Score(task.Expected, actual, task);
        var passed = score >= 1.0 - 1e-9;
        return new BenchResult(task.Id, task.Suite, actual ?? string.Empty, score, sw.Elapsed.TotalMilliseconds, passed);
    }

    private IBenchScorer ResolveScorer(BenchTask task)
    {
        if (task.Scoring == BenchScoring.CustomScorer && task.CustomScorerName is { } name)
        {
            if (_scorers.TryGetValue(name, out var custom)) return custom;
            throw new InvalidOperationException($"Custom scorer not registered: {name}");
        }
        return task.Scoring switch
        {
            BenchScoring.ExactMatch       => _scorers["exact"],
            BenchScoring.Substring        => _scorers["substring"],
            BenchScoring.Regex            => _scorers["regex"],
            BenchScoring.NumericTolerance => _scorers["numeric-tolerance"],
            _                             => _scorers["exact"],
        };
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        var idx = Math.Clamp((int)Math.Floor(p * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[idx];
    }
}
