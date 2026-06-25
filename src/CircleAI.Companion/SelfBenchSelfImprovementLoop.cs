// SelfBenchSelfImprovementLoop.cs
//
// (Phase E7) Implements HER/Jarvis ISelfImprovementLoop by orchestrating
// CircleAI.SelfBench: run the named suite against the current AIService
// as baseline, ask the host for a candidate AIService (e.g. one with a
// freshly-trained LoRA adapter), A/B compare, and only "apply" the
// candidate if the regression gate passes.
//
// The "apply candidate" step is a host-supplied callback so this class
// stays free of MNN/adapter-management plumbing — it just runs the gate.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion.HerJarvis;
using CircleAI.Hosting;
using CircleAI.SelfBench;

namespace CircleAI.Companion;

public sealed class SelfBenchSelfImprovementLoop : ISelfImprovementLoop
{
    private readonly BenchSuiteRegistry  _registry;
    private readonly AbBenchRunner       _runner;
    private readonly Func<CancellationToken, ValueTask<IAIService>> _baselineFactory;
    private readonly Func<CancellationToken, ValueTask<IAIService>> _candidateFactory;
    private readonly Func<AbVerdict, CancellationToken, ValueTask>   _onPromote;
    private readonly RegressionGateConfig _gate;
    private readonly ConcurrentDictionary<string, double> _bestScores = new(StringComparer.Ordinal);

    public SelfBenchSelfImprovementLoop(
        BenchSuiteRegistry registry,
        AbBenchRunner runner,
        Func<CancellationToken, ValueTask<IAIService>> baselineFactory,
        Func<CancellationToken, ValueTask<IAIService>> candidateFactory,
        Func<AbVerdict, CancellationToken, ValueTask>?  onPromote = null,
        RegressionGateConfig? gate = null)
    {
        _registry         = registry         ?? throw new ArgumentNullException(nameof(registry));
        _runner           = runner           ?? throw new ArgumentNullException(nameof(runner));
        _baselineFactory  = baselineFactory  ?? throw new ArgumentNullException(nameof(baselineFactory));
        _candidateFactory = candidateFactory ?? throw new ArgumentNullException(nameof(candidateFactory));
        _onPromote        = onPromote        ?? ((_, _) => ValueTask.CompletedTask);
        _gate             = gate             ?? new RegressionGateConfig();
    }

    public async ValueTask<SelfImprovementVerdict> CycleAsync(string benchSuiteId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(benchSuiteId)) benchSuiteId = "default";
        var tasks = _registry.Get(benchSuiteId);
        if (tasks.Count == 0)
            return new SelfImprovementVerdict("skipped: no tasks in suite", 0.0);

        var baseline  = await _baselineFactory(ct).ConfigureAwait(false);
        var candidate = await _candidateFactory(ct).ConfigureAwait(false);

        var verdict = await _runner.CompareAsync(benchSuiteId, tasks, baseline, candidate, _gate, ct)
            .ConfigureAwait(false);

        var newScore = verdict.CandidateSummary.MeanScore;
        var applied  = "no change";
        if (verdict.ShouldPromote)
        {
            await _onPromote(verdict, ct).ConfigureAwait(false);
            _bestScores.AddOrUpdate(benchSuiteId, newScore, (_, prev) => Math.Max(prev, newScore));
            applied = $"promoted candidate ({verdict.Reason})";
        }
        else
        {
            applied = $"rejected ({verdict.Reason})";
        }
        return new SelfImprovementVerdict(applied, newScore);
    }
}
