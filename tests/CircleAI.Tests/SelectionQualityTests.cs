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
using CircleAI.Core;
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
        Assert.True(pick.EstimatedBytes < 1_100_000_000L,
            $"Quality.Good on a 1.1 GB phone, but picked {pick.ModelId} " +
            $"at {pick.EstimatedBytes} bytes.");
    }

    [Fact]
    public void DeviceTooSmallForAnything_ReportsNothingFits()
    {
        // 0.25 GB — under even the smallest MinRamGb (0.6). Previously this
        // returned Qwen3-0.6B looking exactly like a healthy selection.
        var pick = new DeviceAwareModelSelector().BestFit(Device(0.25, storageGb: 1),
                                                          ChatCapability.Default);

        Assert.Equal(SelectionQuality.NothingFits, pick.Quality);

        // Still returns the smallest rather than throwing — the fallback
        // contract is unchanged, only now it is HONEST about itself.
        Assert.Equal("Qwen3-0.6B-MNN", pick.ModelId);
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
        var candidates = new DeviceAwareModelSelector().AllCandidates(Device(1.1));

        Assert.NotEmpty(candidates);

        var big = Assert.Single(candidates, c => c.ModelId == "Qwen3-14B-MNN");
        Assert.Equal(SelectionQuality.NothingFits, big.Quality);

        var small = Assert.Single(candidates, c => c.ModelId == "Qwen3-0.6B-MNN");
        Assert.Equal(SelectionQuality.Good, small.Quality);
    }
}
