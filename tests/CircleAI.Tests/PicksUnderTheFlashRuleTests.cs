// PicksUnderTheFlashRuleTests.cs
//
// THE OTHER PLACES A BRAIN IS PICKED, UNDER THE RULE THAT A BRAIN READ FROM FLASH IS NOT HELD TO ITS SIZE IN RAM
// (ModelFit.ReadFromFlash; "RAM IS THE TURBO NOT THE ENGINE"). The owner, 2026-11-13, of what had no test: "First run,
// upgrade pick, best-installed pick under the flash rule" - "Create and add tests - They should be green" - and of which
// brains there are to pick: "we are currently working only with Bonsai 2 family - 27B 9B 4B and 2B".
//
// The selector's own pick is OneBestLlmTests. These are the three beside it:
//
//   FIRST RUN        what a fresh phone fetches first. "A background process for hours of course just like updating an
//                    OS": the phone answers while the best brain is fetched and indexed behind it - so the first brain
//                    is one that answers at once, not six gigabytes of one that cannot answer for hours.
//   THE UPGRADE      what is fetched after that, on Wi-Fi and on charge: the brain one step above the one that answers
//                    ("with each success the model is upgraded" - TheUpgradeClimbsOneStepAtATimeTests is the climb).
//   BEST INSTALLED   which of the brains on the phone is the best one - whatever room is left to download another.
//
// On the catalogue's own rows, with a store of files the right size and nothing in them (SparseStore).

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

[Collection(nameof(BrainSpeedCollection))]   // BrainSpeed.Measured is a static the speed tests set
public sealed class PicksUnderTheFlashRuleTests : IDisposable
{
    private const string TwoB = "Ternary-Bonsai-2-2B";
    private const string FourB = "Ternary-Bonsai-2-4B";
    private const string TwentySevenB = "Ternary-Bonsai-2-27B";
    private const string OldQwen = "Qwen3.5-2B-MNN";
    private const string FourBQwen = "Qwen3.5-4B-MNN";

    private const long Gb = 1_000_000_000;

    private static readonly ModelEngine[] Shipped = [ModelEngine.Mnn, ModelEngine.Managed];

    private readonly ModelRegistryService _registry = new();
    private readonly List<string> _stores = [];

    public PicksUnderTheFlashRuleTests() => BrainSpeed.Measured = _ => null;

