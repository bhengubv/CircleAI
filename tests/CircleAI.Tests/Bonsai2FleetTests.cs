// Bonsai2FleetTests.cs
//
// S5: THE REST OF THE BONSAI 2 FLEET IN THE CATALOGUE, and only what the C# engine can open reachable.
//
// Every file of the fleet the plan names is a row with the plan's size and SHA-256 (read from the
// Hugging Face API, the plan's appendix): the four packings' brains (PTQ1_0 and PQ2_0), the PQ2_0
// files with their MTP head built in, the DFlash 2 drafters and the Q8_0 vision towers. The brains
// carry the model cards' capabilities and both of their samplers.
//
// A ROW THE ENGINE CANNOT OPEN YET IS DEAD DATA UNTIL ITS CODE LANDS: no selector picks it, no setup
// plan names it, no fetch starts or resumes it, no screen offers it, and no request is routed to a
// brain for a capability the engine cannot hand back (tool calls in the XML form the app does not
// read yet; sight before the tower is built). When the code lands, the engine's own gate
// (ManagedChatGenerator.Reads, .Delivers) opens them with no catalogue edit - as it did for the
// PQ2_0-MTP files when the engine learned to draft with their head: they are brains now, and the
// DFlash 2 drafters and the vision towers are what still waits.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircleAI.Assistant;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Sampling;
using Xunit;

namespace CircleAI.Tests;

[Collection(nameof(BrainSpeedCollection))]
public sealed class Bonsai2FleetTests : IDisposable
{
    private readonly ModelRegistryService _registry = new();
    private readonly List<string> _stores = [];

