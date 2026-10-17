// CatalogueHousekeepingTests.cs
//
// The reclaim policy: shed a model a better one on disk has superseded, but never
// strand a device by removing the only thing that fits, and never sacrifice a
// genuinely-smaller fallback that a much-larger better model does not replace.
//
// SINCE 2026-10-16, WHEN IT WAS FIRST SWITCHED ON IN THE SERVICE: nothing is removed
// in favour of a model that has not LOADED on this device, a retired model is dead
// weight once one above the floor has, chat models only, and never the model in use.
// The P30 is why: its OS killed Qwen3.5-0.8B mid-load on 2026-10-10, and deleting the
// 0.6B the moment the 0.8B finished downloading could have left it with no brain.

using System;
using System.Collections.Generic;
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
                          ModelModality modality = ModelModality.Chat, bool retired = false)
        => new(name, "1.0", "MNN-Q4")
        {
            Engine      = ModelEngine.Mnn,
            Modality    = modality,
            TotalBytes  = totalBytes,
            MinRamGb    = 0.5,
            QualityRank = quality,
            Retired     = retired,
        };

    static void Installed(SqliteModelCatalog cat, params string[] ids)
    {
        foreach (var id in ids) cat.SetInstalled(id, true);
    }

    [Fact]
    public void Sheds_the_inferior_twin_once_the_better_one_has_loaded_the_P30_case()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        Installed(cat, "Qwen3-0.6B", "Qwen3.5-0.8B");
        // Neither is compatible (RAM-starved P30) - but the better one has run here.
        cat.RecordLoad("Qwen3.5-0.8B");

        var plan = CatalogueHousekeeping.Plan(cat).Select(r => r.Id).ToList();

        Assert.Contains("Qwen3-0.6B", plan);          // inferior, similar size -> reclaim
        Assert.DoesNotContain("Qwen3.5-0.8B", plan);  // the keeper
    }

    [Fact]
    public void Removes_nothing_in_favour_of_a_model_that_has_never_loaded_here()
    {
        // THE CASE THIS RULE WAS ADDED FOR. The 0.8B has just finished downloading
        // and has not run on this phone yet; until it does, the 0.6B is the brain.
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        Installed(cat, "Qwen3-0.6B", "Qwen3.5-0.8B");

        Assert.Empty(CatalogueHousekeeping.Plan(cat));
    }

    [Fact]
    public void A_refused_model_replaces_nothing_even_though_it_once_loaded()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        Installed(cat, "Qwen3-0.6B", "Qwen3.5-0.8B");
        cat.RecordLoad("Qwen3.5-0.8B");
        cat.Refuse("Qwen3.5-0.8B", CrashVerdict.Reason);

        Assert.Empty(CatalogueHousekeeping.Plan(cat));
    }

    [Fact]
    public void A_retired_model_is_dead_weight_once_one_above_the_floor_has_loaded_whatever_its_size()
    {
        // "Nothing less than Qwen3.5 is the LLM": a retired model is never the brain
        // again, so it is not a fallback, and the size rule that protects fallbacks
        // does not protect it.
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-14B", quality: 14, totalBytes: 9_440_000_000, retired: true));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 8, totalBytes: 550_000_000));
        Installed(cat, "Qwen3-14B", "Qwen3.5-0.8B");
        cat.RecordLoad("Qwen3.5-0.8B");

        var plan = CatalogueHousekeeping.Plan(cat);

        var item = Assert.Single(plan);
        Assert.Equal("Qwen3-14B", item.Id);
        Assert.Contains("retired", item.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_retired_model_stays_while_nothing_above_the_floor_has_loaded()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000, retired: true));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 8, totalBytes: 550_000_000));
        Installed(cat, "Qwen3-0.6B", "Qwen3.5-0.8B");

        Assert.Empty(CatalogueHousekeeping.Plan(cat));
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
            cat.RecordLoad(id);
        }

        var plan = CatalogueHousekeeping.Plan(cat);

        // Each better model is >2x the size of the next one down, so every rung is
        // a real fallback (the 2 B insures the un-soaked 35 B). Nothing reclaimed.
        Assert.Empty(plan);
    }

    [Fact]
    public void A_ladder_of_close_steps_cannot_remove_the_fallback_the_size_rule_keeps()
    {
        // 1.0 -> 1.9 -> 3.7 GB: each step is within 2x, but what survives would be
        // 3.7x the smallest. A model that is itself being removed replaces nothing.
        using var cat = NewCatalog();
        cat.Upsert(Row("A", quality: 7, totalBytes: 1_000_000_000));
        cat.Upsert(Row("K", quality: 8, totalBytes: 1_900_000_000));
        cat.Upsert(Row("K2", quality: 9, totalBytes: 3_700_000_000));
        foreach (var id in new[] { "A", "K", "K2" })
        {
            cat.SetInstalled(id, true);
            cat.SetAssessment(id, compatible: true, rank: 1);
            cat.RecordLoad(id);
        }

        var plan = CatalogueHousekeeping.Plan(cat).Select(r => r.Id).ToList();

        Assert.Equal(new[] { "K" }, plan);   // K goes to K2; A stays, its fallback role intact
    }

    [Fact]
    public void Never_removes_a_model_that_runs_here_for_a_better_one_that_does_not()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("small", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("big", quality: 7, totalBytes: 550_000_000));
        Installed(cat, "small", "big");
        cat.SetAssessment("small", compatible: true, rank: 1);   // the one that fits
        cat.SetAssessment("big", compatible: false, rank: 0);    // better, but won't run here
        cat.RecordLoad("big");

        var plan = CatalogueHousekeeping.Plan(cat);

        Assert.Empty(plan);   // keep the working model even though a better one exists
    }

    [Fact]
    public void Voices_are_not_this_rules_to_judge()
    {
        // The same arithmetic would delete the medium English voice for the high
        // one - similar size, higher rank - and a phone may use the medium one on
        // purpose, for speed.
        using var cat = NewCatalog();
        cat.Upsert(Row("Piper-en_US-lessac-medium", quality: 5, totalBytes: 63_000_000, ModelModality.Tts));
        cat.Upsert(Row("Piper-en_US-lessac-high", quality: 6, totalBytes: 114_000_000, ModelModality.Tts));
        Installed(cat, "Piper-en_US-lessac-medium", "Piper-en_US-lessac-high");
        cat.RecordLoad("Piper-en_US-lessac-high");

        Assert.Empty(CatalogueHousekeeping.Plan(cat));
    }

    [Fact]
    public void The_model_in_use_is_never_reclaimed()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        Installed(cat, "Qwen3-0.6B", "Qwen3.5-0.8B");
        cat.RecordLoad("Qwen3.5-0.8B");

        var plan = CatalogueHousekeeping.Plan(cat, keep: id => id == "Qwen3-0.6B");

        Assert.Empty(plan);
    }

    [Fact]
    public void A_sole_model_is_never_reclaimed()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("only", quality: 6, totalBytes: 450_000_000));
        cat.SetInstalled("only", true);
        cat.RecordLoad("only");
        Assert.Empty(CatalogueHousekeeping.Plan(cat));
    }

    [Fact]
    public void An_uninstalled_inferior_model_is_not_in_the_plan()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        cat.SetInstalled("Qwen3.5-0.8B", true);   // only the better one is on disk
        cat.RecordLoad("Qwen3.5-0.8B");
        // 0.6B is catalogued but not installed -> nothing to reclaim
        Assert.Empty(CatalogueHousekeeping.Plan(cat));
    }

    [Fact]
    public void Reclaim_deletes_the_complete_folder_clears_the_flag_and_says_so()
    {
        using var cat = NewCatalog();
        cat.Upsert(Row("Qwen3-0.6B", quality: 6, totalBytes: 450_000_000));
        cat.Upsert(Row("Qwen3.5-0.8B", quality: 7, totalBytes: 550_000_000));
        Installed(cat, "Qwen3-0.6B", "Qwen3.5-0.8B");
        cat.RecordLoad("Qwen3.5-0.8B");
        var said = new List<string>();

        var dir = Path.Combine(Path.GetTempPath(), "circleai-hk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Qwen3-0.6B"));
        File.WriteAllText(Path.Combine(dir, "Qwen3-0.6B", "installed.json"), "{}");
        Directory.CreateDirectory(Path.Combine(dir, "Qwen3.5-0.8B"));
        File.WriteAllText(Path.Combine(dir, "Qwen3.5-0.8B", "installed.json"), "{}");
        try
        {
            var freed = CatalogueHousekeeping.Reclaim(cat, dir, say: said.Add);

            Assert.Equal(1, freed);
            Assert.False(Directory.Exists(Path.Combine(dir, "Qwen3-0.6B")));   // gone
            Assert.False(cat.IsInstalled("Qwen3-0.6B"));                       // flag cleared
            Assert.True(Directory.Exists(Path.Combine(dir, "Qwen3.5-0.8B")));  // keeper stays
            Assert.True(cat.IsInstalled("Qwen3.5-0.8B"));

            // SAID, because ILogger reaches nothing on Android.
            Assert.Contains(said, s => s.StartsWith("reclaimed Qwen3-0.6B", StringComparison.Ordinal));
            Assert.Contains("planned=1 reclaimed=1", said);
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
        Installed(cat, "Qwen3-0.6B", "Qwen3.5-0.8B");
        cat.RecordLoad("Qwen3.5-0.8B");   // so the 0.6B IS planned, and this tests the marker
        var said = new List<string>();

        var dir = Path.Combine(Path.GetTempPath(), "circleai-hk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Qwen3-0.6B"));   // folder, but NO installed.json
        try
        {
            var freed = CatalogueHousekeeping.Reclaim(cat, dir, say: said.Add);
            Assert.Equal(0, freed);
            Assert.True(Directory.Exists(Path.Combine(dir, "Qwen3-0.6B")));   // not a complete download -> left
            Assert.Contains(said, s => s.StartsWith("left Qwen3-0.6B", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Reclaim_says_its_count_even_when_it_is_zero()
    {
        // A sweep that says nothing reads exactly like a sweep that never ran - the
        // state the service was in until today.
        using var cat = NewCatalog();
        var said = new List<string>();
        var dir = Path.Combine(Path.GetTempPath(), "circleai-hk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(0, CatalogueHousekeeping.Reclaim(cat, dir, say: said.Add));
            Assert.Contains("planned=0 reclaimed=0", said);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
