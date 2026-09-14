// WelcomeTests.cs
//
// The first thing the app says out loud, pinned.
//
// THIS BEHAVIOUR EXISTED TWICE AND THE TWO COPIES SAID DIFFERENT THINGS. One
// head spoke "I am still downloading the rest, but you can talk to me now" in
// the phone's own language; the other played a per-language greeting, which is a
// demo of the voice rather than the sentence that tells somebody what is
// happening. Neither had a test, so neither could notice the other.
//
// It is one implementation now. These are what stop it drifting again.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class WelcomeTests
{
    [Theory]
    [InlineData("zu", "Sawubona")]
    [InlineData("xh", "Molo")]
    [InlineData("af", "Hallo")]
    [InlineData("st", "Dumela")]
    [InlineData("sw", "Habari")]
    [InlineData("en", "Hello")]
    public void Each_language_is_welcomed_in_its_own_words(string tag, string opens)
        => Assert.StartsWith(opens, Welcome.LineFor(tag), StringComparison.Ordinal);

    [Theory]
    [InlineData("en-ZA")]
    [InlineData("zu_ZA")]
    [InlineData("AF")]
    [InlineData("  st  ")]
    public void A_region_suffix_or_stray_casing_still_finds_the_language(string tag)
    {
        // PLATFORMS SPELL A LOCALE DIFFERENTLY. Android hands back "en_ZA",
        // .NET "en-ZA", and a settings screen may hand back whatever somebody
        // typed. A welcome that fell to English on any of those would be English
        // for most of the people this exists for.
        var resolved = Welcome.TagFor(tag);

        Assert.Contains(resolved, Welcome.Languages);
        Assert.Equal(resolved, Welcome.TagFor(resolved));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("qq")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_it_cannot_speak_falls_back_to_English(string? tag)
    {
        Assert.Equal("en", Welcome.TagFor(tag));
        Assert.StartsWith("Hello", Welcome.LineFor(tag), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_language_it_claims_has_a_line_of_its_own()
    {
        // A TABLE THAT FALLS THROUGH IS A TABLE THAT LIES. Languages says these
        // six are spoken; a missing case would silently answer in English while
        // the list went on advertising the language.
        var english = Welcome.LineFor("en");

        foreach (var tag in Welcome.Languages.Where(t => t != "en"))
            Assert.NotEqual(english, Welcome.LineFor(tag));
    }

    [Fact]
    public void It_says_both_of_the_two_things_a_new_person_needs()
    {
        // THE POINT OF THE SENTENCE, NOT ITS WORDING. It has to say that it is
        // still fetching AND that they can already talk to it. A greeting that
        // only says hello - which is what the other head played - leaves somebody
        // watching a progress bar with no idea they can start.
        //
        // Asserted on English only: the translations are a speaker's judgement
        // and this test has no business scoring them.
        var line = Welcome.LineFor("en");

        Assert.Contains("downloading", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("talk to me", line, StringComparison.OrdinalIgnoreCase);
    }
}
