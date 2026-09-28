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

    static DeviceProbe Probe(double ramGb, double storageGb = 100, double? vramGb = null)
        => new(
            RamAvailableBytes: (long)(ramGb * 1_000_000_000),
            StorageFreeBytes:  (long)(storageGb * 1_000_000_000),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Active,
            Connectivity:      Connectivity.Online)
        { VramGb = vramGb, RamTotalBytes = (long)(ramGb * 1_000_000_000) };

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
    public void A_vision_bundle_that_needs_more_resident_than_it_weighs_gets_mmap()
    {
        using var cat = NewCatalog();
        cat.Upsert(new ModelEntry("qwen2.5-vl-3b", "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn, Modality = ModelModality.Vision,
            TotalBytes = 2_736_200_899, MinRamGb = 3.9, MinStorageGb = 2.6, QualityRank = 9,
        });

        new DeviceModelAssessor(cat, new[] { ModelEngine.Mnn }, mmapAllowed: true)
            .Assess(Probe(ramGb: 4.3));   // ~3.6 GB usable after headroom

        Assert.NotNull(cat.Best(ModelModality.Vision));
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
    public void A_model_over_the_form_factor_ceiling_is_incompatible_on_any_device()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("too-big", totalBytes: 14_230_275_808));   // Qwen-Image 2.1 F16
        // 64 GB of RAM and a terabyte free — the device is not the question.
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 64, storageGb: 1000));
        Assert.Null(cat.Best(ModelModality.Chat));
        Assert.Contains(cat.All(), m => m.Name == "too-big");       // catalogued, just not offered
    }

    [Fact]
    public void The_packs_we_actually_run_clear_the_ceiling()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("bonsai-ptq1", qualityRank: 9, totalBytes: 5_946_648_928));  // 5.95 GB
        cat.Upsert(Entry("bonsai-pq2",  qualityRank: 8, totalBytes: 7_210_000_000));  // 7.21 GB
        cat.Upsert(Entry("qwen-image-f16", qualityRank: 99, totalBytes: 14_230_275_808));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 16, storageGb: 200));
        // the oversized one outranks everything and still must not win
        Assert.Equal("bonsai-ptq1", cat.Best(ModelModality.Chat)!.Name);
    }

    [Fact]
    public void A_model_exactly_on_the_ceiling_is_allowed()
    {
        using var cat = NewCatalog();
        cat.Upsert(Entry("on-the-line", totalBytes: (long)DeviceModelAssessor.FormFactorMaxBytes));
        DeviceModelAssessor.MnnOnly(cat).Assess(Probe(ramGb: 16, storageGb: 200));
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
        cat.Upsert(Entry("oversized", totalBytes: 14_230_275_808));
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
    public void The_ceiling_removes_the_giant_MoE_bundles_from_the_real_catalogue()
    {
        // NOT a rule asserted for its own sake — a CONSEQUENCE kept visible. The
        // form-factor rule makes some catalogued models un-offerable on a default
        // host, where before they were reachable via mmap on a roomy device. Naming
        // them means the list cannot grow silently: add another oversized model and
        // this fails until someone writes it down.
        //
        // WHY THE CEILING IS 10 GB AND NOT 8. At 8 GB this list also contained the
        // dense Qwen3-14B-MNN at 9.44 GB — an ordinary chat model, not a giant, and
        // not what the rule was aimed at. 10 GB keeps the 14B and still refuses the
        // MoE bundles, which are 17.75 GB and 22.80 GB and nowhere near the line.
        using var registry = new ModelRegistryService();
        var giants = registry.AllModels
            .Where(m => m.TotalBytes > DeviceModelAssessor.FormFactorMaxBytes)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "Qwen3-30B-A3B-MNN", "Qwen3.6-35B-A3B-MNN" }, giants);

        // the dense 14B sits between the two candidate ceilings, so it is the model
        // that proves which one is in force
        var dense14b = registry.AllModels.Single(m => m.Name == "Qwen3-14B-MNN");
        Assert.True(dense14b.TotalBytes <= DeviceModelAssessor.FormFactorMaxBytes,
            $"Qwen3-14B is {dense14b.TotalBytes / 1e9:F2} GB and must clear a 10 GB ceiling");

        // and every pack we actually run clears it
        foreach (var name in new[] { "Ternary-Bonsai-2-27B", "Qwen-Image-2.1-UC" })
        {
            var m = registry.AllModels.Single(x => x.Name == name);
            Assert.True(m.TotalBytes <= DeviceModelAssessor.FormFactorMaxBytes,
                $"{name} is {m.TotalBytes / 1e9:F2} GB, over the {DeviceModelAssessor.FormFactorMaxBytes / 1e9:F0} GB ceiling");
        }
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
