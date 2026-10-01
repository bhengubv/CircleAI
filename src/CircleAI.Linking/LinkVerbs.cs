// LinkVerbs.cs
//
// The structured verbs a linked app can call beyond chat, and the scope each one
// needs. Kept next to the grant + turn types, portable and desktop-tested, so the
// client library and the CircleAI service share ONE definition of what a verb is
// and what it costs in trust.
//
// CHAT IS THE FLOOR; THESE ARE OPT-IN. Recall and remember reach the person's
// long-term memory (LinkScope.Memory); skills reach the full library
// (LinkScope.Skills). Discovery is the exception — it reads the honest capability
// manifest, which is the same for everyone and carries nothing private, so it
// rides the chat floor. The scope a verb needs is declared HERE, in one place, so
// the service can never under-ask by accident.

using System;
using System.Collections.Generic;

namespace CircleAI.Linking;

/// <summary>A structured request a linked app makes of the shared brain, beyond a chat turn.</summary>
public enum LinkVerb
{
    /// <summary>Read the person's long-term memory for a situation. Needs <see cref="LinkScope.Memory"/>.</summary>
    Recall,

    /// <summary>Write a fact into the person's long-term memory. Needs <see cref="LinkScope.Memory"/>.</summary>
    Remember,

    /// <summary>Search or list the skill library. Needs <see cref="LinkScope.Skills"/>.</summary>
    Skills,

    /// <summary>List what Circle AI can do (the honest manifest). The chat floor is enough.</summary>
    Capabilities,

    // -- SETTING THE SERVICE UP, FROM THE APP -----------------------------------
    //
    // CircleAI has no screens. Every decision a person makes about it - which
    // models it holds, whether it can see as well as hear - has to arrive from an
    // app they can actually open. These five are that road.
    //
    // THE CHAT FLOOR IS ENOUGH FOR ALL OF THEM, deliberately. Three are pure
    // reads, and the two that act are driven by a person tapping "set it up" in an
    // app they have already approved; the service still applies its own metered-
    // connection rules to the download itself. A separate scope would mean a
    // second biometric sheet for something they just asked for by name.

    /// <summary>Whether the service can hold a conversation yet, and what it is waiting for.</summary>
    SetupReadiness,

    /// <summary>What still has to be fetched for full use, and how big each piece is.</summary>
    SetupPlan,

    /// <summary>What the service already holds, so a person can see what they have.</summary>
    SetupCensus,

    /// <summary>
    /// Begin fetching what <see cref="SetupPlan"/> lists. Returns at once.
    /// </summary>
    /// <remarks>
    /// IT CANNOT BLOCK AND IT MUST NOT TRY. A binder transaction is request and
    /// response on one thread, and this work runs for minutes over a phone network.
    /// So this starts it and answers immediately; <see cref="SetupProgress"/> is how
    /// the caller follows along. Pretending to stream would deadlock the binder
    /// thread for the whole download.
    /// </remarks>
    SetupStart,

    /// <summary>Where the run started by <see cref="SetupStart"/> has got to.</summary>
    SetupProgress,

    // -- THE SERVICE'S OWN SETTINGS ---------------------------------------------
    //
    // SAYING "OPEN CIRCLEAI" IS NOT AN ANSWER WHEN CIRCLEAI HAS NO LAUNCHER ICON.
    // The wake-phrase row told a person to open an app they cannot open - honest
    // about where the setting lives and useless as advice. A service with no screens
    // can only be configured from an app that has some, so its settings come across
    // the wire like everything else.

    /// <summary>The wake phrases for a language, each row [text, chosen, builtIn, quality, advice].</summary>
    WakePhrasesFor,

    /// <summary>Judge a typed phrase without adding it: one row of [added, quality, advice].</summary>
    WakePhraseCheck,

    /// <summary>Add a phrase for a language, unless it cannot work at all.</summary>
    WakePhraseAdd,

    /// <summary>Listen for this phrase from now on.</summary>
    WakePhraseChoose,

    /// <summary>Remove a phrase the person added. Built-in phrases stay.</summary>
    WakePhraseRemove,

    // -- LISTENING, AND WHAT THE SERVICE HOLDS ----------------------------------
    //
    // The last two rows that reported instead of controlling. "Answer to its name"
    // told the person to open CircleAI to turn listening on - the same unreachable
    // advice the wake phrase gave - and the abilities read "Nothing for this yet"
    // because the client, having no models, truthfully had nothing. Both answers
    // are about the SERVICE, so both come from it.

    /// <summary>What the resident listener is doing: one row of [state, status, hint].</summary>
    ResidentStatus,

