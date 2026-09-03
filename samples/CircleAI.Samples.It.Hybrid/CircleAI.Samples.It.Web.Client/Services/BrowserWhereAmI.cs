// BrowserWhereAmI.cs
//
// A tab knows what it was told and nothing more.
//
// Reported as Locale rather than as a position, because that is what it is: the
// browser's language settings say where somebody is FROM. Dressing that up as
// "where you are" is the exact bug this interface exists to fix - and Pair
// refuses to suggest a second language from a Locale source, so a browser
// offers no partner rather than a wrong one.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
public sealed class BrowserWhereAmI : IWhereAmI
{
    /// <inheritdoc />
    public Whereabouts Locate()
    {
        var region = System.Globalization.RegionInfo.CurrentRegion?.TwoLetterISORegionName;
        return new Whereabouts(region, CountrySource.Locale, region);
    }
}
