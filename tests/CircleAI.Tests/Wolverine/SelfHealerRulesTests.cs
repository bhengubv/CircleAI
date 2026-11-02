// SelfHealerRulesTests.cs
//
// The loop with rules first (S6), driven with fakes: a known fault is fixed by its rule's ladder
// without the brain; a remedy at its limit hands the ladder on; what worked here before is tried
// first; the brain is never asked about its own failure; a remedy already run where the failure
// happened is recorded, not run again; and an unknown fault is offered to the brain with the list
// of remedies that exist. The pre-S6 behaviour stays pinned by SelfHealerTests.

using CircleAI.Assistant.Lifecycle;
using CircleAI.Core.Logging;
using CircleAI.Hosting.SelfHealing;
using Xunit;
using static CircleAI.Tests.WolverineEvents;

namespace CircleAI.Tests;

public sealed class SelfHealerRulesTests
{
    private readonly WolverineAnalyst _analyst = new();
    private readonly WolverineLog _log = new();
    private readonly WolverinePolicy _policy = new();

    private SelfHealer Healer(Func<string, string?>? remembered = null, params ISafeFix[] fixes) =>
        new(_analyst, _log, fixes, _policy, null, rules: null, remembered: remembered);

    private static FailureContext Failure(HealthEvent e) => DeviceWolverine.ToFailure(e);

    [Fact]
    public async Task A_known_fault_is_fixed_by_its_rule_without_asking_the_brain()
    {
        var bits = new WolverineFix(Remedies.FewerMemoryBits);
        var e = Engine(nameof(FailureKind.OutOfMemory), WorkPhase.Decode);

        var record = await Healer(null, bits).HealAsync(Failure(e));

        Assert.Equal(0, _analyst.Calls);
        Assert.Equal(1, bits.Calls);
        Assert.Equal(HealingOutcome.Healed, record.Outcome);
        Assert.Equal(Remedies.FewerMemoryBits, record.Category);
        Assert.Equal(e.Fingerprint, record.Signature);   // the device's key is WolverineAPI's key
        Assert.False(record.NeedsHuman);
    }

    [Fact]
    public async Task A_remedy_at_its_limit_hands_the_ladder_on_to_the_next()
    {
        var bits = new WolverineFix(Remedies.FewerMemoryBits, result: false);
        var context = new WolverineFix(Remedies.ShorterContext);
        var brain = new WolverineFix(Remedies.SmallerBrain);

        var record = await Healer(null, bits, context, brain).HealAsync(Failure(Engine(nameof(FailureKind.OutOfMemory), WorkPhase.Decode)));

        Assert.Equal((1, 1, 0), (bits.Calls, context.Calls, brain.Calls));
        Assert.Equal(Remedies.ShorterContext, record.Category);
        Assert.Equal(HealingOutcome.Healed, record.Outcome);
    }

    [Fact]
    public async Task Every_remedy_at_its_limit_is_handed_to_a_person()
    {
        var record = await Healer(null,
                new WolverineFix(Remedies.FewerMemoryBits, false), new WolverineFix(Remedies.ShorterContext, false),
                new WolverineFix(Remedies.SmallerBrain, false))
            .HealAsync(Failure(Engine(nameof(FailureKind.OutOfMemory), WorkPhase.Decode)));

        Assert.Equal(HealingOutcome.Failed, record.Outcome);
        Assert.True(record.NeedsHuman);
        Assert.Equal(0, _analyst.Calls);
    }

    [Fact]
    public async Task What_worked_on_this_device_before_is_tried_first()
    {
        var bits = new WolverineFix(Remedies.FewerMemoryBits);
        var context = new WolverineFix(Remedies.ShorterContext);
        var e = Engine(nameof(FailureKind.OutOfMemory), WorkPhase.Decode);

        var record = await Healer(f => f == e.Fingerprint ? Remedies.ShorterContext : null, bits, context).HealAsync(Failure(e));

        Assert.Equal((0, 1), (bits.Calls, context.Calls));
        Assert.Equal(Remedies.ShorterContext, record.Category);
    }

    [Fact]
    public async Task A_remembered_remedy_that_is_not_on_the_ladder_changes_nothing()
    {
        var bits = new WolverineFix(Remedies.FewerMemoryBits);
        var kernels = new WolverineFix(Remedies.ScalarKernels);

        var record = await Healer(_ => Remedies.ScalarKernels, bits, kernels)
            .HealAsync(Failure(Engine(nameof(FailureKind.OutOfMemory), WorkPhase.Decode)));

        Assert.Equal((1, 0), (bits.Calls, kernels.Calls));
        Assert.Equal(Remedies.FewerMemoryBits, record.Category);
    }