    /// <summary>Start listening for the wake phrase. Same row shape as <see cref="ResidentStatus"/>.</summary>
    ResidentStart,

    /// <summary>Stop listening. Same row shape.</summary>
    ResidentStop,

    /// <summary>What this device can actually do: [title, blurb, state, bytes].</summary>
    Abilities,

    /// <summary>What Circle AI holds here: [label, size, regenerable], then a totals row.</summary>
    Footprint,
}

/// <summary>One structured call across the link. Flat and string-shaped for the Bundle hop.</summary>
/// <param name="Verb">Which verb.</param>
/// <param name="Query">Recall: the situation text. Skills: the search text (null lists all).</param>
/// <param name="Text">Remember: the fact to store.</param>
/// <param name="Subject">Remember: the situation key to file it under (optional).</param>
/// <param name="Limit">Recall: the most atoms to return.</param>
public sealed record LinkVerbRequest(
    LinkVerb Verb,
    string? Query = null,
    string? Text = null,
    string? Subject = null,
    int Limit = 5);

/// <summary>A row-shaped answer: zero or more rows, each a few string fields.</summary>
/// <remarks>
/// One reply type for every verb, so the wire has one shape to marshal and test.
/// The columns depend on the verb: recall = [text]; skills = [id, name];
/// capabilities = [id, status, summary]; remember returns Ok with no rows.
/// </remarks>
public sealed record LinkRowsReply(bool Ok, IReadOnlyList<IReadOnlyList<string>> Rows, string? Error)
{
    /// <summary>A successful reply carrying <paramref name="rows"/> (possibly empty).</summary>
    public static LinkRowsReply Success(IReadOnlyList<IReadOnlyList<string>> rows) => new(true, rows, null);

    /// <summary>A failed reply. <paramref name="error"/> says why.</summary>
    public static LinkRowsReply Failure(string error) =>
        new(false, Array.Empty<IReadOnlyList<string>>(), error);
}

/// <summary>Where a verb's required scope is decided — once, so the service can't under-ask.</summary>
public static class LinkVerbs
{
    /// <summary>The scope a caller must hold for <paramref name="verb"/>.</summary>
    public static LinkScope RequiredScope(LinkVerb verb) => verb switch
    {
        LinkVerb.Recall       => LinkScope.Memory,
        LinkVerb.Remember     => LinkScope.Memory,
        LinkVerb.Skills       => LinkScope.Skills,
        LinkVerb.Capabilities => LinkScope.Chat,   // the manifest is not private

        // Setting the service up is using it: see the note on the verbs themselves.
        LinkVerb.SetupReadiness => LinkScope.Chat,
        LinkVerb.SetupPlan      => LinkScope.Chat,
        LinkVerb.SetupCensus    => LinkScope.Chat,
        LinkVerb.SetupStart     => LinkScope.Chat,
        LinkVerb.SetupProgress  => LinkScope.Chat,

        // THE WAKE PHRASE IS WHAT THE MICROPHONE LISTENS FOR, so it is gated on Voice
        // rather than the chat floor: changing it changes what the service does with
        // an open microphone, which is a bigger thing than asking it a question.
        LinkVerb.WakePhrasesFor   => LinkScope.Voice,
        LinkVerb.WakePhraseCheck  => LinkScope.Voice,
        LinkVerb.WakePhraseAdd    => LinkScope.Voice,
        LinkVerb.WakePhraseChoose => LinkScope.Voice,
        LinkVerb.WakePhraseRemove => LinkScope.Voice,

        // Listening is the microphone, so it is Voice. What the service HOLDS is not
        // private - it is the same list the setup census gives - so the chat floor
        // covers reading it.
        LinkVerb.ResidentStatus => LinkScope.Voice,
        LinkVerb.ResidentStart  => LinkScope.Voice,
        LinkVerb.ResidentStop   => LinkScope.Voice,
        LinkVerb.Abilities      => LinkScope.Chat,
        LinkVerb.Footprint      => LinkScope.Chat,
        _                     => LinkScope.Chat,
    };
}

/// <summary>
/// The wire shape of a setup row, in both directions.
/// </summary>
/// <remarks>
/// ONE REPLY TYPE, ROWS OF STRINGS, because that is what a Bundle carries cheaply
/// and what every other verb already uses. These helpers keep the column order in
/// ONE place: a service that writes [title, bytes] and a client that reads
/// [bytes, title] is a bug that compiles, ships, and shows somebody a 0-byte
/// download called "4831838208".
/// </remarks>
public static class LinkSetupRows
{
    /// <summary>Readiness: a single row of [stage, headline, caption, canTalk].</summary>
    public static IReadOnlyList<string> Readiness(
        string stage, string headline, string caption, bool canTalk)
        => new[] { stage, headline, caption, canTalk ? "1" : "0" };

