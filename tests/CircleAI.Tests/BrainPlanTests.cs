// BrainPlanTests.cs
//
// S4, STEP 1 IN THE APP: read the device, then choose the best brain for it - quick enough to
// talk with, not only the highest ranked - build it through the one factory, and say why.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Memory;
using CircleAI.SelfKnowledge;
using Xunit;

namespace CircleAI.Tests;

[CollectionDefinition(nameof(BrainSpeedCollection), DisableParallelization = true)]
public sealed class BrainSpeedCollection;

[Collection(nameof(BrainSpeedCollection))]
public sealed class BrainPlanTests : IDisposable
{
    private readonly Dictionary<string, double> _measured = new(StringComparer.OrdinalIgnoreCase);
    private CircleAI.Inference.Managed.Kernels.KernelThreads.Choice? _kernels;

    public BrainPlanTests()
    {
        BrainSpeed.Measured = id => _measured.TryGetValue(id, out var s) ? s : null;
        BrainSpeed.Kernels = () => _kernels;   // never a measurement left on this machine
    }

    public void Dispose()
    {
        BrainSpeed.Measured = null;
        BrainSpeed.Kernels = null;
    }

    private static ModelEntry Chat(string name, ModelEngine engine, long bytes, int rank) =>
        new(name, "1.0", engine == ModelEngine.Managed ? "PTQ1_0" : "Q4_0")
        {
            Engine = engine,
            Architecture = engine == ModelEngine.Managed ? "qwen35" : "qwen3",
            Modality = ModelModality.Chat,
            TotalBytes = bytes,
            MinRamGb = bytes / 1e9 + 0.1,
            MinStorageGb = bytes / 1e9 + 0.1,
            QualityRank = rank,
        };

