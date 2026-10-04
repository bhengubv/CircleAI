// CatalogueRamClaimTests.cs
//
// A MODEL MAY NOT CLAIM TO NEED LESS MEMORY THAN IT WEIGHS.
//
// Two rows did, and both said 2.5 GB:
//
//   Qwen3-30B-A3B-MNN      17.75 GB of weights    MinRamGb 2.50   ratio 0.14
//   Qwen3.6-35B-A3B-MNN    22.80 GB of weights    MinRamGb 2.50   ratio 0.11
//
// while all thirteen dense chat rows sat between 1.26 and 1.46 times their weight.
// Two differently-sized models asserting an identical figure is the tell: it was the
// "an A3B keeps about 3B active" claim, written down once and applied twice, and
// nobody ever ran either model to check.
//
// WHAT IT COST. On a Circle OS device (Tensor G2, 7.6 GB) on 2026-10-04, the moment
// the 35B finished downloading:
//
//   Process com.bhengubv.circleai.service (pid 28525) has died: signal 6 (Aborted)
//   lowmemorykiller: Kill 'com.android.printspooler' ... 'com.android.contacts'
//     ... 'com.android.camera2' ... 'za.co.circleos.settings'   (ten more)
//
// It did not just fail to answer; it aborted and took a dozen system apps with it.
//
// ModelFit already refuses to invent that discount - MmappedGb returns null without
// a MEASURED MmapResidentGb, and its remarks say a previous version of exactly this
// MoE rule "let a 35B through". The catalogue was still publishing the figure the
// code had stopped believing, and MinRamGb is what screens show a person. These pin
// the rule at the source rather than in one more reader.

using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class CatalogueRamClaimTests
{
    private static ModelEntry[] Chat()
        => new ModelRegistryService().AllModels
            .Where(m => m.Modality == ModelModality.Chat && m.TotalBytes > 0)
            .ToArray();

    private static double WeightGb(ModelEntry m) => m.TotalBytes / DeviceProbe.BytesPerGb;

    [Fact]
    public void Nothing_claims_to_need_less_memory_than_its_own_weights()
    {
        // THE INVARIANT. Weights have to be somewhere. A row below 1.0 is either a
        // measured paging claim - which belongs in MmapResidentGb, where ModelFit
        // looks for it - or a guess, and a guess here is an OOM on somebody's phone.
        var liars = Chat()
            .Where(m => m.MmapResidentGb is null or <= 0)
            .Where(m => m.MinRamGb < WeightGb(m))
            .Select(m => $"{m.Name}: declares {m.MinRamGb} GB, weighs {WeightGb(m):F2} GB")
            .ToList();

        Assert.True(liars.Count == 0,
            "a model claims less RAM than its weights and carries no measured "
            + "MmapResidentGb:\n  " + string.Join("\n  ", liars));
    }

    [Theory]
    [InlineData(1.2, 1.6)]
    public void Every_chat_row_sits_in_the_catalogues_own_band(double low, double high)
    {
        // Weights plus runtime, which is what the thirteen dense rows have always
        // encoded. A band rather than a formula, because these are hand-tuned - but
        // wide enough that a typo or a re-asserted 2.5 lands outside it.
        var strays = Chat()
            .Select(m => (m.Name, Ratio: m.MinRamGb / WeightGb(m)))
            .Where(r => r.Ratio < low || r.Ratio > high)
            .Select(r => $"{r.Name}: {r.Ratio:F2}x its weight")
            .ToList();

        Assert.True(strays.Count == 0,
            $"outside the {low}-{high}x band every other chat row keeps:\n  "
            + string.Join("\n  ", strays));
    }

    [Fact]
    public void The_memory_hint_agrees_with_the_RAM_claim()
    {
        // Two fields saying the same thing, so they drift. Both carried the 2.5.
        var disagreeing = Chat()
            .Where(m => m.MemoryHintBytes > 0)
            .Select(m => (m.Name, Hint: m.MemoryHintBytes / DeviceProbe.BytesPerGb, m.MinRamGb))
            .Where(r => r.Hint < r.MinRamGb * 0.9 || r.Hint > r.MinRamGb * 1.2)
            .Select(r => $"{r.Name}: hint {r.Hint:F2} GB against MinRamGb {r.MinRamGb}")
            .ToList();

        Assert.True(disagreeing.Count == 0,
            "MemoryHintBytes and MinRamGb disagree:\n  " + string.Join("\n  ", disagreeing));
    }

    [Fact]
    public void The_35B_does_not_fit_the_phone_it_aborted_on()
    {
        // THE MEASUREMENT, AS AN ASSERTION. 7.6 GB total; the load aborted with
        // signal 6 and set lowmemorykiller on a dozen system apps. Whatever the
        // catalogue says, it must not say this runs here.
        var moe = Chat().Single(m => m.Name == "Qwen3.6-35B-A3B-MNN");
        var tensorG2 = new DeviceProbe(
            RamAvailableBytes: 7_641_304L * 1024,
            StorageFreeBytes:  99L * 1024 * 1024 * 1024,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);

        Assert.False(ModelChoice.Fits(moe, tensorG2),
            $"{moe.Name} is offered to the 7.6 GB device it aborted on");
    }

    [Fact]
    public void No_chat_row_is_discounted_without_somebody_having_measured_it()
    {
        // MmapResidentGb is the one field that turns the eager requirement off, and
        // ModelFit's remarks are explicit that it means "somebody ran the model on a
        // device and watched the number". Setting it from a datasheet is how the
        // discount comes back.
        foreach (var m in Chat().Where(m => m.MmapResidentGb is > 0))
            Assert.True(m.MmapResidentGb >= ModelFit.MmapResidentFloorGb,
                $"{m.Name} claims {m.MmapResidentGb} GB resident, below the measured floor");
    }
}
