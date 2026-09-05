// CensusCountsTheDiskTests.cs
//
// "5 of 5 on this phone", over a phone carrying thirty-two voices.
//
// The loading screen's whole job is telling somebody what this handset can do,
// and its own header names the mistake to avoid: a count must never outrun what
// it shows - "10 of 10 ready" over a list of seven.
//
// It made the mirror of that mistake. Seen on a P30 on 2026-09-06:
//
//     5 OF 5 ON THIS PHONE
//     ✓ the English voice          one language
//     ✓ the South African voices   10 languages
//     ✓ the ears                   here
//     ✓ the wake word              here
//     ✓ the brain                  here
//
// Every row true, and the screen wrong. Thirty more voices were on the phone and
// none of them was mentioned, because the census walked the PLAN - a fixed list
// of two named voices - instead of the disk. Anything downloaded outside that
// list was invisible to the one screen that exists to report it.
//
// That is this repository's own recurring fault, one fact with two owners, with
// the plan standing in for the disk. The plan answers "what still has to be
// fetched", which is the wrong question to ask about what is already here.
//
// FirstRun had NO TESTS AT ALL when this was written, despite its header saying
// it was moved into the shared sample precisely so that it could be tested. The
// file is compiled into this project rather than copied, so these cannot drift
// from what ships.

using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Samples.It;
using Xunit;

namespace CircleAI.Tests;

public class CensusCountsTheDiskTests
{
    private static ModelEntry Voice(string name, string? languages, long bytes = 1024) =>
        new(Name: name, Version: "1.0", Quantization: "none")
        {
            Modality = ModelModality.Tts,
            Language = languages,
            TotalBytes = bytes,
        };

    /// <summary>The real plan, so these cannot drift from what Census rows.</summary>
    private static IReadOnlyList<Want> Plan => FirstRun.WantedFor(speech: true);

    private static string NamedVoice(int which) =>
        Plan.Where(w => w.Modality == ModelModality.Tts && w.Named is not null)
            .Select(w => w.Named!)
            .ElementAt(which);

    [Fact]
    public void The_plan_names_the_voices_it_fetches()
    {
        // A GUARD ON THE GUARD. Every test below distinguishes "in the plan" from
        // "extra", and if the plan ever stopped naming its voices they would all
        // pass while testing nothing.
        Assert.True(
            Plan.Count(w => w.Modality == ModelModality.Tts && w.Named is not null) >= 2,
            "the plan no longer names its voices, so these tests are not testing anything");
    }

    [Fact]
    public void Voices_beyond_the_plan_get_a_row()
    {
        // THE BUG. Two voices are named in the plan and have rows of their own; a
        // third is on the phone and used to appear nowhere at all.
        var row = FirstRun.OtherVoices(
            [Voice(NamedVoice(0), "en"), Voice("MMS-zul", "zu"), Voice("MMS-xho", "xh")],
            Plan);

        Assert.NotNull(row);
        Assert.True(row!.Value.Present);
        Assert.Equal("2 more voices", row.Value.Title);

        // NAMED, NOT COUNTED. "2 more, 2 languages" told somebody a size and not
        // one thing they might have opened the screen to find out.
        Assert.Contains("isiZulu", row.Value.Detail);
        Assert.Contains("isiXhosa", row.Value.Detail);
    }

    [Fact]
    public void One_extra_voice_is_not_one_more_voices()
    {
        var row = FirstRun.OtherVoices([Voice("MMS-zul", "zu")], Plan);

        Assert.NotNull(row);
        Assert.Equal("one more voice", row!.Value.Title);
        Assert.Equal("isiZulu", row.Value.Detail);
    }

    [Fact]
    public void The_owners_language_is_named_first()
    {
        // THE QUESTION THE ROW EXISTS TO ANSWER is "is mine in there", and a list
        // that buries it seventh is only marginally better than a count. If it is
        // present it leads; if it is absent that is now visible rather than
        // hidden behind "10 languages".
        var voices = new[] { Voice("A", "af,zu,xh,ja,st") };

        Assert.StartsWith("Japanese", FirstRun.OtherVoices(voices, Plan, "ja")!.Value.Detail);
        Assert.StartsWith("isiZulu",  FirstRun.OtherVoices(voices, Plan, "zu")!.Value.Detail);

        // With no opinion it is simply alphabetical, not arbitrary.
        Assert.StartsWith("Afrikaans", FirstRun.OtherVoices(voices, Plan, null)!.Value.Detail);
    }

