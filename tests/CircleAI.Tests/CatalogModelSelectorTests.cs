// CatalogModelSelectorTests.cs
//
// DB-driven selection: the selector reads the catalogue's pre-computed
// `compatible`/`rank` columns rather than sorting a registry on every call. The
// point the plan verifies — "DB-driven BestFit returns the top compatible row" —
// is the first test; the rest cover the capability gate, the quality floor, the
// nothing-compatible fallback, install-awareness, vision modality, and the
// fallback chain.

using System;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class CatalogModelSelectorTests
{
    static (SqliteModelCatalog Cat, CatalogModelSelector Sel) Wire()
    {
        var cat = new SqliteModelCatalog("Data Source=:memory:");
        return (cat, new CatalogModelSelector(cat));
    }

    static DeviceProbe Probe(double ramGb = 8)
        => new(
            RamAvailableBytes: (long)(ramGb * 1_000_000_000),
            StorageFreeBytes:  100_000_000_000,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Active,
            Connectivity:      Connectivity.Online)
        { RamTotalBytes = (long)(ramGb * 1_000_000_000) };

    static ModelEntry Entry(
        string name,
        int qualityRank = 6,
        ModelModality modality = ModelModality.Chat,
        string[]? caps = null,
        string? fallback = null)
        => new(name, "1.0", "MNN-Q4")
        {
            Modality        = modality,
            Engine          = ModelEngine.Mnn,
            TotalBytes      = 100_000_000,
            MinRamGb        = 0.6,
            MinStorageGb    = 0.1,
            Capabilities    = caps,
            QualityRank     = qualityRank,
            FallbackModelId = fallback,
        };

    [Fact]
    public void BestFit_returns_the_top_compatible_row()
    {
        var (cat, sel) = Wire();
        using (cat)
        {
            cat.Upsert(Entry("small", qualityRank: 6));
            cat.SetAssessment("small", compatible: true, rank: 6);
            cat.Upsert(Entry("big", qualityRank: 10));
            cat.SetAssessment("big", compatible: true, rank: 10);

            var s = sel.BestFit(Probe(), ChatCapability.None);
            Assert.Equal("big", s.ModelId);
            Assert.Equal(SelectionQuality.Good, s.Quality);
        }
    }

    [Fact]
    public void BestFit_honours_the_requested_capability_even_at_lower_quality()
    {
        var (cat, sel) = Wire();
        using (cat)
        {
            cat.Upsert(Entry("plain", qualityRank: 10, caps: new[] { "Default" }));
            cat.SetAssessment("plain", compatible: true, rank: 10);
            cat.Upsert(Entry("tooly", qualityRank: 6, caps: new[] { "Default", "Tools" }));
            cat.SetAssessment("tooly", compatible: true, rank: 6);

            var s = sel.BestFit(Probe(), ChatCapability.Tools);
            Assert.Equal("tooly", s.ModelId);   // the only one that satisfies Tools
        }
    }

    [Fact]
    public void BestFit_flags_a_winner_below_the_quality_floor()
    {
        var (cat, sel) = Wire();
        using (cat)
        {
            cat.Upsert(Entry("weak", qualityRank: 4));
            cat.SetAssessment("weak", compatible: true, rank: 4);

            var s = sel.BestFit(Probe(), ChatCapability.None, minQualityRank: 8);
            Assert.Equal("weak", s.ModelId);
            Assert.Equal(SelectionQuality.BelowFloor, s.Quality);
        }
    }

    [Fact]
    public void BestFit_returns_the_least_bad_option_flagged_NothingFits_when_none_compatible()
    {
        var (cat, sel) = Wire();
        using (cat)
        {
            cat.Upsert(Entry("x", qualityRank: 6));
            cat.SetAssessment("x", compatible: false, rank: 0);   // e.g. engine not shipped

            var s = sel.BestFit(Probe(), ChatCapability.None);
            Assert.Equal("x", s.ModelId);
            Assert.Equal(SelectionQuality.NothingFits, s.Quality);
        }
    }

    [Fact]
    public void BestFit_throws_when_nothing_even_satisfies_the_capability()
    {
        var (cat, sel) = Wire();
        using (cat)
        {
            // A speech entry only — a chat request has no candidate at all.
            cat.Upsert(Entry("voice", modality: ModelModality.Tts));
            cat.SetAssessment("voice", compatible: true, rank: 5);

            Assert.Throws<InvalidOperationException>(
                () => sel.BestFit(Probe(), ChatCapability.None));
        }
    }

    [Fact]
    public void RequiresDownload_reflects_whether_the_bundle_is_installed()
    {
        var (cat, sel) = Wire();
        using (cat)
        {
            cat.Upsert(Entry("m", qualityRank: 6));
            cat.SetAssessment("m", compatible: true, rank: 6);

            Assert.True(sel.BestFit(Probe(), ChatCapability.None).RequiresDownload);

            cat.SetInstalled("m", true);
            Assert.False(sel.BestFit(Probe(), ChatCapability.None).RequiresDownload);
        }
    }

    [Fact]
    public void A_vision_request_considers_the_vision_modality()
    {
        var (cat, sel) = Wire();
        using (cat)
        {
            cat.Upsert(Entry("vlm", qualityRank: 8, modality: ModelModality.Vision,
                             caps: new[] { "Default", "Vision" }));
            cat.SetAssessment("vlm", compatible: true, rank: 8);

            var s = sel.BestFit(Probe(), ChatCapability.Vision);
            Assert.Equal("vlm", s.ModelId);
        }
    }

    [Fact]
    public void ChainFor_walks_the_fallback_chain()
    {
        var (cat, sel) = Wire();
        using (cat)
        {
            cat.Upsert(Entry("head", fallback: "mid"));
            cat.Upsert(Entry("mid", fallback: "tail"));
            cat.Upsert(Entry("tail"));

            Assert.Equal(new[] { "head", "mid", "tail" }, sel.ChainFor("head").ToArray());
        }
    }

    [Fact]
    public void AllCandidates_lists_the_compatible_chat_rows()
    {
        var (cat, sel) = Wire();
        using (cat)
        {
            cat.Upsert(Entry("ok", qualityRank: 8));
            cat.SetAssessment("ok", compatible: true, rank: 8);
            cat.Upsert(Entry("no", qualityRank: 6));
            cat.SetAssessment("no", compatible: false, rank: 0);

            var ids = sel.AllCandidates(Probe()).Select(c => c.ModelId).ToArray();
            Assert.Equal(new[] { "ok" }, ids);
        }
    }
}
