// DeviceWhereAmI.cs
//
// Where the phone is, asked of the phone, without a location permission.
//
// WRITTEN BECAUSE THE APP CONFUSED "FROM" WITH "IN". The language default read
// Locale.Default.Country and called the answer "where you are" - so a South
// African standing in Tokyo was offered isiZulu, confidently, in a string that
// claimed to know where he was.
//
// Measured on that P30, in Tokyo:
//
//     gsm.operator.iso-country        (empty)   radio OUT_OF_SERVICE
//     gsm.sim.operator.iso-country    (empty)   no SIM registered
//     persist.sys.timezone            Asia/Tokyo
//     locale                          en-ZA
//
// The network could not answer and the timezone could. That is the whole reason
// the cascade exists rather than one reading.

using Android.Content;
using Android.Telephony;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceWhereAmI : IWhereAmI
{
    private const string Tag = "CircleAI.Where";

    /// <inheritdoc />
    public Whereabouts Locate()
    {
        var home = Home();

        // MOST LIVE FIRST. The network is the strongest claim about where a phone
        // physically is; the timezone is the one that survives having no signal.
        if (Country(Telephony?.NetworkCountryIso) is { } network)
            return Report(new Whereabouts(network, CountrySource.Network, home));

        if (TimeZoneCountries.Country(TimeZoneId()) is { } zone)
            return Report(new Whereabouts(zone, CountrySource.Timezone, home));

        // Falling back to where somebody is FROM, and saying so. A SIM or a
        // locale is not a position, and reporting it as one is the bug.
        if (Country(Telephony?.SimCountryIso) is { } sim)
            return Report(new Whereabouts(sim, CountrySource.Sim, home));

        if (home is { Length: > 0 })
            return Report(new Whereabouts(home, CountrySource.Locale, home));

        return Report(new Whereabouts(null, CountrySource.Unknown, null));
    }

    /// <summary>Where the owner is from: the SIM first, then their language settings.</summary>
    private static string? Home()
        => Country(Telephony?.SimCountryIso)
           ?? Country(Java.Util.Locale.Default?.Country);

    private static TelephonyManager? Telephony
    {
        get
        {
            try
            {
                return Android.App.Application.Context
                    .GetSystemService(Context.TelephonyService) as TelephonyManager;
            }
            catch { return null; }
        }
    }

    /// <summary>The phone's timezone, which needs nothing and no permission.</summary>
    private static string? TimeZoneId()
    {
        try { return Java.Util.TimeZone.Default?.ID; }
        catch { return null; }
    }

    /// <summary>Two letters, upper case, or null for the blanks a dead radio gives.</summary>
    private static string? Country(string? raw)
        => raw is { Length: 2 } ? raw.ToUpperInvariant() : null;

    /// <summary>
    /// Says where it thinks it is and which signal said so.
    /// </summary>
    /// <remarks>
    /// Named signal, not just a country: a wrong suggestion is then one line to
    /// diagnose rather than a guess about what the phone believed.
    /// </remarks>
    private static Whereabouts Report(Whereabouts w)
    {
        Android.Util.Log.Info(Tag,
            $"here={w.Here ?? "unknown"} ({w.Explain}) home={w.Home ?? "unknown"}"
            + (w.Travelling ? " — travelling" : ""));
        return w;
    }
}
