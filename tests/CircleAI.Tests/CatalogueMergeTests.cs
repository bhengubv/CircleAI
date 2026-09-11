// CatalogueMergeTests.cs
//
// What a live catalogue may and may not do to the one that shipped.
//
// THE FIRST TEST HERE IS THE ONE THAT MATTERS. Before CatalogueMerge existed,
// ModelRegistryService.AllModels read (_remoteRegistry ?? _embeddedRegistry) - a
// wholesale REPLACEMENT - and the live client queries ModelScope for MNN bundles
// and classifies everything as Chat or Vision. The curated catalogue holds 69
// text-to-speech voices, the Whisper ASR, the "Hey B" wake word and the Open
// JTalk dictionary, all on Hugging Face buckets and none of them in that answer.
//
// So the first refresh that SUCCEEDED would have taken the app's voice, its ears
// and its wake word away, on every device, with nothing in any log to say why.
// It never fired only because nothing called PrimeFromCatalogAsync.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

public class CatalogueMergeTests
{
    // ── The thing that must never happen ────────────────────────────────

    [Fact]
    public void A_refresh_cannot_take_the_voices_the_ears_or_the_wake_word_away()
    {
        var curated = new List<ModelEntry>
        {
            Entry("Whisper-tiny-ggml",   ModelModality.Asr,      77_691_713),
            Entry("KWS-Zipformer-HeyB",  ModelModality.WakeWord,  6_762_672),
            Entry("OpenJTalk-Dic-ja",    ModelModality.Phonemizer, 109_000_000),
            Entry("mms-zul",             ModelModality.Tts,       40_000_000),
            Entry("Qwen3-0.6B-MNN",      ModelModality.Chat,     500_000_000),
        };

        // What a ModelScope MNN query actually comes back with: chat and vision.
        var live = new List<ModelEntry>
        {
            Entry("Qwen3-0.6B-MNN", ModelModality.Chat,   500_000_000),
            Entry("Qwen4-2B-MNN",   ModelModality.Chat, 1_800_000_000),
        };

        var merged = CatalogueMerge.Combine(curated, live);

        foreach (var must in new[]
        {
            "Whisper-tiny-ggml", "KWS-Zipformer-HeyB", "OpenJTalk-Dic-ja", "mms-zul",
        })
            Assert.Contains(merged, m => m.Name == must);

        Assert.Single(merged, m => m.Modality == ModelModality.Asr);
        Assert.Single(merged, m => m.Modality == ModelModality.WakeWord);
        Assert.Single(merged, m => m.Modality == ModelModality.Tts);
    }

    [Fact]
    public void Nothing_curated_is_ever_dropped_however_odd_the_live_answer_is()
    {
        var curated = Enumerable.Range(0, 40)
            .Select(i => Entry($"curated-{i}", ModelModality.Tts, 1_000_000))
            .ToList();

        foreach (var live in new List<ModelEntry>?[]
        {
            null,
            [],
            [Entry("something-new", ModelModality.Chat, 900_000_000)],
        })
        {
            var merged = CatalogueMerge.Combine(curated, live);
            Assert.All(curated, c => Assert.Contains(merged, m => m.Name == c.Name));
        }
    }

    // ── What a refresh is for ───────────────────────────────────────────

