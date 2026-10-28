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
    /// <summary>A device, stating BOTH what is free now and how big it is.</summary>
    /// <remarks>
    /// TOTAL RAM IS NOT OPTIONAL ANY MORE, AND OMITTING IT MADE THESE TESTS LIE.
    ///
    /// This helper set RamAvailableBytes only, which was complete while every gate
    /// read free RAM. BestFit now ranks on ModelFit.CouldEverHold - the durable
    /// question - and CouldEverHold falls back to the AVAILABLE figure when a probe
    /// reports no total, "because zero means the device could not be measured". So a
    /// probe that stays silent about total has its free RAM read as the whole device,
    /// and 40% of 1.1 GB is 0.44 GB, which is under every model in the register. Four
    /// tests called a phone capable and then described one three times smaller than
    /// they meant.
    ///
    /// NO REAL PHONE LOOKS LIKE THAT. The P30 probe in TheBestInstalledModelWinsTests
    /// is 440 MB free of 3 776 516 kB total - free is ELEVEN PERCENT of the device.
    /// A handset with 1.1 GB spare is a 4 GB handset, so that is what these say now.
    /// Every call states its own total; nothing is inferred.
    /// </remarks>
    private static DeviceProbe Device(double ramGb, double storageGb = 32, double totalGb = 0) =>
        new(
            RamAvailableBytes: (long)(ramGb * 1024 * 1024 * 1024),
            StorageFreeBytes:  (long)(storageGb * 1024 * 1024 * 1024),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online)
        {
            RamTotalBytes = (long)((totalGb > 0 ? totalGb : ramGb) * 1024 * 1024 * 1024),
        };

    /// <summary>A mid-range handset: 4 GB fitted, 1.1 GB spare right now.</summary>
    private static DeviceProbe CapableHandset() => Device(1.1, totalGb: 4.0);

    [Fact]
    public void CapablePhone_ReportsGood()
    {
        var pick = new DeviceAwareModelSelector().BestFit(CapableHandset(), ChatCapability.Default);

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
        //
        // AND IT ASKS THE DURABLE QUESTION, because that is the one BestFit answered.
        // This compared the pick against UsableRamGb - free RAM x 0.85, 0.93 GB on
        // this handset - which is "can it load this second". BestFit ranks on
        // CouldEverHold, so on a 4 GB phone with 1.1 GB spare it correctly names a
        // model larger than the moment allows and LoadableNow is what answers the
        // other question. Asserting the momentary rule against a durable pick is the
        // volatility the durable rule was introduced to remove.
        var probe  = CapableHandset();
        var chosen = new ModelRegistryService().AllModels
            .Single(m => string.Equals(m.Name, pick.ModelId, StringComparison.Ordinal));
        Assert.True(ModelFit.CouldEverHold(chosen, probe),
            $"Quality.Good on a {probe.RamTotalBytes / 1024.0 / 1024 / 1024:0.#} GB phone, but picked "
            + $"{pick.ModelId}, which needs {ModelFit.EagerGb(chosen):0.##} GB resident against a "
            + $"{ModelFit.ResidentCeilingGb(probe.RamTotalBytes / 1024.0 / 1024 / 1024):0.##} GB ceiling.");
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
        // 0.25 GB FITTED, not merely 0.25 GB spare — stated, because CouldEverHold
        // reads total and a silent probe would have it read the free figure instead.
        var pick = new DeviceAwareModelSelector([ModelEngine.Mnn])
            .BestFit(Device(0.25, storageGb: 1, totalGb: 0.25), ChatCapability.Default);

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
        // An MNN handset tops out below rank 10: its 2.5 GB ceiling admits
        // Qwen3.5-2B (rank 9) and refuses the 4B (3.8 GB declared). A caller that
        // needs rank 10+ to be useful should be told so, not handed the 2B as
        // though it were what they asked for.
        //
        // MNN-ONLY, STATED, for the reason DeviceTooSmallForAnything gives above:
        // with no engine set the selector asks the BUILD, which answers from the
        // test host's bin/. With llamabridge.dll staged there, Ternary-Bonsai-2-4B
        // (rank 11, 1.2 GB declared) becomes a candidate, clears the floor of 10
        // and the verdict flips to Good — on a 32-bit P30 that engine does not
        // exist at all. The product did not change; the host's bin/ did.
        var pick = new DeviceAwareModelSelector([ModelEngine.Mnn])
            .BestFit(CapableHandset(), ChatCapability.Default, minQualityRank: 10);

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
            .BestFit(Device(16, storageGb: 64, totalGb: 16), ChatCapability.Default, minQualityRank: 10);

        Assert.Equal(SelectionQuality.Good, pick.Quality);
    }

    [Fact]
    public void DefaultFloorIsZero_SoBehaviourIsUnchangedUnlessAsked()
    {
        // The floor is opt-in on purpose: real thresholds must come from
        // SelfBench measurements, not a number invented in source.
        var withDefault  = new DeviceAwareModelSelector().BestFit(CapableHandset(), ChatCapability.Default);
        var withExplicit = new DeviceAwareModelSelector().BestFit(CapableHandset(), ChatCapability.Default, 0);

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
        var candidates = new DeviceAwareModelSelector().AllCandidates(CapableHandset());

        Assert.NotEmpty(candidates);
        Assert.DoesNotContain(candidates, c => c.ModelId.StartsWith("Qwen3-", StringComparison.Ordinal)
                                            || c.ModelId.StartsWith("Qwen2", StringComparison.Ordinal));

        var big = Assert.Single(candidates, c => c.ModelId == "Qwen3.5-4B-MNN");
        Assert.Equal(SelectionQuality.NothingFits, big.Quality);

        var small = Assert.Single(candidates, c => c.ModelId == "Qwen3.5-0.8B-MNN");
        Assert.Equal(SelectionQuality.Good, small.Quality);
    }
}
