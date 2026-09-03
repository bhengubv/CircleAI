// LanguageSuggestionTests.cs
//
// The app defaulted every person on earth to English.
//
// Not the catalogue - that holds seventy-five languages and English is one row.
// The code around it: one const "en", seven more screens each with their own
// `?? "en"`, and nothing anywhere reading the device locale. A phone set to
// isiZulu opened in English and invited its owner to go find their own language
// in a list of seventy-five.
//
// The risk being tested is not "does it pick something" - it is that the phone's
// own setting is honoured before any guess, that a guess is labelled as one, and
// that English is reached only after asking twice.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class LanguageSuggestionTests
{
    [Fact]
    public void The_phone_setting_wins_over_everything()
    {
        var c = LanguageSuggestion.For(["zu-ZA", "en-ZA"], "ZA");

        Assert.Equal("zu", c.Tags[0]);
        Assert.True(c.FromDevice);
        Assert.Contains("isiZulu", c.Reason);
    }

    [Fact]
    public void Keeps_the_order_the_owner_put_them_in()
    {
        // Android carries an ordered list because somebody set it. The order is
        // theirs, and re-sorting it would be us deciding again.
        var c = LanguageSuggestion.For(["af-ZA", "zu-ZA", "en-ZA"], "ZA");

        Assert.Equal(["af", "zu", "en"], c.Tags);
    }

    [Fact]
    public void Falls_back_to_where_the_phone_is()
    {
        // A locale the app cannot speak: suggest what is spoken there instead of
        // jumping straight to English.
        var c = LanguageSuggestion.For(["nso-ZA"], "ZA", supported: ["zu", "xh", "af", "en"]);

        Assert.Equal("zu", c.Tags[0]);
        Assert.False(c.FromDevice);
        Assert.Contains("where you are", c.Reason);
    }

    [Fact]
    public void A_guess_is_labelled_as_a_guess()
    {
        // FromDevice is the difference between "your phone is set to X" and a
        // screen being tentative. A guess presented confidently is the bias in a
        // politer form.
        Assert.False(LanguageSuggestion.For(null, "NG").FromDevice);
        Assert.True(LanguageSuggestion.For(["ha-NG"], "NG").FromDevice);
    }

    [Theory]
    [InlineData("ZA", "zu")]
    [InlineData("NG", "ha")]
    [InlineData("KE", "sw")]
    [InlineData("ET", "am")]
    [InlineData("IN", "hi")]
    [InlineData("JP", "ja")]
    [InlineData("BD", "bn")]
    [InlineData("HT", "ht")]
    public void Suggests_from_the_region_when_the_phone_is_silent(string region, string expected)
        => Assert.Equal(expected, LanguageSuggestion.Best(null, region));

    [Fact]
    public void English_only_after_asking_twice()
    {
        // A country nobody has checked is left out rather than guessed at - and
        // the fallback says which happened, rather than presenting English as
        // though it had been chosen.
        var c = LanguageSuggestion.For(null, "XX");

        Assert.Equal(["en"], c.Tags);
        Assert.False(c.FromDevice);
        Assert.Contains("did not say", c.Reason);
    }

    [Fact]
    public void Never_suggests_a_language_this_build_cannot_speak()
    {
        // The same broken promise as the language count on the home screen: a
        // suggestion that outruns the installed voices.
        var c = LanguageSuggestion.For(["ja-JP"], "JP", supported: ["en", "zu"]);

        Assert.DoesNotContain("ja", c.Tags);
    }

    [Fact]
    public void Every_region_suggestion_is_a_language_the_catalogue_carries()
    {
        // The table is the second owner of the language list, so it is pinned to
        // the first: a suggestion pointing at a language the app does not have is
        // a worse first screen than none.
        foreach (var region in new[]
                 { "ZA", "ZW", "BW", "LS", "SZ", "NA", "ZM", "MW", "MZ", "TZ", "KE", "UG",
                   "RW", "BI", "ET", "ER", "SO", "MG", "NG", "GH", "BJ", "BF", "ML", "SN",
                   "NE", "CD", "CG", "CF", "IN", "PK", "BD", "LK", "NP", "MM", "TH", "VN",
                   "ID", "PH", "JP", "KR", "CN", "HK", "TW", "RU", "IR", "AF", "HT", "PY",
                   "PG", "FR" })
        {
            foreach (var tag in LanguageSuggestion.For(null, region).Tags)
                Assert.True(SampleLanguages.Find(tag) is not null,
                    $"{region} suggests \"{tag}\", which the catalogue does not carry");
        }
    }

    [Theory]
    [InlineData("zu_ZA")]
    [InlineData(" ZU-za ")]
    [InlineData("zu")]
    public void Reads_a_tag_however_the_platform_spells_it(string tag)
        => Assert.Equal("zu", LanguageSuggestion.Best([tag], null));

    [Fact]
    public void Region_is_read_from_a_full_locale_too()
        => Assert.Equal("sw", LanguageSuggestion.Best(null, "en-KE"));
}