    [Fact]
    public void A_model_the_shipped_catalogue_has_never_heard_of_is_added()
    {
        // The whole point: a model published after this APK was built becomes
        // available without a new APK.
        var curated = new List<ModelEntry> { Entry("Qwen3-0.6B-MNN", ModelModality.Chat, 500_000_000) };
        var live = new List<ModelEntry> { Entry("Qwen4-2B-MNN", ModelModality.Chat, 1_800_000_000) };

        var merged = CatalogueMerge.Combine(curated, live);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, m => m.Name == "Qwen4-2B-MNN");
    }

    [Fact]
    public void A_discovered_model_is_actually_selectable()
    {
        // THE LIVE CLIENT NEVER SETS QualityRank - ModelScope's listing API
        // cannot know it - so a discovered entry arrives at 0. ModelChoice
        // orders by QualityRank descending, so rank 0 means "catalogued and
        // never chosen", which is the same as not catalogued.
        var live = new List<ModelEntry> { Entry("Qwen4-2B-MNN", ModelModality.Chat, 1_800_000_000) };

        var merged = CatalogueMerge.Combine([], live);

        Assert.True(merged[0].QualityRank > 0,
            "a discovered model with rank 0 can never be chosen");
    }

    [Fact]
    public void The_curated_entry_wins_when_both_name_the_same_model()
    {
        // A pin the app downloads against must not change underneath it, and the
        // curated device-fit numbers were measured on a phone.
        var curated = new List<ModelEntry>
        {
            Entry("Qwen3-0.6B-MNN", ModelModality.Chat, 500_000_000) with
            {
                QualityRank = 6, MinRamGb = 0.6, MinStorageGb = 0.5,
            },
        };
        var live = new List<ModelEntry>
        {
            Entry("Qwen3-0.6B-MNN", ModelModality.Chat, 999_999_999) with
            {
                QualityRank = 0, MinRamGb = 1.4,
            },
        };

        var merged = CatalogueMerge.Combine(curated, live);

        var kept = Assert.Single(merged);
        Assert.Equal(6, kept.QualityRank);
        Assert.Equal(0.6, kept.MinRamGb);
        Assert.Equal(500_000_000, kept.TotalBytes);
    }

    [Fact]
    public void Matching_a_curated_name_ignores_case()
    {
        var curated = new List<ModelEntry> { Entry("Qwen3-0.6B-MNN", ModelModality.Chat, 500_000_000) };
        var live = new List<ModelEntry> { Entry("qwen3-0.6b-mnn", ModelModality.Chat, 500_000_000) };

        Assert.Single(CatalogueMerge.Combine(curated, live));
    }

    [Fact]
    public void Curated_entries_keep_their_order_and_come_first()
    {
        // A list order is a decision somebody made, and every screen renders it.
        var curated = new List<ModelEntry>
        {
            Entry("one", ModelModality.Chat, 100),
            Entry("two", ModelModality.Chat, 200),
            Entry("three", ModelModality.Chat, 300),
        };
        var live = new List<ModelEntry> { Entry("new", ModelModality.Chat, 400) };

        var merged = CatalogueMerge.Combine(curated, live);

        Assert.Equal(["one", "two", "three", "new"], merged.Select(m => m.Name));
    }

    [Fact]
    public void A_live_entry_with_no_name_is_skipped_rather_than_catalogued()
    {
        var live = new List<ModelEntry> { Entry("", ModelModality.Chat, 100), Entry("   ", ModelModality.Chat, 100) };

        Assert.Empty(CatalogueMerge.Combine([], live));
    }

    [Fact]
    public void The_same_model_twice_in_one_live_answer_is_catalogued_once()
    {
        var live = new List<ModelEntry>
        {
            Entry("Qwen4-2B-MNN", ModelModality.Chat, 1_800_000_000),
            Entry("Qwen4-2B-MNN", ModelModality.Chat, 1_800_000_000),
        };

        Assert.Single(CatalogueMerge.Combine([], live));
    }

    // ── The derived rank ────────────────────────────────────────────────

    [Theory]
    [InlineData(500_000_000,    6)]    // Qwen3-0.6B — curated 6
    [InlineData(2_600_000_000,  10)]   // Qwen3-4B   — curated 10
    [InlineData(8_900_000_000,  14)]   // Qwen3-14B  — curated 14
    public void A_derived_rank_lands_at_or_just_below_the_curated_equivalent(
        long bytes, int curatedRank)
    {
        // A model that has been run on a P30 should beat one that has not, at
        // the same size. The two ways of getting this wrong are not
        // symmetrical: preferring the untested one costs somebody a model that
        // will not load.
        var derived = CatalogueMerge.RankFor(Entry("x", ModelModality.Chat, bytes));

        Assert.InRange(derived, curatedRank - 2, curatedRank);
    }

    [Fact]
    public void A_bigger_model_never_ranks_below_a_smaller_one()
    {
        var ranks = new[] { 100_000_000L, 500_000_000L, 2_000_000_000L, 8_000_000_000L, 30_000_000_000L }
            .Select(b => CatalogueMerge.RankFor(Entry("x", ModelModality.Chat, b)))
            .ToList();

        for (var i = 1; i < ranks.Count; i++)
            Assert.True(ranks[i] >= ranks[i - 1], $"rank fell from {ranks[i - 1]} to {ranks[i]}");
    }

    [Fact]
    public void A_derived_rank_never_exceeds_the_curated_ladders_top()
    {
        // Otherwise an untested model outranks everything on any device that
        // could hold it, which is the worst possible default.
        Assert.True(CatalogueMerge.RankFor(Entry("huge", ModelModality.Chat, 400_000_000_000)) <= 16);
    }

    [Fact]
    public void A_listing_with_no_size_is_ranked_last_rather_than_never()
    {
        var rank = CatalogueMerge.RankFor(Entry("sizeless", ModelModality.Chat, 0));

        Assert.True(rank > 0, "rank 0 would mean never chosen");
        Assert.True(rank <= 2, "an unmeasurable model should sit at the bottom");
    }

    // ── Telling the two apart ───────────────────────────────────────────

    [Fact]
    public void A_screen_can_tell_a_discovered_model_from_a_shipped_one()
    {
        var curated = new List<ModelEntry> { Entry("shipped", ModelModality.Chat, 100) };
        var found = Entry("found", ModelModality.Chat, 100);

        Assert.True(CatalogueMerge.IsDiscovered(found, curated));
        Assert.False(CatalogueMerge.IsDiscovered(curated[0], curated));
    }

    private static ModelEntry Entry(string name, ModelModality modality, long bytes)
        => new(name, "1", "MNN") { Modality = modality, TotalBytes = bytes };
}
