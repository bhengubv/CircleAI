// StepFourPlanTests.cs
//
// STEP 4, "EVERYTHING ON": the first-run plan takes its brain by the speed-aware rule and, when the
// catalogue has them and they fit, the brain's vision tower and drafter - planned right behind it and
// never on their own - while every background fetcher honours what the person switched off.

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

[Collection(nameof(BrainSpeedCollection))] // BrainSpeed reads a static the speed tests set
public sealed class StepFourPlanTests : IDisposable
{
    private readonly List<string> _stores = [];

    public void Dispose()
    {
        foreach (var dir in _stores)
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }

    private string EmptyStore()
    {
        var dir = Directory.CreateTempSubdirectory("circleai-step4-").FullName;
        _stores.Add(dir);
        return dir;
    }

    private sealed class Rows(IReadOnlyList<ModelEntry> rows) : ModelRegistryService
    {
        public override IReadOnlyList<ModelEntry> AllModels => rows;
    }

    private static readonly IReadOnlyCollection<ModelEngine> Managed = [ModelEngine.Managed, ModelEngine.Mnn];

    private static ModelEntry Brain(string name, string repo, long bytes, int rank) =>
        new(name, "1.0", "PTQ1_0")
        {
            Repo = repo, Engine = ModelEngine.Managed, Architecture = "qwen35", Modality = ModelModality.Chat,
            TotalBytes = bytes, MinRamGb = bytes / 1e9 + 0.1, MinStorageGb = bytes / 1e9 + 0.1, QualityRank = rank,
            BundleFiles = [new BundleFile(name + "-PTQ1_0.gguf", "00", bytes)],
        };

    // A TOWER AND A DRAFTER AN ENGINE OF THIS BUILD CAN READ. Bonsai 2's own are rows of the C# engine,
    // which has no code for them yet; A_companion_the_engine_cannot_read_yet_waits_for_its_code holds
    // them back, and these stand for the day the engine reads them.
    private static ModelEntry Tower(string name, string repo, long bytes, ModelEngine engine = ModelEngine.Mnn) =>
        new(name, "1.0", "Q8_0")
        {
            Repo = repo, Engine = engine, Architecture = "clip", Modality = ModelModality.Vision,
            TotalBytes = bytes, MinRamGb = bytes / 1e9, MinStorageGb = bytes / 1e9, QualityRank = 5,
            BundleFiles = [new BundleFile(name + "-mmproj-Q8_0.gguf", "00", bytes)],
        };

    private static ModelEntry Drafter(string name, long bytes, ModelEngine engine = ModelEngine.Mnn) =>
        new(name, "1.0", "Q8_0")
        {
            Repo = "prism-ml/" + name, Engine = engine, Architecture = "dflash", Modality = ModelModality.Chat,
            TotalBytes = bytes, MinRamGb = bytes / 1e9, MinStorageGb = bytes / 1e9, QualityRank = 1,
            Capabilities = ["Draft"],
            BundleFiles = [new BundleFile(name + ".gguf", "00", bytes)],
        };

    private static ModelEntry StandaloneEyes() =>
        new("SmolVLM-256M-Instruct-MNN", "1.0", "Q4")
        {
            Repo = "MNN/SmolVLM", Modality = ModelModality.Vision, TotalBytes = 300_000_000, MinRamGb = 0.5,
            MinStorageGb = 0.4, QualityRank = 3,
            BundleFiles = [new BundleFile("llm.mnn.weight", "00", 300_000_000)],
        };

