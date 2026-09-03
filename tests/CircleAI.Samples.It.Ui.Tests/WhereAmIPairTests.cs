// WhereAmIPairTests.cs
//
// A South African standing in Tokyo needs English and Japanese.
//
// The interpreter pair was the constants "en" and "zu", in two files that had to
// agree by hand. That is a fine guess in Johannesburg and useless anywhere else,
// and no amount of reading the locale more carefully produces the second half:
// his locale says South Africa, because he IS South African.
//
// Measured on a P30 in Tokyo, which is what these encode: the radio was
// OUT_OF_SERVICE and every network and SIM reading was blank, while
// persist.sys.timezone said Asia/Tokyo the whole time.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class WhereAmIPairTests
{
    [Fact]
    public void A_south_african_in_tokyo_is_offered_japanese()
    {
        var here = new Whereabouts("JP", CountrySource.Timezone, "ZA");

        var (mine, theirs, why) = LanguageSuggestion.Pair("en", here);

        Assert.Equal("en", mine);
        Assert.Equal("ja", theirs);
        Assert.Contains("timezone", why);
    }

    [Fact]
    public void At_home_the_pair_is_local()
    {
        var here = new Whereabouts("ZA", CountrySource.Network, "ZA");

        var (mine, theirs, _) = LanguageSuggestion.Pair("en", here);

        Assert.Equal("zu", theirs);
    }

    [Fact]
    public void A_locale_is_never_treated_as_a_position()
    {
        // THE BUG, PINNED. Locale.Default.Country said ZA for a phone in Tokyo,
        // and the old code called that "where you are". A locale answers where
        // somebody is FROM, so it gets no vote on who is standing opposite.
        var here = new Whereabouts("ZA", CountrySource.Locale, "ZA");

        var (_, theirs, why) = LanguageSuggestion.Pair("en", here);

        Assert.Null(theirs);
        Assert.Contains("nothing on this phone knows where you are", why);
    }

    [Fact]
    public void Never_offers_you_your_own_language_as_the_other_side()
    {
        // An English speaker in Britain gets no partner rather than English
        // twice, which would be an interpreter that translates nothing.
        var here = new Whereabouts("JP", CountrySource.Timezone, "JP");

        var (mine, theirs, _) = LanguageSuggestion.Pair("ja", here);

        Assert.Equal("ja", mine);
        Assert.NotEqual("ja", theirs);
    }

    [Fact]
    public void Says_nothing_rather_than_guessing_for_a_country_nobody_checked()
    {
        var here = new Whereabouts("XX", CountrySource.Network, "ZA");

        var (_, theirs, why) = LanguageSuggestion.Pair("en", here);

        Assert.Null(theirs);
        Assert.Contains("XX", why);
    }

    [Fact]
    public void Knows_when_somebody_is_travelling()
        => Assert.True(new Whereabouts("JP", CountrySource.Timezone, "ZA").Travelling);

    [Fact]
    public void Is_not_travelling_at_home()
        => Assert.False(new Whereabouts("ZA", CountrySource.Network, "ZA").Travelling);

    [Theory]
    [InlineData("Asia/Tokyo", "JP")]
    [InlineData("Africa/Johannesburg", "ZA")]
    [InlineData("Asia/Calcutta", "IN")]
    [InlineData("Asia/Saigon", "VN")]
    [InlineData("Africa/Lagos", "NG")]
    public void Reads_a_country_off_a_timezone(string zone, string country)
        => Assert.Equal(country, TimeZoneCountries.Country(zone));

    [Fact]
    public void An_unknown_zone_says_so_rather_than_guessing()
        => Assert.Null(TimeZoneCountries.Country("Mars/Olympus_Mons"));

    [Fact]
    public void Every_zone_maps_to_a_country_the_app_can_serve()
    {
        // The zone table is the third owner of the country list, so it is pinned
        // to the region table: a timezone that resolves to a country nobody has
        // languages for is a lookup that can only ever return nothing.
        foreach (var (zone, country) in TimeZoneCountries.All)
            Assert.True(LanguageSuggestion.For(null, country).Tags.Count > 0,
                $"{zone} -> {country}, which suggests no languages at all");
    }

    [Fact]
    public void The_explanation_names_the_signal()
    {
        // A wrong suggestion has to be traceable to the thing that caused it.
        Assert.Contains("timezone", new Whereabouts("JP", CountrySource.Timezone, "ZA").Explain);
        Assert.Contains("network", new Whereabouts("JP", CountrySource.Network, "ZA").Explain);
        Assert.Contains("nothing", new Whereabouts(null, CountrySource.Unknown, null).Explain);
    }
}