    [Fact]
    public async Task The_brain_is_never_asked_about_its_own_failure()
    {
        var record = await Healer(null, new WolverineFix(Remedies.Cache))
            .HealAsync(Failure(Engine(nameof(FailureKind.Unknown), WorkPhase.Load)));

        Assert.Equal(0, _analyst.Calls);
        Assert.Equal(HealingOutcome.Recorded, record.Outcome);   // reported, not put to a person who can do nothing
        Assert.False(record.NeedsHuman);
    }

    [Theory]
    [InlineData(true, HealingOutcome.Healed)]
    [InlineData(false, HealingOutcome.Failed)]
    public async Task A_remedy_run_where_it_happened_is_recorded_and_not_run_again(bool worked, HealingOutcome outcome)
    {
        var retry = new WolverineFix(Remedies.ToolCallRetry);
        var e = HealthEvents.Of(HealingRules.ToolsArea, "tool call", "MalformedToolCall", "AIService.AgenticChatAsync")
            with { Remedy = Remedies.ToolCallRetry, Worked = worked };

        var record = await Healer(null, retry).HealAsync(Failure(e));

        Assert.Equal(0, retry.Calls);
        Assert.Equal(0, _analyst.Calls);
        Assert.Equal(outcome, record.Outcome);
        Assert.Equal(!worked, record.NeedsHuman);
        Assert.Equal(Remedies.ToolCallRetry, record.Category);
    }

    [Fact]
    public async Task Suggest_only_names_the_rules_remedy_and_runs_nothing()
    {
        _policy.AutoFix = false;
        var kernels = new WolverineFix(Remedies.ScalarKernels);

        var record = await Healer(null, kernels).HealAsync(Failure(Engine("IllegalInstruction", WorkPhase.Decode)));

        Assert.Equal(0, kernels.Calls);
        Assert.Equal(HealingOutcome.Escalated, record.Outcome);
        Assert.Contains(Remedies.ScalarKernels, record.ActionTaken);
    }

    [Fact]
    public async Task A_rule_with_no_remedy_wired_here_is_handed_on()
    {
        var record = await Healer(null, new WolverineFix(Remedies.Cache)).HealAsync(Failure(Engine("IllegalInstruction", WorkPhase.Decode)));

        Assert.Equal(HealingOutcome.Escalated, record.Outcome);
        Assert.Equal(0, _analyst.Calls);
    }

    [Fact]
    public async Task An_unknown_fault_is_put_to_the_brain_with_the_list_of_remedies_that_exist()
    {
        _analyst.Next = new HealingVerdict(HealingKind.QuickFix, Remedies.Cache, "a stale cache");
        var cache = new WolverineFix(Remedies.Cache);
        var threads = new WolverineFix(Remedies.FewerThreads);
        var e = HealthEvents.From("Voice", "speak", new InvalidOperationException("the voice cache is stale"));

        var record = await Healer(null, cache, threads).HealAsync(Failure(e));

        Assert.Equal(1, _analyst.Calls);
        Assert.Equal(new[] { Remedies.Cache, Remedies.FewerThreads }, _analyst.Last!.Remedies);
        Assert.Equal(1, cache.Calls);
        Assert.Equal(HealingOutcome.Healed, record.Outcome);
    }

    [Fact]
    public async Task Applied_names_the_failure_and_the_remedy_after_the_record_is_written()
    {
        var healer = Healer(null, new WolverineFix(Remedies.ScalarKernels));
        (FailureContext Failure, string Remedy)? seen = null;
        healer.Applied += (f, r) => seen = (f, r);
        var e = Engine("IllegalInstruction", WorkPhase.Decode);

        await healer.HealAsync(Failure(e));

        Assert.NotNull(seen);
        Assert.Equal(Remedies.ScalarKernels, seen!.Value.Remedy);
        Assert.Same(e, seen.Value.Failure.Event);
        Assert.Single(_log.Records);
    }

    [Fact]
    public async Task The_same_fault_three_times_in_a_window_is_escalated_rather_than_turned_again()
    {
        var kernels = new WolverineFix(Remedies.ScalarKernels);
        var healer = Healer(null, kernels);
        HealingRecord? last = null;
        for (var i = 0; i < 4; i++) last = await healer.HealAsync(Failure(Engine("IllegalInstruction", WorkPhase.Decode)));

        Assert.Equal(3, kernels.Calls);
        Assert.Equal(HealingOutcome.Escalated, last!.Outcome);
    }
}
