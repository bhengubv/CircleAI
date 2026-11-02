// HealingKnobsTests.cs
//
// The engine's knobs as Wolverine turns them (S6): each a notch on one of SelfHeal's switches,
// each written to BrainTuning's file - which is what the process that builds the brain reads -
// and each checked by reading that file back. A knob at its floor declines so the ladder moves
// on; the window running out puts the brain back at full; a brain is stepped down from only when
// another is there; a damaged file is fetched again once a window; a stalled download alternates
// its two addresses.

using CircleAI.Assistant;
using CircleAI.Core;
using CircleAI.Core.Logging;
using CircleAI.Hosting.SelfHealing;
using CircleAI.Inference;
using CircleAI.Inference.Managed.Kernels;
using CircleAI.SelfKnowledge;
using Xunit;
using static CircleAI.Tests.WolverineEvents;

namespace CircleAI.Tests;

[Collection(nameof(WolverineCollection))]
public sealed class HealingKnobsTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = TempDir("knobs");
    private readonly WolverineClock _clock = new(Noon);
    private readonly List<string> _said = [];
    private readonly HealingKnobs _knobs;

    public HealingKnobsTests()
    {
        Reset();
        BrainTuning.Directory = _dir;
        _knobs = new HealingKnobs(fullThreads: 3, fullContext: 4096, _clock);
    }

    public void Dispose()
    {
        Reset();
        Delete(_dir);
    }

    private KnobFix Knob(string remedy, string switchName, string? sentence = null) =>
        new(remedy, switchName, _knobs, sentence, _said.Add, null, _clock);

    private static FailureContext Oom() =>
        DeviceWolverine.ToFailure(Engine(nameof(CircleAI.Assistant.Lifecycle.FailureKind.OutOfMemory), CircleAI.Assistant.Lifecycle.WorkPhase.Decode));

    // ── the ladders ──────────────────────────────────────────────────────────

    [Fact]
    public void Each_notch_turns_a_knob_further_and_none_goes_past_its_floor()
    {
        Assert.Equal(new[] { 3, 2, 1, 1 }, new[] { 0, 1, 2, 3 }.Select(n => HealingKnobs.ThreadsAt(3, n)));
        Assert.Equal(new[] { 8, 6, 4, 2, 1 }, new[] { 0, 1, 2, 3, 4 }.Select(n => HealingKnobs.ThreadsAt(8, n)));
        Assert.Equal(new[] { KvCompressionMode.TurboQuant4Bit, KvCompressionMode.TurboQuant3Bit, KvCompressionMode.TurboQuant2Bit, KvCompressionMode.TurboQuant2Bit },
            new[] { 0, 1, 2, 3 }.Select(HealingKnobs.MemoryAt));
        Assert.Equal(new[] { 4096, 2048, 1024, 1024 }, new[] { 0, 1, 2, 3 }.Select(n => HealingKnobs.ContextAt(4096, n)));
        Assert.Equal(new[] { 0, 1024, 512, 256, 256 }, new[] { 0, 1, 2, 3, 4 }.Select(HealingKnobs.ThinkingAt));
    }

    [Fact]
    public void A_notch_that_changes_nothing_is_no_notch()
    {
        var single = new HealingKnobs(fullThreads: 1, fullContext: HealingKnobs.MinContext, _clock);

        Assert.Null(single.ValueAt(SelfHeal.Switch.BrainThreads, 1));
        Assert.Null(single.ValueAt(SelfHeal.Switch.LongContext, 3));
        Assert.True(single.AtFloor(SelfHeal.Switch.BrainThreads, Noon));
    }

    // ── a knob, turned and checked ───────────────────────────────────────────

    [Fact]
    public async Task A_knob_turned_down_is_what_the_brain_will_be_built_with()
    {
        Assert.True(await Knob(Remedies.FewerThreads, SelfHeal.Switch.BrainThreads).TryFixAsync(Oom()));

        var tuning = BrainTuning.Current(Noon);
        Assert.Equal(2, tuning.MaxThreads);
        Assert.Equal(2, tuning.Threads(3));
        Assert.True(SelfHeal.IsOff(SelfHeal.Switch.BrainThreads, Noon));
        Assert.Equal(Noon + SelfHeal.OffFor(1), BrainTuning.ReadKnobs().Single().UntilUtc);
    }

    [Fact]
    public async Task Each_trip_is_one_notch_further_for_a_longer_window()
    {
        var fix = Knob(Remedies.FewerMemoryBits, SelfHeal.Switch.WorkingMemoryBits);
        Assert.True(await fix.TryFixAsync(Oom()));
        Assert.Equal(KvCompressionMode.TurboQuant3Bit, BrainTuning.Current(Noon).WorkingMemory);

        Assert.True(await fix.TryFixAsync(Oom()));
        Assert.Equal(KvCompressionMode.TurboQuant2Bit, BrainTuning.Current(Noon).WorkingMemory);
        Assert.Equal(Noon + SelfHeal.OffFor(2), BrainTuning.ReadKnobs().Single().UntilUtc);
    }

    [Fact]
    public async Task A_knob_at_its_floor_declines_so_the_ladder_moves_on()
    {
        var fix = Knob(Remedies.FewerThreads, SelfHeal.Switch.BrainThreads);
        Assert.True(await fix.TryFixAsync(Oom()));    // 2
        Assert.True(await fix.TryFixAsync(Oom()));    // 1
        Assert.False(await fix.TryFixAsync(Oom()));   // already 1: nothing left to turn

        Assert.Equal(1, BrainTuning.Current(Noon).MaxThreads);
    }

    [Fact]
    public async Task The_window_running_out_puts_the_brain_back_at_full()
    {
        Assert.True(await Knob(Remedies.ShorterContext, SelfHeal.Switch.LongContext).TryFixAsync(Oom()));
        Assert.Equal(2048, BrainTuning.Current(Noon).MaxContext);

        var later = Noon + SelfHeal.OffFor(1) + TimeSpan.FromMinutes(1);
        Assert.True(BrainTuning.Current(later).IsNone);
        Assert.Equal(4096, BrainTuning.Current(later).Context(4096));
    }

    [Fact]
    public async Task A_knob_whose_value_does_not_reach_the_brain_did_not_take()
    {
        BrainTuning.Directory = null;   // nowhere to write it: the brain would be built as before

        Assert.False(await Knob(Remedies.FewerThreads, SelfHeal.Switch.BrainThreads).TryFixAsync(Oom()));
    }

    [Fact]
    public async Task A_knob_a_person_would_notice_is_said_and_a_quiet_one_is_not()
    {
        Assert.True(await Knob(Remedies.FewerMemoryBits, SelfHeal.Switch.WorkingMemoryBits).TryFixAsync(Oom()));
        Assert.Empty(_said);

        Assert.True(await Knob(Remedies.ShorterContext, SelfHeal.Switch.LongContext, WolverineSentences.ShorterContext).TryFixAsync(Oom()));
        Assert.Equal(new[] { WolverineSentences.ShorterContext }, _said);
    }

    [Fact]
    public async Task Scalar_kernels_switch_every_vector_path_off_where_the_brain_is_built()
    {
        try
        {
            Assert.True(await Knob(Remedies.ScalarKernels, SelfHeal.Switch.VectorKernels).TryFixAsync(Oom()));
            var tuning = BrainTuning.Current(Noon);
            Assert.True(tuning.ScalarKernels);

            KernelChoice.ScalarOnly = tuning.ScalarKernels;   // what BrainFactory does with it
            Assert.False(KernelChoice.IsVector(Ptq1.Best));
            Assert.All(Enum.GetValues<KernelPath>().Where(KernelChoice.IsVector), p => Assert.False(Ptq1.CanRun(p)));
            Assert.True(Ptq1.CanRun(KernelPath.Lut));         // the model's own path has no vector instructions
        }
        finally
        {
            KernelChoice.ScalarOnly = false;
        }
    }

    [Fact]
    public async Task The_knobs_an_earlier_run_turned_down_are_projected_again_from_selfheal()
    {
        Assert.True(await Knob(Remedies.TighterThinking, SelfHeal.Switch.ThinkingBudget).TryFixAsync(Oom()));
        File.Delete(Path.Combine(_dir, BrainTuning.FileName));   // the file is lost; SelfHeal still knows

        var tuning = _knobs.Project(Noon);

        Assert.Equal(1024, tuning.ThinkingBudget);
        Assert.Equal(1024, BrainTuning.Current(Noon).ThinkingBudget);
    }

    // ── a smaller brain ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_brain_that_failed_is_stepped_down_from_while_another_is_here()
    {
        var fix = new SmallerBrainFix(_knobs, () => "running-model", _ => true, _said.Add, null, _clock);

        Assert.True(await fix.TryFixAsync(Oom()));

        Assert.True(BrainTuning.Current(Noon).Avoids(Model));
        Assert.True(BrainTuning.Current(Noon).Avoids(Model.ToUpperInvariant()));
        Assert.Equal(new[] { WolverineSentences.SmallerBrain }, _said);
        Assert.False(await fix.TryFixAsync(Oom()));   // already stepped down from it
        Assert.False(BrainTuning.Current(Noon + SelfHeal.OffFor(1) + TimeSpan.FromSeconds(1)).Avoids(Model));
    }

    [Fact]
    public async Task The_smallest_brain_here_is_never_stepped_down_from()
    {
        Assert.False(await new SmallerBrainFix(_knobs, null, _ => false, _said.Add, null, _clock).TryFixAsync(Oom()));
        Assert.True(BrainTuning.Current(Noon).IsNone);
        Assert.Empty(_said);
    }

    // ── downloads ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_damaged_file_is_fetched_again_once_a_window()
    {
        var fetched = new List<string>();
        var fix = new RedownloadFix(m => { fetched.Add(m); return true; }, _said.Add, _clock);
        var damaged = DeviceWolverine.ToFailure(Download(nameof(ModelIntegrityException)));

        Assert.True(await fix.TryFixAsync(damaged));
        Assert.False(await fix.TryFixAsync(damaged));   // the same mirror serving a bad file is not fetched in a loop

        Assert.Equal(new[] { Model }, fetched);
        Assert.Equal(new[] { WolverineSentences.Redownloading }, _said);
        _clock.Now = Noon + SelfHeal.OffFor(1) + TimeSpan.FromSeconds(1);
        Assert.True(await fix.TryFixAsync(damaged));
    }

    [Fact]
    public async Task A_stalled_download_alternates_its_two_addresses()
    {
        var fix = new ResumeDownloadFix(_ => true, _clock);
        var stalled = DeviceWolverine.ToFailure(Download(nameof(ModelDownloadException)));

        Assert.True(await fix.TryFixAsync(stalled));
        Assert.True(HealingKnobs.StartsFromAlternate(Model, Noon));
        DownloadMirrors.PreferAlternate = id => HealingKnobs.StartsFromAlternate(id, Noon);
        Assert.True(DownloadMirrors.StartsFromAlternate(Model));

        Assert.True(await fix.TryFixAsync(stalled));
        Assert.False(HealingKnobs.StartsFromAlternate(Model, Noon));
        Assert.False(DownloadMirrors.StartsFromAlternate(Model));
    }

    [Fact]
    public async Task A_fix_with_nothing_to_act_on_declines()
    {
        var nameless = new FailureContext("boom");
        Assert.False(await new RedownloadFix(_ => true, null, _clock).TryFixAsync(nameless));
        Assert.False(await new ResumeDownloadFix(_ => true, _clock).TryFixAsync(nameless));
        Assert.False(await new SmallerBrainFix(_knobs, () => null, _ => true, null, null, _clock).TryFixAsync(nameless));
    }
}
