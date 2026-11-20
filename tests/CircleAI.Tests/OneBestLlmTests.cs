// OneBestLlmTests.cs
//
// THE OWNER, 2026-11-13, of a P30 that held the 2B and the 27B and answered with the 2B:
//
//   "did housekeeping do its job and make space by removing old LLM? 2B or 9B or 27B house keeping must make the space"
//   "Dont pin anything if the model selector is built right then the best LLM will be selected - if not then you
//    have a bug"
//
// and the indexed mind's own rules: "ONE BEST LLM"; "RAM IS THE TURBO NOT THE ENGINE".
//
// WHAT THAT PHONE SHOWED (its own log, 2026-11-11 to 2026-11-13, at every start):
//
//   Model catalogue: assessed 111 model(s), 0 run on this device (RAM source: PlatformMeasured).
//   Model catalogue: NO compatible chat model for this device (111 catalogued)
//
// 3.6 GB of memory, 113 GB of flash with 4.8 GB of it free, the 2B and the 27B both in its store. Two rules said no to
// every model, the two on the phone among them, so the pick fell to "the smallest eligible entry" - the 2B - and the 27B
// was never looked at:
//
//   - The storage budget is what is free less the 10 GB kept for the phone. Under 10 GB free that is nothing, and it was
//     asked of models that had already landed.
//   - A brain was held to its size in RAM (the 27B declares 7.5 GB against a 2.37 GB ceiling), where that phone's engine
//     reads every layer from flash as it computes and keeps no copy of them: on 2026-11-09 it ran the 27B "0 of 64 layers
//     warm, 64 streamed through 2 slots of 103 MB" with 917 MB available.
//
// And housekeeping removed nothing: it removes a model only for a better one no more than twice its size, which keeps a
// smaller model as the one that at least fits. A brain read from flash fits wherever its file does, so nothing smaller is
// a fallback for it - it is the old LLM, and the space it holds is the space the best one's index needs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Store;
using Xunit;

namespace CircleAI.Tests;

[Collection(nameof(BrainSpeedCollection))]   // BrainSpeed.Measured is a static the speed tests set
public sealed class OneBestLlmTests : IDisposable
{
    // The speeds this phone has measured, by brain: none until a test says a brain has answered here.
    private readonly Dictionary<string, double> _measured = new(StringComparer.Ordinal);

    public OneBestLlmTests() => BrainSpeed.Measured = id => _measured.TryGetValue(id, out var speed) ? speed : null;

    private const string TwoB = "Ternary-Bonsai-2-2B";
    private const string FourB = "Ternary-Bonsai-2-4B";
    private const string NineB = "Ternary-Bonsai-2-9B";
    private const string TwentySevenB = "Ternary-Bonsai-2-27B";
    private const string OldMnn = "Qwen3.5-2B-MNN";   // the 2B of another engine: the brain that P30 had before the fleet's

    private static readonly ModelEngine[] Shipped = [ModelEngine.Mnn, ModelEngine.Managed];

    private readonly ModelRegistryService _registry = new();

    public void Dispose()
    {
        BrainSpeed.Measured = null;
        _registry.Dispose();
    }

    // The P30 as it stood on 2026-11-13: MemTotal 3,776,516 kB, about 1.2 GB of it available, 113 GB of flash, 4.8 GB free.
    private static DeviceProbe P30(bool readsFromFlash, long storageFree = 4_830_988_000) =>
        new(RamAvailableBytes: 1_196_892_000, StorageFreeBytes: storageFree, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        {
            RamTotalBytes = 3_867_152_384,
            StorageTotalBytes = 113_170_432_000,
            BrainsReadFromFlash = readsFromFlash,
        };

    private static SqliteModelCatalog NewCatalog() => new("Data Source=:memory:");

    // The catalogue's own rows for the fleet's PTQ1_0 brains (and any other row named), the ones in `installed` on the phone.
    private SqliteModelCatalog Catalogue(string[] rows, params string[] installed)
    {
        var cat = NewCatalog();
        foreach (var name in rows)
        {
            var row = _registry.GetLatestModel(name);
            Assert.True(row is not null, $"setup: the registry has no row for {name}");
            cat.Upsert(row!);
        }
        foreach (var name in installed) cat.SetInstalled(name, true);
        return cat;
    }

    private static DeviceModelAssessor Assessor(IModelCatalog cat) => new(cat, Shipped);

    private static ModelSelection Pick(IModelCatalog cat, DeviceProbe probe)
    {
        using var selector = new DeviceAwareModelSelector(Shipped, cat);
        return selector.BestFit(probe, ChatCapability.Default);
    }

    // ---- the selector ----

    // "0 run on this device", with the phone's own brain in its store. Room to land is asked of what has yet to land.
    [Fact]
    public void A_brain_already_on_the_phone_is_not_refused_for_want_of_room_to_land()
    {
        var phone = P30(readsFromFlash: false);
        using var cat = Catalogue([TwoB, FourB], installed: TwoB);

        var result = Assessor(cat).Assess(phone);

        Assert.True(cat.Assessment(TwoB)!.Compatible,
            "the 2B is on the phone and its 0.6 GB fits the memory that is free, and the catalogue says it does not run here");
        Assert.False(cat.Assessment(FourB)!.Compatible,
            "the 4B is not on the phone, and with 4.8 GB free there is no room for it beside the 10 GB kept for the phone");
        Assert.Equal(1, result.Compatible);
        Assert.Equal(TwoB, Pick(cat, phone).ModelId);
        Assert.Equal(SelectionQuality.Good, Pick(cat, phone).Quality);
    }

    // "if the model selector is built right then the best LLM will be selected" - with no pin.
    [Fact]
    public void The_best_LLM_on_the_phone_is_selected_where_the_engine_reads_it_from_flash()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = Catalogue([TwoB, FourB, NineB, TwentySevenB], installed: [TwoB, TwentySevenB]);

        Assessor(cat).Assess(phone);
        var pick = Pick(cat, phone);

        Assert.True(cat.Assessment(TwentySevenB)!.Compatible,
            "RAM IS THE TURBO NOT THE ENGINE: the 27B is on the phone's flash, the engine reads it from there, and the catalogue says it does not run here");
        Assert.Equal(TwentySevenB, pick.ModelId);
        Assert.Equal(SelectionQuality.Good, pick.Quality);
        Assert.False(pick.RequiresDownload);
    }

    // The same phone with an engine that keeps a brain in RAM: the 27B's 7.5 GB is asked for, and the 2B is the brain.
    [Fact]
    public void Where_the_engine_keeps_a_brain_in_RAM_the_phone_runs_the_brain_that_fits_it()
    {
        var phone = P30(readsFromFlash: false);
        using var cat = Catalogue([TwoB, TwentySevenB], installed: [TwoB, TwentySevenB]);

        Assessor(cat).Assess(phone);

        Assert.False(cat.Assessment(TwentySevenB)!.Compatible);
        Assert.Equal(TwoB, Pick(cat, phone).ModelId);
    }

    // What is read from flash is the packing the vector kernel computes in tiles - PTQ1_0 on the C# engine. A PQ2_0 file is
    // kept in RAM, and so is a brain of another engine: each is still held to its size.
    [Fact]
    public void Only_a_brain_the_engine_reads_from_flash_is_free_of_the_RAM_rule()
    {
        var phone = P30(readsFromFlash: true);
        const string pq2 = "Ternary-Bonsai-2-27B-PQ2_0";
        const string mnn = "An-8B-MNN";
        using var cat = Catalogue([TwoB, pq2], installed: [TwoB, pq2]);
        cat.Upsert(new ModelEntry(mnn, "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Chat, TotalBytes = 5_510_447_174,
            MinRamGb = 7.2, MinStorageGb = 5.6, QualityRank = 14,
        });
        cat.SetInstalled(mnn, true);

        Assessor(cat).Assess(phone);

        Assert.True(ManagedChatGenerator.ReadsFromFlash("qwen35", "PTQ1_0"));
        Assert.False(ManagedChatGenerator.ReadsFromFlash("qwen35", "PQ2_0"));
        Assert.False(ManagedChatGenerator.ReadsFromFlash("qwen35", "PQ2_0-MTP"));
        Assert.False(ManagedChatGenerator.ReadsFromFlash("clip", "Q8_0"));
        Assert.False(cat.Assessment(pq2)!.Compatible, "a PQ2_0 brain is kept in RAM, and 9 GB of it was let onto a 3.6 GB phone");
        Assert.False(cat.Assessment(mnn)!.Compatible, "an MNN brain is kept in RAM, and it was let onto a phone it does not fit");
        Assert.Equal(TwoB, Pick(cat, phone).ModelId);
    }

