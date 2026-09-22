// CatalogueHousekeepingTests.cs
//
// The reclaim policy: shed a model a better one on disk has superseded, but never
// strand a device by removing the only thing that fits, and never sacrifice a
// genuinely-smaller fallback that a much-larger better model does not replace.

using System;
using System.IO;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public class CatalogueHousekeepingTests
{
    static SqliteModelCatalog NewCatalog() => new("Data Source=:memory:");

    static ModelEntry Row(string name, int quality, long totalBytes,
                          ModelModality modality = ModelModality.Chat)
        => new(name, "1.0", "MNN-Q4")
        {
            Engine      = ModelEngine.Mnn,
            Modality    = modality,
            TotalBytes  = totalBytes,
            MinRamGb    = 0.5,
            QualityRank = quality,
        };

    [Fact]
    public void Sheds_the_inferior_twin_the_P30_case()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        cat.SetInstalled("Qwen3-0.6B", true);
        cat.SetInstalled("Qwen3.5-0.8B", true);
        // Neither is compatible (RAM-starved P30) — but nothing usable is lost by
        // keeping the better of the two.

        var plan = CatalogueHousekeeping.Plan(cat).Select(r => r.Id).ToList();

        Assert.Contains("Qwen3-0.6B", plan);          // inferior, similar size -> reclaim
        Assert.DoesNotContain("Qwen3.5-0.8B", plan);  // the keeper
    }

    [Fact]
    public void Keeps_a_much_smaller_fallback_the_Redmi_case()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-2B", quality: 9, totalBytes: 1_400_000_000));
        cat.Upsert(Row("Qwen3.6-35B-A3B", quality: 16, totalBytes: 22_000_000_000));
        foreach (var id in new[] { "Qwen3-0.6B", "Qwen3.5-2B", "Qwen3.6-35B-A3B" })
        {
            cat.SetInstalled(id, true);
            cat.SetAssessment(id, compatible: true, rank: 1);
        }

        var plan = CatalogueHousekeeping.Plan(cat);

        // Each better model is >2x the size of the next one down, so every rung is
        // a real fallback (the 2 B insures the un-soaked 35 B). Nothing reclaimed.
        Assert.Empty(plan);
    }

    [Fact]
    public void Never_removes_a_model_that_runs_here_for_a_better_one_that_does_not()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("small", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("big", quality: 7, totalBytes: 550_000_000));
        cat.SetInstalled("small", true);
        cat.SetInstalled("big", true);
        cat.SetAssessment("small", compatible: true, rank: 1);   // the one that fits
        cat.SetAssessment("big", compatible: false, rank: 0);    // better, but won't run here

        var plan = CatalogueHousekeeping.Plan(cat);

        Assert.Empty(plan);   // keep the working model even though a better one exists
    }

    [Fact]
    public void A_sole_model_is_never_reclaimed()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("only", quality: 6, totalBytes: 450_000_000));
        cat.SetInstalled("only", true);
        Assert.Empty(CatalogueHousekeeping.Plan(cat));
    }

    [Fact]
    public void An_uninstalled_inferior_model_is_not_in_the_plan()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        cat.SetInstalled("Qwen3.5-0.8B", true);   // only the better one is on disk
        // 0.6B is catalogued but not installed -> nothing to reclaim
        Assert.Empty(CatalogueHousekeeping.Plan(cat));
    }

    [Fact]
    public void Reclaim_deletes_the_complete_folder_and_clears_the_flag()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        cat.SetInstalled("Qwen3-0.6B", true);
        cat.SetInstalled("Qwen3.5-0.8B", true);

        var dir = Path.Combine(Path.GetTempPath(), "circleai-hk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Qwen3-0.6B"));
        File.WriteAllText(Path.Combine(dir, "Qwen3-0.6B", "installed.json"), "{}");
        Directory.CreateDirectory(Path.Combine(dir, "Qwen3.5-0.8B"));
        File.WriteAllText(Path.Combine(dir, "Qwen3.5-0.8B", "installed.json"), "{}");
        try
        {
            var freed = CatalogueHousekeeping.Reclaim(cat, dir);

            Assert.Equal(1, freed);
            Assert.False(Directory.Exists(Path.Combine(dir, "Qwen3-0.6B")));   // gone
            Assert.False(cat.IsInstalled("Qwen3-0.6B"));                       // flag cleared
            Assert.True(Directory.Exists(Path.Combine(dir, "Qwen3.5-0.8B")));  // keeper stays
            Assert.True(cat.IsInstalled("Qwen3.5-0.8B"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_partial_download_without_its_marker_is_left_alone()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        cat.SetInstalled("Qwen3-0.6B", true);
        cat.SetInstalled("Qwen3.5-0.8B", true);

        var dir = Path.Combine(Path.GetTempPath(), "circleai-hk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Qwen3-0.6B"));   // folder, but NO installed.json
        try
        {
            var freed = CatalogueHousekeeping.Reclaim(cat, dir);
            Assert.Equal(0, freed);
            Assert.True(Directory.Exists(Path.Combine(dir, "Qwen3-0.6B")));   // not a complete download -> left
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
