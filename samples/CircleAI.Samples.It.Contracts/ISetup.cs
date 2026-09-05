// ISetup.cs
//
// First run: what this phone still needs, and fetching it.

namespace CircleAI.Samples.It;

/// <summary>How ready the app is, coarsely.</summary>
public enum ReadyStage
{
    /// <summary>Nothing usable yet.</summary>
    Waking,

    /// <summary>It can hear you and speak back, but cannot think yet.</summary>
    CanListen,

    /// <summary>Everything is up.</summary>
    Ready,

    /// <summary>Something needed is missing and will not arrive on its own.</summary>
    NeedsSetup,
}

/// <summary>Readiness as a person would describe it.</summary>
/// <param name="Stage">Coarse state, for choosing what to show.</param>
/// <param name="Headline">The big line. An instruction wherever possible.</param>
/// <param name="Caption">One quieter line under it, or empty.</param>
/// <param name="CanTalk">True when pressing the circle will do something.</param>
/// <remarks>
/// EVERY LINE IS AN INSTRUCTION OR A FACT, never a status word. "Ready",
/// "Initialising", "Loading model" describe the machine's inner life and leave the
/// person to work out what to do about it. "Tap and talk" tells them what to do;
/// "Getting ready - you can talk in a moment" tells them what is happening AND
/// that waiting is the right move.
/// <para>
/// Deliberately short and concrete, because it has to work for someone who is
/// seven and someone who is eighty, on the first read, with no help available.
/// </para>
/// </remarks>
public readonly record struct Readiness(
    ReadyStage Stage, string Headline, string Caption, bool CanTalk);

/// <summary>One thing this device either has or does not.</summary>
/// <param name="Title">What it gives the person - "the ears", not "Whisper-tiny".</param>
/// <param name="Present">Whether the bytes are on this device.</param>
/// <param name="Bytes">How big it is, or would be.</param>
/// <param name="Detail">What it means here - "eleven languages", "not on this phone yet".</param>
public sealed record CensusRow(string Title, bool Present, long Bytes, string Detail);

/// <summary>What this device can do, and what it is still missing.</summary>
/// <param name="Rows">Every capability, present or not, in a fixed order.</param>
/// <param name="Present">How many are here.</param>
/// <param name="Total">How many there are.</param>
/// <param name="Summary">"3 of 5 on this phone", said once so a screen need not.</param>
public sealed record Census(
    IReadOnlyList<CensusRow> Rows, int Present, int Total, string Summary);

/// <summary>One thing setup will fetch.</summary>
/// <param name="Title">What it gives the person - "the voice", not "MMS TTS".</param>
/// <param name="Bytes">How big it is.</param>
public sealed record SetupItem(string Title, long Bytes);

/// <summary>How far setup has got.</summary>
/// <param name="Index">Zero-based step being fetched.</param>
/// <param name="Count">How many steps in total.</param>
/// <param name="Title">The current step's words.</param>
/// <param name="Fraction">0..1 across the WHOLE of setup, weighted by bytes.</param>
/// <param name="Remaining">
/// What is left of the whole setup, not just this part.
/// </param>
/// <remarks>
/// THE NUMBERS ARE THE POINT, because the wait is not one length. The same bundle
/// is minutes on a premium handset and most of an hour on a P30 Lite - and a
/// person wants to know when they can use the phone, which is when the LAST byte
/// lands, not this one.
/// </remarks>
/// <summary>What setup is doing, in the only three states a person cares about.</summary>
/// <remarks>
/// A SMALLER VOCABULARY THAN THE ENGINE'S ON PURPOSE. CircleAI.Core's
/// DownloadPhase has six states - downloading, resuming, retrying, verifying,
/// cached, complete - and this file may not reference it: the csproj beside it
/// says ZERO ProjectReferences, because a WebAssembly client loads this
/// assembly. So the head maps six engine states onto the three that change what
/// a person should DO, and the mapping lives in exactly one place.
/// </remarks>
public enum SetupPhase
{
    /// <summary>Bytes are moving. The estimate means something.</summary>
    Fetching,

    /// <summary>
    /// Checking what arrived. No bytes move and it can take a minute.
    /// </summary>
    /// <remarks>
    /// THE STATE THAT LOOKED LIKE A CRASH. Measured on a Redmi Note 12 Pro+ on
    /// 2026-09-05: the 1.3 GB brain finished downloading, the phone went to 267%
    /// CPU hashing it with the network idle, and the screen sat on "about 20 sec
    /// left" without moving for over a minute. Nothing was wrong. The screen
    /// simply had no way to say what was happening, because the phase was
    /// dropped twice on its way here and the only thing left to render was a
    /// countdown that had stopped being updated.
    /// </remarks>
    Checking,

    /// <summary>Everything is present and verified.</summary>
    Done,
}

/// <param name="Phase">
/// What is happening now. Defaulted, so every existing caller still compiles and
/// still reports a download.
/// </param>
public sealed record SetupProgressReport(
    int Index, int Count, string Title, double Fraction, TimeSpan Remaining,
    SetupPhase Phase = SetupPhase.Fetching);

