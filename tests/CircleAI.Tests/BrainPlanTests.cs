// BrainPlanTests.cs
//
// S4, STEP 1 IN THE APP: read the device, then choose the best brain for it - the Bonsai 2 fleet
// first, then rank, with no speed gate (the owner, 2026-11-04) - build it through the one factory,
// and say what it is and how fast it answers here.

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
        // ON THIS PC THE PQ2_0 2B WAS ESTIMATED from its PTQ1_0 twin by size (2.9 x 429/517 = 2.4)
        // and ran at 10.5. With the kernels' rates - PQ2_0 read 4.3 times faster here - the estimate
        // says so before anything is fetched.
        _measured["Ternary-Bonsai-2-2B"] = 2.9;
        _kernels = new(4, 2.4, "pc") { Ptq1GigabytesPerSecond = 0.10, Pq2GigabytesPerSecond = 0.43 };
        var pc = BrainSpeed.Of(TwoBPq2, [TwoBPtq1, TwoBPq2]);
        Assert.Equal(2.9 * 429.0 / 517.0 * 4.3, pc!.TokensPerSecond, 3);

        // The P30 (S9, 2026-11-02): PTQ1_0 read at 0.377 GB/s, PQ2_0 at 0.408 - about the same.
        _measured["Ternary-Bonsai-2-2B"] = 1.66;
        _kernels = new(1, 0.88, "p30") { Ptq1GigabytesPerSecond = 0.377, Pq2GigabytesPerSecond = 0.408 };
        var p30 = BrainSpeed.Of(TwoBPq2, [TwoBPtq1, TwoBPq2]);
        Assert.Equal(1.66 * 429.0 / 517.0 * (0.408 / 0.377), p30!.TokensPerSecond, 3);
    }

    [Fact]
    public void Without_the_kernels_measured_a_packing_is_estimated_by_size_alone()
    {
        _measured["Ternary-Bonsai-2-2B"] = 1.66;
        var speed = BrainSpeed.Of(TwoBPq2, [TwoBPtq1, TwoBPq2]);
        Assert.Equal(1.66 * 429.0 / 517.0, speed!.TokensPerSecond, 3);
    }

    [Fact]
    public void Another_engine_says_nothing_about_this_ones_speed()
    {
        _measured["Qwen3.5-0.8B-MNN"] = 12.5;
        Assert.Null(BrainSpeed.Of(Bonsai4B, [SmallMnn, Bonsai4B]));
    }

    // ── the pick: the Bonsai 2 fleet first, then rank, and no speed gate ──────

    [Fact]
    public void The_pick_is_the_Bonsai_2_fleet_first_then_rank_whatever_the_speed()
    {
        var all = new[] { SmallMnn, Bonsai2B, Bonsai4B };
        Assert.Equal(Bonsai4B.Name, ModelChoice.Best(all)!.Name);

        // Measured slow here - the C# 2B at 1.47, the MNN brain at 12.5 - the fleet still comes first: how
        // fast a brain answers is the engine's to make the most of, not a reason to choose another.
        _measured["Ternary-Bonsai-2-2B"] = 1.47;
        _measured["Qwen3.5-0.8B-MNN"] = 12.5;
        Assert.Equal(Bonsai4B.Name, ModelChoice.Best(all)!.Name);

        // A brain outside the fleet ranked above every fleet brain still comes after them; with no fleet
        // brain, the best of the rest; with nothing, nothing.
        var bigMnn = Chat("Qwen3.5-4B-MNN", ModelEngine.Mnn, 2_500_000_000, rank: 11);
        Assert.Equal(Bonsai2B.Name, ModelChoice.Best([SmallMnn, bigMnn, Bonsai2B])!.Name);
        Assert.Equal(bigMnn.Name, ModelChoice.Best([SmallMnn, bigMnn])!.Name);
        Assert.Null(ModelChoice.Best([]));
        Assert.True(ModelChoice.Better(Bonsai2B, bigMnn));
        Assert.False(ModelChoice.Better(Bonsai2B, Bonsai4B));
    }

    [Fact]
    public void The_selector_takes_the_best_Bonsai_the_device_can_hold_whatever_its_speed()
    {
        using var registry = new Rows([SmallMnn, Bonsai2B, Bonsai4B]);
        var selector = new DeviceAwareModelSelector(registry, engines: [ModelEngine.Mnn, ModelEngine.Managed]);

        Assert.Equal(Bonsai4B.Name, selector.BestFit(P30(), ChatCapability.Default).ModelId);

        _measured["Ternary-Bonsai-2-2B"] = 1.47;
        _measured["Qwen3.5-0.8B-MNN"] = 12.5;
        Assert.Equal(Bonsai4B.Name, selector.BestFit(P30(), ChatCapability.Default).ModelId);
        Assert.Equal(Bonsai4B.Name, selector.LoadableNow(P30(), ChatCapability.Default)!.ModelId);
    }

    // ── the plan, said out loud ───────────────────────────────────────────────

    [Fact]
    public void The_plan_names_the_brain_its_engine_and_how_fast_it_answers_here()
    {
        _measured["Ternary-Bonsai-2-2B"] = 1.47;
        var context = new DeviceContext(P30(), CpuFeatures.Read(), 4, 4, PowerState.Unknown, FormFactor.Phone);

        var said = BrainPlan.Explain(context, Bonsai4B, [SmallMnn, Bonsai2B, Bonsai4B]);

        Assert.StartsWith("Ternary-Bonsai-2-4B on the C# engine, 3 threads", said, StringComparison.Ordinal);
        Assert.Contains("about 0.7 tokens a second (estimated from Ternary-Bonsai-2-2B)", said, StringComparison.Ordinal);
        Assert.DoesNotContain(" - not ", said, StringComparison.Ordinal);   // speed passes nothing over
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
    public void A_phone_counts_the_cores_it_has_and_not_the_ones_its_process_started_on()
    {
        var p30 = CpuTree(1_709_000, 1_709_000, 1_709_000, 1_709_000, 2_208_000, 2_208_000, 2_208_000, 2_208_000);
        try
        {
            Assert.Equal(8, DeviceContext.CountCores(phone: true, p30, allowed: 3));
            Assert.Equal(3, DeviceContext.CountCores(phone: false, p30, allowed: 3));
            Assert.Equal(3, DeviceContext.CountCores(phone: true, Path.Combine(p30, "nowhere"), allowed: 3));
        }
        finally
        {
            Directory.Delete(p30, recursive: true);
        }
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
    public async Task A_speed_measured_before_a_relaunch_is_still_known_after_it()
    {
        // FOUND ON THE P30, 2026-11-02: the 2B had been measured at 1.66 - and a relaunch forgot it,
        // because only load outcomes were read back. The speed is what the plan says about a brain, so
        // it is read back; it no longer chooses one (the owner, 2026-11-04).
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
                BrainSpeed.Measured = null;   // what the relaunched device remembers, not this test's table
                Assert.Equal(1.66 * 429.0 / 950.0, BrainSpeed.Of(Bonsai4B, registry.AllModels)!.TokensPerSecond, 2);
                Assert.Equal(Bonsai4B.Name, selector.BestFit(P30(), ChatCapability.Default).ModelId);
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
