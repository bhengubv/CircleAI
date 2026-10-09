// SetupOfferSurvivesLowMemoryTests.cs
//
// A phone must be able to fetch the models it cannot currently load.
//
// THE DEADLOCK THIS PREVENTS, measured on the P30 on 2026-10-09 with an empty
// model directory:
//
//   12:24:06  service starts, assesses the catalogue with ~750 MB free
//             -> "catalogue: 90 assessed, 6 run on this device"
//   12:24:xx  service finishes loading; ~200 MB free
//   12:25     Setup asks for a plan; every model is refused on RAM
//             -> plan is empty
//             -> "Everything it needs is already here.  [Nothing to fetch]"
//
// The phone could not download the models it needed BECAUSE it had no free RAM,
// and much of that RAM was gone because the service that would do the
// downloading was resident. It could not heal itself, and the one screen that
// exists to fix that reported success.
//
// THE CONFLATION. FirstRun.Plan gated the OFFER on ModelFit.Fits, which reads
// DeviceProbe.UsableRamGb - derived from FREE ram. ModelFit's own remarks
// already separate the two questions: free RAM is "correct for 'can I load it
// right now', wrong for 'is this model a candidate on this phone at all'". A
// download needs DISK, not headroom; RAM is the question at load time, and the
// selector and CrashVerdict are what answer it then.
//
// WHAT MUST NOT REGRESS. The offer gate was tightened in the first place because
// a Circle OS handset was offered a 22.8 GB MoE at an honest size, accepted it,
// and the load aborted the process and took a dozen system apps with it - "the
// offer was the defect". CouldEverHold still refuses that, because it asks
// against TOTAL memory, which does not move. Both halves are pinned below.

using System;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class SetupOfferSurvivesLowMemoryTests
{
    /// <summary>The real P30: 3.6 GB of RAM, and whatever is free at the moment.</summary>
    private static DeviceProbe P30(double freeRamGb, double freeStorageGb = 31) =>
        new(
            RamAvailableBytes: (long)(freeRamGb * 1_000_000_000d),
            StorageFreeBytes: (long)(freeStorageGb * 1_000_000_000d),
            Gpu: GpuKind.None,
            CpuCores: 8,
            Thermal: ThermalClass.Passive,
            Connectivity: Connectivity.Online)
        {
            RamTotalBytes = (long)(3.6 * 1_000_000_000d),
            StorageTotalBytes = (long)(108 * 1_000_000_000d),
        };

    private static ModelEntry Model(string name, double gb, double minRamGb) =>
        new(name, "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn,
            Modality = ModelModality.Chat,
            TotalBytes = (long)(gb * 1_000_000_000d),
            MinStorageGb = gb,
            MinRamGb = minRamGb,
            QualityRank = 20,
        };

    [Fact]
    public void A_model_this_phone_can_hold_is_still_offered_when_memory_is_momentarily_full()
    {
        // THE FIX. 1.2 GB of weights on a 3.6 GB phone is a reasonable model; at
        // 0.2 GB free it cannot be LOADED this second, and that is fine - nobody
        // is loading it, they are downloading it. Refusing here is what left the
        // P30 unable to recover.
        var model = Model("qwen-2b", gb: 1.2, minRamGb: 1.4);
        var starved = P30(freeRamGb: 0.2);

        Assert.False(ModelFit.Fits(model, starved),
            "precondition: the volatile rule refuses it, which is why the bug existed");

        Assert.True(ModelFit.CouldEverHold(model, starved),
            "3.6 GB of RAM can hold 1.2 GB of weights; free RAM is not the question for a download");
        Assert.True(ModelFit.FitsStorage(model, starved.StorageFreeGb),
            "31 GB free is plenty of disk for 1.2 GB");
    }

    [Fact]
    public void The_offer_does_not_move_when_free_memory_does()
    {
        // The property that makes an offer trustworthy: ask twice under different
        // load and get the same answer. ModelFit.Fits cannot do this, which is
        // why "5 run on this device" became 6 simply because another app closed.
        var model = Model("qwen-2b", gb: 1.2, minRamGb: 1.4);

        var busy = ModelFit.CouldEverHold(model, P30(freeRamGb: 0.1));
        var idle = ModelFit.CouldEverHold(model, P30(freeRamGb: 3.0));

        Assert.Equal(busy, idle);
        Assert.True(busy);
    }

    [Fact]
    public void The_model_that_caused_the_Circle_OS_abort_is_still_refused()
    {
        // THE GUARD THAT MUST NOT REGRESS. 22.8 GB of weights, offered at an
        // honest size to a 7.6 GB handset, accepted, and the load aborted the
        // process with signal 6 and set lowmemorykiller on a dozen system apps.
        // Loosening the offer rule must not re-open that, and it does not:
        // CouldEverHold asks against TOTAL memory, which no amount of waiting
        // changes.
        var moe = Model("qwen3-30b-a3b", gb: 22.8, minRamGb: 2.5);
        var tensorG2 = new DeviceProbe(
            RamAvailableBytes: (long)(4.0 * 1_000_000_000d),
            StorageFreeBytes: (long)(60 * 1_000_000_000d),
            Gpu: GpuKind.None, CpuCores: 8,
            Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        {
            RamTotalBytes = (long)(7.6 * 1_000_000_000d),
            StorageTotalBytes = (long)(128 * 1_000_000_000d),
        };

        Assert.False(ModelFit.CouldEverHold(moe, tensorG2),
            "22.8 GB of weights can never be resident on a 7.6 GB phone, idle or not");
    }

    [Fact]
    public void A_download_is_still_refused_when_the_disk_cannot_hold_it()
    {
        // The half that SHOULD be volatile. Free disk is exactly the right
        // question for a download, and unlike RAM it does not come back by
        // closing an app.
        // 2 GB of weights, which a 3.6 GB phone CAN hold - so RAM is deliberately
        // not the objection and the disk gate is the only thing left to fail.
        // A first draft used 40 GB and failed its own precondition: 40 GB can
        // never be resident on 3.6 GB, so CouldEverHold refused it before the
        // disk was ever consulted, and the test proved nothing about storage.
        var big = Model("big", gb: 2.0, minRamGb: 1.0);
        var nearlyFull = P30(freeRamGb: 3.0, freeStorageGb: 1);

        Assert.True(ModelFit.CouldEverHold(big, nearlyFull), "RAM is not the objection here");
        Assert.False(ModelFit.FitsStorage(big, nearlyFull.StorageFreeGb),
            "2 GB cannot land in 1 GB of free space");
    }

    [Fact]
    public void An_unmeasurable_device_is_not_told_it_can_do_nothing()
    {
        // RamTotalBytes is 0 when no platform head installed the probe. Refusing
        // everything on that basis is how the smallest phones get told they can
        // run nothing - the failure DeviceProbe.MeasurementWarning exists to
        // shout about. CouldEverHold returns true and lets the finer gates speak.
        var model = Model("qwen-2b", gb: 1.2, minRamGb: 1.4);
        var unmeasured = new DeviceProbe(
            RamAvailableBytes: 0, StorageFreeBytes: (long)(31 * 1_000_000_000d),
            Gpu: GpuKind.None, CpuCores: 8,
            Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online);

        Assert.True(ModelFit.CouldEverHold(model, unmeasured));
    }
}