    // One fact, one owner: every pick asks ModelFit, so the durable question and the momentary one answer as the catalogue does.
    [Fact]
    public void Every_fit_question_gives_a_brain_read_from_flash_the_same_answer()
    {
        var big = _registry.GetLatestModel(TwentySevenB)!;
        var reads = P30(readsFromFlash: true, storageFree: 20_000_000_000);
        var keeps = P30(readsFromFlash: false, storageFree: 20_000_000_000);

        Assert.True(ModelFit.ReadFromFlash(big, reads));
        Assert.False(ModelFit.ReadFromFlash(big, keeps));
        Assert.True(ModelFit.CouldEverHold(big, reads), "the durable question still held a brain read from flash to its size in RAM");
        Assert.False(ModelFit.CouldEverHold(big, keeps));
        Assert.True(ModelFit.Fits(big, reads), "the momentary question still held a brain read from flash to the RAM that is free");
        Assert.False(ModelFit.Fits(big, keeps));

        // Room on flash is still asked: the file has to be somewhere.
        Assert.False(ModelFit.Fits(big, P30(readsFromFlash: true, storageFree: 3_000_000_000)));
    }

    // PER-DEVICE PACKING (OPEN-GAPS C8): a brain's two packings carry the same QualityRank, so Best breaks the tie by how
    // fast THIS device decodes each - the GB/s it reads a packing over the bytes a token reads, not GB/s alone. The brain
    // is unchanged (speed is no gate); only its encoding is chosen, and only where both packings are candidates - a device
    // with the RAM to hold the PQ2_0 one. On the P30 the PQ2_0 9B is filtered by fit before this (kept to RAM, 2.4 GB).
    [Fact]
    public void The_faster_decoding_packing_of_a_brain_is_taken_per_device()
    {
        var ptq1 = _registry.GetLatestModel(NineB)!;                        // Ternary-Bonsai-2-9B, PTQ1_0, 1.997 GB
        var pq2 = _registry.GetLatestModel("Ternary-Bonsai-2-9B-PQ2_0")!;   // the same brain, PQ2_0, 2.417 GB
        Assert.Equal(ptq1.QualityRank, pq2.QualityRank);                    // same rank: the tie this breaks
        var set = new[] { ptq1, pq2 };

        var saved = ModelChoice.KernelsHere;
        try
        {
            // A desktop reads PQ2_0 about 3.6x faster; even over its larger file it decodes faster, so it is taken.
            ModelChoice.KernelsHere = () => new CircleAI.Inference.Managed.Kernels.KernelThreads.Choice(
                8, 0, "desktop", Ptq1GigabytesPerSecond: 1.0, Pq2GigabytesPerSecond: 3.6);
            Assert.Equal("Ternary-Bonsai-2-9B-PQ2_0", ModelChoice.Best(set)!.Name);

            // The P30's own kernel race: PQ2_0 reads a touch faster per byte (1.70 vs 1.45 GB/s) but is 21% larger, so
            // PTQ1_0 decodes faster - GB/s alone would wrongly pick PQ2_0 here; GB/s over bytes keeps PTQ1_0.
            ModelChoice.KernelsHere = () => new CircleAI.Inference.Managed.Kernels.KernelThreads.Choice(
                3, 0, "p30", Ptq1GigabytesPerSecond: 1.451, Pq2GigabytesPerSecond: 1.698);
            Assert.Equal(NineB, ModelChoice.Best(set)!.Name);

            // Nothing measured yet (first run): no packing preference, the smaller PTQ1_0 wins on MinRamGb, as before.
            ModelChoice.KernelsHere = () => null;
            Assert.Equal(NineB, ModelChoice.Best(set)!.Name);
        }
        finally
        {
            ModelChoice.KernelsHere = saved;
        }
    }

    // "Best LLM is X, X is used; a missing X is fetched" - X does not depend on what happens to be on the phone. With room
    // for it to land, the best LLM is the one wanted; on the phone as it stood, with 4.8 GB free, it is not.
    [Fact]
    public void With_room_to_land_the_best_LLM_is_the_one_wanted_whether_or_not_it_is_here_yet()
    {
        using var cat = Catalogue([TwoB, FourB, NineB, TwentySevenB], installed: TwoB);

        var roomy = P30(readsFromFlash: true, storageFree: 40_000_000_000);
        Assessor(cat).Assess(roomy);
        var wanted = Pick(cat, roomy);
        Assert.Equal(TwentySevenB, wanted.ModelId);
        Assert.True(wanted.RequiresDownload);

        var full = P30(readsFromFlash: true);
        Assessor(cat).Assess(full);
        Assert.Equal(TwoB, Pick(cat, full).ModelId);
    }

    // ---- housekeeping ----

    private static Func<ModelEntry, bool> ReadFromFlashOn(DeviceProbe phone) => e => ModelFit.ReadFromFlash(e, phone);

    // "house keeping must make the space": the 27B has loaded here, so the 2B and the 9B are the old LLMs.
    [Fact]
    public void Housekeeping_makes_the_space_once_the_best_LLM_has_loaded_here()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = Catalogue([TwoB, NineB, TwentySevenB], installed: [TwoB, NineB, TwentySevenB]);
        Assessor(cat).Assess(phone);
        cat.RecordLoad(TwoB);
        cat.RecordLoad(TwentySevenB);

