// EveryModelArrivesByDefaultTests.cs
//
// "BY DEFAULT ALL MODELS MUST BE DOWNLOADED." (the owner, 2026-10-24)
//
// The first-run plan fetched ONE model per ability - the English voice, the South
// African voices, the phone's own language, the ears, the wake word, the brain, the
// eyes - and then stopped. Seven entries out of a catalogue of ninety. Everything
// else sat behind a "Turn on" button on the abilities screen, which is a chore
// wearing the word "ability"; and the home screen meanwhile claimed seventy-odd
// languages spoken out loud on a phone holding three voices.
//
// THE RULE IS ONE QUESTION: "is this the best model given the resources we have?"
// For every modality but one the answer is additive - another voice is a language
// this phone could not speak, another wake word is a phrase it could not hear - so
// all of them are the answer, and all of them arrive.
//
// THE BRAIN IS WHERE THE SAME QUESTION SAYS ONE. Only one is loaded at a time and the
// best one wins; the rest are gigabytes of alternative that will never be chosen
// while it is there. On the P30 the looser rule picks Qwen3.5-2B - 1.9 GB against a
// 2.37 GB ceiling - and loading it is what set off the system-wide low-memory kills
// on 2026-10-10. Fetching it anyway is not "all models", it is a worse answer.
// TheBrainFloorTests pins that half; this pins the rest.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Assistant;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class EveryModelArrivesByDefaultTests : IDisposable
{
    private readonly ModelRegistryService _registry = new();
    private readonly List<string> _stores = [];

    public void Dispose()
    {
        _registry.Dispose();
        foreach (var dir in _stores)
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }

    private string Store()
    {
        var dir = Path.Combine(Path.GetTempPath(), "circleai-plan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _stores.Add(dir);
        return dir;
    }

    /// <summary>A phone with room: 12 GB of RAM, 6 GB free, and storage to spare.</summary>
    private static DeviceProbe Roomy() => new(
        RamAvailableBytes: 6_000_000_000,
        StorageFreeBytes: 200_000_000_000,
        Gpu: GpuKind.None, CpuCores: 8,
        Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
    {
        RamTotalBytes = 12_000_000_000,
    };

    /// <summary>The P30 as measured: 3.6 GB total, about 0.44 GB available.</summary>
    private static DeviceProbe P30() => new(
        RamAvailableBytes: 440_000_000,
        StorageFreeBytes: 29_000_000_000,
        Gpu: GpuKind.None, CpuCores: 8,
        Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
    {
        RamTotalBytes = 3_776_516L * 1024,
    };

    private IReadOnlyList<SetupStep> Plan(DeviceProbe probe) =>
        FirstRun.Plan(_registry, new BundleModelLoader(Store(), _registry), probe, speech: true);

    private bool CanRun(ModelEntry m, DeviceProbe probe)
        => !m.Retired
        && ModelFit.CouldEverHold(m, probe)
        && ModelFit.FitsStorage(m, probe.StorageFreeGb);

    [Fact]
    public void Every_voice_this_phone_can_run_is_fetched_without_being_asked_for()
    {
        // THE CLAIM ON THE HOME SCREEN IS THE TEST. It says "N languages, spoken out
        // loud", counted from the catalogue, on a phone that had downloaded three
        // voices. Either the claim is false or every voice arrives; this is the second.
        var probe = Roomy();
        var planned = Plan(probe)
            .Where(s => s.Modality == ModelModality.Tts)
            .Select(s => s.Model.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var runnable = _registry.AllModels
            .Where(m => m.Modality == ModelModality.Tts && CanRun(m, probe))
            .Select(m => m.Name)
            .ToList();

        Assert.NotEmpty(runnable);
        var missing = runnable.Where(n => !planned.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            $"{missing.Count} voice(s) this phone can run are not fetched by default: {string.Join(", ", missing.Take(8))}");
    }

    [Theory]
    [InlineData(ModelModality.Asr)]
    [InlineData(ModelModality.WakeWord)]
    [InlineData(ModelModality.Vision)]
    public void Every_other_ability_arrives_in_full(ModelModality modality)
    {
        // Each of these is additive too: better ears hear more, another wake word is
        // another phrase, better eyes see more. None of them was fetched beyond the
        // first - "Seeing" in particular sat behind a Turn on button on a phone that
        // had already fetched the voice, the ears, the wake word and the brain.
        var probe = Roomy();
        var planned = Plan(probe)
            .Where(s => s.Modality == modality)
            .Select(s => s.Model.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var runnable = _registry.AllModels
            .Where(m => m.Modality == modality && CanRun(m, probe))
            .Select(m => m.Name)
            .ToList();

        if (runnable.Count == 0) return;   // nothing catalogued for this ability yet

        var missing = runnable.Where(n => !planned.Contains(n)).ToList();
        Assert.True(missing.Count == 0,
            $"{modality}: {missing.Count} model(s) this phone can run are not fetched: {string.Join(", ", missing.Take(8))}");
    }

    [Fact]
    public void Nothing_is_planned_twice()
    {
        // The ability slots run first and the rest follows; a name in both would
        // download the same bytes twice and report the step twice on the screen.
        var names = Plan(Roomy()).Select(s => s.Model.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_abilities_still_come_first_so_the_phone_is_useful_soonest()
    {
        // THE ORDER IS THE WHOLE DESIGN. The loading screen hands over the moment the
        // voice and the ears have landed; putting ninety entries in front of them
        // would hold somebody on a progress bar for the entire catalogue.
        var plan = Plan(Roomy());
        var titles = plan.Select(s => s.Title).ToList();

        var ears = titles.IndexOf("the ears");
        var brain = titles.IndexOf("the brain");
        Assert.True(ears >= 0 && brain > ears, "the ability slots are no longer in front");

        // And what follows them is ordered smallest first, so each extra language
        // arrives as soon as it can rather than behind the largest file on the phone.
        var tail = plan.Skip(titles.IndexOf("the eyes") + 1).Select(s => s.Model.TotalBytes).ToList();
        if (tail.Count > 1)
            Assert.True(tail.SequenceEqual(tail.OrderBy(b => b)),
                "the long tail is not smallest-first");
    }

    [Fact]
    public void Only_one_brain_is_ever_fetched()
    {
        // The same question - "is this the best model given the resources we have?" -
        // has exactly one answer for a slot where only one can be loaded. The others
        // are gigabytes that will never be chosen while it is there, and on the P30
        // one of them is the model whose load set off the low-memory kills.
        foreach (var probe in new[] { Roomy(), P30() })
            Assert.True(Plan(probe).Count(s => s.Modality == ModelModality.Chat) <= 1,
                "more than one brain is being fetched");
    }

    [Fact]
    public void A_model_this_phone_cannot_run_is_not_fetched()
    {
        // RESOURCES ARE THE FILTER AND THE ONLY ONE. "All models" means all the models
        // this device can actually run; a 21 GB bundle on a phone with 29 GB free and
        // 3.6 GB of RAM is not a capability, it is a download that ends in a kill.
        var probe = P30();
        foreach (var step in Plan(probe))
            Assert.True(CanRun(step.Model, probe),
                $"{step.Model.Name} is planned for a phone that cannot run it");
    }

    [Fact]
    public void What_is_already_on_disk_is_not_fetched_again()
    {
        var probe = Roomy();
        var first = Plan(probe);
        Assert.NotEmpty(first);

        // Put the smallest planned model on disk, at full size, and plan again.
        var smallest = first.OrderBy(s => s.Model.TotalBytes).First().Model;
        var store = Store();
        using var loader = new BundleModelLoader(store, _registry);
        foreach (var f in smallest.BundleFiles ?? [])
        {
            var path = Path.Combine(store, smallest.Name, f.Name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[f.SizeBytes]);
        }

        var again = FirstRun.Plan(_registry, loader, probe, speech: true);
        Assert.DoesNotContain(again, s =>
            string.Equals(s.Model.Name, smallest.Name, StringComparison.OrdinalIgnoreCase));
    }
}