    [Fact]
    public void A_long_list_says_what_it_is_not_showing()
    {
        // The row is one line on a phone, so it caps at three - and states what
        // the cap hides rather than dropping it silently, which is the same rule
        // the loading screen applies to itself.
        var row = FirstRun.OtherVoices([Voice("A", "af,zu,xh,ja,st")], Plan, "ja");

        Assert.NotNull(row);
        Assert.Contains("+2 more", row!.Value.Detail);
        Assert.Contains("Japanese", row.Value.Detail);
    }

    [Fact]
    public void A_regional_tag_is_named_as_its_language()
    {
        // Catalogue entries carry things like "pt-BR" and "es-MX". The row must
        // read "Portuguese", not "pt-BR", and a phone set to "pt" must match it.
        var row = FirstRun.OtherVoices([Voice("A", "pt-BR")], Plan, "pt");

        Assert.NotNull(row);
        Assert.DoesNotContain("-BR", row!.Value.Detail);
    }

    [Fact]
    public void The_plans_own_voices_are_not_counted_twice()
    {
        // They already have rows. Counting them here would put the South African
        // bundle on screen twice and inflate the summary - the same class of
        // wrong in the other direction.
        var row = FirstRun.OtherVoices(
            Plan.Where(w => w.Modality == ModelModality.Tts && w.Named is not null)
                .Select(w => Voice(w.Named!, "en")),
            Plan);

        Assert.Null(row);
    }

    [Fact]
    public void A_phone_with_nothing_extra_gets_no_row()
    {
        // The screen must not grow an empty row reading "0 more". A fresh install
        // has exactly what the plan fetched, and its census is the four
        // capabilities and nothing else.
        Assert.Null(FirstRun.OtherVoices([], Plan));
    }

    [Fact]
    public void Languages_are_gathered_across_voices_and_deduplicated()
    {
        // Three voices, four languages between them, one shared. The row is about
        // what the phone can SPEAK, so a language named twice is named once.
        // isiZulu is in two of the three voices, and the owner speaks it - so it
        // leads, and it must appear ONCE. Four languages between them, three
        // shown, so isiXhosa is the one behind "+1 more".
        var row = FirstRun.OtherVoices(
            [Voice("A", "zu,xh,st"), Voice("B", "zu"), Voice("C", "af")],
            Plan, "zu");

        Assert.NotNull(row);
        Assert.Equal("3 more voices", row!.Value.Title);
        Assert.StartsWith("isiZulu", row.Value.Detail);        // the owner's, first
        Assert.Equal(1, Occurrences(row.Value.Detail, "isiZulu"));
        Assert.Contains("+1 more", row.Value.Detail);          // four named, three shown
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
            n++;
        return n;
    }

    [Fact]
    public void A_voice_with_no_language_tag_is_still_counted()
    {
        // The count and the languages come apart here, and the row must not
        // silently drop a voice because the catalogue has no tag for it. The
        // TITLE still says two; the detail can only say what it knows.
        var row = FirstRun.OtherVoices([Voice("A", ""), Voice("B", null)], Plan);

        Assert.NotNull(row);
        Assert.Equal("2 more voices", row!.Value.Title);
        Assert.Equal("here", row.Value.Detail);
    }

    [Fact]
    public void Only_voices_are_counted()
    {
        // The row says "voices". An ASR or chat bundle arriving here would make it
        // say something untrue about what the phone can speak in.
        var ears = new ModelEntry(Name: "Whisper-tiny", Version: "1.0", Quantization: "none")
        {
            Modality = ModelModality.Asr,
            Language = "en",
            TotalBytes = 1024,
        };

        Assert.Null(FirstRun.OtherVoices([ears], Plan));
    }

    [Fact]
    public void The_bytes_are_the_sum_of_what_is_there()
    {
        var row = FirstRun.OtherVoices([Voice("A", "zu", 1000), Voice("B", "xh", 2500)], Plan);

        Assert.NotNull(row);
        Assert.Equal(3500, row!.Value.Bytes);
    }
}