    // The P30's shape: a 3.9 GB phone with about 1.5 GB free.
    private static DeviceProbe P30() =>
        new(RamAvailableBytes: 1_500_000_000, StorageFreeBytes: 20_000_000_000, Gpu: GpuKind.None,
            CpuCores: 8, Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        { RamTotalBytes = 3_867_000_000 };

    private sealed class Rows(IReadOnlyList<ModelEntry> rows) : ModelRegistryService
    {
        public override IReadOnlyList<ModelEntry> AllModels => rows;
    }

    private static readonly ModelEntry SmallMnn = Chat("Qwen3.5-0.8B-MNN", ModelEngine.Mnn, 550_000_000, rank: 8);
    private static readonly ModelEntry Bonsai2B = Chat("Ternary-Bonsai-2-2B", ModelEngine.Managed, 429_000_000, rank: 9);
    private static readonly ModelEntry Bonsai4B = Chat("Ternary-Bonsai-2-4B", ModelEngine.Managed, 950_000_000, rank: 10);

    // ── speed: measured, then estimated from a sibling on the same engine ─────

    [Fact]
    public void A_measured_speed_is_used_as_measured()
    {
        _measured["Qwen3.5-0.8B-MNN"] = 12.5;
        var speed = BrainSpeed.Of(SmallMnn, [SmallMnn, Bonsai2B]);
        Assert.Equal(12.5, speed!.TokensPerSecond, 3);
        Assert.Equal("Qwen3.5-0.8B-MNN", speed.From);
    }

    [Fact]
    public void An_unmeasured_brain_is_estimated_from_a_measured_one_on_the_same_engine_by_its_weight()
    {
        // The 2B answered at 1.47 on the P30; the 4B reads 950/429 as many bytes a token.
        _measured["Ternary-Bonsai-2-2B"] = 1.47;
        var speed = BrainSpeed.Of(Bonsai4B, [SmallMnn, Bonsai2B, Bonsai4B]);
        Assert.Equal(1.47 * 429.0 / 950.0, speed!.TokensPerSecond, 3);
        Assert.Equal("Ternary-Bonsai-2-2B", speed.From);
        Assert.True(BrainSpeed.TooSlow(Bonsai4B, [SmallMnn, Bonsai2B, Bonsai4B]));
    }

    // ── packings (OPEN-GAPS C8): the same weights read at different speeds ─────────

    private static ModelEntry Packed(string name, string packing, long bytes) =>
        new(name, "1.0", packing)
        {
            Engine = ModelEngine.Managed,
            Architecture = "qwen35",
            Modality = ModelModality.Chat,
            TotalBytes = bytes,
            MinRamGb = bytes / 1e9 + 0.1,
            MinStorageGb = bytes / 1e9 + 0.1,
            QualityRank = 9,
        };

    private static readonly ModelEntry TwoBPtq1 = Packed("Ternary-Bonsai-2-2B", "PTQ1_0", 429_000_000);
    private static readonly ModelEntry TwoBPq2 = Packed("Ternary-Bonsai-2-2B-PQ2_0", "PQ2_0", 517_000_000);
    private static readonly ModelEntry FourBPq2 = Packed("Ternary-Bonsai-2-4B-PQ2_0", "PQ2_0", 1_146_000_000);

    [Fact]
    public void A_brain_is_estimated_from_its_own_packing_before_another()
    {
        // This PC: PTQ1_0 2.9 tokens a second, PQ2_0 10.5 - a PQ2_0 4B is scaled from the PQ2_0 2B.
        _measured["Ternary-Bonsai-2-2B"] = 2.9;
        _measured["Ternary-Bonsai-2-2B-PQ2_0"] = 10.5;
        var speed = BrainSpeed.Of(FourBPq2, [TwoBPtq1, TwoBPq2, FourBPq2]);
        Assert.Equal("Ternary-Bonsai-2-2B-PQ2_0", speed!.From);
        Assert.Equal(10.5 * 517.0 / 1146.0, speed.TokensPerSecond, 3);
    }

    [Fact]
    public void Across_packings_the_devices_own_kernels_scale_the_estimate()
    {
        // ON THIS PC THE PQ2_0 2B WAS RULED TOO SLOW from its PTQ1_0 twin by size (2.9 x 429/517 =
        // 2.4) and ran at 10.5. With the kernels' rates - PQ2_0 read 4.3 times faster here - it is
        // estimated quick enough to talk with, before anything is fetched.
        _measured["Ternary-Bonsai-2-2B"] = 2.9;
        _kernels = new(4, 2.4, "pc") { Ptq1GigabytesPerSecond = 0.10, Pq2GigabytesPerSecond = 0.43 };
        var pc = BrainSpeed.Of(TwoBPq2, [TwoBPtq1, TwoBPq2]);
        Assert.Equal(2.9 * 429.0 / 517.0 * 4.3, pc!.TokensPerSecond, 3);
        Assert.False(BrainSpeed.TooSlow(TwoBPq2, [TwoBPtq1, TwoBPq2]));

        // The P30 (S9, 2026-11-02): PTQ1_0 read at 0.377 GB/s, PQ2_0 at 0.408 - still too slow.
        _measured["Ternary-Bonsai-2-2B"] = 1.66;
        _kernels = new(1, 0.88, "p30") { Ptq1GigabytesPerSecond = 0.377, Pq2GigabytesPerSecond = 0.408 };
        var p30 = BrainSpeed.Of(TwoBPq2, [TwoBPtq1, TwoBPq2]);
        Assert.Equal(1.66 * 429.0 / 517.0 * (0.408 / 0.377), p30!.TokensPerSecond, 3);
        Assert.True(BrainSpeed.TooSlow(TwoBPq2, [TwoBPtq1, TwoBPq2]));
    }

    [Fact]
    public void Without_the_kernels_measured_a_packing_is_estimated_by_size_alone()
    {
        _measured["Ternary-Bonsai-2-2B"] = 1.66;
        var speed = BrainSpeed.Of(TwoBPq2, [TwoBPtq1, TwoBPq2]);
        Assert.Equal(1.66 * 429.0 / 517.0, speed!.TokensPerSecond, 3);
    }

    [Fact]
    public void Another_engine_says_nothing_about_this_one_and_unknown_is_never_too_slow()
    {
        _measured["Qwen3.5-0.8B-MNN"] = 12.5;
        Assert.Null(BrainSpeed.Of(Bonsai4B, [SmallMnn, Bonsai4B]));
        Assert.False(BrainSpeed.TooSlow(Bonsai4B, [SmallMnn, Bonsai4B]));
    }

    [Fact]
    public void The_pick_is_the_best_quick_brain_and_the_fastest_when_none_is_quick()
    {
        var all = new[] { SmallMnn, Bonsai2B, Bonsai4B };

        // Nothing measured: rank decides, as it always did.
        Assert.Equal(Bonsai4B.Name, BrainSpeed.Pick(all, all)!.Name);

        // The C# engine measured slow here: both Bonsai rows are passed over for the quick one.
        _measured["Ternary-Bonsai-2-2B"] = 1.47;
        _measured["Qwen3.5-0.8B-MNN"] = 12.5;
        Assert.Equal(SmallMnn.Name, BrainSpeed.Pick(all, all)!.Name);

        // Every one too slow: speed is what decides.
        _measured["Qwen3.5-0.8B-MNN"] = 2.0;
        Assert.Equal(SmallMnn.Name, BrainSpeed.Pick(all, all)!.Name);
        Assert.Null(BrainSpeed.Pick([], all));
    }

    [Fact]
    public void The_selector_passes_over_a_brain_too_slow_to_talk_with_on_this_device()
    {
        using var registry = new Rows([SmallMnn, Bonsai2B, Bonsai4B]);
        var selector = new DeviceAwareModelSelector(registry, engines: [ModelEngine.Mnn, ModelEngine.Managed]);

        Assert.Equal(Bonsai4B.Name, selector.BestFit(P30(), ChatCapability.Default).ModelId);

        _measured["Ternary-Bonsai-2-2B"] = 1.47;
        _measured["Qwen3.5-0.8B-MNN"] = 12.5;
        Assert.Equal(SmallMnn.Name, selector.BestFit(P30(), ChatCapability.Default).ModelId);
        Assert.Equal(SmallMnn.Name, selector.LoadableNow(P30(), ChatCapability.Default)!.ModelId);
    }

    // ── the plan, said out loud ───────────────────────────────────────────────

    [Fact]
    public void The_plan_names_the_brain_and_the_one_it_passed_over_for_being_too_slow()
    {
        _measured["Ternary-Bonsai-2-2B"] = 1.47;
        _measured["Qwen3.5-0.8B-MNN"] = 12.5;
        var context = new DeviceContext(P30(), CpuFeatures.Read(), 4, 4, PowerState.Unknown, FormFactor.Phone);

        var said = BrainPlan.Explain(context, SmallMnn, [SmallMnn, Bonsai2B, Bonsai4B]);

        Assert.StartsWith("Qwen3.5-0.8B-MNN on MNN", said, StringComparison.Ordinal);
        Assert.Contains("about 12.5 tokens a second", said, StringComparison.Ordinal);
        Assert.Contains("not Ternary-Bonsai-2-4B", said, StringComparison.Ordinal);
        Assert.Contains("estimated from Ternary-Bonsai-2-2B", said, StringComparison.Ordinal);
        Assert.Contains("below the 4 a conversation needs", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_brain_on_the_CSharp_engine_says_how_many_threads_it_was_given()
    {
        var context = new DeviceContext(P30(), CpuFeatures.Read(), 4, 4, PowerState.Unknown, FormFactor.Phone);
        Assert.StartsWith("Ternary-Bonsai-2-2B on the C# engine, 3 threads", BrainPlan.Explain(context, Bonsai2B, [Bonsai2B]), StringComparison.Ordinal);
        Assert.Contains("no brain", BrainPlan.Explain(context, null, [Bonsai2B]), StringComparison.Ordinal);
    }

    // ── the device, read ──────────────────────────────────────────────────────

    private static string CpuTree(params long[] topKhz)
    {
        var root = Path.Combine(Path.GetTempPath(), "circleai-cpu-" + Guid.NewGuid().ToString("N")[..8]);
        for (var i = 0; i < topKhz.Length; i++)
        {
            var dir = Directory.CreateDirectory(Path.Combine(root, "cpu" + i, "cpufreq")).FullName;
            File.WriteAllText(Path.Combine(dir, "cpuinfo_max_freq"), topKhz[i] + "\n");
        }
        Directory.CreateDirectory(Path.Combine(root, "cpufreq"));   // not a core: the kernel's own folder
        return root;
    }

    [Fact]
    public void The_fast_cores_are_read_from_the_kernel_and_the_little_ones_are_not_counted()
    {
        // The P30's Kirin 710: 4 x A53 at 1.71 GHz, 4 x A73 at 2.2 GHz.
        var p30 = CpuTree(1_709_000, 1_709_000, 1_709_000, 1_709_000, 2_208_000, 2_208_000, 2_208_000, 2_208_000);
        // A Pixel 7a's Tensor G2: 4 x A55 at 1.8, 2 x A78 at 2.35, 2 x X1 at 2.85 GHz.
        var pixel = CpuTree(1_803_000, 1_803_000, 1_803_000, 1_803_000, 2_350_000, 2_350_000, 2_850_000, 2_850_000);
        try
        {
            Assert.Equal((4, 4), DeviceContext.Cores(p30, 8));
            Assert.Equal((4, 4), DeviceContext.Cores(pixel, 8));
            Assert.Equal((6, 0), DeviceContext.Cores(Path.Combine(p30, "nowhere"), 6));
        }
        finally
        {
            Directory.Delete(p30, recursive: true);
            Directory.Delete(pixel, recursive: true);
        }
    }

    [Fact]
    public void A_brain_gets_the_fast_cores_less_one_and_never_none()
    {
        static DeviceContext With(int fast) => new(P30(), CpuFeatures.Read(), fast, 0, PowerState.Unknown, FormFactor.Phone);
        Assert.Equal(3, With(4).BrainThreads);
        Assert.Equal(1, With(1).BrainThreads);
        Assert.Equal(7, With(8).BrainThreads);
    }

    [Fact]
    public void The_platform_says_the_form_factor_and_power_and_a_failing_hook_reads_as_unknown()
    {
        try
        {
            DeviceContext.FormFactorProbe = () => FormFactor.Tablet;
            DeviceContext.PowerProbe = () => new PowerState(15, false, Hot: true);
            var read = DeviceContext.Read(P30());
            Assert.Equal(FormFactor.Tablet, read.FormFactor);
            Assert.True(read.Power.Low);
            Assert.True(read.Power.Hot);

            DeviceContext.FormFactorProbe = () => throw new InvalidOperationException("no screen");
            DeviceContext.PowerProbe = () => throw new InvalidOperationException("no battery");
            read = DeviceContext.Read(P30());
            Assert.Equal(FormFactor.Phone, read.FormFactor);   // from the tier: 3.9 GB is a phone
            Assert.Equal(PowerState.Unknown, read.Power);
        }
        finally
        {
            DeviceContext.FormFactorProbe = null;
            DeviceContext.PowerProbe = null;
        }
    }

    // ── one factory, and the engine read off the file ─────────────────────────

    private static string GgufFile(string architecture, uint tensorType, int dataBytes)
    {
        var bytes = new GgufBuilder()
            .String("general.architecture", architecture)
            .Tensor("blk.0.ffn_up.weight", [128], tensorType, new byte[dataBytes])
            .Build();
        var path = Path.Combine(Path.GetTempPath(), "circleai-engine-" + Guid.NewGuid().ToString("N")[..8] + ".gguf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_file_with_no_row_is_opened_by_the_engine_its_contents_need()
    {
        var bonsai = GgufFile("qwen35", tensorType: 143, dataBytes: 28);      // PTQ1_0: 128 weights in 28 bytes
        var other = GgufFile("llama", tensorType: 0, dataBytes: 512);          // F32
        var broken = Path.Combine(Path.GetTempPath(), "circleai-engine-" + Guid.NewGuid().ToString("N")[..8] + ".gguf");
        File.WriteAllBytes(broken, [1, 2, 3]);
        try
        {
            Assert.Equal(ModelEngine.Managed, BrainFactory.EngineOf(bonsai));
            Assert.Equal(ModelEngine.LlamaCpp, BrainFactory.EngineOf(other));
            Assert.Equal(ModelEngine.LlamaCpp, BrainFactory.EngineOf(broken));
            Assert.Equal(ModelEngine.Mnn, BrainFactory.EngineOf(Path.Combine("models", "Qwen3.5-0.8B-MNN", "config.json")));
        }
        finally
        {
            File.Delete(bonsai);
            File.Delete(other);
            File.Delete(broken);
        }
    }

    [Fact]
    public void The_catalogue_runs_the_Bonsai_2_fleet_on_the_CSharp_engine()
    {
        // Every file of the fleet is on the C# engine (S5 catalogued the whole fleet); the brains in
        // both packings, and the PQ2_0 files with their MTP head, run there today; the drafters and
        // the vision towers wait for their code (Bonsai2FleetTests).
        using var registry = new ModelRegistryService();
        var fleet = registry.AllModels.Where(m => m.Name.StartsWith("Ternary-Bonsai-2-", StringComparison.Ordinal)).ToList();
        Assert.Equal(16, fleet.Count);
        Assert.All(fleet, m => Assert.Equal(ModelEngine.Managed, m.Engine));
        var brains = fleet.Where(m => m.Quantization is "PTQ1_0" or "PQ2_0" or "PQ2_0-MTP").ToList();
        Assert.Equal(10, brains.Count);
        Assert.All(brains, m => Assert.Equal(ModelEngine.Managed, EngineChoice.Run(m, [ModelEngine.Mnn, ModelEngine.Managed])));
    }

    [Fact]
    public void A_Bonsai_row_on_the_CSharp_engine_is_present_and_resolves_to_its_gguf()
    {
        // FOUND ON THE P30, 2026-11-02: once the rows moved to the C# engine, the loader asked the
        // 2B for MNN's config.json - four checks there read "llama.cpp" as "a GGUF" - and called
        // all 429 MB of it "not on this device".
        using var registry = new ModelRegistryService();
        var twoB = registry.GetLatestModel("Ternary-Bonsai-2-2B")!;
        Assert.Equal(ModelEngine.Managed, twoB.Engine);

        var store = Path.Combine(Path.GetTempPath(), "circleai-bonsai-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            foreach (var f in twoB.BundleFiles!)
                SparseStore.Write(Path.Combine(store, twoB.Name, f.Name), f.SizeBytes);

            using var loader = new BundleModelLoader(store, registry);
            Assert.True(loader.ModelPresent(twoB.Name));
            Assert.EndsWith(".gguf", loader.GetModelPath(twoB.Name), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(store)) Directory.Delete(store, recursive: true);
        }
    }

    // ── what a relaunch keeps ─────────────────────────────────────────────────

    [Fact]
    public async Task A_speed_measured_before_a_relaunch_still_rules_the_choice_after_it()
    {
        // FOUND ON THE P30, 2026-11-02: the plan passed over the 4B once the 2B had been measured
        // at 1.66 - and a relaunch forgot it, because only load outcomes were read back. The
        // selector wanted the 4B again, and the upgrade path would have fetched it.
        var dir = Path.Combine(Path.GetTempPath(), "circleai-self-" + Guid.NewGuid().ToString("N")[..8]);
        var was = SelfModel.Current;
        try
        {
            using (var memory = new MemoryService(dir))
            {
                new SelfModel(memory).LearnSpeed(Bonsai2B.Name, 1.66);
                var subject = SelfSubjects.ModelSpeed(Bonsai2B.Name);
                for (var i = 0; i < 200 && !(await memory.AllAsync(limit: 50)).Any(a => a.Subject == subject); i++)
                    await Task.Delay(50);
            }

            using (var memory = new MemoryService(dir))
            {
                var relaunched = new SelfModel(memory);
                await relaunched.LoadAsync();
                Assert.Equal(1.66, relaunched.About(Bonsai2B.Name).TokensPerSecond!.Value, 2);

                SelfModel.Current = relaunched;
                using var registry = new Rows([SmallMnn, Bonsai2B, Bonsai4B]);
                var selector = new DeviceAwareModelSelector(registry, engines: [ModelEngine.Mnn, ModelEngine.Managed]);
                Assert.True(BrainSpeed.TooSlow(Bonsai4B, registry.AllModels));
                Assert.Equal(SmallMnn.Name, selector.BestFit(P30(), ChatCapability.Default).ModelId);
            }

            Assert.Null(SelfModel.SpeedIn("Ternary-Bonsai-2-2B loaded and answered here"));
        }
        finally
        {
            SelfModel.Current = was;
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Every_site_that_builds_a_brain_asks_the_one_factory()
    {
        // The three that wrote `new QwenTextGenerator` themselves now go through BrainFactory, and
        // AIService's BuildByEngine is that factory - read from the sources, the way the repo's
        // other wiring guards are.
        var src = Path.Combine(RepoRoot(), "src", "CircleAI");
        foreach (var file in new[]
                 {
                     Path.Combine(src, "Hosting", "ServiceCollectionExtensions.cs"),
                     Path.Combine(src, "Assistant.Runtime", "CircleAISession.cs"),
                     Path.Combine(src, "InferenceServer", "Lifecycle", "MnnInferenceBridgeFactory.cs"),
                     Path.Combine(src, "Hosting", "AIService.cs"),
                 })
        {
            var code = File.ReadAllText(file);
            Assert.Contains("BrainFactory.Default.Build(", code, StringComparison.Ordinal);
            Assert.DoesNotContain("new QwenTextGenerator(", code, StringComparison.Ordinal);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CircleAI.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
