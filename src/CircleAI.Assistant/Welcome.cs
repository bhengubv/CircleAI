// Welcome.cs
//
// What it says out loud the first time, while the rest is still arriving.
//
// THE WAIT IS THE ONE MOMENT IT HAS SOMEBODY'S ATTENTION, and on a slow link
// that moment is three quarters of an hour long. Setup fetches the voice FIRST
// on purpose - about 110 MB against the brain's many gigabytes - so the phone
// can speak within a minute or two on almost any connection, and everything
// after that is a wait it can fill itself.
//
// NOT FILLER, AND NOT A LANGUAGE DEMO. It says the two things a new person
// actually needs - that they can talk to it, and that it is still getting ready
// - in a real voice, in a language they speak.
//
// PROVEN ON ONE HEAD AND NOWHERE ELSE, WHICH IS WHY IT IS HERE NOW. This lived
// as two private statics inside an Activity. The other head plays a per-language
// greeting instead - a demo of what the voice can do rather than the sentence
// that tells somebody what is happening - so one behaviour had two
// implementations and they had already drifted apart.
//
// The words live here. A head speaks them.

namespace CircleAI.Assistant;

/// <summary>The first thing it says, in the language of whoever is holding it.</summary>
public static class Welcome
{
    /// <summary>
    /// The languages a welcome exists in.
    /// </summary>
    /// <remarks>
    /// SOUTH AFRICA FIRST, AND THAT IS THE PRODUCT'S OPINION RATHER THAN AN
    /// OVERSIGHT. isiZulu, isiXhosa, Afrikaans, Sesotho and Swahili are languages
    /// this thing exists to speak that other assistants do not; English is the
    /// fallback and not the default.
    /// </remarks>
    public static IReadOnlyList<string> Languages { get; } =
        ["zu", "xh", "af", "st", "sw", "en"];

    /// <summary>
    /// Which welcome to speak, given the phone's own language.
    /// </summary>
    /// <param name="deviceLanguage">
    /// A language tag, usually from the platform's current locale. A region
    /// suffix is ignored and anything unrecognised falls back to English.
    /// </param>
    /// <remarks>
    /// THE PHONE'S LANGUAGE, NOT A LIST POSITION. Somebody in Soweto should not
    /// be welcomed in a language they did not choose because the catalogue
    /// happened to be alphabetical.
    /// </remarks>
    public static string TagFor(string? deviceLanguage)
    {
        var tag = deviceLanguage?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(tag)) return "en";

        // "en-ZA" and "zu_ZA" both arrive here, depending on the platform.
        var cut = tag.IndexOfAny(['-', '_']);
        if (cut > 0) tag = tag[..cut];

        return Languages.Contains(tag) ? tag : "en";
    }

    /// <summary>What it says while the rest downloads, in that language.</summary>
    /// <remarks>
    /// THE TWO FACTS, IN THIS ORDER: it is still fetching, and you can already
    /// talk to it. Reversing them makes the second sound like a consolation.
    /// </remarks>
    public static string LineFor(string? deviceLanguage) => TagFor(deviceLanguage) switch
    {
        "zu" => "Sawubona. Ngiyalanda okusele. Ungakhuluma nami manje.",
        "xh" => "Molo. Ndisalanda okuseleyo. Ungathetha nam ngoku.",
        "af" => "Hallo. Ek laai nog die res af. Jy kan nou al met my praat.",
        "st" => "Dumela. Ke sa jarolla tse ling. O ka bua le nna hona joale.",
        "sw" => "Habari. Bado ninapakua sehemu iliyobaki. Unaweza kuzungumza nami sasa.",
        _    => "Hello. I am still downloading the rest, but you can talk to me now.",
    };
}
