// DeviceModelAssessorTests.cs
//
// The assessment that fills `compatible`/`rank`. `compatible` is the AND of four
// gates — engine shipped, RAM fits, storage fits, licence free — and each gate
// is exercised here on its own. `rank` stays quality-dominant. A row that fails
// any gate is not returned by Best(); a row that passes is, best quality first.

using System;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class DeviceModelAssessorTests
{
    static SqliteModelCatalog NewCatalog() => new("Data Source=:memory:");

    // storageTotalGb defaults to 128 — the handset the share-of-device rule is sized
    // against, so a test that does not care about the budget gets a realistic one
    // (30% of 128 GB = 38.4 GB) rather than an accidental 0 that falls back.
    static DeviceProbe Probe(double ramGb, double storageGb = 100, double? vramGb = null,
                             double storageTotalGb = 128)
        => new(
            RamAvailableBytes: (long)(ramGb * 1_000_000_000),
            StorageFreeBytes:  (long)(storageGb * 1_000_000_000),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Active,
            Connectivity:      Connectivity.Online)
        {
            VramGb = vramGb,
            RamTotalBytes = (long)(ramGb * 1_000_000_000),
            StorageTotalBytes = (long)(storageTotalGb * 1_000_000_000),
        };

    static ModelEntry Entry(
        string name,
        ModelEngine engine = ModelEngine.Mnn,
        int qualityRank = 6,
        double minRamGb = 0.6,
        double minStorageGb = 0.5,
        ModelModality modality = ModelModality.Chat,
        long totalBytes = 100_000_000)
        => new(name, "1.0", "MNN-Q4")
        {
            Engine       = engine,
            Modality     = modality,
            TotalBytes   = totalBytes,
            MinRamGb     = minRamGb,
            MinStorageGb = minStorageGb,
            QualityRank  = qualityRank,
        };

    [Fact]
    public void An_MNN_model_that_fits_is_compatible()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("qwen"));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Equal("qwen", cat.Best(ModelModality.Chat)!.Name);
    }

    [Fact]
    public void An_engine_the_device_does_not_ship_is_incompatible()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("bonsai-gguf", engine: ModelEngine.LlamaCpp));  // GGUF, no engine shipped
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Null(cat.Best(ModelModality.Chat));   // the bit tells the truth
    }

    [Fact]
    public void A_model_that_needs_more_RAM_than_the_device_has_is_incompatible()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("big", minRamGb: 4.0));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 1.0));   // usable ~0.85 GB
        Assert.Null(cat.Best(ModelModality.Chat));
    }

    [Fact]
    public void A_model_that_needs_more_storage_than_is_free_is_incompatible()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("m", minStorageGb: 50));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8, storageGb: 1));
        Assert.Null(cat.Best(ModelModality.Chat));
    }

    [Fact]
    public void A_VRAM_requirement_gates_when_the_device_reports_none()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("needs-gpu") with { MinVramGb = 6.0 });

        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8, vramGb: null));
        Assert.Null(cat.Best(ModelModality.Chat));

        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8, vramGb: 8));
        Assert.NotNull(cat.Best(ModelModality.Chat));
    }

    [Fact]
    public void Rank_tracks_measured_quality()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("small", qualityRank: 6, minRamGb: 0.6));
        cat.Upsert(Entry("big",   qualityRank: 10, minRamGb: 1.5));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Equal("big", cat.Best(ModelModality.Chat)!.Name);
    }

    [Fact]
    public void Assess_returns_the_number_of_rows_scored()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("a"));
        cat.Upsert(Entry("b"));
        cat.Upsert(Entry("c"));
        var result = DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Equal(3, result.Assessed);
        Assert.Equal(3, result.Compatible);
    }

    // An MoE bundle (Qwen3-30B-A3B: 18 GB of weights on disk, but only ~3B active
    // experts resident) advertises a low MinRamGb that is only truthful when the
    // runtime memory-maps the weights. The compatible bit must follow what the
    // runtime will actually do: recommend it when mmap pages the rest off disk,
    // refuse it when the whole file would have to live in RAM and OOM the phone.
    [Fact]
    public void An_MoE_bundle_is_compatible_only_when_mmap_pages_its_weights()
    {
        static ModelEntry Moe() => new("qwen3-30b-a3b", "3.0", "MNN-Q4")
        {
            Engine       = ModelEngine.Mnn,
            Modality     = ModelModality.Chat,
            TotalBytes   = 18_000_000_000,   // 18 GB on disk
            MinRamGb     = 2.5,              // only the active experts, IF mmap'd
            MinStorageGb = 18.0,
            QualityRank  = 15,
        };

        var phone = Probe(ramGb: 5, storageGb: 64);   // usable ~4.25 GB: 2.5 fits, 18 does not

        // The ceiling is lifted here ON PURPOSE. This test is about the mmap/RAM
        // maths in EffectiveMinRamGb — an 18 GB MoE is the only shape that exercises
        // it — and the 8 GB form-factor rule would otherwise refuse the bundle before
        // that maths ever runs, silently gutting the coverage while staying green.
        // The ceiling has its own tests below; this one keeps its own subject.
        const double NoCeiling = 1e12;

        using (var cat = NewCatalog())
        {
            cat.Upsert(Moe());
            new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: false, maxModelBytes: NoCeiling).Assess(phone);
            Assert.Null(cat.Best(ModelModality.Chat));   // honest: 18 GB can't be resident
        }

        using (var cat = NewCatalog())
        {
            cat.Upsert(Moe());
            new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true, maxModelBytes: NoCeiling).Assess(phone);
            Assert.Equal("qwen3-30b-a3b", cat.Best(ModelModality.Chat)!.Name);   // mmap pages the rest
        }
    }

    // A giant that must page tens of GB off disk before its first token stays
    // COMPATIBLE (a person can choose it) but must never be the silent default — a
    // model that answers quickly outranks it, whatever the quality gap.
    [Fact]
    public void A_giant_that_must_mmap_is_not_the_default_when_a_fast_model_fits()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("moe-35b", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Chat,
            TotalBytes = 22_000_000_000, MinRamGb = 2.5, MinStorageGb = 22.0, QualityRank = 16,
        });
        cat.Upsert(Entry("fast-2b", qualityRank: 9, minRamGb: 1.9, minStorageGb: 1.5));

        // mmap on, ample RAM + storage so BOTH are compatible. Ceiling lifted for the
        // same reason as above: the subject here is that a giant never becomes the
        // silent DEFAULT, which needs it compatible in the first place.
        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true, maxModelBytes: 1e12)
            .Assess(Probe(ramGb: 12, storageGb: 128));

        Assert.Equal(2, cat.Compatible(ModelModality.Chat).Count);        // both available
        Assert.Equal("fast-2b", cat.Best(ModelModality.Chat)!.Name);      // fast one is the default
    }

    // A dense bundle's MinRamGb already includes its full weights, so the mmap
    // flag must not change its verdict — guards the Max() in EffectiveMinRamGb.
    [Fact]
    public void A_dense_bundle_is_unaffected_by_the_mmap_flag()
    {
        var phone = Probe(ramGb: 5, storageGb: 64);

        foreach (var mmap in new[] { false, true })
        {
            using var cat = NewCatalog();
            cat.Upsert(Entry("qwen3-1.7b", minRamGb: 1.7, minStorageGb: 1.2));  // TotalBytes 0.1 GB « 1.7
            new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: mmap).Assess(phone);
            Assert.Equal("qwen3-1.7b", cat.Best(ModelModality.Chat)!.Name);
        }
    }

    // ---- vision bundles -----------------------------------------------------

    // A VLM carries a vision encoder alongside the LLM, so it needs MORE
    // resident than it weighs on disk. Qwen2.5-VL-3B is 2.74 GB with MinRamGb
    // 3.9 — above a 3.6 GB phone — so it sat permanently incompatible even
    // though mmap is exactly what such a model needs. It weighs less than the
    // 8 GB giant threshold, so the old gate never offered it mmap.
    [Fact]
    public void A_bundle_gets_the_mmap_discount_only_when_somebody_measured_it()
    {
        // THE RULE IS A MEASUREMENT, NOT A FORMULA. The obvious arithmetic is
        // MinRamGb - weight: assume the declared requirement splits into weights plus
        // runtime and subtract the paged half. It was in ModelFit and it offered a
        // 2.85 GB model to a 1.1 GB handset, because Qwen3.5-4B declares 3.8 GB and
        // the formula produced 0.95. One model measured at 0.8 GB does not license
        // the same sum for every other model.
        ModelEntry Vlm(double? measured) => new("qwen2.5-vl-3b", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Vision,
            TotalBytes = 2_736_200_899, MinRamGb = 3.9, MinStorageGb = 2.6, QualityRank = 9,
            MmapResidentGb = measured,
        };

        // 3.9 GB declared against ~3.6 GB usable: eagerly it does not fit, and with
        // no measurement there is nothing that says paging would help.
        using (var cat = NewCatalog())
        {
            cat.Upsert(Vlm(null));
            new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true)
                .Assess(Probe(ramGb: 4.3));
            Assert.Null(cat.Best(ModelModality.Vision));
        }

        // Measured at 1.2 GB resident, so it fits and is offered.
        using (var cat = NewCatalog())
        {
            cat.Upsert(Vlm(1.2));
            new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true)
                .Assess(Probe(ramGb: 4.3));
            Assert.NotNull(cat.Best(ModelModality.Vision));
        }
    }

    // With mmap off it must stay out: eagerly it needs its full weight resident
    // plus the encoder, and loading it would OOM.
    [Fact]
    public void The_same_vision_bundle_stays_out_when_mmap_is_off()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("qwen2.5-vl-3b", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Vision,
            TotalBytes = 2_736_200_899, MinRamGb = 3.9, MinStorageGb = 2.6, QualityRank = 9,
        });

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: false)
            .Assess(Probe(ramGb: 4.3));

        Assert.Null(cat.Best(ModelModality.Vision));
    }

    // A small VLM whose MinRamGb already covers its weights must NOT be pushed
    // onto the slow mmap path — SmolVLM-256M is 311 MB and fits eagerly.
    [Fact]
    public void A_small_vision_bundle_that_fits_eagerly_is_not_mmapped()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("smolvlm-256m", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Vision,
            TotalBytes = 311_428_914, MinRamGb = 0.5, MinStorageGb = 0.4, QualityRank = 4,
        });

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true)
            .Assess(Probe(ramGb: 4.3));

        Assert.NotNull(cat.Best(ModelModality.Vision));
    }

    // ---- GGUF / engine honesty ---------------------------------------------

    // Bonsai 2 27B is catalogued so the app can SAY it exists, and gated so it
    // never offers a download that cannot load. Measured from the GGUF header
    // 2026-09-25: architecture "qwen35", 5.95 GB, PTQ1_0 (1.75 bits/weight,
    // 32 weights per 7-byte block). The engine is llama.cpp and this device
    // ships MNN, so the honest verdict is incompatible — not absent.
    [Fact]
    public void Bonsai_is_catalogued_but_gated_off_an_MNN_only_device()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("Ternary-Bonsai-2-27B", "1.0", "PTQ1_0")
        {
            Engine = ModelEngine.LlamaCpp, Modality = ModelModality.Chat,
            TotalBytes = 5_946_648_928, MinRamGb = 7.5, MinStorageGb = 6.0, QualityRank = 16,
        });

        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 32, storageGb: 200));

        // Present in the catalogue...
        Assert.Contains(cat.All(), m => m.Name == "Ternary-Bonsai-2-27B");
        // ...and never selected, however much RAM the device has.
        Assert.Null(cat.Best(ModelModality.Chat));
    }

    // ---- form-factor ceiling ------------------------------------------------
    // Phones and tablets are the form factors we cater for, so a pack above 8 GB is
    // not shippable however roomy the device in hand happens to be. These pin that
    // as a PRODUCT rule: the gate is judged on the weight, never on the device.

    const long GB = 1_000_000_000;

    [Fact]
    public void A_model_over_the_budget_is_refused_however_much_is_free_right_now()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("too-big", totalBytes: 22_800_000_000));   // Qwen3.6-35B-A3B
        // THE POINT: a 32 GB handset with 64 GB of RAM and a terabyte reported free.
        // The free figure is deliberately absurd, because free space is NOT what the
        // budget is computed from — 30% of the 32 GB the device actually has is
        // 9.6 GB, and a 22.8 GB pack does not fit inside it no matter what a
        // momentary free-space reading claims.
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 64, storageGb: 1000, storageTotalGb: 32));
        Assert.Null(cat.Best(ModelModality.Chat));
        Assert.Contains(cat.All(), m => m.Name == "too-big");       // catalogued, just not offered
    }

    [Fact]
    public void The_packs_we_actually_run_clear_the_ceiling()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("bonsai-ptq1", qualityRank: 9, totalBytes: 5_946_648_928));  // 5.95 GB
        cat.Upsert(Entry("bonsai-pq2",  qualityRank: 8, totalBytes: 7_210_000_000));  // 7.21 GB
        cat.Upsert(Entry("moe-35b", qualityRank: 99, totalBytes: 22_800_000_000));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 16, storageGb: 200));
        // the oversized one outranks everything and still must not win
        Assert.Equal("bonsai-ptq1", cat.Best(ModelModality.Chat)!.Name);
    }

    [Fact]
    public void A_model_exactly_on_the_ceiling_is_allowed()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("on-the-line", totalBytes: (long)DeviceModelAssessor.BudgetBytesFor(Probe(64))));
        // RAM deliberately far past the ceiling. Without mmap EffectiveMinRamGb rises
        // to the full weight, so a probe sized near the ceiling has the RAM gate
        // refusing the model before the ceiling is ever consulted — the test would
        // then pass or fail for the wrong reason. This one is only about the boundary
        // being inclusive, so nothing else is allowed to be the binding constraint.
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 64, storageGb: 200));
        Assert.Equal("on-the-line", cat.Best(ModelModality.Chat)!.Name);   // ceiling is a max, not a limit below
    }

    [Fact]
    public void A_host_can_lift_the_ceiling_deliberately()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("big", totalBytes: 40 * GB));
        // desktop/server hosts are not phones; the rule is injectable so lifting it
        // is a decision at a call site rather than an accident of a roomy device.
        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: false, maxModelBytes: 64d * GB)
            .Assess(Probe(ramGb: 128, storageGb: 2000));
        Assert.Equal("big", cat.Best(ModelModality.Chat)!.Name);
    }

    [Fact]
    public void The_experiment_hook_still_bypasses_the_ceiling()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("oversized", totalBytes: 22_800_000_000));
        try
        {
            // proving an oversized pack's runtime path on a real phone is the one
            // case allowed to ignore the product rule
            DeviceModelAssessor.ExperimentForceCompatibleId = "oversized";
            DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 4, storageGb: 200));
            Assert.Equal("oversized", cat.Best(ModelModality.Chat)!.Name);
        }
        finally
        {
            DeviceModelAssessor.ExperimentForceCompatibleId = null;
        }
    }

    [Fact]
    public void A_zero_TotalBytes_entry_is_not_refused_by_the_ceiling()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("unknown-size", totalBytes: 0));   // 0 = unsized, like the storage gate
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Equal("unknown-size", cat.Best(ModelModality.Chat)!.Name);
    }

    [Fact]
    public void The_budget_scales_with_the_device_instead_of_being_one_number()
    {
        // THE WHOLE REASON A SHARE REPLACED A FIXED CEILING, shown against the real
        // catalogue. A number in gigabytes means something different on every handset:
        // 20 GB is a sixth of a 128 GB phone and two thirds of a 32 GB one, so one
        // figure is wrong nearly everywhere. A share is right by construction, and
        // what it costs is that the offering now DIFFERS BY DEVICE — which is correct,
        // and is the thing worth pinning.
        using var registry = new ModelRegistryService();

        string[] Refused(double totalGb)
        {
            var budget = DeviceModelAssessor.BudgetBytesFor(Probe(8, storageTotalGb: totalGb));
            return registry.AllModels
                .Where(m => m.TotalBytes > budget)
                .Select(m => m.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
        }

        // 128 GB handset -> 38.4 GB. Everything catalogued fits, including the 22.8 GB
        // MoE. A big phone is not the device this rule is protecting.
        Assert.Empty(Refused(128));

        // 64 GB -> 19.2 GB. The 35B MoE goes; the 30B at 17.75 still fits.
        Assert.Equal(new[] { "Qwen3.6-35B-A3B-MNN" }, Refused(64));

        // 32 GB -> 9.6 GB, the cheap handset this is really for. Both MoE bundles go
        // and so does the dense 14B at 9.44... which FITS, by 160 MB. That is the
        // share doing its job rather than a round number doing it by luck.
        Assert.Equal(
            new[] { "Qwen3-30B-A3B-MNN", "Qwen3.6-35B-A3B-MNN" },
            Refused(32));

        // The packs actually in hand must survive the cheap handset, or the work of
        // fetching and verifying them bought nothing.
        var tightBudget = DeviceModelAssessor.BudgetBytesFor(Probe(8, storageTotalGb: 32));
        foreach (var name in new[] { "Ternary-Bonsai-2-27B", "Qwen-Image-2.1-UC" })
        {
            var m = registry.AllModels.Single(x => x.Name == name);
            Assert.True(m.TotalBytes <= tightBudget,
                $"{name} is {m.TotalBytes / 1e9:F2} GB, over a 32 GB handset's {tightBudget / 1e9:F1} GB budget");
        }
    }

    [Fact]
    public void A_device_that_does_not_report_its_disk_falls_back_rather_than_guessing()
    {
        // Only a head can read total size on Android; a desktop build or an un-wired
        // head reports 0. Inferring it from FREE space would reintroduce the drift the
        // share exists to remove — a budget computed from a momentarily empty disk is
        // the fixed-ceiling problem wearing a different hat.
        var unknown = Probe(8, storageTotalGb: 0);
        Assert.Equal(DeviceModelAssessor.FallbackMaxBytes, DeviceModelAssessor.BudgetBytesFor(unknown));

        // and a device that DOES report it never uses the fallback
        Assert.Equal(128e9 * DeviceModelAssessor.StorageShareOfDevice,
                     DeviceModelAssessor.BudgetBytesFor(Probe(8, storageTotalGb: 128)));
    }

    // ---- quantisation the loaded backend can read -----------------------------
    // "Do we ship llama.cpp" and "can THIS llama.cpp open THIS pack" are different
    // questions, and only the second keeps Bonsai off a stock build.

    [Fact]
    public void Stock_llama_cpp_reads_the_ordinary_quants_and_not_the_bonsai_ones()
    {
        var stock = LlamaQuantSupport.Stock;
        foreach (var q in new[] { "Q8_0", "Q6_K", "Q4_K_M", "Q2_K", "F16", "BF16" })
            Assert.True(stock.Reads(q), $"stock should read {q}");

        // The trap this table exists for: upstream HAS ternary types, and they are
        // NOT Bonsai's. TQ1_0 is a 256-element superblock at 1.6875 bpw; PTQ1_0 is a
        // 128-element, 28-byte block with its own fp16 scale and a Hadamard rotation
        // folded into the weights. Alike names, incompatible bytes.
        Assert.True(stock.Reads("TQ1_0"));
        Assert.False(stock.Reads("PTQ1_0"));
        Assert.False(stock.Reads("PQ2_0"));
    }

    [Fact]
    public void The_prism_fork_is_a_superset_of_stock()
    {
        // Why shipping the fork costs nothing in coverage: everything stock opens,
        // it opens. If that ever stops being true, choosing a backend becomes a
        // trade-off rather than a free upgrade, and this should fail first.
        Assert.All(LlamaQuantSupport.Stock.Quants,
            q => Assert.True(LlamaQuantSupport.Prism.Reads(q), $"prism must still read {q}"));
        Assert.True(LlamaQuantSupport.Prism.Reads("PTQ1_0"));
        Assert.True(LlamaQuantSupport.Prism.Reads("PQ2_0"));
    }

    [Fact]
    public void With_no_native_bridge_nothing_GGUF_can_be_read()
    {
        // The honest default. Until the native library is built there is no backend,
        // so every GGUF row is refused rather than offered and failed at load.
        if (LlamaGenerator.IsAvailable) return;   // a built bridge is tested by the rows below
        Assert.Null(LlamaQuantSupport.Loaded);
        Assert.False(LlamaQuantSupport.CanRead("Q8_0"));
        Assert.False(LlamaQuantSupport.CanRead("PTQ1_0"));
    }

    [Fact]
    public void An_MNN_quantisation_string_is_never_refused_by_the_quant_gate()
    {
        // MNN rows carry values like "MNN-Q4" that say nothing about GGUF. The ENGINE
        // gate is what keeps them away from llama.cpp; this predicate must not be the
        // thing that removes them, or every MNN model disappears the day a bridge
        // loads. Guards the "nobody claims it, so leave it alone" branch.
        using var cat = NewCatalog();
        cat.Upsert(Entry("mnn-row"));                       // Engine.Mnn, "MNN-Q4"
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 8));
        Assert.Equal("mnn-row", cat.Best(ModelModality.Chat)!.Name);
    }

    [Fact]
    public void A_GGUF_row_is_refused_when_the_loaded_backend_cannot_read_its_quantisation()
    {
        // The whole point, stated as catalogue behaviour: Bonsai is PTQ1_0, so even a
        // device that ships llama.cpp, has the RAM and clears the ceiling must not be
        // offered it unless the loaded build reads PTQ1_0.
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("bonsai", "1.0", "PTQ1_0")
        {
            Engine = ModelEngine.LlamaCpp, Modality = ModelModality.Chat,
            TotalBytes = 5_946_648_928, MinRamGb = 7.5, MinStorageGb = 6.0, QualityRank = 16,
        });

        // engine deliberately declared shipped, so ONLY the quant gate can refuse it
        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn, ModelEngine.LlamaCpp }, mmapAllowed: false)
            .Assess(Probe(ramGb: 16, storageGb: 200));

        var expected = LlamaQuantSupport.Loaded?.Reads("PTQ1_0") == true;
        Assert.Equal(expected, cat.Best(ModelModality.Chat) is not null);
    }

    // ---- the reference device -------------------------------------------------
    // Huawei P30 Lite (MAR-LX1M), the floor this product supports: measured at
    // 3.6 GB RAM (3,776,516 kB), 1.19 GB available, 115.9 GB storage with 29.8 GB
    // free, Kirin 710, no GPU, EMUI, no GMS. RAM and availability read off the
    // device on 2026-09-28; the doc's "38 GB" was free space that day, not size.
    // docs/HARDWARE_FINDINGS_HUAWEI_P30.md. Anything below it is told to upgrade;
    // everything at or above it must stay multimodal.

    static DeviceProbe P30() => new(
            RamAvailableBytes: 1_400_000_000,      // measured free, not total
            StorageFreeBytes:  20_000_000_000,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online)
        { RamTotalBytes = 3_776_516_000, StorageTotalBytes = 115_886_522_368 };

    [Fact]
    public void The_P30_can_reach_a_3B_even_though_the_fast_model_stays_the_default()
    {
        // WHAT THIS GUARDS. Qwen2.5-3B declares 3.1 GB and weighs 2.37 GB, so without
        // mmap the gate computed max(3.1, 2.37) = 3.1 GB and REFUSED it outright
        // against ~1.19 GB usable. That model has been proven to load and generate on
        // this exact phone at ~800 MB resident. The capability was real and
        // unreachable — this repo's signature defect, arrived at from the gate's side.
        //
        // It is now COMPATIBLE: offerable, and a person can choose it. It is not the
        // DEFAULT, and that is also right rather than a compromise — its measured
        // first token on this phone is 32.8 s, so SlowLoadRankPenalty puts it beneath
        // anything that answers quickly. Reachable and not silently chosen are
        // different claims, and only the first one was broken.
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("qwen2.5-3b", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Chat,
            TotalBytes = 2_370_000_000, MinRamGb = 3.1, MinStorageGb = 2.4, QualityRank = 9,
            // The measurement that makes this reachable: ~0.8 GB resident on a P30
            // Lite with weight-mmap and a single thread, 2026-09-22. Without it the
            // model stays on its 3.1 GB eager figure and is refused, which is the
            // correct answer for any model nobody has run.
            MmapResidentGb = 0.8,
        });
        cat.Upsert(Entry("qwen3.5-0.8b", qualityRank: 7, minRamGb: 0.8,
                         minStorageGb: 0.6, totalBytes: 550_000_000));

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true).Assess(P30());

        var reachable = cat.Compatible(ModelModality.Chat).Select(m => m.Name).ToList();
        Assert.Contains("qwen2.5-3b", reachable);          // the fix: it exists for this phone
        Assert.Equal("qwen3.5-0.8b", cat.Best(ModelModality.Chat)!.Name);   // and fast still wins
    }

    [Fact]
    public void Without_mmap_the_3B_is_not_reachable_at_all_on_the_P30()
    {
        // The other half of the claim, so the test above cannot pass for an unrelated
        // reason: with mmap off the same entry is refused outright rather than merely
        // ranked low. That is the state the reference device was actually in.
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("qwen2.5-3b", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Chat,
            TotalBytes = 2_370_000_000, MinRamGb = 3.1, MinStorageGb = 2.4, QualityRank = 9,
        });

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: false).Assess(P30());

        Assert.Empty(cat.Compatible(ModelModality.Chat));
    }

    [Fact]
    public void The_P30_stays_multimodal_and_disk_is_not_what_limits_it()
    {
        // The product goal stated as a test: every modality the device can serve, it
        // DOES serve. On this phone RAM is the binding constraint and disk is not
        // close — the whole set is about 3.6 GB against a budget of 30% of 115.9 GB =
        // 34.8 GB, so a fuller set is affordable on disk and refused only on memory.
        using var cat = NewCatalog();
        cat.Upsert(Entry("chat",   qualityRank: 7, minRamGb: 0.8, minStorageGb: 0.6, totalBytes: 550_000_000));
        // A SMALL VLM, because no vision bundle has a measured mmap figure yet and an
        // unmeasured one gets no discount. This is what the reference device really
        // gets today: vision is served, by the model that fits without paging.
        cat.Upsert(new ModelEntry("vision", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Vision,
            TotalBytes = 310_000_000, MinRamGb = 0.5, MinStorageGb = 0.4, QualityRank = 4,
        });
        cat.Upsert(Entry("asr", modality: ModelModality.Asr, minRamGb: 0.4, minStorageGb: 0.1, totalBytes: 80_000_000));
        cat.Upsert(Entry("tts", modality: ModelModality.Tts, minRamGb: 0.5, minStorageGb: 0.2, totalBytes: 110_000_000));
        cat.Upsert(Entry("wake", modality: ModelModality.WakeWord, minRamGb: 0.1, minStorageGb: 0.1, totalBytes: 10_000_000));

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true).Assess(P30());

        foreach (var m in new[] { ModelModality.Chat, ModelModality.Vision,
                                  ModelModality.Asr, ModelModality.Tts, ModelModality.WakeWord })
            Assert.True(cat.Best(m) is not null, $"{m} has nothing on the reference device");

        // and the budget is nowhere near binding
        var used = new[] { "chat", "vision", "asr", "tts", "wake" }
            .Sum(n => cat.All().Single(e => e.Name == n).TotalBytes);
        var budget = DeviceModelAssessor.BudgetBytesFor(P30());
        Assert.True(used < budget / 2,
            $"the set is {used / 1e9:F2} GB of a {budget / 1e9:F2} GB budget — if this ever tightens, disk has become the constraint and the allocator story changes");
    }

    // ---- the phone has to go on being a phone ---------------------------------

    [Fact]
    public void Ten_gigabytes_of_free_storage_survives_whatever_we_install()
    {
        // Photographs, messages, an OS update, the apps somebody actually bought the
        // device for. A model that takes the last of the disk has made the handset
        // worse at its job in exchange for being clever.
        //
        // 40 GB total, 12 GB free: the share allows 12 GB, the reserve allows 2.
        var tight = Probe(ramGb: 4, storageGb: 12, storageTotalGb: 40);
        Assert.Equal(2e9, DeviceModelAssessor.BudgetBytesFor(tight), 1e6);

        // The reference P30: 115.9 GB total, ~29 GB free. Share 34.8, reserve 19.
        // The reserve binds, and the phone keeps its 10 GB.
        var p30 = Probe(ramGb: 4, storageGb: 29, storageTotalGb: 115.9);
        Assert.Equal(19e9, DeviceModelAssessor.BudgetBytesFor(p30), 1e8);

        // A roomy device is limited by the share instead, since it has plenty spare.
        var roomy = Probe(ramGb: 8, storageGb: 200, storageTotalGb: 256);
        Assert.Equal(256e9 * DeviceModelAssessor.StorageShareOfDevice,
                     DeviceModelAssessor.BudgetBytesFor(roomy), 1e6);
    }

    [Fact]
    public void A_nearly_full_phone_is_offered_nothing_rather_than_its_last_gigabyte()
    {
        // The honest end of the rule. With 6 GB free there is no room that can be
        // taken without dropping below the reserve, so the budget is zero and every
        // model is refused — including ones that would "fit" the free space. Telling
        // somebody to clear space is better than silently filling it.
        var nearlyFull = Probe(ramGb: 4, storageGb: 6, storageTotalGb: 64);
        Assert.Equal(0.0, DeviceModelAssessor.BudgetBytesFor(nearlyFull));

        using var cat = NewCatalog();
        cat.Upsert(Entry("small", totalBytes: 450_000_000));   // 0.45 GB, would fit 6 GB free
        DeviceModelAssessor.MnnOnly(cat).Assess(nearlyFull);
        Assert.Null(cat.Best(ModelModality.Chat));
    }

    [Fact]
    public void An_unreadable_free_space_figure_does_not_invent_a_reserve()
    {
        // 0 free means "not measured", the same convention the storage gate uses. A
        // reserve subtracted from a reading we never got would refuse every model on
        // every desktop, where only a platform head can report these numbers.
        var unknownFree = Probe(ramGb: 8, storageGb: 0, storageTotalGb: 128);
        Assert.Equal(128e9 * DeviceModelAssessor.StorageShareOfDevice,
                     DeviceModelAssessor.BudgetBytesFor(unknownFree), 1e6);
    }

    [Fact]
    public void No_catalogued_model_claims_a_paging_discount_nobody_measured()
    {
        // A MEASURED FIGURE IS A CLAIM ABOUT A PHONE, so adding one should be a
        // deliberate act by somebody who watched the number, not a line that drifts in
        // with a catalogue update. This fails the moment an entry gains one — which is
        // the point: read the remark on ModelEntry.MmapResidentGb, confirm a person
        // really ran it on a device, then update this list.
        //
        // It is empty today and that is not an oversight. 0.8 GB was briefly recorded
        // for Qwen2.5-3B from a note rather than a run anybody here watched, and its
        // only effect was to offer a 2.37 GB model to a 1.19 GB phone. An unverified
        // measurement is worse than none, because it wears the authority of one.
        using var registry = new ModelRegistryService();
        var claimed = registry.AllModels
            .Where(m => m.MmapResidentGb is > 0)
            .Select(m => $"{m.Name} ({m.MmapResidentGb} GB)")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(claimed.Count == 0,
            "these entries claim a measured memory-mapped figure; confirm somebody ran "
            + "each on a device and then update this test: "
            + string.Join(", ", claimed));
    }

    [Fact]
    public void ShippedEngines_always_has_MNN_and_adds_llama_only_when_the_native_library_loaded()
    {
        var engines = DeviceModelAssessor.ShippedEngines();
        Assert.Contains(ModelEngine.Mnn, engines);
        // The bridge is a build fact probed at runtime, so this asserts the
        // CORRESPONDENCE rather than a constant: whatever LlamaGenerator reports is
        // what the assessor must gate on. Green before the native library is built
        // and green after, which is the point — flipping GGUF rows to compatible
        // must need no catalogue edit and no app release.
        Assert.Equal(LlamaGenerator.IsAvailable, engines.Contains(ModelEngine.LlamaCpp));
    }
}
