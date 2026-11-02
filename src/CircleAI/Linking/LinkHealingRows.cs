// LinkHealingRows.cs
//
// THE HEALING VERB'S ROWS, IN BOTH DIRECTIONS (S6).
//
// One reply type for every verb - rows of strings - and the column order in ONE place, for the
// reason LinkSetupRows gives: a service that writes [trips, name] and a client that reads
// [name, trips] is a bug that compiles. Every row starts with its kind, so a client reads the
// rows it knows and skips the ones a newer service added.
//
// NOTHING A PERSON SAID IS IN ANY OF THEM. A healing record crosses the link as its fingerprint,
// its area, the remedy and what was done - never its message, which is a failure's template and
// stays on the device with the log. The rows go to any app holding the chat grant.

using System.Globalization;

namespace CircleAI.Linking;

/// <summary>Reads and changes how the service heals itself - the service end of the healing verbs.</summary>
public interface ILinkHealing
{
    /// <summary>The healing rows: summary, switches, knobs, then up to <paramref name="limit"/> records.</summary>
    Task<IReadOnlyList<IReadOnlyList<string>>> RowsAsync(int limit, CancellationToken ct = default);

    /// <summary>
    /// Applies the named arguments in <see cref="LinkHealingRows"/>. Null when every one applied;
    /// otherwise why one could not be.
    /// </summary>
    /// <param name="args">The arguments: autonomy, reports, handled, selfcheck, endpoint.</param>
    /// <param name="firstParty">Whether the caller is one of our own apps - the only ones that may move the endpoint.</param>
    /// <param name="ct">Cancellation.</param>
    Task<string?> SetAsync(IReadOnlyDictionary<string, string> args, bool firstParty, CancellationToken ct = default);
}

/// <summary>The wire shape of the healing rows, both ways.</summary>
public static class LinkHealingRows
{
    /// <summary>Set the autonomy dial: off, suggest-only or auto-fix-safe.</summary>
    public const string ArgAutonomy = "autonomy";

    /// <summary>Switch the WolverineAPI reports: on or off.</summary>
    public const string ArgReports = "reports";

    /// <summary>Mark one record handled, by its id.</summary>
    public const string ArgHandled = "handled";

    /// <summary>"1" runs one sample failure through the real loop.</summary>
    public const string ArgSelfCheck = "selfcheck";

    /// <summary>Where reports are sent (https), or empty to keep them on the device. Our own apps only.</summary>
    public const string ArgEndpoint = "endpoint";

    /// <summary>The summary row's kind.</summary>
    public const string Summary = "summary";

    /// <summary>A switch row's kind.</summary>
    public const string Switch = "switch";

    /// <summary>A knob row's kind.</summary>
    public const string Knob = "knob";

    /// <summary>A record row's kind.</summary>
    public const string Item = "item";

    /// <summary>
    /// [summary, autonomy, reports, queued, endpointSet, lastSentUtc, recentFailures, healed, needsYou, lastHealedUtc].
    /// </summary>
    public static IReadOnlyList<string> SummaryRow(string autonomy, bool reports, int queued, bool endpointSet,
        DateTimeOffset? lastSentUtc, int recentFailures, int healed, int needsYou, DateTimeOffset? lastHealedUtc)
        => [Summary, autonomy, Flag(reports), N(queued), Flag(endpointSet), Time(lastSentUtc), N(recentFailures), N(healed), N(needsYou), Time(lastHealedUtc)];

    /// <summary>[switch, name, light, trips, why, backOnUtc] - one of SelfHeal's switches.</summary>
    public static IReadOnlyList<string> SwitchRow(string name, string light, int trips, string why, DateTimeOffset? backOnUtc)
        => [Switch, name, light, N(trips), why, Time(backOnUtc)];

    /// <summary>[knob, name, value, untilUtc] - one knob in force on the brain.</summary>
    public static IReadOnlyList<string> KnobRow(string name, string value, DateTimeOffset untilUtc)
        => [Knob, name, value, Time(untilUtc)];

    /// <summary>
    /// [item, id, atUtc, area, fingerprint, remedy, actionTaken, outcome, needsHuman, handled, recommended].
    /// </summary>
    public static IReadOnlyList<string> ItemRow(string id, DateTimeOffset atUtc, string area, string fingerprint, string remedy,
        string actionTaken, string outcome, bool needsHuman, bool handled, string recommended)
        => [Item, id, Time(atUtc), area, fingerprint, remedy, actionTaken, outcome, Flag(needsHuman), Flag(handled), recommended];

    /// <summary>The kind a row says it is; empty for a row with nothing in it.</summary>
    public static string KindOf(IReadOnlyList<string> row) => LinkSetupRows.Text(row, 0);

    /// <summary>A column as a time, or null when it is empty or will not parse.</summary>
    public static DateTimeOffset? TimeAt(IReadOnlyList<string> row, int at) =>
        DateTimeOffset.TryParse(LinkSetupRows.Text(row, at), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private static string Flag(bool value) => value ? "1" : "0";

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset? value) => value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;
}
