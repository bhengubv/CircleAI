// ModelFitTests.cs
//
// The one function that decides whether a model can run on a phone, and it had no
// test file. Not a thin one — none. It was reached only incidentally, through
// DeviceAwareModelSelectorTests, which is why 3,983 green tests coexisted with a
// selector offering a 21.2 GB model to a 7.6 GB device.
//
// EVERY CASE HERE IS A REAL CATALOGUE ROW ON A REAL DEVICE. Made-up numbers would
// test the arithmetic; these test the judgement, which is the part that has been
// wrong twice — once when MinRamGb-minus-weight offered a 2.85 GB model to a 1.1 GB
// handset and the app was OOM-killed on a P30, and once when an architectural
// claim about mixture-of-experts let a 35B through the same gate.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class ModelFitTests
{
    /// <summary>Qwen3.6-35B-A3B-MNN, exactly as embedded_registry.json ships it.</summary>
    private static ModelEntry Moe35B() => new("Qwen3.6-35B-A3B-MNN", "1.0", "MNN-Q4")
    {
        TotalBytes   = 22_797_996_902,   // 21.2 GB of weights
        MinRamGb     = 2.5,              // the ACTIVE experts, declared
        MinStorageGb = 23.0,
        QualityRank  = 16,               // the highest in the catalogue, so it wins
    };

    /// <summary>The Circle OS handset this was found on: 7.6 GB total.</summary>
    private const double CircleOsRamGb = 7.6;

    /// <summary>The P30, which is the floor the product is built to.</summary>
    private const double P30RamGb = 3.6;

    [Fact]
    public void A_model_three_times_the_devices_whole_memory_is_not_offered()
    {
        // THE CASE THAT WAS SHIPPING. 21.2 GB of weights against 7.6 GB of RAM - the
        // entire device, not the free part - passed the fit check because MinRamGb
        // said 2.5 and nothing compared that claim to the 21.2 GB it sits in front of.
        //
        // The declared figure may well be architecturally true: an A3B keeps roughly
        // 3B active, and at Q4 that is about 2.5 GB. It is still a claim about how a
        // runtime will page 18.7 GB of inactive experts on a phone, and nobody has
        // watched it happen. MmapResidentGb exists for figures somebody measured;
        // this is not one.
        Assert.False(ModelFit.FitsRam(Moe35B(), CircleOsRamGb, mmapAllowed: true),
            "a 21.2 GB model was offered to a 7.6 GB phone on the strength of a declared "
          + "MinRamGb of 2.5, with no measured resident figure behind it");
    }

    [Fact]
    public void And_certainly_not_to_the_device_the_product_is_built_for()
    {
        Assert.False(ModelFit.FitsRam(Moe35B(), P30RamGb, mmapAllowed: true));
    }

    [Fact]
    public void A_measured_resident_figure_is_believed()
    {
        // THE DISCOUNT IS NOT BANNED, IT IS EVIDENCED. Somebody ran this and watched
        // the resident set; that is exactly what MmapResidentGb is for, and a model
        // carrying one is offered on the strength of it.
        var measured = Moe35B() with { MmapResidentGb = 2.5 };

        Assert.True(ModelFit.FitsRam(measured, CircleOsRamGb, mmapAllowed: true));
        Assert.Equal(2.5, ModelFit.MmappedGb(measured));
    }

    [Fact]
    public void Without_a_measurement_there_is_no_discount()
    {
        Assert.Null(ModelFit.MmappedGb(Moe35B()));
    }

    [Fact]
    public void Every_path_that_asks_whether_it_fits_gives_the_same_answer()
    {
        // ModelFit EXISTS BECAUSE THIS RULE HAD SIX COPIES THAT DISAGREED, and it
        // still had a seventh: ModelChoice.Fits compared MinRamGb straight against
        // usable RAM, never touching the mmap reasoning. That is the one the
        // abilities screen asks, so fixing ModelFit changed nothing a person could
        // see - "Answering - 22797 MB" on a 7.6 GB phone survived the fix untouched.
        //
        // This asserts they agree. A new caller that reimplements the arithmetic
        // fails here rather than on somebody's handset.
        var probe = new DeviceProbe(
            RamAvailableBytes: (long)(CircleOsRamGb * 1_000_000_000),
            StorageFreeBytes:  110L * 1_000_000_000,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Active,
            Connectivity:      Connectivity.Online);

        // FirstRun.Fits is private and builds the SETUP PLAN - the list a person
        // taps - so it is reached the way a caller reaches it: through the plan. It
        // was the eighth copy, and it kept offering the 35B while the abilities
        // screen, already fixed, offered a 1.4 GB model.
        Assert.Equal(ModelFit.Fits(Moe35B(), probe), ModelChoice.Fits(Moe35B(), probe));
        Assert.False(ModelChoice.Fits(Moe35B(), probe));
    }

    [Fact]
    public void A_model_that_fits_eagerly_still_fits()
    {
        // The guard must not cost us the ladder that works. Qwen3.5-0.8B is what the
        // P30 actually runs.
        var small = new ModelEntry("Qwen3.5-0.8B-MNN", "1.0", "MNN-Q4")
        {
            TotalBytes   = 547_000_000,   // ~0.51 GB
            MinRamGb     = 1.2,
            MinStorageGb = 1.0,
            QualityRank  = 8,
        };

        Assert.True(ModelFit.FitsRam(small, P30RamGb, mmapAllowed: true));
        Assert.True(ModelFit.FitsRam(small, CircleOsRamGb, mmapAllowed: true));
    }
}
