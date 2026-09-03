namespace CircleAI.Samples.It;

/// <summary>How far along a capability actually is, on THIS device, right now.</summary>
/// <remarks>
/// THREE SEPARATE QUESTIONS THAT THE APP ONLY EVER ASKED ONE OF. The setup
/// census answers "is it downloaded" and stops there, so a file that is present
/// and unopenable, and a hook that nothing ever set, both read as ready.
/// <para>
/// That is not hypothetical. The hybrid shipped for weeks able to hear and
/// translate and never speak, because <c>ItSpeaker.MobilePhonemizerFactory</c>
/// was null: no catalogue row covered it - espeak is a csproj link and a line in
/// OnCreate, not a download - so nothing could have reported it. It surfaced as
/// one sentence under a translation somebody had already given up on.
/// </para>
/// </remarks>
public enum WiringStage
{
    /// <summary>Not on the device at all.</summary>
    Absent,

    /// <summary>The bytes are here. Nothing has tried to open them.</summary>
    Present,

    /// <summary>It opened. Whether anything is holding it is a separate question.</summary>
    Loads,

    /// <summary>Present, opens, AND something in this build is actually using it.</summary>
    Wired,

    /// <summary>It is here and it does not work. <see cref="WiringRow.Detail"/> says why.</summary>
    Broken,
}

/// <summary>One capability, and how true the claim about it is.</summary>
/// <param name="Title">What it gives a person - "English voice", not "MMS TTS".</param>
/// <param name="Kind">Grouping for a screen: "hook", "voice", "engine".</param>
/// <param name="Stage">How far it actually got.</param>
/// <param name="Detail">
/// The reason, in the words the failure itself used. Never a summary of it: the
/// string that made this findable was the one the code already produced.
/// </param>
public sealed record WiringRow(string Title, string Kind, WiringStage Stage, string Detail)
{
    /// <summary>Whether this can be relied on to do its job.</summary>
    public bool Working => Stage == WiringStage.Wired;
}

/// <summary>What this build can actually do, as opposed to what it offers.</summary>
/// <param name="Rows">Every capability asked about, working or not.</param>
/// <param name="Working">How many reached <see cref="WiringStage.Wired"/>.</param>
/// <param name="Claimed">How many the app offers - the catalogue's number.</param>
/// <remarks>
/// CLAIMED AND WORKING ARE BOTH REPORTED, and the gap between them is the whole
/// point. The home screen says "78 languages, spoken out loud" from a catalogue
/// count; on a build with no phonemizer wired the true number was one. A report
/// that only carried the working figure would be as unfalsifiable as the claim.
/// </remarks>
public sealed record WiringReport(IReadOnlyList<WiringRow> Rows, int Working, int Claimed)
{
    /// <summary>"1 of 78 voices actually speak on this phone", said once.</summary>
    public string Summary => $"{Working} of {Claimed} working";

    /// <summary>Only the rows that are not doing their job.</summary>
    public IReadOnlyList<WiringRow> Failing =>
        Rows.Where(r => !r.Working).ToList();
}

/// <summary>Asks the device what is really wired, rather than what was intended.</summary>
/// <remarks>
/// EVERY ANSWER COMES FROM THE REAL PATH. A probe that re-derives whether a voice
/// can speak is a second implementation of that rule, free to drift from the one
/// that runs - and two sources of truth is the shape of the bug this exists to
/// catch. So it calls what the app calls and reports what came back.
/// </remarks>
public interface IWiringProbe
{
    /// <summary>
    /// The runtime hooks: the things a head must set that no download can supply.
    /// </summary>
    /// <remarks>
    /// Fast, and safe to run at startup. These are the wires that are either
    /// connected or not - a null static, an unpacked data directory, a native
    /// library that loads - so none of it needs a model or the network.
    /// </remarks>
    Task<WiringReport> HooksAsync(CancellationToken ct = default);

    /// <summary>
    /// Which languages this phone can actually speak out loud.
    /// </summary>
    /// <remarks>
    /// SLOW ON PURPOSE, and not called from a hot path: the only honest way to
    /// know whether a voice speaks is to make it speak, so this walks the
    /// catalogue and asks the real synthesis path for each one.
    /// <para>
    /// <paramref name="languages"/> narrows it - a diagnostics screen showing one
    /// row should cost one voice, not seventy-eight. Null means the whole
    /// catalogue.
    /// </para>
    /// </remarks>
    Task<WiringReport> VoicesAsync(
        IEnumerable<string>? languages = null,
        IProgress<WiringRow>? progress = null,
        CancellationToken ct = default);
}
