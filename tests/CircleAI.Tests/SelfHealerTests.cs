// SelfHealerTests.cs
//
// The self-heal loop, driven with fakes — no brain, no device. Proves the rules that
// keep it safe: it records every failure, only auto-runs a safe fix at the top
// autonomy, never calls the brain cold, never loop-heals the same failure, and never
// throws — every path ends in a recorded outcome.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting.SelfHealing;
using Xunit;

namespace CircleAI.Tests;

public sealed class SelfHealerTests
{
    // ── fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeAnalyst : IFailureAnalyst
    {
        public int Calls;
        public HealingVerdict Next = HealingVerdict.NeedsAHuman("nothing set");
        public bool Throw;
        public Task<HealingVerdict> AnalyseAsync(FailureContext failure, CancellationToken ct = default)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("analyst boom");
            return Task.FromResult(Next);
        }
    }

    private sealed class FakeLog : IHealingLog
    {
        public readonly List<HealingRecord> Records = new();
        public bool ThrowOnAppend;
        public Task AppendAsync(HealingRecord record, CancellationToken ct = default)
        {
            if (ThrowOnAppend) throw new InvalidOperationException("db down");
            Records.Add(record);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<HealingRecord>> RecentAsync(int limit = 100, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<HealingRecord>>(Records);
        public Task MarkHandledAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountSinceAsync(DateTimeOffset since, CancellationToken ct = default)
            => Task.FromResult(Records.Count);
    }

    private sealed class FakeFix : ISafeFix
    {
        public FakeFix(string category) => Category = category;
        public string Category { get; }
        public int Calls;
        public bool Result = true;
        public bool Throw;
        public Task<bool> TryFixAsync(FailureContext failure, CancellationToken ct = default)
        {
            Calls++;
            if (Throw) throw new InvalidOperationException("fix boom");
            return Task.FromResult(Result);
        }
    }

    private sealed class FakePolicy : ISelfHealPolicy
    {
        public bool Analyse = true, AutoFix = true, Ready = true;
        public Task<bool> ShouldAnalyseAsync(CancellationToken ct = default) => Task.FromResult(Analyse);
        public Task<bool> MayAutoFixAsync(CancellationToken ct = default) => Task.FromResult(AutoFix);
        public Task<bool> BrainReadyAsync(CancellationToken ct = default) => Task.FromResult(Ready);
    }

    private static HealingVerdict Quick(string category = "cache")
        => new(HealingKind.QuickFix, category, "safe to retry", RecommendedAction: "retry");
    private static FailureContext Fail(string message = "boom", string source = "Test")
        => new(message, Source: source);

    private static (SelfHealer Healer, FakeAnalyst A, FakeLog L, FakeFix F, FakePolicy P) Build(string fixCategory = "cache")
    {
        var a = new FakeAnalyst();
        var l = new FakeLog();
        var f = new FakeFix(fixCategory);
        var p = new FakePolicy();
        return (new SelfHealer(a, l, new[] { f }, p), a, l, f, p);
    }

    // ── behaviour ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Records_every_failure_even_when_autonomy_is_off()
    {
        var (healer, a, l, f, p) = Build();
        p.Analyse = false;   // Off

        var record = await healer.HealAsync(Fail());

        Assert.Single(l.Records);
        Assert.Equal(HealingOutcome.Recorded, record.Outcome);
        Assert.Equal(0, a.Calls);   // never diagnosed
        Assert.Equal(0, f.Calls);   // never fixed
        Assert.False(record.NeedsHuman);
    }

    [Fact]
    public async Task SuggestOnly_analyses_and_escalates_without_running_a_fix()
    {
        var (healer, a, l, f, p) = Build();
        p.AutoFix = false;   // suggest-only
        a.Next = Quick();

        var record = await healer.HealAsync(Fail());

        Assert.Equal(1, a.Calls);
        Assert.Equal(0, f.Calls);
        Assert.Equal(HealingOutcome.Escalated, record.Outcome);
        Assert.True(record.NeedsHuman);
    }

    [Fact]
    public async Task AutoFixSafe_runs_a_matching_quickfix()
    {
        var (healer, a, l, f, p) = Build(fixCategory: "cache");
        a.Next = Quick("stale-cache");   // matches "cache"

        var record = await healer.HealAsync(Fail());

        Assert.Equal(1, f.Calls);
        Assert.Equal(HealingOutcome.Healed, record.Outcome);
        Assert.False(record.NeedsHuman);
    }

    [Fact]
    public async Task A_patch_is_escalated_never_auto_fixed()
    {
        var (healer, a, l, f, p) = Build();
        a.Next = new HealingVerdict(HealingKind.Patch, "null-ref", "add a guard");

        var record = await healer.HealAsync(Fail());

        Assert.Equal(0, f.Calls);
        Assert.Equal(HealingOutcome.Escalated, record.Outcome);
        Assert.True(record.NeedsHuman);
    }

    [Fact]
    public async Task A_fix_that_does_not_take_escalates_as_failed()
    {
        var (healer, a, l, f, p) = Build();
        a.Next = Quick();
        f.Result = false;

        var record = await healer.HealAsync(Fail());

        Assert.Equal(1, f.Calls);
        Assert.Equal(HealingOutcome.Failed, record.Outcome);
        Assert.True(record.NeedsHuman);
    }

    [Fact]
    public async Task A_quickfix_with_no_matching_remedy_is_escalated()
    {
        var (healer, a, l, f, p) = Build(fixCategory: "cache");
        a.Next = Quick("transient-network");   // no matching fix

        var record = await healer.HealAsync(Fail());

        Assert.Equal(0, f.Calls);
        Assert.Equal(HealingOutcome.Escalated, record.Outcome);
    }

    [Fact]
    public async Task A_cold_brain_is_never_asked_to_diagnose()
    {
        var (healer, a, l, f, p) = Build();
        p.Ready = false;

        var record = await healer.HealAsync(Fail());

        Assert.Equal(0, a.Calls);   // the whole point of the gate
        Assert.Equal(HealingOutcome.AwaitingBrain, record.Outcome);
        Assert.True(record.NeedsHuman);
    }

    [Fact]
    public async Task A_recurring_failure_is_escalated_rather_than_re_healed()
    {
        var (healer, a, l, f, p) = Build();
        a.Next = Quick();

        // Same signature four times in the window: the first three heal, the fourth
        // is escalated as recurring — and the analyst is not called a fourth time.
        HealingRecord? last = null;
        for (var i = 0; i < 4; i++) last = await healer.HealAsync(Fail());

        Assert.Equal(3, a.Calls);
        Assert.Equal(3, f.Calls);
        Assert.Equal(HealingOutcome.Escalated, last!.Outcome);
        Assert.Contains("recurring", last.ActionTaken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_never_throws_when_the_analyst_throws()
    {
        var (healer, a, l, f, p) = Build();
        a.Throw = true;

        var record = await healer.HealAsync(Fail());   // must not throw

        Assert.Equal(HealingOutcome.Escalated, record.Outcome);
        Assert.Single(l.Records);
    }

    [Fact]
    public async Task It_never_throws_when_the_log_throws()
    {
        var (healer, a, l, f, p) = Build();
        a.Next = Quick();
        l.ThrowOnAppend = true;

        var record = await healer.HealAsync(Fail());   // must not throw despite a dead log

        Assert.Equal(HealingOutcome.Healed, record.Outcome);
        Assert.Empty(l.Records);
    }

    [Fact]
    public async Task It_raises_HealingChanged_after_a_heal()
    {
        var (healer, a, l, f, p) = Build();
        a.Next = Quick();
        var fired = 0;
        healer.HealingChanged += () => fired++;

        await healer.HealAsync(Fail());

        Assert.Equal(1, fired);
    }

    [Fact]
    public void The_fire_and_forget_Heal_does_not_throw()
    {
        var (healer, a, l, f, p) = Build();
        var ex = Record.Exception(() => healer.Heal(new InvalidOperationException("x"), "Test"));
        Assert.Null(ex);
    }
}
