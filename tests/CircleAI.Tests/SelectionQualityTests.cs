// SelectionQualityTests.cs
//
// FIT IS NOT FUNCTION.
//
// BestFit gates on RAM and storage, then returns the best-ranked survivor —
// and when nothing survives it returns the smallest entry rather than throwing.
// That fallback is correct (a wearable should get the small model, not an
// exception) but until 2026-07-20 it was INDISTINGUISHABLE from a good pick.
// A caller had no way to tell "this device can comfortably run this" from
// "nothing here fits, here is the least-bad option", so a product could ship
// technically-running and practically useless on cheap hardware.
//
// ModelSelection.Quality carries that distinction. These tests pin it.

using System;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class SelectionQualityTests
{
    private static DeviceProbe Device(double ramGb, double storageGb = 32) =>
        new(
            RamAvailableBytes: (long)(ramGb * 1024 * 1024 * 1024),
            StorageFreeBytes:  (long)(storageGb * 1024 * 1024 * 1024),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);

    [Fact]
    public void CapablePhone_ReportsGood()
    {
        var pick = new DeviceAwareModelSelector().BestFit(Device(1.1), ChatCapability.Default);

        Assert.Equal(SelectionQuality.Good, pick.Quality);

        // The VERDICT is what this test is for; which model earns it is the
        // catalogue's business and changes whenever a better one is added. It
        // pinned "Qwen3-0.6B-MNN" and failed the day Qwen3.5-0.8B was
        // catalogued — a newer model that also fits, which is an improvement
        // reported as a regression. What must hold is that a Good verdict comes
        // with something the phone can actually hold.
        // DISK BYTES WERE STANDING IN FOR RAM, which held only while every model
        // loaded eagerly. A model with a MEASURED memory-mapped figure is far smaller
        // in memory than on disk, so a byte threshold now fails on models the phone
        // can genuinely hold. The claim being made is about memory; it now measures
        // memory.
        var chosen = new ModelRegistryService().AllModels
            .Single(m => string.Equals(m.Name, pick.ModelId, StringComparison.Ordinal));
        var needGb = ModelFit.EffectiveMinRamGb(chosen, Device(1.1).UsableRamGb);
        Assert.True(needGb <= Device(1.1).UsableRamGb + ModelFit.Eps,
            $"Quality.Good on a 1.1 GB phone, but picked {pick.ModelId} " +
            $"needing {needGb:0.##} GB resident.");
    }

    [Fact]
    public void DeviceTooSmallForAnything_ReportsNothingFits()
    {
        // 0.25 GB — under even the smallest MinRamGb. Previously this returned
        // Qwen3-0.6B looking exactly like a healthy selection.
        //
        // MNN-ONLY, STATED: without an engine set the selector asks the build, which
        // answers from the test host's bin/, so this moved when llamabridge.dll was
        // staged for win-x64 and nothing about the product had changed.
        var pick = new DeviceAwareModelSelector([ModelEngine.Mnn])
            .BestFit(Device(0.25, storageGb: 1), ChatCapability.Default);

        Assert.Equal(SelectionQuality.NothingFits, pick.Quality);

        // Still returns something rather than throwing — the fallback contract is
        // unchanged, only now it is HONEST about itself. And what it returns is above
        // the floor ("nothing less than Qwen3.5 is the LLM", 2026-10-16), so the
        // retired Qwen3-0.6B this used to name is no longer an answer. The NAME was
        // asserted here and a smaller eligible Qwen3.5 entering the registry is not a
        // defect, so the rule is what is pinned.
        var chosen = new ModelRegistryService().AllModels.Single(m => m.Name == pick.ModelId);
        Assert.False(chosen.Retired, pick.ModelId + " is retired");
        Assert.Equal(ModelEngine.Mnn, chosen.Engine);
        Assert.StartsWith("Qwen3.", pick.ModelId, StringComparison.Ordinal);
    }

    [Fact]
    public void BelowRequestedFloor_ReportsBelowFloor()
    {
        // The 1.1 GB phone can only hold Qwen3-0.6B (QualityRank 6). A caller
        // that needs rank 10+ to be useful should be told so, not handed the
        // 0.6B as though it were fine.
        var pick = new DeviceAwareModelSelector()
            .BestFit(Device(1.1), ChatCapability.Default, minQualityRank: 10);

        Assert.Equal(SelectionQuality.BelowFloor, pick.Quality);

        // BelowFloor means exactly one thing: the best that fits is still worse
        // than the caller said they needed. Assert THAT, not which model it
        // happens to be — the answer improved to Qwen3.5-0.8B (rank 7) and the
        // verdict is unchanged, because 7 is still under the requested 10.
        var chosen = new CircleAI.Core.Models.ModelRegistryService().AllModels
            .Single(m => string.Equals(m.Name, pick.ModelId, System.StringComparison.Ordinal));
        Assert.True(chosen.QualityRank < 10,
            $"{pick.ModelId} is rank {chosen.QualityRank}, which meets the floor of 10 — " +
            "so the verdict should not have been BelowFloor.");
    }

    [Fact]
    public void MeetingTheFloor_ReportsGood()
    {
        // A 16 GB device reaches Qwen3-14B (rank 14), clearing a floor of 10.
        var pick = new DeviceAwareModelSelector()
            .BestFit(Device(16, storageGb: 64), ChatCapability.Default, minQualityRank: 10);

        Assert.Equal(SelectionQuality.Good, pick.Quality);
    }

    [Fact]
    public void DefaultFloorIsZero_SoBehaviourIsUnchangedUnlessAsked()
    {
        // The floor is opt-in on purpose: real thresholds must come from
        // SelfBench measurements, not a number invented in source.
        var withDefault  = new DeviceAwareModelSelector().BestFit(Device(1.1), ChatCapability.Default);
        var withExplicit = new DeviceAwareModelSelector().BestFit(Device(1.1), ChatCapability.Default, 0);

        Assert.Equal(SelectionQuality.Good, withDefault.Quality);
        Assert.Equal(withDefault.ModelId, withExplicit.ModelId);
        Assert.Equal(withDefault.Quality, withExplicit.Quality);
    }

    [Fact]
    public void AllCandidates_MarksUnrunnableEntriesHonestly()
    {
        // A "what could run here" listing that marked the 14B as Good on a
        // 1.1 GB phone would be actively misleading.
        // (The 14B and 0.6B this was written against are retired below the floor
        // - "nothing less than Qwen3.5 is the LLM" - so it asks the same question of
        // the Qwen3.5 family, and checks no retired model is listed at all.)
        var candidates = new DeviceAwareModelSelector().AllCandidates(Device(1.1));

        Assert.NotEmpty(candidates);
        Assert.DoesNotContain(candidates, c => c.ModelId.StartsWith("Qwen3-", StringComparison.Ordinal)
                                            || c.ModelId.StartsWith("Qwen2", StringComparison.Ordinal));

        var big = Assert.Single(candidates, c => c.ModelId == "Qwen3.5-4B-MNN");
        Assert.Equal(SelectionQuality.NothingFits, big.Quality);

        var small = Assert.Single(candidates, c => c.ModelId == "Qwen3.5-0.8B-MNN");
        Assert.Equal(SelectionQuality.Good, small.Quality);
    }
}
