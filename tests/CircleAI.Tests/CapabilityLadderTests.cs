// CapabilityLadderTests.cs
//
// A system that can only learn "no" converges on doing nothing.
//
// On 2026-10-10 a 3.87 GB P30 ended the day running a 0.6B. Every single
// decision on the way there was defensible - a model killed the service, so
// the fit rule tightened; the tighter rule refused the next model down, so it
// tightened again. Nothing was broken and the product got worse, because
// CrashVerdict records what killed the device and NOTHING recorded what
// worked.
//
// These pin the accelerator. The floor only rises, the climb is one rung at a
// time, and a bad moment is not mistaken for a verdict.

using System;
using System.Collections.Generic;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference.Residency;
using Xunit;

namespace CircleAI.Tests;

public sealed class CapabilityLadderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);

    private static ModelEntry M(string name, int rank) =>
        new(name, "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn,
            Modality = ModelModality.Chat,
            TotalBytes = 1_000_000_000,
            MinRamGb = 1.0,
            QualityRank = rank,
        };

    /// <summary>Four rungs, worst to best - the P30's real shape.</summary>
    private static readonly List<ModelEntry> Rungs =
    [
        M("qwen-0.6b", 10),
        M("qwen-0.8b", 20),
        M("qwen-2b",   30),
        M("qwen-4b",   40),
    ];

    /// <summary>The P30 with a stated amount actually free.</summary>
    private static DeviceProbe P30(double freeGb) =>
        new(RamAvailableBytes: (long)(freeGb * 1_000_000_000d),
            StorageFreeBytes: (long)(30 * 1_000_000_000d),
            Gpu: GpuKind.None, CpuCores: 8,
            Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        {
            RamTotalBytes = 3_776_516L * 1024,
            StorageTotalBytes = (long)(108 * 1_000_000_000d),
        };

    private static ModelTrial Worked(string id, double tps = 5) =>
        new(id, TrialOutcome.Loaded, Now, FreeRamGbAtStart: 1.2,
            PeakResidentGb: 0.9, TokensPerSecond: tps);

    private static ModelTrial Killed(string id, double freeGb) =>
        new(id, TrialOutcome.Killed, Now, FreeRamGbAtStart: freeGb);

    // ── it climbs ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_phone_that_has_tried_nothing_starts_at_the_bottom()
    {
        // NOT THE TOP. Reaching straight for the best candidate is the 22.8 GB
        // MoE on a 7.6 GB handset that aborted and took a dozen system apps
        // with it. A wrong guess must cost one restart, not a crash loop.
        Assert.Equal("qwen-0.6b", CapabilityLadder.NextToTry(Rungs, [], P30(1.2)));
    }

    [Fact]
    public void Success_moves_it_up_one_rung()
    {
        var trials = new List<ModelTrial> { Worked("qwen-0.6b") };
        Assert.Equal("qwen-0.8b", CapabilityLadder.NextToTry(Rungs, trials, P30(1.2)));
    }

    [Fact]
    public void It_keeps_climbing_while_the_evidence_allows()
    {
        var trials = new List<ModelTrial> { Worked("qwen-0.6b"), Worked("qwen-0.8b"), Worked("qwen-2b") };
        Assert.Equal("qwen-4b", CapabilityLadder.NextToTry(Rungs, trials, P30(1.5)));
    }

    [Fact]
    public void And_settles_when_there_is_nothing_left_above()
    {
        var trials = new List<ModelTrial>
        {
            Worked("qwen-0.6b"), Worked("qwen-0.8b"), Worked("qwen-2b"), Worked("qwen-4b"),
        };
        Assert.Null(CapabilityLadder.NextToTry(Rungs, trials, P30(1.5)));
        Assert.Equal("qwen-4b", CapabilityLadder.BestKnownGood(Rungs, trials));
    }

    // ── the floor never drops ────────────────────────────────────────────────

    [Fact]
    public void A_later_kill_does_not_undo_what_already_worked()
    {
        // THE RATCHET THIS TYPE EXISTS TO STOP. A phone that ran the 2B once can
        // run it again when it is next quiet. Dropping the floor on one bad
        // moment is exactly how the P30 talked itself down to a 0.6B.
        var trials = new List<ModelTrial> { Worked("qwen-2b"), Killed("qwen-2b", 0.9) };

        Assert.Equal("qwen-2b", CapabilityLadder.BestKnownGood(Rungs, trials));
    }

    [Fact]
    public void A_model_that_answered_uselessly_slowly_is_not_a_floor()
    {
        // Evidence it LOADED is not evidence it is worth having. Natural speech
        // is 4-5 tokens a second; a third of one is a person waiting on a
        // machine, and a better model there is not a better experience.
        var trials = new List<ModelTrial> { Worked("qwen-4b", tps: 0.3) };

        Assert.Null(CapabilityLadder.BestKnownGood(Rungs, trials));
    }

    // ── a bad moment is not a verdict ────────────────────────────────────────

    [Fact]
    public void A_single_kill_in_a_tight_moment_is_retried_when_there_is_room()
    {
        // Measured on the P30: Qwen3.5-2B was killed with 982 MB free and had
        // run earlier the same day with more headroom. Treating that kill as
        // final throws away a model the phone demonstrably runs.
        var trials = new List<ModelTrial> { Killed("qwen-2b", 0.98) };

        Assert.False(CapabilityLadder.IsWorthAttempting("qwen-2b", trials, P30(1.1)),
            "barely roomier than when it died - the same attempt with the same answer");
        Assert.True(CapabilityLadder.IsWorthAttempting("qwen-2b", trials, P30(1.6)),
            "materially roomier - worth finding out");
    }

    [Fact]
    public void Two_kills_is_a_verdict_however_quiet_the_phone_gets()
    {
        var trials = new List<ModelTrial> { Killed("qwen-2b", 0.9), Killed("qwen-2b", 1.4) };

        Assert.False(CapabilityLadder.IsWorthAttempting("qwen-2b", trials, P30(3.0)));
    }

    [Fact]
    public void Too_slow_is_never_retried_because_waiting_cannot_fix_it()
    {
        // A killed model might work when the phone is quiet. A model that
        // answers at 0.3 tokens a second will be just as useless then - the
        // failure is a property of the pairing, not of the moment.
        var trials = new List<ModelTrial>
        {
            new("qwen-4b", TrialOutcome.TooSlow, Now, 2.5, 1.8, 0.3),
        };

        Assert.False(CapabilityLadder.IsWorthAttempting("qwen-4b", trials, P30(3.0)));
    }

    [Fact]
    public void A_kill_does_not_block_the_rung_below_it()
    {
        // The whole point of climbing one at a time: a failure at the top must
        // leave everything beneath it reachable.
        var trials = new List<ModelTrial> { Killed("qwen-4b", 1.2), Killed("qwen-4b", 1.3) };

        Assert.Equal("qwen-0.6b", CapabilityLadder.NextToTry(Rungs, trials, P30(1.2)));
    }

    [Fact]
    public void Once_the_top_is_refused_it_settles_on_the_best_that_worked()
    {
        var trials = new List<ModelTrial>
        {
            Worked("qwen-0.6b"), Worked("qwen-0.8b"), Worked("qwen-2b"),
            Killed("qwen-4b", 1.2), Killed("qwen-4b", 1.3),
        };

        Assert.Null(CapabilityLadder.NextToTry(Rungs, trials, P30(1.3)));
        Assert.Equal("qwen-2b", CapabilityLadder.BestKnownGood(Rungs, trials));
    }

    [Fact]
    public void It_never_proposes_something_it_has_already_run()
    {
        var trials = new List<ModelTrial> { Worked("qwen-2b") };
        Assert.False(CapabilityLadder.IsWorthAttempting("qwen-2b", trials, P30(2.0)));
    }

    [Fact]
    public void An_empty_candidate_list_asks_for_nothing_rather_than_throwing()
    {
        // The honest answer on a phone holding no models, and it is reached on
        // first run before anything has downloaded.
        Assert.Null(CapabilityLadder.NextToTry([], [], P30(1.2)));
        Assert.Null(CapabilityLadder.BestKnownGood([], []));
    }
}