    private static DeviceProbe Roomy() =>
        new(RamAvailableBytes: 12_000_000_000, StorageFreeBytes: 200_000_000_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        { RamTotalBytes = 16_000_000_000 };

    private static DeviceProbe Tight() =>
        new(RamAvailableBytes: 2_500_000_000, StorageFreeBytes: 20_000_000_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        { RamTotalBytes = 3_000_000_000 };

    // ── recognising a brain's companions ─────────────────────────────────────

    [Fact]
    public void A_tower_is_a_vision_row_whose_file_is_a_projector_and_a_drafter_says_so()
    {
        Assert.True(BrainCompanions.IsTower(Tower("T", "r", 1)));
        Assert.False(BrainCompanions.IsTower(StandaloneEyes()));      // a whole VLM is not a tower
        Assert.True(BrainCompanions.IsDrafter(Drafter("Ternary-Bonsai-2-4B-DFlash2", 1)));
        Assert.False(BrainCompanions.IsDrafter(Brain("Ternary-Bonsai-2-4B", "r", 1, 1)));
    }

    [Fact]
    public void A_companion_belongs_to_the_brain_that_shares_its_repo_or_begins_its_name()
    {
        var brain2B = Brain("Ternary-Bonsai-2-2B", "prism-ml/Ternary-Bonsai-2-2B-gguf", 429_000_000, 9);
        var brain27B = Brain("Ternary-Bonsai-2-27B", "prism-ml/Ternary-Bonsai-2-27B-gguf", 5_900_000_000, 15);
        var tower2B = Tower("Ternary-Bonsai-2-2B-Vision", "prism-ml/Ternary-Bonsai-2-2B-gguf", 365_000_000);
        var drafter27B = Drafter("Ternary-Bonsai-2-27B-DFlash2", 1_500_000_000);

        Assert.True(BrainCompanions.Belongs(tower2B, brain2B));
        Assert.False(BrainCompanions.Belongs(tower2B, brain27B));
        Assert.True(BrainCompanions.Belongs(drafter27B, brain27B));
        Assert.False(BrainCompanions.Belongs(drafter27B, brain2B));  // "2-27B-" does not begin "2-2B-"

        // A brain's PQ2_0 sibling shares its family, and so its drafter.
        var pq2 = brain27B with { Name = "Ternary-Bonsai-2-27B-PQ2_0", Quantization = "PQ2_0" };
        Assert.Equal("Ternary-Bonsai-2-27B", BrainCompanions.Family(pq2));
        Assert.True(BrainCompanions.Belongs(drafter27B, pq2));
    }

    [Fact]
    public void Only_companions_that_fit_beside_the_brain_are_taken_one_of_each_kind()
    {
        var brain = Brain("Ternary-Bonsai-2-4B", "prism-ml/Ternary-Bonsai-2-4B-gguf", 950_000_000, 11);
        var q8 = Tower("Ternary-Bonsai-2-4B-Vision-Q8_0", "prism-ml/Ternary-Bonsai-2-4B-gguf", 367_000_000);
        var bf16 = Tower("Ternary-Bonsai-2-4B-Vision-BF16", "prism-ml/Ternary-Bonsai-2-4B-gguf", 676_000_000);
        var drafter = Drafter("Ternary-Bonsai-2-4B-DFlash2", 1_575_000_000);
        var catalogue = new[] { brain, q8, bf16, drafter };

        var roomy = BrainCompanions.ThatFit(brain, catalogue, Roomy(), engines: Managed);
        Assert.Equal(new[] { q8.Name, drafter.Name }, roomy.Select(c => c.Entry.Name));   // the smaller tower

        // 3 GB: a 1.5 GB ceiling holds the brain and its tower, not the drafter as well.
        var tight = BrainCompanions.ThatFit(brain, catalogue, Tight(), engines: Managed);
        Assert.Equal(new[] { q8.Name }, tight.Select(c => c.Entry.Name));

        // What the person switched off is not taken.
        Assert.DoesNotContain(BrainCompanions.ThatFit(brain, catalogue, Roomy(), declined: id => id == q8.Name, engines: Managed),
                              c => c.Entry.Name == q8.Name);
    }

    [Fact]
    public void A_companion_the_engine_cannot_read_yet_waits_for_its_code()
    {
        // BONSAI 2'S OWN ROWS: a "clip" tower and a "dflash" drafter on the C# engine, which reads
        // neither yet. Planned now they would be 1.9 GB of the person's data that nothing can load.
        var brain = Brain("Ternary-Bonsai-2-4B", "prism-ml/Ternary-Bonsai-2-4B-gguf", 950_000_000, 11);
        var tower = Tower("Ternary-Bonsai-2-4B-Vision", "prism-ml/Ternary-Bonsai-2-4B-gguf", 367_000_000, ModelEngine.Managed);
        var drafter = Drafter("Ternary-Bonsai-2-4B-DFlash2", 1_575_000_000, ModelEngine.Managed);

        Assert.Empty(BrainCompanions.ThatFit(brain, [brain, tower, drafter], Roomy(), engines: Managed));

        using var registry = new Rows([brain, tower, drafter]);
        using var loader = new BundleModelLoader(EmptyStore(), registry);
        var plan = FirstRun.Plan(registry, loader, Roomy(), speech: false, engines: Managed);
        Assert.Equal(new[] { brain.Name }, plan.Select(s => s.Model.Name));
    }

    // ── the plan takes them ───────────────────────────────────────────────────

    [Fact]
    public void The_plan_puts_the_brains_tower_and_drafter_right_behind_it_and_never_on_their_own()
    {
        var brain = Brain("Ternary-Bonsai-2-4B", "prism-ml/Ternary-Bonsai-2-4B-gguf", 950_000_000, 11);
        var tower = Tower("Ternary-Bonsai-2-4B-Vision", "prism-ml/Ternary-Bonsai-2-4B-gguf", 367_000_000);
        var drafter = Drafter("Ternary-Bonsai-2-4B-DFlash2", 1_575_000_000);
        var otherTower = Tower("Ternary-Bonsai-2-27B-Vision", "prism-ml/Ternary-Bonsai-2-27B-gguf", 629_000_000);
        var eyes = StandaloneEyes();
        using var registry = new Rows([brain, tower, drafter, otherTower, eyes]);
        using var loader = new BundleModelLoader(EmptyStore(), registry);

        var plan = FirstRun.Plan(registry, loader, Roomy(), speech: false, engines: Managed);
        var names = plan.Select(s => s.Model.Name).ToList();

        var at = names.IndexOf(brain.Name);
        Assert.True(at >= 0, "the brain was not planned: " + string.Join(", ", names));
        Assert.Equal(tower.Name, names[at + 1]);
        Assert.Equal(drafter.Name, names[at + 2]);
        Assert.Equal("the brain's eyes", plan[at + 1].Title);
        Assert.Equal("faster answers", plan[at + 2].Title);

        // A tower for another brain is fetched nowhere; a whole vision model is still "the eyes".
        Assert.DoesNotContain(otherTower.Name, names);
        Assert.Equal("the eyes", plan.Single(s => s.Model.Name == eyes.Name).Title);

        // One brain - a drafter is not one - and it is the one a fetcher lifts to the front.
        Assert.Single(plan, s => s.Modality == ModelModality.Chat && !BrainCompanions.IsCompanion(s.Model));
        Assert.Equal(brain.Name, FirstRun.FirstBrain(plan));
    }

    [Fact]
    public void Without_companion_rows_the_plan_is_what_it_was_and_the_person_can_decline_them()
    {
        var brain = Brain("Ternary-Bonsai-2-2B", "prism-ml/Ternary-Bonsai-2-2B-gguf", 429_000_000, 9);
        var tower = Tower("Ternary-Bonsai-2-2B-Vision", "prism-ml/Ternary-Bonsai-2-2B-gguf", 365_000_000);

        using (var bare = new Rows([brain]))
        using (var loader = new BundleModelLoader(EmptyStore(), bare))
            Assert.Equal(new[] { brain.Name }, FirstRun.Plan(bare, loader, Roomy(), speech: false, engines: Managed).Select(s => s.Model.Name));

        using var registry = new Rows([brain, tower]);
        using var loader2 = new BundleModelLoader(EmptyStore(), registry);
        var plan = FirstRun.Plan(registry, loader2, Roomy(), speech: false, engines: Managed, declined: id => id == tower.Name);
        Assert.DoesNotContain(plan, s => s.Model.Name == tower.Name);
    }

    [Fact]
    public void Every_catalogued_model_has_a_title_a_person_can_read()
    {
        Assert.Equal("the brain", FirstRun.TitleOf(Brain("b", "r", 1, 1)));
        Assert.Equal("the brain's eyes", FirstRun.TitleOf(Tower("t", "r", 1)));
        Assert.Equal("faster answers", FirstRun.TitleOf(Drafter("d", 1)));
        Assert.Equal("better eyes", FirstRun.TitleOf(StandaloneEyes()));
        Assert.Null(FirstRun.FirstBrain([]));
    }

    [Fact]
    public void The_census_survives_two_downloads_owed_under_the_same_words()
    {
        // The long tail names several models alike ("the French voice" twice); the census built a
        // dictionary straight off the plan and threw on the second one.
        static ModelEntry Voice(string name) => new(name, "1.0", "int8")
        {
            Modality = ModelModality.Tts, Language = "fr", TotalBytes = 60_000_000, MinRamGb = 0.2, MinStorageGb = 0.1,
            Repo = "voices/" + name, BundleFiles = [new BundleFile(name + ".onnx", "00", 60_000_000)],
        };
        using var registry = new Rows([Voice("fr-voice-a"), Voice("fr-voice-b")]);
        using var loader = new BundleModelLoader(EmptyStore(), registry);

        var plan = FirstRun.Plan(registry, loader, Roomy(), speech: true, engines: Managed);
        Assert.Equal(2, plan.Count(s => s.Model.Language == "fr"));

        var census = FirstRun.Census(registry, loader, Roomy(), speech: true);
        Assert.Equal(census.Rows.Count, census.Total);
    }

    // ── the background fetchers honour the declined list ────────────────────

    [Fact]
    public void A_download_stopped_halfway_is_not_resumed_once_the_person_switched_it_off()
    {
        using var registry = new ModelRegistryService();
        var bonsai = registry.AllModels.First(m => m.Name == "Ternary-Bonsai-2-2B");
        var store = EmptyStore();
        var file = bonsai.BundleFiles![0];
        Directory.CreateDirectory(Path.Combine(store, bonsai.Name));
        SparseStore.Write(Path.Combine(store, bonsai.Name, file.Name), file.SizeBytes / 3);   // a third down

        using var loader = new BundleModelLoader(store, registry);
        Assert.Contains(ModelSetup.Unfinished(loader, engines: [ModelEngine.Managed]), u => u.Name == bonsai.Name);
        Assert.DoesNotContain(ModelSetup.Unfinished(loader, engines: [ModelEngine.Managed], declined: id => id == bonsai.Name),
                              u => u.Name == bonsai.Name);

        // A declined list that throws refuses nothing: a resume is never cancelled on a guess.
        Assert.Contains(ModelSetup.Unfinished(loader, engines: [ModelEngine.Managed], declined: _ => throw new InvalidOperationException()),
                        u => u.Name == bonsai.Name);
    }
}
