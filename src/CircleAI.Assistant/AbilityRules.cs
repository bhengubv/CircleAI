// AbilityRules.cs
//
// When an ability is On, Ready, Available, TooBig or NotCatalogued.
//
// THIS RULE EXISTED IN TWO HEADS AND NEITHER COULD BE TESTED. The Blazor head's
// copy lived inside DeviceFacts.AbilitiesAsync, which opens a model registry, a
// bundle loader and a device probe - none of which exist off a phone. The native
// head's copy lived inside AbilitiesActivity.Row, which additionally needs an
// Android Activity. So the one piece of logic a person actually reads off the
// screen was the one piece nothing could assert, and it drifted:
//
//   The native head never learned Ready. It printed "✓ On" whenever a model was
//   on disk, which is the exact lie AbilityState.Ready was added to stop - the
//   Blazor head was fixed and this one was not, and no test went red because no
//   test could reach either.
//
// So the decision is here, over primitives, in the assembly that already owns
// AbilityRow and AbilityState - which has zero project references and is loaded
// by a browser, so it cannot take a dependency on a model registry to get them.
// The callers do the I/O and hand in the answers.

namespace CircleAI.Assistant;

/// <summary>Turns what a phone has into what a row says.</summary>
public static class AbilityRules
{
    /// <summary>One row, decided.</summary>
    /// <param name="title">What it is. A verb, not a noun.</param>
    /// <param name="blurb">What it means for you, in one sentence.</param>
    /// <param name="needsNoModel">
    /// True for an ability that is arithmetic rather than a download - music and
    /// lexical search are managed code and work on a phone with nothing on it.
    /// </param>
    /// <param name="needsListening">
    /// True for an ability that is only ON while something is running - waking.
    /// </param>
    /// <param name="chosenBytes">
    /// The size of the model this ability would use, or null when nothing in the
    /// catalogue will run on this phone.
    /// </param>
    /// <param name="present">Whether that model is already on the phone.</param>
    /// <param name="anyCatalogued">Whether anything serves this ability at all.</param>
    /// <param name="listening">Whether the resident listener holds the microphone.</param>
    /// <param name="route">The screen this head can open for it, or null.</param>
    public static AbilityRow Decide(
        string title,
        string blurb,
        bool needsNoModel,
        bool needsListening,
        long? chosenBytes,
        bool present,
        bool anyCatalogued,
        bool listening,
        string? route = null)
    {
        // AN ABILITY THAT NEEDS NO MODEL IS ALWAYS ON. Every branch below decides
        // a row's state by whether a model is on disk, which cannot answer for
        // something synthesised in managed code - it reported "unavailable" for
        // the one capability that works on a phone with nothing on it.
        if (needsNoModel)
            return new AbilityRow(title, blurb, AbilityState.On, TryRoute: route);

        if (present)
        {
            // PRESENT IS NOT RUNNING, AND A ROW USED TO SAY IT WAS. "Waking ✓ On"
            // appeared the moment the bundle finished downloading, while nothing
            // was listening - and three rows below, on the same screen, a toggle
            // read IsListening LIVE, because Android can kill the service and a
            // remembered bool drifts. One screen, two answers, one of them true.
            var running = !needsListening || listening;
            return new AbilityRow(title, blurb,
                running ? AbilityState.On : AbilityState.Ready,
                TryRoute: route);
        }

        // A SIZE BELONGS TO A ROW THAT IS OFFERING A DOWNLOAD AND TO NO OTHER.
        if (chosenBytes is { } bytes)
            return new AbilityRow(title, blurb, AbilityState.Available, bytes);

        // NOTHING CATALOGUED is not "will not fit", and a screen must not confuse
        // them: "needs more memory" tells a person their phone is the problem and
        // invites them to go buy a better one, for a model that does not exist on
        // any phone yet. That is our gap and it should read like our gap.
        return new AbilityRow(title, blurb,
            anyCatalogued ? AbilityState.TooBig : AbilityState.NotCatalogued);
    }

    /// <summary>Fill a blurb's <c>{n}</c> with a language count that reads.</summary>
    /// <remarks>
    /// "READS THINGS OUT LOUD, IN 1 LANGUAGES", ON A P30, 2026-09-12. The count
    /// was right - one voice installed, one language - and the sentence was not,
    /// because the plural lived in the template and only the digit was
    /// substituted. A number dropped into a fixed plural reads as a machine
    /// talking, which is the register this screen exists to stay out of.
    /// <para>
    /// Here rather than beside the count because it is a rule about words, and
    /// the count needs a model registry and a phone to compute - which is what
    /// kept it untestable and therefore wrong.
    /// </para>
    /// </remarks>
    /// <param name="template">A blurb containing <c>{n}</c>.</param>
    /// <param name="languages">How many languages are installed; 0 if unknown.</param>
    public static string Languages(string template, int languages)
    {
        if (!template.Contains("{n}", StringComparison.Ordinal)) return template;

        // NOT A NUMBER AT ALL when there is nothing to count. "in 0 languages" is
        // arithmetic reported at somebody who asked what their phone can do.
        return languages switch
        {
            <= 0 => "Reads things out loud, in your language",
            1    => template.Replace("{n}", "1 language"),
            _    => template.Replace("{n}", $"{languages} languages"),
        };
    }
}
