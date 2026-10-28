// TheBestInstalledModelWinsTests.cs
//
// "ONLY ONE RULE: IS THIS THE BEST MODEL GIVEN THE RESOURCES WE HAVE?" (the owner,
// 2026-10-24)
//
// ModelChoice.For answered "which model" with FirstOrDefault over REGISTRY ORDER for
// anything already on disk. Registry order is not an opinion about anything. It is
// the same shape as the defect that had the P30 running Qwen3-0.6B for a week - "the
// first entry in the registry" - and that was fixed by RETIRING the old models rather
// than by fixing the rule, so it sat here waiting for a second live model to land
// beside the first:
//
//     idx 78  Qwen3.5-0.8B   rank 8
//     idx 79  Qwen3.5-2B     rank 9
//     idx 80  Qwen3.5-4B     rank 11
//     idx 81  Qwen3.6-35B    rank 16
//
// A phone holding the 0.8B and the 4B named the 0.8B because of those index numbers
// and nothing else. The hourly review makes that arrangement ordinary rather than
// rare, because it fetches better models onto phones that can hold them.
//
// AND RANK ALONE WOULD HAVE BEEN WORSE. ModelOffer.Worth does not fit-check a chat
// model - it returns true at its Chat branch deliberately, so that a tight moment
// cannot veto the only brain there is - so ordering installed models by rank with no
// other guard names the 4B on the P30, whose load got the service killed by
// iAwareF[LowMem] on 2026-10-09. The rule is the best model GIVEN THE RESOURCES, and
// the resources half is CouldEverHold.
//
// "INSTALLED" IN THIS FILE'S NAME IS HISTORY, NOT THE RULE. The owner struck out
// "INSTALLED FIRST, ALWAYS. Something already on the phone is the answer whatever the
// catalogue thinks of it", and ModelChoice.cs now says why in as many words: "Best LLM
// is X, X is used; a missing X is FETCHED, never substituted." What is on disk no
// longer decides anything - it is the best model THIS DEVICE CAN HOLD, downloaded if
// it is not here yet.
//
// Two tests in here asserted the old rule by asserting a NAME that only happened to
// be in the store, and they failed the moment the ceiling admitted something better:
// the P30's answer is Qwen3.5-2B, which ModelFit's measured table calls out
// explicitly - "admits the 2B (1.9, which ran)". They pin the rule now. The three
// that still name a model do so where that model is genuinely the best the device can
// hold, which is the thing being tested.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class TheBestInstalledModelWinsTests : IDisposable
{
    private readonly ModelRegistryService _registry = new();
    private readonly List<string> _stores = [];

    public void Dispose()
    {
        _registry.Dispose();
        foreach (var dir in _stores)
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }

    /// <summary>A model store with these models present at their catalogued sizes.</summary>
    private string Store(params string[] present)
    {
        var dir = Path.Combine(Path.GetTempPath(), "circleai-best-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _stores.Add(dir);

        foreach (var name in present)
        {
            var entry = _registry.GetLatestModel(name)
                        ?? throw new InvalidOperationException(name + " is not in the registry");

            foreach (var f in entry.BundleFiles ?? [])
            {
                var path = Path.Combine(dir, name, f.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[Math.Min(f.SizeBytes, 64)]);

                // The anchor has to be at its full catalogued length or ModelPresent
                // reads the bundle as half-fetched. Sparse, so the test does not write
                // gigabytes: SetLength on a fresh file costs nothing on NTFS.
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Write);
                fs.SetLength(f.SizeBytes);
            }

            // MNN chat bundles need config.json to count as present at all.
            var config = Path.Combine(dir, name, "config.json");
            if (!File.Exists(config)) File.WriteAllText(config, "{}");
        }

        return dir;
    }

    /// <summary>The P30 as measured: 3.867 GB total, ceiling 2.37 GB.</summary>
    private static DeviceProbe P30() => new(
        RamAvailableBytes: 440_000_000,
        StorageFreeBytes: 29_000_000_000,
        Gpu: GpuKind.None, CpuCores: 8,
        Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
    {
        RamTotalBytes = 3_776_516L * 1024,
    };

    /// <summary>A phone with room: 12 GB total, 6 GB free.</summary>
    private static DeviceProbe Roomy() => new(
        RamAvailableBytes: 6_000_000_000,
        StorageFreeBytes: 200_000_000_000,
        Gpu: GpuKind.None, CpuCores: 8,
        Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
    {
        RamTotalBytes = 12_000_000_000,
    };

    /// <summary>An MNN-only build, stated rather than probed.</summary>
    /// <remarks>
    /// ModelChoice.For asks DeviceModelAssessor.ShippedEngines() when no engine set is
    /// given, and that answers from whichever native libraries sit in the test host's
    /// bin/ - so staging llamabridge.dll for win-x64 changed what this file selected
    /// with no product change behind it. MNN-only is also the honest device here: these
    /// are P30 cases, and the P30's 32-bit ABI has no llama.cpp at all.
    /// </remarks>
    private static readonly ModelEngine[] MnnOnly = [ModelEngine.Mnn];

    private ModelEntry? Chosen(string store, DeviceProbe probe,
                               IReadOnlyCollection<ModelEngine>? engines = null)
    {
        using var loader = new BundleModelLoader(store, _registry);
        return ModelChoice.For(ModelModality.Chat, _registry, loader, probe,
                               catalog: null, engines: engines ?? MnnOnly);
    }

    [Fact]
    public void The_best_installed_brain_wins_not_the_first_one_in_the_file()
    {
        // Qwen3.5-0.8B is at index 78 and Qwen3.5-4B at index 80, so the old rule
        // named the 0.8B on a phone that can hold both - for no reason but the order
        // they were written down in.
        var chosen = Chosen(Store("Qwen3.5-0.8B-MNN", "Qwen3.5-4B-MNN"), Roomy());

        Assert.NotNull(chosen);
        Assert.Equal("Qwen3.5-4B-MNN", chosen!.Name);
    }

    [Fact]
    public void Order_on_disk_does_not_decide_it()
    {
        // The same two models, named the other way round. A rule that reads a list
        // rather than the models would answer differently.
        var a = Chosen(Store("Qwen3.5-0.8B-MNN", "Qwen3.5-4B-MNN"), Roomy());
        var b = Chosen(Store("Qwen3.5-4B-MNN", "Qwen3.5-0.8B-MNN"), Roomy());

        Assert.Equal(a?.Name, b?.Name);
    }

    [Fact]
    public void The_P30_never_gets_the_4B_whatever_is_sitting_on_its_disk()
    {
        // THE 4B IS THE ONE THAT KILLED IT: 2.85 GB of weights, 3.8 GB declared,
        // against a 2.37 GB ceiling. It downloaded for six minutes on 2026-10-09 and
        // the load got the whole service killed by iAwareF[LowMem]. Ranking installed
        // models with no resources check would name it here.
        //
        // THIS ASSERTED "Qwen3.5-0.8B-MNN" AND THAT WAS THE STRUCK-OUT RULE.
        // "Best LLM is X, X is used; a missing X is FETCHED, never substituted" - so
        // the answer is the best model the P30 CAN hold, not the best one that happens
        // to be on its disk. ModelFit's own measured table says what that is:
        // "P30 3.867 GB -> ceiling 2.37 GB   refuses the 4B (3.8, killed it), admits
        // the 2B (1.9, which ran)". The 2B is the answer and it is correct; naming the
        // 0.8B because it was already there is what had this phone on Qwen3-0.6B for a
        // week.
        //
        // So the rule is pinned, not the name: the 4B is never named on this handset,
        // and whatever is named is something it could actually hold.
        var chosen = Chosen(Store("Qwen3.5-0.8B-MNN", "Qwen3.5-4B-MNN"), P30());

        Assert.NotNull(chosen);
        Assert.NotEqual("Qwen3.5-4B-MNN", chosen!.Name);
        Assert.True(ModelFit.CouldEverHold(chosen, P30()),
            $"the P30 was told its brain is {chosen.Name}, needing "
            + $"{ModelFit.EagerGb(chosen):0.##} GB against a 2.37 GB ceiling");
    }

    [Fact]
    public void A_model_on_disk_this_phone_cannot_hold_is_not_named_at_all()
    {
        // THE CIRCLE OS CASE, and it was still live for chat. A complete 22.8 GB MoE
        // on a 7.6 GB phone was named as the chosen model on the abilities screen
        // while the service refused to load it - "one rule, one answer", with two
        // answers. ModelOffer.Worth does not catch it: its Chat branch returns true
        // without a fit check, on purpose.
        var chosen = Chosen(Store("Qwen3.6-35B-A3B-MNN"), P30());

        Assert.True(chosen is null || chosen.Name != "Qwen3.6-35B-A3B-MNN",
            $"the P30 was told its brain is {chosen?.Name}, which it cannot hold");
    }

    [Fact]
    public void With_nothing_holdable_on_disk_the_brain_is_still_answered()
    {
        // Falling through must not mean falling silent: null told setup there was
        // nothing to fetch, which is how a phone ends up with no brain and nothing
        // fetching one. The smallest this device could ever hold is the answer.
        //
        // THE RULE, NOT THE NAME. This read Assert.Equal("Qwen3.5-0.8B-MNN", ...) - the
        // selector's answer on the day it was written - so it failed the moment a
        // smaller runnable Qwen3.5 entered the registry, for no defect. Which model is
        // smallest is the registry's business and choosing it is the selector's; that
        // SOMETHING holdable is named is this test's.
        var chosen = Chosen(Store("Qwen3.6-35B-A3B-MNN"), P30());

        Assert.NotNull(chosen);
        Assert.NotEqual("Qwen3.6-35B-A3B-MNN", chosen!.Name);
        Assert.False(chosen.Retired, $"{chosen.Name} is retired");
        Assert.True(ModelFit.CouldEverHold(chosen, P30()),
            $"the P30 was told its brain is {chosen.Name}, which it could never hold");
        Assert.True(EngineChoice.CanRun(chosen, MnnOnly),
            $"the P30 was told its brain is {chosen.Name}, which " + EngineChoice.Explain(chosen, MnnOnly));
    }

    [Fact]
    public void A_retired_model_on_disk_is_never_the_answer()
    {
        // "Nothing less than Qwen3.5 is the LLM", and the floor is data: the P30 ran
        // Qwen3-0.6B for a week because the installed-first rule named it.
        //
        // THE RULE, NOT THE NAME, for the reason the test below this one gives. This
        // pinned "Qwen3.5-0.8B-MNN" - the other model in the store - which is the
        // installed-first rule again by the back door: it only passed while the best
        // holdable model happened to be one of the two on disk. The answer on this
        // handset is Qwen3.5-2B, which is not in the store and is fetched. What this
        // test is for is that the RETIRED row is never it.
        var chosen = Chosen(Store("Qwen3-0.6B-MNN", "Qwen3.5-0.8B-MNN"), P30());

        Assert.NotNull(chosen);
        Assert.NotEqual("Qwen3-0.6B-MNN", chosen!.Name);
        Assert.False(chosen.Retired, $"{chosen.Name} is retired and was named as the brain");
    }

    [Fact]
    public void The_answer_does_not_change_with_the_free_memory_of_the_moment()
    {
        // COULD-EVER-HOLD, NOT FITS-NOW. UsableRamGb comes off MemAvailable, so a rule
        // reading it names a different brain at nine in the morning and at midnight -
        // measured on a 7.6 GB phone at versionCode 53, which refused a complete
        // 1.4 GB model it runs fine because 1.8 GB happened to be free.
        var store = Store("Qwen3.5-0.8B-MNN", "Qwen3.5-2B-MNN");

        var busy = new DeviceProbe(
            RamAvailableBytes: 300_000_000, StorageFreeBytes: 60_000_000_000,
            Gpu: GpuKind.None, CpuCores: 8,
            Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        { RamTotalBytes = 8_000_000_000 };

        var quiet = new DeviceProbe(
            RamAvailableBytes: 5_000_000_000, StorageFreeBytes: 60_000_000_000,
            Gpu: GpuKind.None, CpuCores: 8,
            Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        { RamTotalBytes = 8_000_000_000 };

        Assert.Equal(Chosen(store, busy)?.Name, Chosen(store, quiet)?.Name);
    }
}
