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

    private ModelEntry? Chosen(string store, DeviceProbe probe)
    {
        using var loader = new BundleModelLoader(store, _registry);
        return ModelChoice.For(ModelModality.Chat, _registry, loader, probe);
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
    public void The_P30_keeps_the_0_8B_even_with_the_4B_sitting_beside_it()
    {
        // 2.85 GB of weights, 3.8 GB declared, against a 2.37 GB ceiling. Downloading
        // it took six minutes on 2026-10-09 and loading it got the whole service
        // killed. Ranking installed models with no resources check would name it here.
        var chosen = Chosen(Store("Qwen3.5-0.8B-MNN", "Qwen3.5-4B-MNN"), P30());

        Assert.NotNull(chosen);
        Assert.Equal("Qwen3.5-0.8B-MNN", chosen!.Name);
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
        var chosen = Chosen(Store("Qwen3.6-35B-A3B-MNN"), P30());

        Assert.NotNull(chosen);
        Assert.Equal("Qwen3.5-0.8B-MNN", chosen!.Name);
    }

    [Fact]
    public void A_retired_model_on_disk_is_never_the_answer()
    {
        // "Nothing less than Qwen3.5 is the LLM", and the floor is data: the P30 ran
        // Qwen3-0.6B for a week because the installed-first rule named it.
        var chosen = Chosen(Store("Qwen3-0.6B-MNN", "Qwen3.5-0.8B-MNN"), P30());

        Assert.NotNull(chosen);
        Assert.Equal("Qwen3.5-0.8B-MNN", chosen!.Name);
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