    /// <summary>One plan item: [title, bytes].</summary>
    public static IReadOnlyList<string> PlanItem(string title, long bytes)
        => new[] { title, bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) };

    /// <summary>One census row: [title, present, bytes, detail].</summary>
    public static IReadOnlyList<string> CensusRow(string title, bool present, long bytes, string detail)
        => new[]
        {
            title,
            present ? "1" : "0",
            bytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            detail,
        };

    /// <summary>Progress: a single row of [index, count, title, fraction, remainingSeconds, phase].</summary>
    public static IReadOnlyList<string> Progress(
        int index, int count, string title, double fraction, double remainingSeconds, string phase)
        => new[]
        {
            index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            title,
            fraction.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            remainingSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            phase,
        };

    /// <summary>One wake phrase: [text, chosen, builtIn, quality, advice].</summary>
    public static IReadOnlyList<string> WakePhrase(
        string text, bool chosen, bool builtIn, string quality, string advice)
        => new[] { text, chosen ? "1" : "0", builtIn ? "1" : "0", quality, advice };

    /// <summary>A verdict on a phrase: [added, quality, advice].</summary>
    public static IReadOnlyList<string> WakeVerdict(bool added, string quality, string advice)
        => new[] { added ? "1" : "0", quality, advice };

    /// <summary>The resident listener: [state, status, hint, heard].</summary>
    /// <remarks>
    /// HEARD IS A COUNTER, NOT AN EVENT, BECAUSE A BINDER CANNOT PUSH. The wake
    /// phrase fires in the service's process and there is no verb that calls back, so
    /// a client watching the wake screen had no way to learn it had been heard — the
    /// one thing somebody opens that screen to find out. A monotonic count turns the
    /// event into state: a client that sees the number go up knows it woke, without
    /// the service having to reach into it.
    /// </remarks>
    public static IReadOnlyList<string> Resident(
        string state, string status, string hint, int heard = 0)
        => new[]
        {
            state, status, hint,
            heard.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

    /// <summary>One ability: [title, blurb, state, bytes, route].</summary>
    /// <remarks>
    /// THE ROUTE IS WHAT MAKES THE ROW DO ANYTHING, AND IT WAS DROPPED HERE.
    /// AbilityRow.TryRoute is how a screen tells "turn this on" from "fetch this":
    /// the Waking row is the one ability whose button starts a listener rather than
    /// a download, and the screen decides which by reading the route. Four columns
    /// left it null on the far side, so a linked client sent every Waking tap down
    /// the download path — a 548 MB setup run for a bundle already on the phone,
    /// after which the row still read "Turn on" because nothing had been started.
    /// <para>
    /// Empty for an ability the serving head has no screen for, which is already
    /// what TryRoute means — see IDeviceFacts: "a row that looks tappable and does
    /// nothing is worse than a plain one".
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Ability(
        string title, string blurb, string state, long? bytes, string? route = null)
        => new[]
        {
            title, blurb, state,
            bytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            route ?? string.Empty,
        };

    /// <summary>One storage line: [label, size, regenerable].</summary>
    public static IReadOnlyList<string> Storage(string label, string size, bool regenerable)
        => new[] { label, size, regenerable ? "1" : "0" };

    /// <summary>Read a column, or a default when the row is short or the text will not parse.</summary>
    /// <remarks>
    /// A SHORT ROW IS A VERSION SKEW, NOT A CRASH. The two apps are installed and
    /// updated separately, so an older service will one day answer a newer client.
    /// Every read here tolerates a missing column rather than throwing across the
    /// link, because a setup screen that cannot render is worse than one missing a
    /// number.
    /// </remarks>
    public static string Text(IReadOnlyList<string> row, int at, string fallback = "")
        => row is not null && at < row.Count && row[at] is not null ? row[at] : fallback;

    /// <summary>Read a column as a long.</summary>
    public static long Number(IReadOnlyList<string> row, int at, long fallback = 0)
        => long.TryParse(Text(row, at), System.Globalization.NumberStyles.Integer,
                         System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Read a column as a double.</summary>
    public static double Fraction(IReadOnlyList<string> row, int at, double fallback = 0)
        => double.TryParse(Text(row, at), System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    /// <summary>Read a column as a flag: "1" is true and everything else is false.</summary>
    public static bool Flag(IReadOnlyList<string> row, int at)
        => Text(row, at) == "1";
}