    public void Dispose()
    {
        BrainSpeed.Measured = null;
        _registry.Dispose();
        foreach (var dir in _stores)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The P30's memory - 3.6 GB, about 1.2 of it available - with the flash free that a test gives it.
    private static DeviceProbe Phone(bool readsFromFlash, long storageFree) =>
        new(RamAvailableBytes: 1_196_892_000, StorageFreeBytes: storageFree, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        {
            RamTotalBytes = 3_867_152_384,
            StorageTotalBytes = 113_170_432_000,
            BrainsReadFromFlash = readsFromFlash,
        };

    // A model store holding these models: every file of each at its catalogued size, with nothing in it.
    private BundleModelLoader Holding(params string[] models)
    {
        var dir = Path.Combine(Path.GetTempPath(), "circleai-picks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _stores.Add(dir);
        foreach (var name in models)
        {
            var row = _registry.GetLatestModel(name);
            Assert.True(row?.BundleFiles is { Count: > 0 }, $"setup: the registry has no bundle for {name}");
            foreach (var file in row!.BundleFiles!) SparseStore.Write(Path.Combine(dir, name, file.Name), file.SizeBytes);
        }
        return new BundleModelLoader(dir, _registry);
    }

    // The brains this device may be offered, as the background fetcher lists them: live, and no brain's companion.
    private List<ModelEntry> Offered() =>
        [.. _registry.AllModels.Where(e => e.Modality == ModelModality.Chat && !e.Retired && !BrainCompanions.IsCompanion(e))];

    // The brain a first run plans to fetch: a chat model that is nobody's companion. Null when it plans none.
    private static string? BrainPlanned(IEnumerable<SetupStep> plan) =>
        plan.Where(s => s.Model.Modality == ModelModality.Chat && !BrainCompanions.IsCompanion(s.Model)).Select(s => s.Model.Name).SingleOrDefault();

    // ---- first run ----

    // "just like updating an OS": the phone works while the big thing is prepared behind it. With room for every brain
    // of the family and an engine that reads them from flash, a fresh phone still fetches first the brain it can answer
    // with in minutes - 429 MB - and not 5.9 GB of the 27B, which on that phone writes a word every 26 to 80 seconds
    // until an index it has not yet built has passed its check.
    [Fact]
    public void A_fresh_phone_fetches_first_the_brain_it_can_answer_with_at_once()
    {
        var phone = Phone(readsFromFlash: true, storageFree: 60 * Gb);
        using var loader = Holding();

        var plan = FirstRun.Plan(_registry, loader, phone, speech: false, engines: Shipped);

        Assert.Equal(TwoB, BrainPlanned(plan));
        Assert.True(BrainInWaiting.Ready(_registry.GetLatestModel(TwoB)!, phone, index: null));
        Assert.False(BrainInWaiting.Ready(_registry.GetLatestModel(TwentySevenB)!, phone, index: null));
    }

    // The same phone where the engine keeps a brain in RAM: the same first brain - what the flash rule did not change.
    [Fact]
    public void Where_the_engine_keeps_a_brain_in_RAM_the_first_brain_is_the_same_one()
    {
        using var loader = Holding();

        Assert.Equal(TwoB, BrainPlanned(FirstRun.Plan(_registry, loader, Phone(readsFromFlash: false, storageFree: 60 * Gb), speech: false, engines: Shipped)));
    }

    // And always of the family: a first run never plans a Qwen where a brain of the family runs.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_first_brain_is_always_one_of_the_family(bool readsFromFlash)
    {
        using var loader = Holding();
        foreach (var free in new[] { 2 * Gb, 8 * Gb, 60 * Gb })
        {
            var planned = BrainPlanned(FirstRun.Plan(_registry, loader, Phone(readsFromFlash, free), speech: false, engines: Shipped));
            Assert.True(planned is not null && ModelChoice.Fleet(_registry.GetLatestModel(planned)!),
                $"with {free / Gb} GB of flash free the first run plans {planned ?? "no brain"}");
        }
    }

    // A phone that already answers plans no second brain at first run: the better one is the upgrade's to fetch.
    [Fact]
    public void A_phone_that_already_has_a_working_brain_plans_no_other_at_first_run()
    {
        var phone = Phone(readsFromFlash: true, storageFree: 60 * Gb);
        using var loader = Holding(TwoB);

        Assert.Null(BrainPlanned(FirstRun.Plan(_registry, loader, phone, speech: false, engines: Shipped)));

        // And that P30, holding the 2B and the 27B with 2.8 GB free: nothing more is owed.
        using var both = Holding(TwoB, TwentySevenB);
        Assert.Null(BrainPlanned(FirstRun.Plan(_registry, both, Phone(readsFromFlash: true, storageFree: 2_800_000_000), speech: false,
            proven: id => id is TwoB or TwentySevenB, engines: Shipped)));
    }

    // ---- the upgrade ----

    // "With each success the model is upgraded, until a limit pattern is established": behind the brain that answers,
    // what is fetched next is the brain one step above it - not the best one the flash has room to download, which is
    // how the 27B went to that P30 from the 2B in one jump (this test said so until 2026-11-14).
    [Fact]
    public void The_upgrade_is_the_brain_one_step_above_the_one_that_answers_where_the_flash_has_room_for_it()
    {
        using var loader = Holding(TwoB);
        bool Present(string name) => loader.ModelPresent(name);
        string? Upgrade(DeviceProbe phone) => ModelChoice.Upgrade(Offered(), Present, phone, Shipped)?.Name;

        Assert.Equal(FourB, Upgrade(Phone(readsFromFlash: true, storageFree: 60 * Gb)));
        Assert.Equal(FourB, Upgrade(Phone(readsFromFlash: true, storageFree: 4 * Gb)));
        Assert.Equal(FourB, Upgrade(Phone(readsFromFlash: true, storageFree: 1_500_000_000)));
        Assert.Null(Upgrade(Phone(readsFromFlash: true, storageFree: 400_000_000)));       // no room for the 4B's file
    }

    // Where the engine keeps a brain in RAM the step up is the same one, and the climb ends where memory does.
    [Fact]
    public void Where_the_engine_keeps_a_brain_in_RAM_the_upgrade_is_the_same_step_up()
    {
        using var loader = Holding(TwoB);
        var device = Phone(readsFromFlash: false, storageFree: 60 * Gb);

        Assert.Equal(FourB, ModelChoice.Upgrade(Offered(), name => loader.ModelPresent(name), device, Shipped)?.Name);

        // From the 4B: the 9B's 2.5 GB is past the 2.37 GB one model may claim of that phone, and nothing is fetched.
        using var further = Holding(TwoB, FourB);
        Assert.Null(ModelChoice.Upgrade(Offered(), name => further.ModelPresent(name), device, Shipped));
    }

    // No brain here yet is the first run's to settle, the best one here leaves nothing to fetch, and a Qwen is never the
    // upgrade of a brain of the family - "we are currently working only with Bonsai 2 family".
    [Fact]
    public void Nothing_is_fetched_as_an_upgrade_without_a_brain_here_or_past_the_best_one_and_never_a_Qwen()
    {
        var roomy = Phone(readsFromFlash: true, storageFree: 60 * Gb);
        using (var empty = Holding())
            Assert.Null(ModelChoice.Upgrade(Offered(), name => empty.ModelPresent(name), roomy, Shipped));
        using (var best = Holding(TwoB, TwentySevenB))
            Assert.Null(ModelChoice.Upgrade(Offered(), name => best.ModelPresent(name), roomy, Shipped));

        // From the old Qwen, the upgrade is a brain of the family - even its 2B, of the same rank.
        using var old = Holding(OldQwen);
        foreach (var phone in new[] { roomy, Phone(readsFromFlash: true, storageFree: 700_000_000), Phone(readsFromFlash: false, storageFree: 60 * Gb) })
        {
            var upgrade = ModelChoice.Upgrade(Offered(), name => old.ModelPresent(name), phone, Shipped);
            Assert.True(upgrade is not null && ModelChoice.Fleet(upgrade), $"from {OldQwen} the upgrade is {upgrade?.Name ?? "nothing"}");
        }
        Assert.Equal(TwoB, ModelChoice.Upgrade(Offered(), name => old.ModelPresent(name), Phone(readsFromFlash: true, storageFree: 700_000_000), Shipped)?.Name);
    }

    // ---- the best brain on the phone ----

    // That P30 held the 2B and the 27B with 2.8 GB of flash free. Which of the two is the best brain on it does not
    // depend on whether there is room to download the 27B a second time: its file is on the flash already.
    [Fact]
    public void The_best_brain_on_the_phone_is_the_best_one_there_whatever_room_is_left_to_download_another()
    {
        using var loader = Holding(TwoB, TwentySevenB);

        foreach (var free in new[] { 400_000_000, 2_800_000_000, 60 * Gb })
        {
            var best = loader.BestInstalledChatModel(Phone(readsFromFlash: true, storageFree: free), engines: Shipped);
            Assert.True(best == TwentySevenB, $"with {free / 1_000_000} MB of flash free the best brain on the phone is said to be {best}");
        }
    }

    // Where the engine keeps a brain in RAM, the 27B's 7.5 GB is asked for and the 2B is the best the phone has.
    [Fact]
    public void Where_the_engine_keeps_a_brain_in_RAM_the_best_brain_on_the_phone_is_the_one_its_memory_holds()
    {
        using var loader = Holding(TwoB, TwentySevenB);

        Assert.Equal(TwoB, loader.BestInstalledChatModel(Phone(readsFromFlash: false, storageFree: 60 * Gb), engines: Shipped));
    }

    // "MNN answers only where no fleet brain can": beside a brain of the family, a Qwen is never the best on the phone -
    // not the one of its own rank, and not one ranked above it.
    [Fact]
    public void Beside_a_brain_of_the_family_a_Qwen_is_never_the_best_brain_on_the_phone()
    {
        var roomy = new DeviceProbe(RamAvailableBytes: 12 * Gb, StorageFreeBytes: 200 * Gb, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        { RamTotalBytes = 16 * Gb, StorageTotalBytes = 256 * Gb };
        using var loader = Holding(TwoB, OldQwen, FourBQwen);

        Assert.True(_registry.GetLatestModel(FourBQwen)!.QualityRank > _registry.GetLatestModel(TwoB)!.QualityRank, "setup: the Qwen is not ranked above the 2B");
        Assert.Equal(TwoB, loader.BestInstalledChatModel(roomy, engines: Shipped));
        Assert.Equal(TwoB, loader.BestInstalledChatModel(Phone(readsFromFlash: true, storageFree: 4 * Gb), engines: Shipped));

        // Refused here - it crashed this phone - the family's brain is not an answer, and the best Qwen that fits is.
        Assert.Equal(FourBQwen, loader.BestInstalledChatModel(roomy, refused: id => id == TwoB, engines: Shipped));
    }
}
