// HomeCountsInstalledVoicesTests.cs
//
// "78 languages, spoken out loud", on a phone that speaks about thirteen.
//
// The catalogue is every Tts tag in the registry: what this device COULD speak
// once everything is downloaded. That is exactly the right answer for a language
// picker, whose whole job is offering downloads, and exactly the wrong one under
// the words "spoken out loud" - which is a claim about now.
//
// Seen on a P30 on 2026-09-06. Home said 78 while the loading screen, fixed that
// same morning to read the disk rather than the plan, said about thirteen. Two
// screens, one phone, six times apart, and both numbers computed honestly from
// the source each happened to ask.
//
// The number had already been wrong twice before: "10 plus" was a hedge, and
// SampleLanguages.All.Count was 75 names against 78 voices. This is the third
// and it is a different mistake from the first two - not a wrong table, a right
// table answering a different question.
//
// NULL IS NOT FALSE. A browser has no model store to inspect and must not report
// zero installed voices; it reports null on every row and is counted as its whole
// catalogue, which is the honest answer for it.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class HomeCountsInstalledVoicesTests
{
    /// <summary>The count Home puts in "N languages, spoken out loud".</summary>
    /// <remarks>
    /// The rule itself, lifted out of the page so it can be exercised. Home runs
    /// exactly this over what the host returns.
    /// </remarks>
    private static int Spoken(IReadOnlyList<VoiceRow> catalogue) =>
        catalogue.Any(r => r.Installed is not null)
            ? catalogue.Count(r => r.Installed == true)
            : catalogue.Count;

    [Fact]
    public void A_phone_counts_what_it_has_not_what_it_could_get()
    {
        // THE BUG. Four of these are on the device and the rest are downloads.
        var catalogue = new List<VoiceRow>
        {
            new("en", 60_000_000, Installed: true),
            new("zu", 60_000_000, Installed: true),
            new("xh", 60_000_000, Installed: true),
            new("ja", 137_000_000, Installed: false),
            new("ko", 90_000_000, Installed: false),
            new("sw", 40_000_000, Installed: false),
        };

        Assert.Equal(3, Spoken(catalogue));
        Assert.NotEqual(catalogue.Count, Spoken(catalogue));
    }

    [Fact]
    public void A_head_that_cannot_tell_still_reports_its_catalogue()
    {
        // NULL IS NOT FALSE. A browser has no model store; answering "0 languages,
        // spoken out loud" there would be a new lie in place of the old one, and
        // the browser's catalogue IS what it can speak.
        var catalogue = new List<VoiceRow>
        {
            new("en", null), new("zu", null), new("ja", null),
        };

        Assert.Equal(3, Spoken(catalogue));
    }

    [Fact]
    public void A_phone_with_nothing_installed_yet_claims_nothing()
    {
        // A fresh install before the first download. Zero is the truth here, and
        // Home prints "Spoken out loud, in your language" rather than "0
        // languages" - the page already guards on the count being positive.
        var catalogue = new List<VoiceRow>
        {
            new("en", 60_000_000, Installed: false),
            new("zu", 60_000_000, Installed: false),
        };

        Assert.Equal(0, Spoken(catalogue));
    }

    [Fact]
    public void A_mixed_answer_trusts_the_rows_that_know()
    {
        // Defensive: a head that can tell for some languages and not others must
        // not fall back to the whole catalogue, or one unanswerable row would
        // restore the original overclaim.
        var catalogue = new List<VoiceRow>
        {
            new("en", 60_000_000, Installed: true),
            new("zu", 60_000_000, Installed: null),
            new("ja", 137_000_000, Installed: false),
        };

        Assert.Equal(1, Spoken(catalogue));
    }

    [Fact]
    public void An_empty_catalogue_is_not_a_crash()
    {
        Assert.Equal(0, Spoken([]));
    }
}