        var plan = CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone));

        Assert.Equal(new[] { NineB, TwoB }, plan.Select(r => r.Id).OrderByDescending(id => id, StringComparer.Ordinal).ToArray());
        Assert.All(plan, r => Assert.Contains(TwentySevenB, r.Reason, StringComparison.Ordinal));
    }

    // A replacement proves itself first: until the 27B has loaded on this phone, the 2B is its only brain.
    [Fact]
    public void Nothing_is_removed_for_a_best_LLM_that_has_not_loaded_here()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = Catalogue([TwoB, TwentySevenB], installed: [TwoB, TwentySevenB]);
        Assessor(cat).Assess(phone);
        cat.RecordLoad(TwoB);

        Assert.Empty(CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone)));
    }

    // The brain that is answering is never taken away, whatever has loaded beside it.
    [Fact]
    public void The_brain_in_use_is_kept_while_the_space_is_made()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = Catalogue([TwoB, NineB, TwentySevenB], installed: [TwoB, NineB, TwentySevenB]);
        Assessor(cat).Assess(phone);
        cat.RecordLoad(TwentySevenB);

        var plan = CatalogueHousekeeping.Plan(cat, keep: id => id == TwoB, readFromFlash: ReadFromFlashOn(phone));

        Assert.Equal(new[] { NineB }, plan.Select(r => r.Id).ToArray());
    }

    // Where a brain is kept in RAM the smaller one is the one that at least fits, and it stays - the rule as it was.
    [Fact]
    public void A_brain_kept_in_RAM_still_keeps_its_much_smaller_fallback()
    {
        var phone = P30(readsFromFlash: false);
        using var cat = Catalogue([TwoB, TwentySevenB], installed: [TwoB, TwentySevenB]);
        foreach (var id in new[] { TwoB, TwentySevenB })
        {
            cat.SetAssessment(id, compatible: true, rank: 1);
            cat.RecordLoad(id);
        }

        Assert.Empty(CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone)));
        Assert.Empty(CatalogueHousekeeping.Plan(cat));
    }

    // The space is made when the best LLM can take over: while its index is being built it answers at seconds a token,
    // and the brain beside it is what the phone answers with (BrainInWaiting).
    [Fact]
    public void The_brain_that_answers_stays_until_the_best_LLMs_index_is_complete()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = Catalogue([TwoB, NineB, TwentySevenB], installed: [TwoB, NineB, TwentySevenB]);
        Assessor(cat).Assess(phone);
        cat.RecordLoad(TwoB);
        cat.RecordLoad(TwentySevenB);
        Func<ModelEntry, bool> Ready(IndexingStatus? index) => e => BrainInWaiting.Ready(e, phone, e.Name == TwentySevenB ? index : null);

        Assert.Empty(CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone), ready: Ready(null)));
        Assert.Empty(CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone), ready: Ready(new IndexingStatus(92, 93))));

        // Complete with a check that failed is not ready either: an index that serves nobody replaces nothing.
        Assert.Empty(CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone), ready: Ready(new IndexingStatus(93, 93, 250))));

        var plan = CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone), ready: Ready(new IndexingStatus(93, 93, 969)));
        Assert.Equal(new[] { NineB, TwoB }, plan.Select(r => r.Id).ToArray());
    }

    // The same through a start of the service: the catalogue reconciled with the store, assessed, and housekeeping run
    // with the engine's own record of how far the 27B's index has got.
    [Fact]
    public void At_each_start_the_2B_is_kept_while_the_27Bs_index_is_built_and_shed_once_it_is_complete()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = NewCatalog();
        var root = Path.Combine(Path.GetTempPath(), "circleai-onebest-" + Guid.NewGuid().ToString("N")[..8]);
        var models = Path.Combine(root, "models");
        var index = Path.Combine(root, "store");
        try
        {
            Directory.CreateDirectory(index);
            foreach (var id in new[] { TwoB, TwentySevenB })
            {
                Directory.CreateDirectory(Path.Combine(models, id));
                File.WriteAllText(Path.Combine(models, id, "installed.json"), "{}");
                File.WriteAllBytes(Path.Combine(models, id, id + "-PTQ1_0.gguf"), new byte[4096]);
            }
            void Start() => CatalogueBootstrap.Run(cat, Assessor(cat), phone, seedFrom: _registry, modelsDirectory: models,
                reclaimInferior: true, indexDirectory: index);
            void Record(int done, int total, int? agrees = null) => File.WriteAllText(Path.Combine(index, TwentySevenB + "-PTQ1_0.gguf.indexing"),
                $"CAIINDEXING 1\nmodel={TwentySevenB}-PTQ1_0.gguf\nlength=5946648928\nrequest=abc\ndone={done}\ntotal={total}\n"
                + (agrees is { } measured ? $"agrees={measured}\n" : string.Empty));

            Start();
            cat.RecordLoad(TwoB);
            cat.RecordLoad(TwentySevenB);

            Start();
            Assert.True(Directory.Exists(Path.Combine(models, TwoB)), "the 27B's index is not begun, and the brain that answers was removed for it");

            Record(37, 93);
            Start();
            Assert.True(Directory.Exists(Path.Combine(models, TwoB)), "the 27B's index is 39% built, and the brain that answers was removed for it");

            // Complete with its check failed, neither goes: the 27B's next index lists more of its neurons (the owner,
            // 2026-11-15: "clean up and try again") - so that stage is At_a_start_of_that_phone_the_old_LLM_goes_and_the_
            // 27B_whose_index_failed_stays_for_one_listing_more, and this one goes on to an index that passed.

            // Complete and passed - and the 27B has not answered on this phone yet: the 2B is still its only proven brain.
            Record(93, 93, 969);
            Start();
            Assert.True(Directory.Exists(Path.Combine(models, TwoB)), "the 27B's index is complete but it has never answered here, and the brain that answers was removed for it");

            // It has answered here (its speed measured on this phone): now the 2B is the old LLM.
            _measured[TwentySevenB] = 0.3;
            Start();
            Assert.False(Directory.Exists(Path.Combine(models, TwoB)), "the 27B's index is complete and it has answered here, and the old LLM is still in the store");
            Assert.True(Directory.Exists(Path.Combine(models, TwentySevenB)));
            Assert.False(cat.IsInstalled(TwoB));
            Assert.True(cat.IsInstalled(TwentySevenB));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // THE OWNER, 2026-11-13, of that P30's store: "Qwen3.5-2B-MNN (1.4 GB), the 69 voices (7.3 GB) and the 7.66 GB log are
    // untouched. ==>> flash not RAM". Both 2Bs carry rank 9. Which of the two is the brain has one owner - the fleet's,
    // before any other engine's (ModelChoice.Best) - and housekeeping asked the rank alone, found neither better, and kept
    // 1.4 GB of a brain the selector never picks.
    [Fact]
    public void The_old_LLM_of_another_engine_goes_once_the_fleets_brain_of_its_rank_has_loaded_here()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = Catalogue([TwoB, OldMnn], installed: [TwoB, OldMnn]);
        Assessor(cat).Assess(phone);
        var (fleets, old) = (_registry.GetLatestModel(TwoB)!, _registry.GetLatestModel(OldMnn)!);
        Assert.True(ModelChoice.Fleet(fleets) && !ModelChoice.Fleet(old) && fleets.QualityRank == old.QualityRank,
            "setup: the two 2Bs are the fleet's and another engine's, at one rank");
        cat.RecordLoad(OldMnn);

        // The fleet's 2B has not loaded here: the old one is the phone's only proven brain, and nothing goes.
        Assert.Empty(CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone)));

        cat.RecordLoad(TwoB);
        var item = Assert.Single(CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone)));

        Assert.Equal(OldMnn, item.Id);
        Assert.Contains(TwoB, item.Reason, StringComparison.Ordinal);
        Assert.Equal(TwoB, Pick(cat, phone).ModelId);   // what is kept is what the selector picks
    }

    // ONE OWNER FOR "WHICH IS THE BETTER BRAIN". Asked the rank alone, housekeeping removes the fleet's 2B - the brain the
    // selector picks - for another engine's brain a rank above it and near its size, and the phone fetches its brain again.
    [Fact]
    public void Housekeeping_never_removes_the_brain_the_selector_picks_for_one_it_does_not()
    {
        var phone = P30(readsFromFlash: true);
        const string other = "Another-Engines-3B";
        using var cat = Catalogue([TwoB], installed: TwoB);
        cat.Upsert(new ModelEntry(other, "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Chat, TotalBytes = 600_000_000,
            MinRamGb = 0.8, MinStorageGb = 0.7, QualityRank = 10,
        });
        cat.SetInstalled(other, true);
        Assessor(cat).Assess(phone);
        cat.SetAssessment(other, compatible: true, rank: 2);   // it runs here too, whatever today's free memory says
        cat.RecordLoad(TwoB);
        cat.RecordLoad(other);

        var pick = Pick(cat, phone).ModelId;
        var plan = CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone)).Select(r => r.Id).ToArray();

        Assert.Equal(TwoB, pick);
        Assert.DoesNotContain(pick, plan);
        Assert.Equal(new[] { other }, plan);
    }

    // That P30 as it stood on 2026-11-13: the 27B's index complete and failed (one word in four, at fifteen in a hundred
    // of its neurons), the fleet's 2B answering, the old 2B of another engine beside them. At the next start the old LLM
    // goes - 1.4 GB of the phone's flash. The 27B STAYS (the owner, 2026-11-15: "no models should be blocked - if it fails
    // then check why it failed. clean up and try again"): too few neurons is a reason with a remedy, and its next index,
    // listing more, needs its file. The brain that answers stays, and so does the record of the 27B's check, which is
    // what says which share failed.
    [Fact]
    public void At_a_start_of_that_phone_the_old_LLM_goes_and_the_27B_whose_index_failed_stays_for_one_listing_more()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = NewCatalog();
        var root = Path.Combine(Path.GetTempPath(), "circleai-onebest-" + Guid.NewGuid().ToString("N")[..8]);
        var models = Path.Combine(root, "models");
        var index = Path.Combine(root, "store");
        try
        {
            Directory.CreateDirectory(index);
            foreach (var (id, file) in new[] { (TwoB, TwoB + "-PTQ1_0.gguf"), (TwentySevenB, TwentySevenB + "-PTQ1_0.gguf"), (OldMnn, "llm.mnn.weight") })
            {
                Directory.CreateDirectory(Path.Combine(models, id));
                File.WriteAllText(Path.Combine(models, id, "installed.json"), "{}");
                File.WriteAllBytes(Path.Combine(models, id, file), new byte[4096]);
            }
            File.WriteAllText(Path.Combine(index, TwentySevenB + "-PTQ1_0.gguf.indexing"),
                $"CAIINDEXING 1\nmodel={TwentySevenB}-PTQ1_0.gguf\nlength=5946648928\nrequest=abc\ndone=71\ntotal=71\nagrees=250\n");
            void Start() => CatalogueBootstrap.Run(cat, Assessor(cat), phone, seedFrom: _registry, modelsDirectory: models,
                reclaimInferior: true, indexDirectory: index);

            Start();
            foreach (var id in new[] { TwoB, TwentySevenB, OldMnn }) cat.RecordLoad(id);
            _measured[TwentySevenB] = 0.04;   // it has answered here, a token every 26 seconds, with no index
            Start();

            Assert.False(Directory.Exists(Path.Combine(models, OldMnn)), "the old LLM of another engine is still in the store, beside the fleet's 2B that answers");
            Assert.False(cat.IsInstalled(OldMnn));
            Assert.True(Directory.Exists(Path.Combine(models, TwoB)), "the brain that answers was removed");
            Assert.True(cat.IsInstalled(TwoB));
            Assert.True(Directory.Exists(Path.Combine(models, TwentySevenB)), "the 27B went, and its next index - listing more of its neurons - needs its file");
            Assert.True(cat.IsInstalled(TwentySevenB));
            Assert.Equal(new IndexingStatus(71, 71, 250), BrainInWaiting.IndexOf(_registry.GetLatestModel(TwentySevenB)!, index));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- "we need to clean up after ourselves or we will kill users phones" ----

    // THE OWNER, 2026-11-14, of what the 27B's failed index left on that P30 - 3.6 GB of a phone with 4.3 GB free: "they
    // can go we need to clean up after ourselves or we will kill users phones". The 27B is not loaded again once its
    // index has failed, so its engine never gets to remove them; the service does at every start, from the record
    // alone. The record stays - it is what says the check failed - and so does everything that is no index's.
    [Fact]
    public void At_a_start_what_the_failed_index_left_on_the_flash_goes_and_its_record_stays()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = NewCatalog();
        var root = Path.Combine(Path.GetTempPath(), "circleai-onebest-" + Guid.NewGuid().ToString("N")[..8]);
        var models = Path.Combine(root, "models");
        var index = Path.Combine(root, "store");
        var big = TwentySevenB + "-PTQ1_0.gguf";
        var small = TwoB + "-PTQ1_0.gguf";
        try
        {
            Directory.CreateDirectory(index);
            foreach (var (id, file) in new[] { (TwoB, small), (TwentySevenB, big) })
            {
                Directory.CreateDirectory(Path.Combine(models, id));
                File.WriteAllText(Path.Combine(models, id, "installed.json"), "{}");
                File.WriteAllBytes(Path.Combine(models, id, file), new byte[4096]);
            }
            File.WriteAllText(Path.Combine(index, big + ".indexing"),
                $"CAIINDEXING 1\nmodel={big}\nlength=5946648928\nrequest=abc\ndone=71\ntotal=71\nagrees=250\n");

            // What its index left: Watch's records, the core index, the core's pieces, the cached portion, and a write
            // of pieces that was stopped. And what the engine keeps for the two brains that is no index's.
            foreach (var (kind, bytes) in new[] { ("watch", 9000), ("index", 3000), ("core", 27000), ("cached", 9000), ("core.partial", 500) })
                File.WriteAllBytes(Path.Combine(index, big + "." + kind), new byte[bytes]);
            File.WriteAllBytes(Path.Combine(index, big + ".head"), new byte[700]);
            File.WriteAllBytes(Path.Combine(index, small + ".head"), new byte[300]);
            string[] Held() => [.. Directory.EnumerateFiles(index).Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal)];
            var before = Held();

            // A host that deletes nothing - the desktop service says what is dead weight and leaves it - leaves this too.
            CatalogueBootstrap.Run(cat, Assessor(cat), phone, seedFrom: _registry, modelsDirectory: models, indexDirectory: index);
            Assert.Equal(before, Held());

            CatalogueBootstrap.Run(cat, Assessor(cat), phone, seedFrom: _registry, modelsDirectory: models,
                reclaimInferior: true, indexDirectory: index);

            Assert.Equal(new[] { big + ".head", big + ".indexing", small + ".head" }.OrderBy(name => name, StringComparer.Ordinal).ToArray(), Held());
            Assert.Equal(new IndexingStatus(71, 71, 250), BrainInWaiting.IndexOf(_registry.GetLatestModel(TwentySevenB)!, index));
            Assert.True(Directory.Exists(Path.Combine(models, TwoB)) && Directory.Exists(Path.Combine(models, TwentySevenB)),
                "a brain went with what its index left");

            // And at the start after, there is nothing more to remove.
            CatalogueBootstrap.Run(cat, Assessor(cat), phone, seedFrom: _registry, modelsDirectory: models,
                reclaimInferior: true, indexDirectory: index);
            Assert.Equal(3, Held().Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // A brain that goes takes with it what the engine made of it. Housekeeping removed the folder in the model store; the
    // head's store - and for a brain that answered through its index, gigabytes of pieces - sat on in the engine's own
    // folder under the file's name. With the upgrade climbing a step at a time, every brain a phone grows out of would
    // have left that behind. The brain that stays keeps its own, and so does a file that only begins alike.
    [Fact]
    public void When_a_brain_goes_what_the_engine_kept_for_it_goes_with_it()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = NewCatalog();
        var root = Path.Combine(Path.GetTempPath(), "circleai-onebest-" + Guid.NewGuid().ToString("N")[..8]);
        var models = Path.Combine(root, "models");
        var store = Path.Combine(root, "store");
        var small = BrainInWaiting.FileOf(_registry.GetLatestModel(TwoB)!)!;
        var next = BrainInWaiting.FileOf(_registry.GetLatestModel(FourB)!)!;
        try
        {
            Directory.CreateDirectory(store);
            foreach (var (id, file) in new[] { (TwoB, small), (FourB, next) })
            {
                Directory.CreateDirectory(Path.Combine(models, id));
                File.WriteAllText(Path.Combine(models, id, "installed.json"), "{}");
                File.WriteAllBytes(Path.Combine(models, id, file), new byte[4096]);
            }
            foreach (var kind in new[] { "head", "ffn-fired", "indexing", "core", "cached", "use", "core.partial" })
                File.WriteAllBytes(Path.Combine(store, small + "." + kind), new byte[1000]);
            File.WriteAllBytes(Path.Combine(store, next + ".head"), new byte[2000]);
            File.WriteAllBytes(Path.Combine(store, small + "-and-more.head"), new byte[3000]);   // begins alike, and is not the 2B's
            File.WriteAllText(Path.Combine(store, "flash-profile.txt"), "the device's, not a model's");
            void Start() => CatalogueBootstrap.Run(cat, Assessor(cat), phone, seedFrom: _registry, modelsDirectory: models,
                reclaimInferior: true, indexDirectory: store);

            Start();
            foreach (var id in new[] { TwoB, FourB }) cat.RecordLoad(id);
            Start();   // the 4B has loaded here: the 2B is the old LLM now, and goes

            Assert.False(Directory.Exists(Path.Combine(models, TwoB)), "setup: the 2B is still in the model store beside the 4B that has loaded");
            Assert.True(Directory.Exists(Path.Combine(models, FourB)));
            Assert.Equal(
                new[] { "flash-profile.txt", next + ".head", small + "-and-more.head" }.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                Directory.EnumerateFiles(store).Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- a brain whose index failed its check here: tried again listing more neurons, and gone only when nothing is left ----

    // The index each check failed with on that P30, at fifteen in a hundred of each layer's neurons: the 9B one word in
    // eight, the 27B one in four. And the same, had they listed every neuron by this engine: nothing left to try.
    private static readonly IndexingStatus NineFailed = new(39, 39, 125);
    private static readonly IndexingStatus TwentySevenFailed = new(71, 71, 250);
    private static readonly IndexingStatus NineSpent = NineFailed with { EveryNeuron = true };
    private static readonly IndexingStatus TwentySevenSpent = TwentySevenFailed with { EveryNeuron = true };

    private static IndexingStatus? FailedIndexOf(ModelEntry e) =>
        e.Name == NineB ? NineFailed : e.Name == TwentySevenB ? TwentySevenFailed : null;

    private static IndexingStatus? SpentIndexOf(ModelEntry e) =>
        e.Name == NineB ? NineSpent : e.Name == TwentySevenB ? TwentySevenSpent : null;

    // THE OWNER, 2026-11-15: "no models should be blocked - if it fails then check why it failed. clean up and try again,
    // everything depends on the reason why". On that P30 the 27B and the 9B, whose indexes failed at fifteen in a hundred
    // of their neurons, stay: their next indexes list more, and need their files. Had they failed with every neuron listed
    // - the engine's fault, nothing the phone can mend - both would go for the 4B that answers (the owner, 2026-11-14:
    // "Yes, remove them").
    [Fact]
    public void A_brain_whose_index_failed_stays_for_one_listing_more_and_goes_only_when_nothing_is_left_to_try()
    {
        var phone = P30(readsFromFlash: true, storageFree: 5_480_000_000);
        using var cat = Catalogue([FourB, NineB, TwentySevenB], installed: [FourB, NineB, TwentySevenB]);
        Assessor(cat).Assess(phone);
        foreach (var id in new[] { FourB, NineB, TwentySevenB }) cat.RecordLoad(id);   // the two loaded there to build their indexes
        IReadOnlyList<HousekeepingReclaim> Plan(Func<ModelEntry, IndexingStatus?> index) =>
            CatalogueHousekeeping.Plan(cat, readFromFlash: ReadFromFlashOn(phone), ready: e => BrainInWaiting.Ready(e, phone, index(e)),
                failedHere: e => BrainInWaiting.FailedHere(e, phone, index(e)));

        Assert.Empty(Plan(FailedIndexOf));

        var plan = Plan(SpentIndexOf);
        Assert.Equal(new[] { TwentySevenB, NineB }, plan.Select(r => r.Id).ToArray());
        Assert.All(plan, r => Assert.Equal(
            "its index failed its check here with every neuron listed - nothing is left to try until the engine changes - and Ternary-Bonsai-2-4B has loaded here and answers",
            r.Reason));
    }

    // No phone is left without a brain: a brain with nothing left to try stays while nothing else here has loaded and
    // answers - it is the only brain, the other has not loaded here, or nothing is left to try of the other either - and
    // while the service is running it, until it is not.
    [Fact]
    public void A_brain_with_nothing_left_to_try_stays_while_nothing_else_here_answers_or_while_it_is_running()
    {
        var phone = P30(readsFromFlash: true, storageFree: 5_480_000_000);
        IReadOnlyList<HousekeepingReclaim> Plan(IModelCatalog cat, Func<string, bool>? keep = null) =>
            CatalogueHousekeeping.Plan(cat, keep, ReadFromFlashOn(phone),
                ready: e => BrainInWaiting.Ready(e, phone, SpentIndexOf(e)),
                failedHere: e => BrainInWaiting.FailedHere(e, phone, SpentIndexOf(e)));

        using (var alone = Catalogue([TwentySevenB], installed: TwentySevenB))
        {
            Assessor(alone).Assess(phone);
            alone.RecordLoad(TwentySevenB);
            Assert.Empty(Plan(alone));   // the phone's only brain: it answers as well as it can
        }

        using var cat = Catalogue([FourB, NineB, TwentySevenB], installed: [FourB, NineB, TwentySevenB]);
        Assessor(cat).Assess(phone);
        cat.RecordLoad(NineB);
        cat.RecordLoad(TwentySevenB);
        Assert.Empty(Plan(cat));   // the 4B has landed and not loaded here, and the other two failed: nothing answers

        cat.RecordLoad(FourB);
        Assert.Equal(new[] { NineB }, Plan(cat, keep: id => id == TwentySevenB).Select(r => r.Id).ToArray());
        Assert.Equal(new[] { TwentySevenB, NineB }, Plan(cat).Select(r => r.Id).ToArray());
    }

    // The same through a start of the service, on that P30, as its records say. At fifteen in a hundred: nothing goes, and
    // the climb waits on the best of them while its next index - listing more neurons - is built. With every neuron listed,
    // by this engine: the 27B and the 9B go from the model store with what the engine kept for them, the 4B that answers
    // stays, and the record of each check stays in the engine's folder - so the climb, reading those records, knows
    // nothing is left to try of either, and fetches neither again until the engine changes.
    [Fact]
    public void At_a_start_the_brains_whose_index_failed_stay_for_one_listing_more_and_their_records_say_so()
    {
        var phone = P30(readsFromFlash: true, storageFree: 5_480_000_000);
        using var cat = NewCatalog();
        var root = Path.Combine(Path.GetTempPath(), "circleai-onebest-" + Guid.NewGuid().ToString("N")[..8]);
        var models = Path.Combine(root, "models");
        var store = Path.Combine(root, "store");
        ModelEntry Row(string name) => _registry.GetLatestModel(name)!;
        try
        {
            Directory.CreateDirectory(store);
            foreach (var id in new[] { FourB, NineB, TwentySevenB })
            {
                Directory.CreateDirectory(Path.Combine(models, id));
                File.WriteAllText(Path.Combine(models, id, "installed.json"), "{}");
                File.WriteAllBytes(Path.Combine(models, id, BrainInWaiting.FileOf(Row(id))!), new byte[4096]);
            }
            WriteRecord(store, BrainInWaiting.FileOf(Row(NineB))!, NineFailed.Done, NineFailed.Total, NineFailed.Agreement);
            WriteRecord(store, BrainInWaiting.FileOf(Row(TwentySevenB))!, TwentySevenFailed.Done, TwentySevenFailed.Total, TwentySevenFailed.Agreement);
            void Start() => CatalogueBootstrap.Run(cat, Assessor(cat), phone, seedFrom: _registry, modelsDirectory: models,
                reclaimInferior: true, indexDirectory: store);

            Start();
            foreach (var id in new[] { FourB, NineB, TwentySevenB }) cat.RecordLoad(id);
            Start();

            Assert.Equal(new[] { FourB, NineB, TwentySevenB }.OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                Directory.EnumerateDirectories(models).Select(folder => Path.GetFileName(folder)!).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(NineFailed, BrainInWaiting.IndexOf(Row(NineB), store));

            var roomy = P30(readsFromFlash: true, storageFree: 14_040_000_000);
            bool Here(string name) => Directory.Exists(Path.Combine(models, name));
            var step = ModelChoice.NextStep(
                _registry.AllModels.Where(e => e.Modality == ModelModality.Chat && !e.Retired && !BrainCompanions.IsCompanion(e)),
                Here, roomy,
                e => BrainInWaiting.StepOf(e, roomy, Here(e.Name), proven: cat.Assessment(e.Name)?.Proven == true,
                    BrainInWaiting.IndexOf(e, store), measuredSpeed: null),
                Shipped);
            Assert.Null(step.Fetch);
            Assert.False(step.Limit);
            Assert.Equal("Ternary-Bonsai-2-27B is here and is being made ready, and Ternary-Bonsai-2-4B answers: nothing is fetched until it has passed or failed",
                step.Said);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void At_a_start_the_brains_with_nothing_left_to_try_go_and_the_climb_still_reads_those_steps_as_failed()
    {
        var phone = P30(readsFromFlash: true, storageFree: 5_480_000_000);
        using var cat = NewCatalog();
        var root = Path.Combine(Path.GetTempPath(), "circleai-onebest-" + Guid.NewGuid().ToString("N")[..8]);
        var models = Path.Combine(root, "models");
        var store = Path.Combine(root, "store");
        ModelEntry Row(string name) => _registry.GetLatestModel(name)!;
        var four = BrainInWaiting.FileOf(Row(FourB))!;
        var nine = BrainInWaiting.FileOf(Row(NineB))!;
        var big = BrainInWaiting.FileOf(Row(TwentySevenB))!;
        try
        {
            Directory.CreateDirectory(store);
            foreach (var (id, file) in new[] { (FourB, four), (NineB, nine), (TwentySevenB, big) })
            {
                Directory.CreateDirectory(Path.Combine(models, id));
                File.WriteAllText(Path.Combine(models, id, "installed.json"), "{}");
                File.WriteAllBytes(Path.Combine(models, id, file), new byte[4096]);
                File.WriteAllBytes(Path.Combine(store, file + ".head"), new byte[1000]);
            }
            foreach (var (file, failed) in new[] { (nine, NineFailed), (big, TwentySevenFailed) })
                WriteRecord(store, file, failed.Done, failed.Total, failed.Agreement, share: 100, engine: IndexingQueue.EngineVersion);
            void Start() => CatalogueBootstrap.Run(cat, Assessor(cat), phone, seedFrom: _registry, modelsDirectory: models,
                reclaimInferior: true, indexDirectory: store);
            string[] Held() => [.. Directory.EnumerateFiles(store).Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal)];

            Start();
            foreach (var id in new[] { FourB, NineB, TwentySevenB }) cat.RecordLoad(id);
            Start();

            Assert.Equal(new[] { FourB }, Directory.EnumerateDirectories(models).Select(folder => Path.GetFileName(folder)!).ToArray());
            Assert.True(cat.IsInstalled(FourB));
            Assert.False(cat.IsInstalled(NineB) || cat.IsInstalled(TwentySevenB));
            Assert.Equal(new[] { four + ".head", IndexingQueue.RecordName(nine), IndexingQueue.RecordName(big) }
                .OrderBy(name => name, StringComparer.Ordinal).ToArray(), Held());
            Assert.Equal(NineSpent, BrainInWaiting.IndexOf(Row(NineB), store));
            Assert.Equal(TwentySevenSpent, BrainInWaiting.IndexOf(Row(TwentySevenB), store));

            // The climb, as the fetcher asks it, with the 8.6 GB given back: nothing is left to try of either, nothing is
            // fetched, and the 4B is the phone's limit until the engine changes.
            var roomy = P30(readsFromFlash: true, storageFree: 14_040_000_000);
            bool Here(string name) => Directory.Exists(Path.Combine(models, name));
            var step = ModelChoice.NextStep(
                _registry.AllModels.Where(e => e.Modality == ModelModality.Chat && !e.Retired && !BrainCompanions.IsCompanion(e)),
                Here, roomy,
                e => BrainInWaiting.StepOf(e, roomy, Here(e.Name), proven: cat.Assessment(e.Name)?.Proven == true,
                    BrainInWaiting.IndexOf(e, store), measuredSpeed: null),
                Shipped);
            Assert.Null(step.Fetch);
            Assert.True(step.Limit);
            Assert.Equal(NineIsTheLimitUntilTheEngineChanges, step.Said);

            // And at the start after, nothing more goes.
            Start();
            Assert.Equal(3, Held().Length);
            Assert.True(Directory.Exists(Path.Combine(models, FourB)) && cat.IsInstalled(FourB));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- "Yes, do it": each failed check's record kept where the phone does not clear it ----

    // A record as the engine writes it (IndexingQueue's own format), for the model whose file is named: with the share its
    // lists were asked for and the engine that made it where they are given, as this engine writes them; without, as an
    // earlier one did.
    private static void WriteRecord(string directory, string file, int done, int total, int? agrees, int? share = null, string? engine = null)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, IndexingQueue.RecordName(file)),
            $"CAIINDEXING 1\nmodel={file}\nlength=4096\nrequest=abc\ndone={done}\ntotal={total}\n"
            + (share is { } asked ? $"share={asked}\n" : string.Empty)
            + (engine is { } made ? $"engine={made}\n" : string.Empty)
            + (agrees is { } measured ? $"agrees={measured}\n" : string.Empty));
    }

    // What the climb says on that P30 once nothing is left to try of the 9B.
    private const string NineIsTheLimitUntilTheEngineChanges =
        "Ternary-Bonsai-2-9B is the next step up from Ternary-Bonsai-2-4B, and its index failed its check here with every neuron listed - the engine's fault, not this device's: Ternary-Bonsai-2-4B answers, and is this device's limit until the engine changes";

    private static string[] Names(string directory) => Directory.Exists(directory)
        ? [.. Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)!).OrderBy(name => name, StringComparer.Ordinal)]
        : [];

    // THE OWNER, 2026-11-14, of keeping each failed check's record somewhere Android cannot clear it: "Yes, do it". What
    // is kept is the record of a check that failed, byte for byte, and nothing else of the engine's folder; keeping it
    // again changes nothing; and once the engine's record says a later index of that brain has begun, the kept copy goes,
    // because that index's record is what says how it went.
    [Fact]
    public void The_record_of_a_failed_check_is_kept_outside_the_engines_folder_until_a_later_index_begins()
    {
        var root = Path.Combine(Path.GetTempPath(), "circleai-kept-" + Guid.NewGuid().ToString("N")[..8]);
        var store = Path.Combine(root, "cache-store");
        var kept = Path.Combine(root, "files-index-checks");
        var nine = BrainInWaiting.FileOf(_registry.GetLatestModel(NineB)!)!;
        var four = BrainInWaiting.FileOf(_registry.GetLatestModel(FourB)!)!;
        var two = BrainInWaiting.FileOf(_registry.GetLatestModel(TwoB)!)!;
        try
        {
            WriteRecord(store, nine, 39, 39, 125);    // failed: one word in eight
            WriteRecord(store, four, 32, 32, 969);    // passed
            WriteRecord(store, two, 12, 40, null);    // being built
            File.WriteAllText(Path.Combine(store, "flash-profile.txt"), "the device's, not a check's");

            var first = IndexingQueue.KeepFailed(store, kept);
            Assert.Equal(new[] { nine }, first.Kept.ToArray());
            Assert.Empty(first.LetGo);
            Assert.Equal(new[] { IndexingQueue.RecordName(nine) }, Names(kept));
            Assert.Equal(File.ReadAllText(Path.Combine(store, IndexingQueue.RecordName(nine))),
                File.ReadAllText(Path.Combine(kept, IndexingQueue.RecordName(nine))));

            var again = IndexingQueue.KeepFailed(store, kept);
            Assert.Empty(again.Kept);
            Assert.Empty(again.LetGo);

            // THE NEXT INDEX OF THE 9B, LISTING MORE OF ITS NEURONS, HAS BEGUN (IndexingRungs): its record is kept in the
            // failed one's place, so a phone that clears its cache folder does not build it again at the share that failed.
            WriteRecord(store, nine, 3, 39, null, share: 30, engine: IndexingQueue.EngineVersion);
            var next = IndexingQueue.KeepFailed(store, kept);
            Assert.Equal(new[] { nine }, next.Kept.ToArray());
            Assert.Empty(next.LetGo);
            Assert.Equal(File.ReadAllText(Path.Combine(store, IndexingQueue.RecordName(nine))),
                File.ReadAllText(Path.Combine(kept, IndexingQueue.RecordName(nine))));

            // It passed: the kept copy goes, as it does when an index at the first share has begun again.
            WriteRecord(store, nine, 39, 39, 931, share: 30, engine: IndexingQueue.EngineVersion);
            var served = IndexingQueue.KeepFailed(store, kept);
            Assert.Empty(served.Kept);
            Assert.Equal(new[] { nine }, served.LetGo.ToArray());
            Assert.Empty(Names(kept));

            WriteRecord(store, nine, 39, 39, 125);
            IndexingQueue.KeepFailed(store, kept);
            WriteRecord(store, nine, 3, 39, null);
            var later = IndexingQueue.KeepFailed(store, kept);
            Assert.Empty(later.Kept);
            Assert.Equal(new[] { nine }, later.LetGo.ToArray());
            Assert.Empty(Names(kept));

            // And a folder that is not there keeps nothing.
            var none = IndexingQueue.KeepFailed(Path.Combine(root, "nowhere"), kept);
            Assert.Empty(none.Kept);
            Assert.Empty(none.LetGo);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // Where the engine's folder holds no record of a brain, the kept copy is what is read; where it holds one, that one
    // is - a later index's - and with no kept folder named, nothing is read from one.
    [Fact]
    public void A_brains_index_is_read_from_the_kept_copy_only_where_the_engines_folder_holds_none()
    {
        var root = Path.Combine(Path.GetTempPath(), "circleai-kept-" + Guid.NewGuid().ToString("N")[..8]);
        var store = Path.Combine(root, "cache-store");
        var kept = Path.Combine(root, "files-index-checks");
        var row = _registry.GetLatestModel(NineB)!;
        var nine = BrainInWaiting.FileOf(row)!;
        try
        {
            Directory.CreateDirectory(store);
            WriteRecord(kept, nine, 39, 39, 125);

            Assert.Equal(NineFailed, BrainInWaiting.IndexOf(row, store, kept));
            Assert.Null(BrainInWaiting.IndexOf(row, store));   // no kept folder named, and none set for the process

            WriteRecord(store, nine, 3, 39, null);
            Assert.Equal(new IndexingStatus(3, 39), BrainInWaiting.IndexOf(row, store, kept));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // THE CASE IT IS FOR, on that P30 had both failed with every neuron listed: a start removes the 27B and the 9B and keeps
    // the records of their checks; then the phone, short of room, clears the app's cache folder, and the engine's folder
    // with it. The climb, reading the kept copies, still knows nothing is left to try of either: nothing is fetched, and
    // the 4B is the phone's limit until the engine changes. At the next start nothing is let go or kept again, and
    // nothing comes back.
    [Fact]
    public void When_the_phone_clears_the_engines_folder_the_kept_records_still_end_the_climb_at_the_4B()
    {
        var phone = P30(readsFromFlash: true, storageFree: 5_480_000_000);
        using var cat = NewCatalog();
        var root = Path.Combine(Path.GetTempPath(), "circleai-onebest-" + Guid.NewGuid().ToString("N")[..8]);
        var models = Path.Combine(root, "models");
        var store = Path.Combine(root, "cache-store");
        var kept = Path.Combine(root, "files-index-checks");
        ModelEntry Row(string name) => _registry.GetLatestModel(name)!;
        var nine = BrainInWaiting.FileOf(Row(NineB))!;
        var big = BrainInWaiting.FileOf(Row(TwentySevenB))!;
        try
        {
            foreach (var id in new[] { FourB, NineB, TwentySevenB })
            {
                Directory.CreateDirectory(Path.Combine(models, id));
                File.WriteAllText(Path.Combine(models, id, "installed.json"), "{}");
                File.WriteAllBytes(Path.Combine(models, id, BrainInWaiting.FileOf(Row(id))!), new byte[4096]);
            }
            WriteRecord(store, nine, NineFailed.Done, NineFailed.Total, NineFailed.Agreement, share: 100, engine: IndexingQueue.EngineVersion);
            WriteRecord(store, big, TwentySevenFailed.Done, TwentySevenFailed.Total, TwentySevenFailed.Agreement, share: 100, engine: IndexingQueue.EngineVersion);
            void Start() => CatalogueBootstrap.Run(cat, Assessor(cat), phone, seedFrom: _registry, modelsDirectory: models,
                reclaimInferior: true, indexDirectory: store, keptDirectory: kept);

            Start();
            foreach (var id in new[] { FourB, NineB, TwentySevenB }) cat.RecordLoad(id);
            Start();
            Assert.Equal(new[] { FourB }, Directory.EnumerateDirectories(models).Select(folder => Path.GetFileName(folder)!).ToArray());
            Assert.Equal(new[] { IndexingQueue.RecordName(nine), IndexingQueue.RecordName(big) }.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                Names(kept));

            // The phone clears the app's cache folder.
            Directory.Delete(store, recursive: true);

            var roomy = P30(readsFromFlash: true, storageFree: 14_040_000_000);
            bool Here(string name) => Directory.Exists(Path.Combine(models, name));
            UpgradeStep Climb(string? keptFolder) => ModelChoice.NextStep(
                _registry.AllModels.Where(e => e.Modality == ModelModality.Chat && !e.Retired && !BrainCompanions.IsCompanion(e)),
                Here, roomy,
                e => BrainInWaiting.StepOf(e, roomy, Here(e.Name), proven: cat.Assessment(e.Name)?.Proven == true,
                    BrainInWaiting.IndexOf(e, store, keptFolder), measuredSpeed: null),
                Shipped);

            var step = Climb(kept);
            Assert.Null(step.Fetch);
            Assert.True(step.Limit);
            Assert.Equal(NineIsTheLimitUntilTheEngineChanges, step.Said);

            // Without the kept copies, this is the 9B's 2 GB fetched again into the same check.
            Assert.Equal(NineB, Climb(Path.Combine(root, "nothing-kept")).Fetch?.Name);

            Start();
            Assert.Equal(2, Names(kept).Length);
            Assert.Equal(new[] { FourB }, Directory.EnumerateDirectories(models).Select(folder => Path.GetFileName(folder)!).ToArray());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ---- while the best LLM's index is built ----

    // "a background process for hours of course just like updating an OS is hours of time": the phone goes on answering.
    // On that P30 the 27B with no index wrote a token every 26 to 74 seconds; the 2B, seven or eight a second.
    [Fact]
    public void Until_the_best_LLMs_index_is_complete_the_brain_the_phone_keeps_in_RAM_answers()
    {
        var phone = P30(readsFromFlash: true);
        var best = _registry.GetLatestModel(TwentySevenB)!;
        var others = new[] { _registry.GetLatestModel(NineB)!, _registry.GetLatestModel(TwoB)! };

        // Not begun, just begun, nearly done: the 27B cannot answer yet. Of the others the phone keeps the 2B in RAM -
        // the 9B's 2.5 GB is past the 2.37 GB one model may claim of its memory - so the 2B answers.
        foreach (var index in new IndexingStatus?[] { null, new IndexingStatus(0, 93), new IndexingStatus(92, 93) })
        {
            Assert.False(BrainInWaiting.Ready(best, phone, index));
            Assert.Equal(TwoB, BrainInWaiting.StandIn(best, others, phone, e => e.Name == TwentySevenB ? index : null)?.Name);
        }

        // Complete with a check that failed - "the same top word as every neuron 25.0% of the time": still not ready.
        var failed = new IndexingStatus(93, 93, 250);
        Assert.False(BrainInWaiting.Ready(best, phone, failed));
        Assert.Equal(TwoB, BrainInWaiting.StandIn(best, others, phone, e => e.Name == TwentySevenB ? failed : null)?.Name);

        // Complete and passed: the 27B answers, and nothing stands in for it.
        var complete = new IndexingStatus(93, 93, 969);
        Assert.True(BrainInWaiting.Ready(best, phone, complete));
        Assert.Null(BrainInWaiting.StandIn(best, others, phone, e => e.Name == TwentySevenB ? complete : null));
    }

    // "A replacement proves itself first." A complete index says the work is done, not that the brain it serves loads and
    // answers on this phone: that P30 ended two loads of the 27B at the three minutes a load's step is allowed. The brain
    // has taken over once it has answered here; until then what it would replace stays.
    [Fact]
    public void A_brain_that_waited_on_its_index_has_taken_over_once_it_has_answered_here()
    {
        var phone = P30(readsFromFlash: true);
        var best = _registry.GetLatestModel(TwentySevenB)!;
        var two = _registry.GetLatestModel(TwoB)!;
        var complete = new IndexingStatus(71, 71, 969);

        Assert.False(BrainInWaiting.HasTakenOver(best, phone, new IndexingStatus(70, 71), 0.3));   // the check is still to run
        Assert.False(BrainInWaiting.HasTakenOver(best, phone, new IndexingStatus(71, 71, 250), 0.3));   // and this one failed it
        Assert.False(BrainInWaiting.HasTakenOver(best, phone, complete, null));                    // complete, never answered
        Assert.True(BrainInWaiting.HasTakenOver(best, phone, complete, 0.3));                      // complete, and it answers

        // A brain the phone keeps in RAM waited on nothing, and one kept in RAM by its engine likewise.
        Assert.True(BrainInWaiting.HasTakenOver(two, phone, null, null));
        Assert.True(BrainInWaiting.HasTakenOver(best, P30(readsFromFlash: false), null, null));
    }

    // A brain the phone keeps in RAM needs no index and waits on nothing; and where the engine keeps every brain in RAM,
    // what loads answers.
    [Fact]
    public void A_brain_kept_in_RAM_is_ready_and_nothing_stands_in_for_it()
    {
        var phone = P30(readsFromFlash: true);
        var two = _registry.GetLatestModel(TwoB)!;
        var big = _registry.GetLatestModel(TwentySevenB)!;

        Assert.True(BrainInWaiting.Ready(two, phone, null));
        Assert.Null(BrainInWaiting.StandIn(two, [big], phone, _ => null));
        Assert.True(BrainInWaiting.Ready(big, P30(readsFromFlash: false), null));
    }

    // THE INDEX IS THE TURBO OF A BRAIN THE PHONE KEEPS IN RAM, NOT WHAT MAKES IT ANSWER. Begun, complete and not yet
    // measured, failed or passed: the 2B answers on that phone as it did before anything indexed it, it has taken over,
    // and nothing stands in for it - least of all the old 2B of another engine.
    [Fact]
    public void A_brain_the_phone_keeps_in_RAM_answers_whatever_has_become_of_its_index()
    {
        var phone = P30(readsFromFlash: true);
        var two = _registry.GetLatestModel(TwoB)!;
        var others = new[] { _registry.GetLatestModel(OldMnn)!, _registry.GetLatestModel(TwentySevenB)! };

        foreach (var index in new IndexingStatus?[] { new IndexingStatus(3, 40), new IndexingStatus(40, 40), new IndexingStatus(40, 40, 250), new IndexingStatus(40, 40, 969) })
        {
            Assert.True(BrainInWaiting.Ready(two, phone, index), $"with its index at {index}, the 2B the phone keeps in RAM is not ready to answer");
            Assert.True(BrainInWaiting.HasTakenOver(two, phone, index, null), $"with its index at {index}, the 2B has not taken over the phone it answers on");
            Assert.Null(BrainInWaiting.StandIn(two, others, phone, e => e.Name == TwoB ? index : null));
        }
    }

    // "a mini cooper must do the best it can do with what it has": with nothing here that the phone keeps in RAM, the
    // best LLM answers itself while its index is built.
    [Fact]
    public void With_nothing_here_to_stand_in_the_best_LLM_answers_as_well_as_it_can()
    {
        var phone = P30(readsFromFlash: true);
        var best = _registry.GetLatestModel(TwentySevenB)!;

        Assert.Null(BrainInWaiting.StandIn(best, [], phone, _ => null));
        Assert.Null(BrainInWaiting.StandIn(best, [_registry.GetLatestModel(NineB)!], phone, _ => null));
    }

    // Two brains, two records: the engine names each record for its model's file, and what a person is told is how far
    // the brain in waiting has got - not the record of the brain that happens to be loaded.
    [Fact]
    public void The_index_a_person_is_told_about_is_the_brain_in_waitings_by_its_files_name()
    {
        var best = _registry.GetLatestModel(TwentySevenB)!;
        var two = _registry.GetLatestModel(TwoB)!;
        var now = new DateTimeOffset(2026, 11, 13, 6, 0, 0, TimeSpan.Zero);
        var dir = Path.Combine(Path.GetTempPath(), "circleai-onebest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal("Ternary-Bonsai-2-27B-PTQ1_0.gguf", BrainInWaiting.FileOf(best));
            Assert.Null(BrainInWaiting.IndexOf(best, dir));

            File.WriteAllText(Path.Combine(dir, "Ternary-Bonsai-2-27B-PTQ1_0.gguf.indexing"),
                "CAIINDEXING 1\nmodel=Ternary-Bonsai-2-27B-PTQ1_0.gguf\nlength=5946648928\nrequest=abc\ndone=37\ntotal=93\n");
            // The 2B was loaded after it, and the engine recorded that it is not indexed: the newest record in the folder.
            File.WriteAllText(Path.Combine(dir, "Ternary-Bonsai-2-2B-PTQ1_0.gguf.indexing"),
                "CAIINDEXING 1\nmodel=Ternary-Bonsai-2-2B-PTQ1_0.gguf\nlength=429183456\nrequest=-\ndone=0\ntotal=0\n");
            File.SetLastWriteTimeUtc(Path.Combine(dir, "Ternary-Bonsai-2-27B-PTQ1_0.gguf.indexing"), DateTime.UtcNow.AddMinutes(-5));

            Assert.Equal(new IndexingStatus(37, 93), BrainInWaiting.IndexOf(best, dir));
            Assert.Null(BrainInWaiting.IndexOf(two, dir));
            Assert.Null(IndexReadiness.Read(dir, now));   // the newest record is the 2B's, which says nothing

            var told = IndexReadiness.Read(dir, now, BrainInWaiting.FileOf(best));
            Assert.Equal("Until indexing is complete AI is functioning at 39%.", told?.Detail);

            var tracker = new ReadinessTracker();
            tracker.Report(ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Ready, ReadinessSentences.Ready, now, TwoB));
            IndexReadiness.Report(tracker, dir, now, BrainInWaiting.FileOf(best));
            Assert.Equal("Until indexing is complete AI is functioning at 39%.", IndexReadiness.Caption(tracker.Current));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // "did housekeeping do its job" has to have an answer on the phone: what the store holds, what went, what that freed.
    [Fact]
    public void Housekeeping_says_what_the_store_holds_and_what_it_freed()
    {
        var phone = P30(readsFromFlash: true);
        using var cat = Catalogue([TwoB, TwentySevenB], installed: [TwoB, TwentySevenB]);
        Assessor(cat).Assess(phone);
        cat.RecordLoad(TwentySevenB);

        var dir = Path.Combine(Path.GetTempPath(), "circleai-onebest-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var (id, bytes) in new[] { (TwoB, 3_000_000), (TwentySevenB, 7_000_000) })
            {
                Directory.CreateDirectory(Path.Combine(dir, id));
                File.WriteAllText(Path.Combine(dir, id, "installed.json"), "{}");
                File.WriteAllBytes(Path.Combine(dir, id, id + "-PTQ1_0.gguf"), new byte[bytes]);
            }
            var said = new List<string>();

            var reclaimed = CatalogueHousekeeping.Reclaim(cat, dir, say: said.Add, readFromFlash: ReadFromFlashOn(phone));

            Assert.Equal(1, reclaimed);
            Assert.False(Directory.Exists(Path.Combine(dir, TwoB)));
            Assert.True(File.Exists(Path.Combine(dir, TwentySevenB, TwentySevenB + "-PTQ1_0.gguf")));
            Assert.False(cat.IsInstalled(TwoB));
            Assert.Contains($"holds {TwentySevenB}: 7 MB", said);
            Assert.Contains($"holds {TwoB}: 3 MB", said);
            Assert.Contains(said, s => s.StartsWith($"reclaimed {TwoB}", StringComparison.Ordinal));
            Assert.Contains("freed 3 MB", said);
            Assert.Contains("planned=1 reclaimed=1", said);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
