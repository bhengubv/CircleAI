// LanguageTableTests.cs
//
// Two language tables, and the drift between them.
//
// THERE ARE TWO AND THERE CANNOT EASILY BE ONE. CircleAI.Assistant has zero
// ProjectReferences so a WASM head can load it, and CircleAI.Languages is where
// the richer LanguageTag lives - writing system, right-to-left, region - which
// SampleLanguage does not carry. Merging them means giving all sixty-nine
// entries a writing system and an RTL flag, and those have to be SOURCED rather
// than guessed: getting RTL wrong renders somebody's language backwards.
//
// So there are two, and the job of this file is to make sure they cannot
// silently disagree. Measured on 2026-09-11:
//
//   KnownLanguages   20 entries
//   SampleLanguages  69 entries
//   Only in KnownLanguages    es, pt   (Spanish and Portuguese)
//   Only in SampleLanguages   51, including ja, ko, ru, ur, vi, th, bn
//   Shared tags whose names disagree:   ZERO
//
// That last line is the invariant worth pinning. The counts will move as
// languages are added; the names of the ones both tables know must not.
//
// IT ALREADY COST SOMETHING. LlmTranslationEngine reached for KnownLanguages
// because that is the table it can see, and silently degraded translation for
// most of the app - Japanese is not among the twenty, on a product carrying a
// whole Open JTalk prosody stack for Japanese, so the prompt read "from English
// to ja". Caught by a test asking for Japanese, not by anything structural.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Assistant;
using CircleAI.Languages;
using Xunit;

namespace CircleAI.Tests;

public class LanguageTableTests
{
    [Fact]
    public void Where_both_tables_know_a_language_they_call_it_the_same_thing()
    {
        // THE INVARIANT. Two names for one language is how a picker says
        // "isiZulu" and a prompt says "Zulu" about the same tap.
        var disagreements = new List<string>();

        foreach (var known in KnownLanguages.All)
        {
            var sample = SampleLanguages.Find(known.BcpTag);
            if (sample is null) continue;

            if (!string.Equals(known.DisplayName, sample.Name, StringComparison.Ordinal))
                disagreements.Add(
                    $"{known.BcpTag}: KnownLanguages says '{known.DisplayName}', " +
                    $"SampleLanguages says '{sample.Name}'");
        }

        Assert.True(disagreements.Count == 0,
            "The two language tables disagree about a name:\n  " +
            string.Join("\n  ", disagreements));
    }

    [Fact]
    public void Where_both_tables_know_a_language_they_agree_on_its_native_name()
    {
        // The native name is what somebody actually looks for in a list, and it
        // is the field most likely to be typed twice and typed differently.
        var disagreements = new List<string>();

        foreach (var known in KnownLanguages.All)
        {
            var sample = SampleLanguages.Find(known.BcpTag);
            if (sample is null) continue;

            if (!string.Equals(known.NativeName, sample.Native, StringComparison.Ordinal))
                disagreements.Add(
                    $"{known.BcpTag}: '{known.NativeName}' vs '{sample.Native}'");
        }

        Assert.True(disagreements.Count == 0,
            "The two language tables disagree about a native name:\n  " +
            string.Join("\n  ", disagreements));
    }

    [Fact]
    public void Neither_table_lists_a_language_twice()
    {
        Assert.Equal(
            KnownLanguages.All.Count,
            KnownLanguages.All.Select(l => l.BcpTag).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.Equal(
            SampleLanguages.All.Count,
            SampleLanguages.All.Values.Select(l => l.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_language_has_a_name_and_a_native_name()
    {
        // An entry with a blank name shows as an empty row in a picker, which
        // reads as a rendering bug rather than as missing data.
        Assert.All(KnownLanguages.All, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.BcpTag));
            Assert.False(string.IsNullOrWhiteSpace(l.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(l.NativeName));
        });

        Assert.All(SampleLanguages.All.Values, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.Tag));
            Assert.False(string.IsNullOrWhiteSpace(l.Name));
            Assert.False(string.IsNullOrWhiteSpace(l.Native));
        });
    }

    [Fact]
    public void Find_is_not_case_sensitive_about_a_tag()
    {
        // BCP-47 says subtags are case-insensitive. A model config writing "ZU"
        // used to get null back, and null here means a screen shows the raw tag
        // where the language's name belongs.
        Assert.NotNull(SampleLanguages.Find("ZU"));
        Assert.NotNull(SampleLanguages.Find("zu"));
        Assert.Equal(SampleLanguages.Find("zu"), SampleLanguages.Find("Zu"));
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("zu-ZA", "zu")]
    [InlineData("ja-JP", "ja")]
    public void A_regional_tag_finds_its_language(string regional, string primary)
    {
        // WHAT ANDROID ACTUALLY HANDS BACK. Locale tags arrive with a region far
        // more often than without one, and an exact-match lookup answered none
        // of them - so the commonest input was the one that failed.
        var found = SampleLanguages.Find(regional);

        Assert.NotNull(found);
        Assert.Equal(primary, found!.Tag);
    }

    [Fact]
    public void A_tag_for_a_language_we_do_not_offer_is_still_null()
    {
        // The repairs must not start inventing. Null is the honest answer for a
        // language that is not in the catalogue, and the caller's job is to show
        // the tag rather than a fabricated row.
        Assert.Null(SampleLanguages.Find("qq"));
        Assert.Null(SampleLanguages.Find("qq-QQ"));
        Assert.Null(SampleLanguages.Find(""));
        Assert.Null(SampleLanguages.Find(null));
    }
}
