namespace CircleAI.Samples.It;

/// <summary>How a country was worked out, so a wrong answer can be traced.</summary>
/// <remarks>
/// ORDERED MOST-LIVE FIRST, and that order is the whole design. A phone carries
/// two different facts that are easy to confuse: where its owner is FROM, and
/// where the phone IS. Reading the second off the first is how an English
/// speaker standing in Tokyo gets offered isiZulu.
/// </remarks>
public enum CountrySource
{
    /// <summary>Nothing said. Not an answer, and must not be dressed as one.</summary>
    Unknown,

    /// <summary>The language settings. Where somebody is FROM, not where they are.</summary>
    Locale,

    /// <summary>The SIM's home country. Also where they are from.</summary>
    Sim,

    /// <summary>
    /// The phone's timezone. Live, offline, and needs no permission or signal.
    /// </summary>
    /// <remarks>
    /// The one that works on a phone with no service. Measured on a P30 in Tokyo
    /// with the radio OUT_OF_SERVICE: every network reading was blank and
    /// Asia/Tokyo was sitting there the whole time.
    /// </remarks>
    Timezone,

    /// <summary>The network the phone is actually registered on. The strongest.</summary>
    /// <remarks>
    /// NOT THE CELL TOWER, and that is a deliberate omission. A phone with no
    /// service still sees a tower and its MCC - 440 was sitting in dumpsys on a
    /// P30 in Tokyo while every network reading was blank - but getAllCellInfo
    /// and getServiceState both need a LOCATION permission from inside an app.
    /// Asking for GPS to guess which language to offer costs far more than the
    /// question is worth, so the timezone answers it instead and this signal
    /// stays out.
    /// </remarks>
    Network,
}

/// <summary>Where the phone is, how that was decided, and where it is from.</summary>
/// <param name="Here">
/// Two-letter country the phone is in NOW, or null when nothing could say.
/// </param>
/// <param name="Source">Which signal answered, so a wrong answer is traceable.</param>
/// <param name="Home">
/// Two-letter country the owner is from - SIM or locale - or null. Deliberately
/// separate from <paramref name="Here"/>: a traveller is both, and an
/// interpreter needs each for a different side of the conversation.
/// </param>
public sealed record Whereabouts(string? Here, CountrySource Source, string? Home)
{
    /// <summary>True when the phone is somewhere other than home.</summary>
    /// <remarks>
    /// The case the interpreter exists for. Not a fact worth acting on alone -
    /// somebody may live abroad - but it is the difference between suggesting a
    /// second language and suggesting nothing.
    /// </remarks>
    public bool Travelling =>
        Here is { Length: > 0 } && Home is { Length: > 0 } &&
        !string.Equals(Here, Home, StringComparison.OrdinalIgnoreCase);

    /// <summary>Plain words for a screen, naming the signal rather than hiding it.</summary>
    public string Explain => Source switch
    {
        CountrySource.Network   => "the network this phone is on",
        CountrySource.Timezone  => "this phone's timezone",
        CountrySource.Sim       => "this phone's SIM",
        CountrySource.Locale    => "this phone's language settings",
        _                       => "nothing on this phone said",
    };
}

/// <summary>Where the phone is, without asking for a location permission.</summary>
/// <remarks>
/// NO LOCATION PERMISSION, ON PURPOSE. Every signal here is one Android hands
/// out freely - the network's country, the visible tower's country code, the
/// timezone, the SIM. An assistant that demanded GPS to guess which language to
/// offer would be asking for far more than the question is worth, and this app
/// does not send anything anywhere to answer it either.
/// <para>
/// It answers the question the language default kept getting wrong. Locale says
/// where somebody is FROM; this says where they ARE. A South African in Tokyo
/// needs English for themselves and Japanese for the person opposite, and no
/// amount of reading the locale more carefully will ever produce the second.
/// </para>
/// </remarks>
public interface IWhereAmI
{
    /// <summary>Work out where the phone is, best signal first.</summary>
    Whereabouts Locate();
}