    public void Dispose()
    {
        _registry.Dispose();
        foreach (var dir in _stores)
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static readonly ModelEngine[] Shipped = [ModelEngine.Mnn, ModelEngine.Managed, ModelEngine.Onnx, ModelEngine.Ggml];

    // A desktop with room for every model in the fleet, so nothing below is excluded by memory.
    private static DeviceProbe Roomy() =>
        new(RamAvailableBytes: 60_000_000_000, StorageFreeBytes: 900_000_000_000, Gpu: GpuKind.None,
            CpuCores: 16, Thermal: ThermalClass.Active, Connectivity: Connectivity.Online)
        { RamTotalBytes = 64_000_000_000, StorageTotalBytes = 2_000_000_000_000 };

    private string Store()
    {
        var dir = Path.Combine(Path.GetTempPath(), "circleai-fleet-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _stores.Add(dir);
        return dir;
    }

    /// <summary>The plan's fleet table and appendix: row, file, bytes, SHA-256.</summary>
    public static TheoryData<string, string, long, string> Fleet => new()
    {
        { "Ternary-Bonsai-2-2B", "Ternary-Bonsai-2-2B-PTQ1_0.gguf", 429_183_456, "36061ad406df8a4903a6db005be350c50fabfaf717f9ef0acf4b76adcd9929fa" },
        { "Ternary-Bonsai-2-2B-PQ2_0", "Ternary-Bonsai-2-2B-PQ2_0.gguf", 517_312_992, "d5f01a4ecaf49239653308ee732ad1a0c5f802c0ddb997549fc7b694c8b45928" },
        { "Ternary-Bonsai-2-2B-Vision", "Ternary-Bonsai-2-2B-mmproj-Q8_0.gguf", 364_664_352, "ac9934e8d7966d0d62442c202ec8fdad99a045dea79366c1a4a246a46f5c0533" },
        { "Ternary-Bonsai-2-4B", "Ternary-Bonsai-2-4B-PTQ1_0.gguf", 949_549_920, "56711263ec9a9ffeaa74a3a0e58dece8127cf9268404746c721113ca76994d60" },
        { "Ternary-Bonsai-2-4B-PQ2_0", "Ternary-Bonsai-2-4B-PQ2_0.gguf", 1_146_465_120, "d406b1e912c0d2a4c124ad10bf38a76ae662fdc4d24b176451403dc7b7f30409" },
        { "Ternary-Bonsai-2-4B-PQ2_0-MTP", "Ternary-Bonsai-2-4B-PQ2_0-MTP-Q8_0.gguf", 1_274_642_176, "0acf517de1dc3d85c403a60df6b8b07a4878ac174f368d4390be98406a4d6c0d" },
        { "Ternary-Bonsai-2-4B-DFlash2", "Ternary-Bonsai-2-4B-DFlash2-BF16.gguf", 1_575_278_208, "4402be311018d01e31ecd0a894326e2f7bd28bc27a288c5b949799fe000808d8" },
        { "Ternary-Bonsai-2-4B-Vision", "Ternary-Bonsai-2-4B-mmproj-Q8_0.gguf", 366_894_624, "db939d5f9b7014137810205e91d05e5f54afd72b0b3af9d33bd7d82f7906a55d" },
        { "Ternary-Bonsai-2-9B", "Ternary-Bonsai-2-9B-PTQ1_0.gguf", 1_997_486_976, "3561df7ac7d26a71ffa991ac2db740b10841c3a350aa2676ccd3ff5d428b4e8b" },
        { "Ternary-Bonsai-2-9B-PQ2_0", "Ternary-Bonsai-2-9B-PQ2_0.gguf", 2_416_851_840, "7a89ab48f5038c704480f60781f9b8d4035351f5f6beb7d11fad80001fde23f9" },
        { "Ternary-Bonsai-2-9B-PQ2_0-MTP", "Ternary-Bonsai-2-9B-PQ2_0-MTP-Q8_0.gguf", 2_675_410_720, "c79fcdfaf6b7bf47089b50dadcfd11f9ce7d3e96adf3bbefde7912e5ef69e22b" },
        { "Ternary-Bonsai-2-9B-DFlash2", "Ternary-Bonsai-2-9B-DFlash2-Q8_0.gguf", 1_574_233_856, "95149abdd584f7a69ea5ead38790d5481454428d2c1616527ff0d45422a762b4" },
        { "Ternary-Bonsai-2-9B-Vision", "Ternary-Bonsai-2-9B-mmproj-Q8_0.gguf", 624_229_664, "e0cb6f6988cea323ef798cd77d019bbb819af75582f034eaab0e7232314953db" },
        { "Ternary-Bonsai-2-27B", "Ternary-Bonsai-2-27B-PTQ1_0.gguf", 5_946_648_928, "53107f530aa52eb00912263ab1ee29bd199261c87cd7b4ad4ca1318c1fe33ee3" },
        { "Ternary-Bonsai-2-27B-PQ2_0", "Ternary-Bonsai-2-27B-PQ2_0.gguf", 7_206_168_928, "3907dc1658db1f78a9826bf8d5bcb8dc65db0d466388937af57f2294fae62ec1" },
        { "Ternary-Bonsai-2-27B-Vision", "Ternary-Bonsai-2-27B-mmproj-Q8_0.gguf", 629_246_976, "6807ede61d570bb86ba34b756a0fa109edc33668604de867c6ea6d8f1d631903" },
    };

    private IReadOnlyList<ModelEntry> Rows() =>
        _registry.AllModels.Where(m => m.Name.StartsWith("Ternary-Bonsai-2-", StringComparison.Ordinal)).ToList();

    // A brain the C# engine runs: both packings, and the PQ2_0 files with their MTP head (S5's step 4).
    private static bool IsBrain(ModelEntry m) => m.Quantization is "PTQ1_0" or "PQ2_0" or "PQ2_0-MTP";

    private static bool IsCompanion(ModelEntry m) => !IsBrain(m);

    [Theory]
    [MemberData(nameof(Fleet))]
    public void Every_file_of_the_fleet_is_catalogued_with_the_plans_size_and_hash(string name, string file, long bytes, string sha256)
    {
        var row = _registry.GetLatestModel(name);
        Assert.NotNull(row);
        Assert.Equal("prism-ml/" + name.Split('-').Take(4).Aggregate((a, b) => a + "-" + b) + "-gguf", row.Repo);
        Assert.Equal(ModelSource.HuggingFace, row.Source);
        Assert.Equal(ModelEngine.Managed, row.Engine);
        Assert.Equal("Apache-2.0", row.License);
        var only = Assert.Single(row.BundleFiles!);
        Assert.Equal(new BundleFile(file, sha256, bytes), only);
        Assert.Equal(bytes, row.TotalBytes);
    }

    [Fact]
    public void The_catalogue_holds_the_whole_fleet_and_nothing_else_under_its_name()
    {
        Assert.Equal(Fleet.Select(r => (string)r[0]).OrderBy(n => n), Rows().Select(m => m.Name).OrderBy(n => n));
    }

    [Fact]
    public void Each_brain_carries_the_cards_capabilities_and_both_of_its_samplers()
    {
        // The cards: tool calls, thinking and a 262K context; thinking off 0.7 / 0.8 / 20 / presence 1.5,
        // thinking on 1.0 / 0.95 / 20 / presence 1.0, penalties over the last 64 - which are exactly
        // the numbers the engine carries for a model with no profile.
        var brains = Rows().Where(m => m.Modality == ModelModality.Chat && m.Architecture == "qwen35").ToList();
        Assert.Equal(10, brains.Count);
        foreach (var m in brains)
        {
            Assert.Equal(new[] { "Default", "Tools", "Reasoning", "LongContext" }, m.Capabilities);
            Assert.NotNull(m.Generation);
            Assert.False(m.Generation!.EnableThinking);
            Assert.Equal(SamplingSettings.Direct, ManagedChatGenerator.Card(m.Generation, thinking: false));
            Assert.Equal(SamplingSettings.Thinking, ManagedChatGenerator.Card(m.Generation, thinking: true));
        }
        Assert.All(Rows().Where(m => m.Modality == ModelModality.Vision), m => Assert.Equal(new[] { "Vision" }, m.Capabilities));
        Assert.All(Rows().Where(m => m.Architecture == "dflash"), m => Assert.Empty(m.Capabilities!));
    }

    [Fact]
    public void Today_the_CSharp_engine_runs_the_ten_brains_and_neither_the_drafters_nor_the_towers()
    {
        foreach (var m in Rows())
        {
            if (IsBrain(m))
            {
                Assert.Equal(ModelEngine.Managed, EngineChoice.Run(m, Shipped));
                Assert.True(EngineChoice.Opens(m, Shipped), m.Name);
            }
            else
            {
                Assert.Null(EngineChoice.Run(m, Shipped));
                Assert.False(EngineChoice.Opens(m, Shipped), m.Name);
                Assert.Contains("does not read yet", EngineChoice.Explain(m, Shipped));
            }
        }
        Assert.Equal(10, Rows().Count(IsBrain));
        Assert.Equal(6, Rows().Count(IsCompanion));
    }

    [Fact]
    public void Setup_never_plans_a_companion_the_engine_cannot_open()
    {
        using var loader = new BundleModelLoader(Store(), _registry);
        var plan = FirstRun.Plan(_registry, loader, Roomy(), speech: true, engines: Shipped);

        var companions = Rows().Where(IsCompanion).Select(m => m.Name).ToHashSet();
        Assert.NotEmpty(plan);
        Assert.DoesNotContain(plan, s => companions.Contains(s.Model.Name));
        // The eyes slot, too - it takes any Vision row that fits, and the towers outrank SmolVLM.
        Assert.DoesNotContain(plan, s => s.Modality == ModelModality.Vision && s.Model.Engine == ModelEngine.Managed);
    }

    [Fact]
    public void No_screen_or_ability_names_a_tower_as_the_eyes()
    {
        using var loader = new BundleModelLoader(Store(), _registry);
        var eyes = ModelChoice.For(ModelModality.Vision, _registry, loader, Roomy(), engines: Shipped);
        Assert.True(eyes is null || eyes.Engine != ModelEngine.Managed, eyes?.Name);

        var sweep = new SpeechModelSelector(_registry).BestFor(Roomy(), ModelModality.Vision);
        Assert.True(sweep is null || !sweep.ModelId.StartsWith("Ternary-Bonsai-2-", StringComparison.Ordinal), sweep?.ModelId);
    }

    [Fact]
    public void A_companion_is_never_fetched_or_resumed_and_a_brain_is()
    {
        // Half of a tower and half of a PQ2_0 brain on disk: the brain's download is carried on, the
        // tower's is not; and of the whole fleet planned from nothing, only the brains start.
        var dir = Store();
        foreach (var name in new[] { "Ternary-Bonsai-2-4B-Vision", "Ternary-Bonsai-2-2B-PQ2_0" })
        {
            var file = _registry.GetLatestModel(name)!.BundleFiles![0];
            Directory.CreateDirectory(Path.Combine(dir, name));
            SparseStore.Write(Path.Combine(dir, name, file.Name), file.SizeBytes / 2);
        }
        using var loader = new BundleModelLoader(dir, _registry);

        foreach (var m in Rows())
            Assert.Equal(IsBrain(m), loader.CanRunHere(m.Name, Shipped));

        var unfinished = ModelSetup.Unfinished(loader, Shipped).Select(u => u.Name).ToList();
        Assert.Contains("Ternary-Bonsai-2-2B-PQ2_0", unfinished);
        Assert.DoesNotContain("Ternary-Bonsai-2-4B-Vision", unfinished);

        var planned = Rows().Select(m => (m.Name, m.TotalBytes)).ToList();
        var started = ModelSetup.NotStarted(loader, planned, engines: Shipped).Select(s => s.Name).OrderBy(n => n).ToList();
        Assert.Equal(Rows().Where(IsBrain).Select(m => m.Name).Where(n => n != "Ternary-Bonsai-2-2B-PQ2_0").OrderBy(n => n), started);
    }

    [Fact]
    public void The_upgrade_path_never_offers_a_companion()
    {
        // Above the 2B there are only the 4B's drafter and tower here, so there is nothing to offer;
        // with the 4B's MTP file beside them - a brain with its draft head - that is the upgrade.
        var running = _registry.GetLatestModel("Ternary-Bonsai-2-2B")!;
        var companions = new[] { "Ternary-Bonsai-2-4B-DFlash2", "Ternary-Bonsai-2-4B-Vision" }
            .Select(n => _registry.GetLatestModel(n)!).Append(running).ToList();
        Assert.Null(EngineChoice.Better(running, companions, Roomy(), present: _ => false, shipped: Shipped));

        var withBrain = companions.Append(_registry.GetLatestModel("Ternary-Bonsai-2-4B-PQ2_0-MTP")!).ToList();
        Assert.Equal("Ternary-Bonsai-2-4B-PQ2_0-MTP", EngineChoice.Better(running, withBrain, Roomy(), present: _ => false, shipped: Shipped)?.Name);
    }

    private sealed class Catalogue(IReadOnlyList<ModelEntry> rows) : ModelRegistryService
    {
        public override IReadOnlyList<ModelEntry> AllModels => rows;
    }

    [Fact]
    public void A_request_needing_tool_calls_or_sight_is_not_sent_to_a_Bonsai_brain_and_one_needing_thought_is()
    {
        // The Bonsai brains declare tool calls (the card), but the C# engine writes them in the XML
        // form the app does not read yet, so the selector counts only what the engine delivers.
        var mnn = new ModelEntry("Qwen3.5-0.8B-MNN-like", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Chat, Architecture = "qwen3.5", QualityRank = 8,
            TotalBytes = 550_000_000, MinRamGb = 0.8, MinStorageGb = 0.7,
            Capabilities = ["Default", "Tools", "Reasoning", "LongContext"],
        };
        var rows = Rows().Append(mnn).ToList();
        using var catalogue = new Catalogue(rows);
        using var selector = new DeviceAwareModelSelector(catalogue, Shipped);

        Assert.Equal(mnn.Name, selector.BestFit(Roomy(), ChatCapability.Tools).ModelId);
        Assert.Throws<InvalidOperationException>(() => new DeviceAwareModelSelector(new Catalogue(Rows()), Shipped).BestFit(Roomy(), ChatCapability.Vision));

        var thinker = rows.Single(m => m.Name == selector.BestFit(Roomy(), ChatCapability.Reasoning).ModelId);
        Assert.Equal(ModelEngine.Managed, thinker.Engine);
        Assert.True(IsBrain(thinker), thinker.Name);
        Assert.Equal(ChatCapability.Default | ChatCapability.Reasoning | ChatCapability.LongContext,
            EngineChoice.Delivers(thinker, ChatCapability.Default | ChatCapability.Tools | ChatCapability.Reasoning | ChatCapability.LongContext | ChatCapability.Vision));
    }
}
