// TheBrainFloorTests.cs
//
// "NOTHING LESS THAN QWEN3.5 IS THE LLM." (the owner, 2026-10-16)
//
// The P30 ran Qwen3-0.6B for a week while Qwen3.5-0.8B was missing, and nothing
// fetched the 0.8B, because every route that can name a brain accepted a lesser one:
//   - the service's installed-model fallback put the 0.6B in charge;
//   - setup's plan counted the 0.6B as "the brain is here" and fetched nothing;
//   - ModelChoice named the first installed chat model in registry order on every
//     screen - and Qwen3-0.6B is the registry's first entry;
//   - the selectors tied Qwen3-1.7B with Qwen3.5-0.8B at rank 8.
//
// The floor is DATA - ModelEntry.Retired, in the registry - so no model name is
// written into the service. These pin that every route honours it, and that on the
// P30's measured numbers the answer is Qwen3.5-0.8B: never the 2B, whose load set off
// the system-wide low-memory kills on 2026-10-10.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using CircleAI.Assistant;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class TheBrainFloorTests : IDisposable
{
    private readonly ModelRegistryService _registry = new();
    private readonly List<string> _stores = [];

    public void Dispose()
    {
        _registry.Dispose();
        foreach (var dir in _stores)
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
    }

    /// <summary>The P30 as measured: MemTotal 3 776 516 kB, about 0.44 GB available.</summary>
    private static DeviceProbe P30(long availableBytes = 440_000_000) =>
        Probe(availableBytes, totalBytes: 3_776_516L * 1024);

    private static DeviceProbe Probe(long availableBytes, long totalBytes) => new(
        RamAvailableBytes: availableBytes,
        StorageFreeBytes: 29_000_000_000,
        Gpu: GpuKind.None, CpuCores: 8,
        Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
    {
        RamTotalBytes = totalBytes,
    };

    private IEnumerable<ModelEntry> Llms() =>
        _registry.AllModels.Where(m => m.Modality == ModelModality.Chat);

    [Fact]
    public void Every_qwen35_brain_answers_deterministically_and_without_loops()
    {
        // P30, 2026-10-20: one maize prompt, byte for byte the same, was answered "late
        // October or November" five times in one session and "late autumn" or "September"
        // five times in the next - temperature 0.2 and top-k 10 deciding a near-tie. Greedy
        // made it the same every time, and looped on two of eight typed answers; the
        // repetition penalty and then greedy did neither: maize four times out of four the
        // same and right, 28 of 28 spoken, no typed answer repeating itself.
        var brains = Llms().Where(m => m.Generation?.Sampler is not null).ToList();
        Assert.NotEmpty(brains);
        foreach (var m in brains)
            Assert.True(m.Generation!.SamplerConfigJson() == "{\"sampler_type\":\"penalty\",\"penalty\":1.1,\"penalty_sampler\":\"greedy\"}",
                $"{m.Name} samples with {m.Generation.SamplerConfigJson()}");
    }

    [Fact]
    public void A_sampler_sends_exactly_what_was_measured()
    {
        Assert.Equal("{\"sampler_type\":\"greedy\"}",
            new GenerationProfile(Sampler: new SamplerProfile(SamplerType: "greedy")).SamplerConfigJson());
        Assert.Equal("{\"sampler_type\":\"penalty\",\"penalty\":1.1,\"penalty_sampler\":\"greedy\"}",
            new GenerationProfile(Sampler: new SamplerProfile(SamplerType: "penalty", Penalty: 1.1)).SamplerConfigJson());
        Assert.Contains("\"mixed_samplers\"",
            new GenerationProfile(Sampler: new SamplerProfile()).SamplerConfigJson());
    }

    private ModelEntry Model(string name) =>
        _registry.AllModels.Single(m => m.Name == name);

    private string Store(params string[] installed)
    {
        var dir = Path.Combine(Path.GetTempPath(), "circleai-floor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _stores.Add(dir);
        foreach (var name in installed)
        {
            var bundle = Path.Combine(dir, name);
            Directory.CreateDirectory(bundle);
            foreach (var f in Model(name).BundleFiles!)
                SparseStore.Write(Path.Combine(bundle, f.Name), f.SizeBytes);
        }
        return dir;
    }

    // ── the data ────────────────────────────────────────────────────────────

    private static readonly Regex Qwen35OrNewer = new(@"^Qwen(3\.[5-9]|[4-9])", RegexOptions.CultureInvariant);

    [Fact]
    public void Every_LLM_the_registry_will_run_is_Qwen3_5_or_newer()
    {
        var below = Llms()
            .Where(m => !m.Retired)
            .Where(m => !Qwen35OrNewer.IsMatch(m.Name)
                        && m.Architecture is not ("qwen35" or "qwen3.5"))
            .Select(m => m.Name)
            .ToList();

        Assert.True(below.Count == 0,
            "below the floor and not retired: " + string.Join(", ", below));
    }

    [Fact]
    public void Every_lesser_Qwen_LLM_in_the_registry_is_retired()
    {
        // "Qwen3-" with a dash is the Qwen3 generation; "Qwen3." with a dot is 3.5+.
        var lesser = Llms().Where(m => m.Name.StartsWith("Qwen3-", StringComparison.Ordinal)
                                    || m.Name.StartsWith("Qwen2", StringComparison.Ordinal))
                           .ToList();

        Assert.NotEmpty(lesser);   // a pattern that matches nothing proves nothing
        Assert.All(lesser, m => Assert.True(m.Retired, m.Name + " is below the floor"));
    }

    [Fact]
    public void The_floor_still_leaves_a_brain_the_P30_could_hold()
    {
        Assert.Contains(Llms(), m => !m.Retired && ModelFit.CouldEverHold(m, P30()));
    }

    // ── the selectors ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(300_000_000L)]
    [InlineData(440_000_000L)]
    [InlineData(1_000_000_000L)]
    [InlineData(2_500_000_000L)]
    [InlineData(6_000_000_000L)]
    [InlineData(30_000_000_000L)]
    public void The_registry_selector_never_answers_a_retired_model(long available)
    {
        using var selector = new DeviceAwareModelSelector(_registry);

        var pick = selector.BestFit(
            Probe(available, totalBytes: Math.Max(available * 2, 4_000_000_000L)),
            ChatCapability.Default);

        Assert.False(Model(pick.ModelId).Retired, pick.ModelId + " is retired");
    }

    [Fact]
    public void The_catalogue_never_offers_a_retired_model_as_compatible()
    {
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        CatalogueSeed.SeedFromEmbedded(cat, _registry);
        foreach (var e in cat.All()) cat.SetAssessment(e.Name, compatible: true, rank: e.QualityRank);

        Assert.All(cat.Compatible(ModelModality.Chat), e => Assert.False(e.Retired, e.Name));
        Assert.False(cat.Best(ModelModality.Chat)!.Retired);
    }

    [Fact]
    public void When_nothing_fits_the_catalogue_selector_answers_the_smallest_Qwen3_5_not_a_lesser_model()
    {
        // The P30 at 0.44 GB free: the assessment marks every LLM incompatible, and
        // the "least-bad option" used to be whatever was smallest - a retired 0.5B.
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        CatalogueSeed.SeedFromEmbedded(cat, _registry);
        foreach (var e in cat.All()) cat.SetAssessment(e.Name, compatible: false, rank: 0);

        var pick = CatalogModelSelector.Preferring(cat, _registry).BestFit(P30(), ChatCapability.Default);

        Assert.Equal("Qwen3.5-0.8B-MNN", pick.ModelId);
    }

    // ── the installed-model fallback ────────────────────────────────────────

    [Fact]
    public void The_installed_fallback_will_not_stand_a_lesser_model_in_for_Qwen3_5()
    {
        using var loader = new BundleModelLoader(Store("Qwen3-0.6B-MNN"), _registry);

        Assert.Null(loader.BestInstalledChatModel(P30()));
    }

    [Fact]
    public void With_both_on_disk_the_installed_fallback_takes_the_Qwen3_5()
    {
        using var loader = new BundleModelLoader(Store("Qwen3-0.6B-MNN", "Qwen3.5-0.8B-MNN"), _registry);

        Assert.Equal("Qwen3.5-0.8B-MNN", loader.BestInstalledChatModel(P30()));
    }

    // ── what the screens name and what setup fetches ────────────────────────

    [Fact]
    public void The_screens_name_Qwen3_5_on_the_P30_even_with_the_0_6B_on_disk()
    {
        using var loader = new BundleModelLoader(Store("Qwen3-0.6B-MNN"), _registry);

        var chosen = ModelChoice.For(ModelModality.Chat, _registry, loader, P30());

        Assert.Equal("Qwen3.5-0.8B-MNN", chosen?.Name);
    }

    [Fact]
    public void Setup_fetches_the_0_8B_for_the_P30_and_never_the_2B()
    {
        // The 0.6B on disk used to fill this slot, so nothing was fetched at all; and
        // ranking by quality among what the P30 could EVER hold picks the 2B.
        using var loader = new BundleModelLoader(Store("Qwen3-0.6B-MNN"), _registry);

        var plan = FirstRun.Plan(_registry, loader, P30(), speech: false);

        var brain = Assert.Single(plan, s => s.Modality == ModelModality.Chat);
        Assert.Equal("Qwen3.5-0.8B-MNN", brain.Model.Name);
    }

    [Fact]
    public void Setup_still_gives_a_phone_with_room_a_bigger_Qwen3_5()
    {
        using var loader = new BundleModelLoader(Store(), _registry);

        var plan = FirstRun.Plan(_registry, loader,
            Probe(availableBytes: 6_000_000_000, totalBytes: 12_000_000_000), speech: false);

        var brain = Assert.Single(plan, s => s.Modality == ModelModality.Chat).Model;
        Assert.False(brain.Retired);
        Assert.True(brain.QualityRank > Model("Qwen3.5-0.8B-MNN").QualityRank,
            $"a 12 GB phone with 6 GB free was given {brain.Name}");
    }

    // ── the catalogue column ────────────────────────────────────────────────

    [Fact]
    public void Retired_survives_the_catalogue_round_trip_and_a_resync_can_lift_it()
    {
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        var retired = Model("Qwen3-0.6B-MNN");
        cat.Upsert(retired);

        Assert.True(cat.Get(retired.Name)!.Retired);

        Assert.True(cat.ReSync(retired with { Retired = false }));
        Assert.False(cat.Get(retired.Name)!.Retired);
    }

    [Fact]
    public void A_load_is_stamped_kept_across_a_resync_and_forgotten_on_a_new_version()
    {
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        var m = Model("Qwen3.5-0.8B-MNN");
        cat.Upsert(m);
        Assert.False(cat.Assessment(m.Name)!.Proven);

        cat.RecordLoad(m.Name);
        Assert.True(cat.Assessment(m.Name)!.Proven);

        cat.ReSync(m);
        Assert.True(cat.Assessment(m.Name)!.Proven, "a refresh of the same version is not a new model");

        cat.ReSync(m with { Version = m.Version + "-next" });
        Assert.False(cat.Assessment(m.Name)!.Proven, "a new version has not loaded here yet");
    }

    // ── the service ─────────────────────────────────────────────────────────

    [Fact]
    public void The_service_waits_for_the_catalogue_before_it_chooses_and_outside_the_lock()
    {
        // THE FIRST VERSION OF THIS TEST PASSED ON CODE THAT DID NOT COMPILE. It only
        // checked that the wait came before the selection in the text - and the wait
        // sat inside lock (Gate), which is CS1996 on the only target that builds this
        // file, while every test leg, which excludes it, stayed green. Found by
        // review, not by this test. So it now pins the shape that matters: the wait
        // is a call made BEFORE the lock, and nothing between the lock and the
        // selection awaits.
        var neuron = Source("src", "CircleAI", "Device", "CircleNeuronService.cs");
        var build  = neuron.IndexOf("private async System.Threading.Tasks.Task BuildNodeAsync()", StringComparison.Ordinal);
        var wait   = neuron.IndexOf("await WaitForCatalogueAsync()", build, StringComparison.Ordinal);
        var gate   = neuron.IndexOf("lock (Gate)", build, StringComparison.Ordinal);
        var choose = neuron.IndexOf("CatalogModelSelector.Preferring(Catalogue, registry)", build, StringComparison.Ordinal);

        Assert.True(build >= 0 && wait > build, "the brain never waits for the catalogue");
        Assert.True(gate > wait, "the wait must come before lock (Gate)");
        Assert.True(choose > gate, "the selection is expected inside the lock");
        Assert.DoesNotContain("await ", neuron[gate..choose], StringComparison.Ordinal);
    }

    [Fact]
    public void The_catalogue_is_wired_before_anything_can_start_the_brain()
    {
        // It was opened inside a background task after the download sweep, while
        // resuming listening - which starts the brain - ran first: a race the
        // catalogue won by about 130 ms on the P30. And the last load's crash must be
        // recorded BEFORE the bootstrap, whose housekeeping reads the verdicts.
        var app = Source("src", "CircleAIService", "ServiceApplication.cs");

        var assign  = app.IndexOf("CircleAI.Device.CircleNeuronService.Catalogue = catalogue;", StringComparison.Ordinal);
        // The supervisor classifies how the last run ended (2026-10-17); a surviving breadcrumb is no longer a strike by itself.
        var crash   = app.IndexOf("_supervisor?.RecoverFromPreviousRun();", StringComparison.Ordinal);
        var ready   = app.IndexOf("CircleAI.Device.CircleNeuronService.CatalogueReady = System.Threading.Tasks.Task.Run(", StringComparison.Ordinal);
        var resume  = app.IndexOf("_ = resident.ResumeAsync();", StringComparison.Ordinal);
        var sweep   = app.IndexOf(".SweepAbandoned(ModelStore.Path", StringComparison.Ordinal);

        Assert.True(assign >= 0 && crash > assign, "the crash is recorded after the catalogue is assigned");
        Assert.True(ready > crash, "the bootstrap must start after the crash is recorded");
        Assert.True(resume > ready, "the brain can start before the catalogue is wired");
        Assert.True(sweep > ready, "the catalogue is still waiting on the download sweep");
    }

    // ── the offer gate and setup's notion of "the brain is here" ────────────

    [Fact]
    public void A_free_RAM_verdict_at_boot_does_not_veto_the_brain()
    {
        // On the P30 the assessment saw 0.44 GB free and marked every Qwen3.5
        // incompatible. As a veto, that meant setup planned no brain and the screens
        // named none - while the selector wanted the 0.8B.
        using var cat = new SqliteModelCatalog("Data Source=:memory:");
        CatalogueSeed.SeedFromEmbedded(cat, _registry);
        foreach (var e in cat.All()) cat.SetAssessment(e.Name, compatible: false, rank: 0);

        Assert.True(ModelOffer.Worth(cat, "Qwen3.5-0.8B-MNN"));
        Assert.False(ModelOffer.Worth(cat, "Qwen3-0.6B-MNN"));          // retired
        cat.Refuse("Qwen3.5-2B-MNN", CrashVerdict.Reason);
        Assert.False(ModelOffer.Worth(cat, "Qwen3.5-2B-MNN"));          // refused

        using var loader = new BundleModelLoader(Store("Qwen3-0.6B-MNN"), _registry);
        Assert.Equal("Qwen3.5-0.8B-MNN",
            ModelChoice.For(ModelModality.Chat, _registry, loader, P30(), cat)?.Name);

        var plan = FirstRun.Plan(_registry, loader, P30(), speech: false,
                                 declined: ModelOffer.NotWorthOffering(cat));
        Assert.Equal("Qwen3.5-0.8B-MNN", Assert.Single(plan, s => s.Modality == ModelModality.Chat).Model.Name);
    }

    [Fact]
    public void A_2B_the_P30_cannot_run_now_does_not_stand_in_for_the_brain()
    {
        // "Could ever hold" counted a Qwen3.5-2B as the P30's brain - and loading it
        // with ~1 GB free is what set off the low-memory kills on 2026-10-10. With it
        // on disk the 0.8B was never fetched.
        using var loader = new BundleModelLoader(Store("Qwen3.5-2B-MNN"), _registry);

        var plan = FirstRun.Plan(_registry, loader, P30(), speech: false);

        Assert.Equal("Qwen3.5-0.8B-MNN", Assert.Single(plan, s => s.Modality == ModelModality.Chat).Model.Name);
    }

    [Fact]
    public void A_brain_that_has_loaded_here_fills_the_slot_on_a_busy_moment()
    {
        using var loader = new BundleModelLoader(Store("Qwen3.5-2B-MNN"), _registry);

        var plan = FirstRun.Plan(_registry, loader, P30(), speech: false,
                                 proven: id => id == "Qwen3.5-2B-MNN");

        Assert.DoesNotContain(plan, s => s.Modality == ModelModality.Chat);
    }

    [Fact]
    public void With_the_0_8B_on_disk_setup_fetches_no_brain()
    {
        using var loader = new BundleModelLoader(Store("Qwen3.5-0.8B-MNN"), _registry);

        Assert.DoesNotContain(FirstRun.Plan(_registry, loader, P30(), speech: false),
                              s => s.Modality == ModelModality.Chat);
    }

    [Fact]
    public void A_half_fetched_retired_model_is_never_resumed()
    {
        // Resumed on every start, biggest first, with no metered-network gate - so a
        // partial Qwen3-30B would be finished at somebody's expense, ahead of the
        // Qwen3.5 brain, for a model that can never run.
        var dir = Store();
        foreach (var name in new[] { "Qwen3-30B-A3B-MNN", "Qwen3.5-0.8B-MNN" })
        {
            var first = Model(name).BundleFiles!.OrderBy(f => f.SizeBytes).First();
            Directory.CreateDirectory(Path.Combine(dir, name));
            SparseStore.Write(Path.Combine(dir, name, first.Name), first.SizeBytes);
        }
        using var loader = new BundleModelLoader(dir, _registry);

        var owed = ModelSetup.Unfinished(loader).Select(u => u.Name).ToList();

        Assert.DoesNotContain("Qwen3-30B-A3B-MNN", owed);
        Assert.Contains("Qwen3.5-0.8B-MNN", owed);
    }

    // ── a brain this build cannot open: the Windows service, 2026-10-19 ──────────
    //
    // The Windows build ships MNN and no llama.cpp. On a 19.8 GB box the registry's top
    // fit was Ternary-Bonsai-2-27B, a GGUF, 3.6 GB of it half down: setup picked it,
    // the selector (which reads the engine) wanted Qwen3.5-4B, the resume would have
    // finished the Bonsai, and nothing ever fetched the 4B - no brain, ever.

    private static DeviceProbe ThisWindowsBox() => Probe(availableBytes: 8_100_000_000, totalBytes: 21_260_000_000L);
    private static readonly ModelEngine[] MnnOnly = [ModelEngine.Mnn];
    private static readonly ModelEngine[] MnnAndLlama = [ModelEngine.Mnn, ModelEngine.LlamaCpp];

    [Fact]
    public void Setup_never_picks_a_brain_this_build_cannot_open()
    {
        using var loader = new BundleModelLoader(Store(), _registry);

        var mnn = FirstRun.Plan(_registry, loader, ThisWindowsBox(), speech: false, engines: MnnOnly)
            .Single(s => s.Modality == ModelModality.Chat).Model;
        Assert.Equal(ModelEngine.Mnn, mnn.Engine);
        Assert.StartsWith("Qwen3.5", mnn.Name, StringComparison.Ordinal);

        // With llama.cpp in the build a GGUF may win on rank - and is then runnable.
        var both = FirstRun.Plan(_registry, loader, ThisWindowsBox(), speech: false, engines: MnnAndLlama)
            .Single(s => s.Modality == ModelModality.Chat).Model;
        Assert.True(both.QualityRank >= mnn.QualityRank);
    }

    [Fact]
    public void A_half_fetched_model_this_build_cannot_open_is_never_resumed()
    {
        var dir = Store();
        foreach (var name in new[] { "Ternary-Bonsai-2-27B", "Qwen3.5-0.8B-MNN" })
        {
            var first = Model(name).BundleFiles!.OrderBy(f => f.SizeBytes).First();
            Directory.CreateDirectory(Path.Combine(dir, name));
            SparseStore.Write(Path.Combine(dir, name, first.Name), first.SizeBytes / 2);
        }
        using var loader = new BundleModelLoader(dir, _registry);

        var mnnOnly = ModelSetup.Unfinished(loader, MnnOnly).Select(u => u.Name).ToList();
        Assert.DoesNotContain("Ternary-Bonsai-2-27B", mnnOnly);
        Assert.Contains("Qwen3.5-0.8B-MNN", mnnOnly);
        Assert.Contains("Ternary-Bonsai-2-27B", ModelSetup.Unfinished(loader, MnnAndLlama).Select(u => u.Name));
    }

    [Fact]
    public void The_persona_rides_in_the_turn_because_mnn_never_read_it_from_config()
    {
        // P30, 2026-10-20: set as MNN's system_prompt it was "accepted" and never read -
        // "What is your name?" came back "I am Qwen3.5". The bridge templates the user turn
        // alone, so the persona has to be in it.
        var generator = Source("src", "CircleAI", "Inference", "QwenTextGenerator.cs");

        Assert.DoesNotContain("{\\\"system_prompt\\\":", generator);
        Assert.Contains("_mnnPersona = null;", generator);
    }

    [Fact]
    public void The_desktop_service_chooses_from_the_assessed_catalogue_and_keeps_house()
    {
        var host = Source("src", "CircleAIService", "Host", "Program.cs");

        Assert.Contains("CatalogModelSelector.Preferring(catalogue, registry)", host);
        Assert.DoesNotContain("Preferring(catalogue: null", host);
        Assert.Contains("CatalogueHousekeeping.Reclaim(", host);
        Assert.Contains("CatalogueHousekeeping.SweepAbandoned(", host);
        Assert.Contains("catalogue.RecordLoad(", host);
        Assert.Contains("ModelSetup.ResumeUnfinishedAsync(", host);
        Assert.Contains("ModelSetup.FetchNotStartedAsync(", host);
    }

    [Fact]
    public void The_service_switches_housekeeping_on_and_keeps_the_running_model_out_of_it()
    {
        var app = Source("src", "CircleAIService", "ServiceApplication.cs");

        Assert.Contains("reclaimInferior: true", app);
        Assert.Contains("CircleAI.Device.CircleNeuronService.RunningModel", app);
    }

    [Fact]
    public void An_explicit_ModelId_below_the_floor_is_not_obeyed()
    {
        var neuron = Source("src", "CircleAI", "Device", "CircleNeuronService.cs");

        Assert.Contains("registry.GetLatestModel(wanted!) is { Retired: true }", neuron);
    }

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir!;
    }
}