/// <summary>Something worth doing while setup runs.</summary>
/// <param name="Title">The invitation.</param>
/// <param name="Body">Why it is worth the tap.</param>
/// <param name="Action">The button's words, or null for a step that only reads.</param>
/// <param name="Route">Where the action goes, or null.</param>
/// <remarks>
/// THE WAIT, SPENT. On a slow link this is forty-five minutes somebody has to
/// spend anyway; on a fast one it barely appears. Offering something useful during
/// it is the difference between a progress bar and an app.
/// </remarks>
public sealed record TourStep(string Title, string Body, string? Action, string? Route);

/// <summary>What first run needs, and how to do it.</summary>
public interface ISetup
{
    /// <summary>Whether anything at all is installed yet.</summary>
    Task<Readiness> ReadinessAsync(CancellationToken ct = default);

    /// <summary>
    /// What this device still needs, in the order that makes it useful soonest.
    /// </summary>
    /// <remarks>
    /// Anything already on disk is skipped, so this doubles as "finish what was
    /// interrupted": a setup that died halfway through the brain resumes at the
    /// brain rather than starting again from the voice.
    /// </remarks>
    Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default);

    /// <summary>
    /// What this device can already do, and what it cannot yet.
    /// </summary>
    /// <remarks>
    /// THE SAME LIST THE PLAN COMES FROM, read the other way. The plan says what
    /// is still to fetch; this says what is here - and a screen showing both is
    /// showing one fact twice rather than two facts that could disagree.
    ///
    /// It exists because the seconds somebody spends waiting are the only
    /// seconds they will ever spend reading about what their phone can do. On a
    /// cheap handset more is missing, and the people most likely to be missing
    /// something are exactly who this is for.
    /// </remarks>
    Task<Census> CensusAsync(CancellationToken ct = default);

    /// <summary>Whether a run is already in flight.</summary>
    /// <remarks>
    /// THE PAGE CANNOT KNOW THIS ON ITS OWN. Setup keeps "am I running" in the
    /// component, and a component dies the moment somebody taps Home on the bar
    /// while 817 MB is coming down. Coming back built a fresh one that knew
    /// nothing, showed the plan again and offered Start - which started a SECOND
    /// concurrent download of the same files, over the same connection, onto a
    /// phone that was already struggling with the first.
    /// </remarks>
    bool IsRunning { get; }

    /// <summary>
    /// Fetch everything in the plan, reporting progress across all of it.
    /// </summary>
    /// <remarks>
    /// IDEMPOTENT. Calling it while a run is in flight ATTACHES to that run
    /// rather than starting another, so a page that comes back mid-download picks
    /// the progress up where it is instead of duplicating the work.
    /// </remarks>
    Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default);

    /// <summary>What to offer while the wait runs.</summary>
    Task<IReadOnlyList<TourStep>> TourAsync(TimeSpan remaining, CancellationToken ct = default);

    /// <summary>Ask for the microphone, here, without going anywhere.</summary>
    /// <remarks>
    /// THE BUTTON SAID "ALLOW THE MICROPHONE" AND WAS A LINK. It routed to the
    /// wake screen, which asked on arrival - so pressing it moved somebody to a
    /// different page mid-setup and the prompt appeared over that instead. A
    /// control whose label is a verb has to do the verb.
    /// </remarks>
    Task<bool> AllowMicrophoneAsync(CancellationToken ct = default);

    /// <summary>Ask to be exempt from battery killing, here.</summary>
    /// <remarks>
    /// "ALLOW IT TO RUN" WENT TO THIS APP'S OWN SETTINGS SCREEN, which has never
    /// had a battery control on it - so the one step that decides whether the
    /// assistant is still alive in an hour did nothing at all.
    /// <para>
    /// Returns false when nothing could be opened, which is a real outcome:
    /// vendors move these screens between firmwares, and a phone that has none
    /// should be told what to look for rather than shown a button that failed
    /// silently.
    /// </para>
    /// </remarks>
    Task<bool> AllowBackgroundAsync(CancellationToken ct = default);

    /// <summary>Whether this phone will let the assistant keep running.</summary>
    /// <remarks>
    /// ASKING WAS ONLY EVER OFFERED DURING FIRST-RUN SETUP, so a phone set up
    /// before the step existed - or one where somebody said no - had no
    /// exemption and no route back to one. Measured on a P30 on 2026-09-05:
    /// exemption not granted, and the always-listening service was hibernated by
    /// EMUI while its owner spoke to it for eleven minutes.
    /// <para>
    /// A setting you can only choose once, during a flow you pass through in the
    /// first minute, is not a setting. This is what lets the switch it protects
    /// show the question again, and show it ONLY when the answer is no - an
    /// always-visible prompt about battery optimisation is noise on the phones
    /// that already work.
    /// </para>
    /// <para>
    /// TRUE WHEN THERE IS NOTHING TO ASK FOR. A head with no background service
    /// has nothing that can be killed, so it says yes rather than offering a
    /// button that would do nothing.
    /// </para>
    /// </remarks>
    Task<bool> BackgroundAllowedAsync(CancellationToken ct = default);
}
