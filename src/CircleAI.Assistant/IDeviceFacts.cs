// IDeviceFacts.cs
//
// What the "What it can do" screen needs, and only a head can answer.
//
// Abilities are read off the model registry and the device probe: which models
// are catalogued, which are actually on disk, which would fit in this phone's
// memory, and what the phone is. None of that exists in a browser tab, and none
// of it belongs in the shared UI - the screen renders rows, it does not decide
// what a row says.

namespace CircleAI.Assistant;

/// <summary>What state an ability is in on this device.</summary>
public enum AbilityState
{
    /// <summary>Downloaded, wired, AND actually doing its job right now.</summary>
    On,

    /// <summary>
    /// Everything it needs is here, and it is not switched on.
    /// </summary>
    /// <remarks>
    /// THE STATE THAT WAS MISSING, AND ITS ABSENCE WAS A LIE. On used to mean "a
    /// model is on disk", so Settings printed "Waking ✓ On" the moment the wake
    /// bundle finished downloading - while nothing was listening. Three rows
    /// below it sat a toggle that reads IsListening LIVE, because Android can
    /// kill the service and a remembered bool would drift. One screen, two
    /// answers, and only one of them was true.
    /// <para>
    /// This file already carried the warning: "a build advertised Waking ✓ On on
    /// a phone that could not wake at all". That was fixed for the case where the
    /// CODE was missing and not for the case where the code is there and nothing
    /// is running.
    /// </para>
    /// <para>
    /// Distinct from <see cref="Available"/> because nothing needs downloading -
    /// a screen must offer a switch here, not a size.
    /// </para>
    /// </remarks>
    Ready,

    /// <summary>Catalogued and it fits - it just has not been downloaded.</summary>
    Available,

    /// <summary>Catalogued, but this phone does not have the memory or the space.</summary>
    TooBig,

    /// <summary>Nothing in the catalogue serves this at all.</summary>
    NotCatalogued,
}

/// <summary>One thing the phone can do, in the words a person would use.</summary>
/// <param name="Title">What it is. A verb, not a noun - "Talking", not "TTS".</param>
/// <param name="Blurb">What it means for you, in one sentence.</param>
/// <param name="State">Where it stands on this device.</param>
/// <param name="Bytes">
/// Download size when it is <see cref="AbilityState.Available"/>, else null.
/// </param>
/// <param name="TryRoute">
/// A screen that demonstrates it, or null. An ability that is ON should be
/// somewhere you can GO rather than just a tick - but a row that looks tappable
/// and does nothing is worse than a plain one, so this is null unless a screen
/// really exists.
/// </param>
public sealed record AbilityRow(
    string Title,
    string Blurb,
    AbilityState State,
    long? Bytes = null,
    string? TryRoute = null);

/// <summary>One labelled fact about the phone.</summary>
public sealed record PhoneFact(string Title, string Value);

/// <summary>What this phone is, and what CircleAI does about it.</summary>
/// <param name="Facts">The plain-language lines, in order.</param>
/// <param name="Technical">
/// The model-by-model detail, shown only when asked for. Two audiences: the owner
/// wants to turn something on, the developer wants to know what it costs.
/// </param>
public sealed record PhoneFacts(
    IReadOnlyList<PhoneFact> Facts,
    IReadOnlyList<string> Technical);

/// <summary>One category of Circle AI's storage, ready to render.</summary>
/// <param name="Label">What it is, in a person's words — "Downloaded models".</param>
/// <param name="Size">How big, human-readable — "2.0 GB".</param>
/// <param name="Regenerable">
/// True when it comes back for free (skills, voice data, scratch); false for the
/// precious things that cost data to replace or cannot be got back at all —
/// downloaded models and what the phone remembers.
/// </param>
public sealed record StorageLine(string Label, string Size, bool Regenerable);

/// <summary>What Circle AI is using on this phone, for a storage screen.</summary>
/// <param name="Lines">The breakdown by category.</param>
/// <param name="Total">Everything Circle AI holds — "2.1 GB".</param>
/// <param name="Freeable">
/// What one tap can free right now at no cost to the person — the regenerable
/// scratch — human-readable, or empty when there is nothing to free.
/// </param>
/// <param name="Policy">
/// The standing rule that keeps the scratch in bounds on its own, in a sentence —
/// "Clears scratch older than 30 days · caps it at 256 MB." — so a background
/// tidy-up is shown, never a surprise. Empty for a head that does not self-manage.
/// </param>
public sealed record StorageReport(
    IReadOnlyList<StorageLine> Lines, string Total, string Freeable, string Policy = "")
{
    /// <summary>The answer for a head that cannot measure its own footprint (the
    /// browser): an empty breakdown, so its storage screen shows nothing rather
    /// than a fabricated number.</summary>
    public static StorageReport None { get; } =
        new(System.Array.Empty<StorageLine>(), string.Empty, string.Empty);
}

/// <summary>Answers the "what can it do, and on what" questions for a head.</summary>
public interface IDeviceFacts
{
    /// <summary>
    /// The abilities, in the order the screen shows them.
    /// </summary>
    /// <remarks>
    /// DRIVEN BY WHAT THE BUILD CAN ACTUALLY RUN, not by what is on disk. A model
    /// left behind by an earlier install is not an ability: the chat-only APK
    /// shipped without the speech stack and still advertised "Waking ✓ On" on a
    /// phone that could not wake at all. Files on disk are not an ability - an
    /// ability is code that runs.
    /// </remarks>
    Task<IReadOnlyList<AbilityRow>> AbilitiesAsync(CancellationToken ct = default);

    /// <summary>What this phone is.</summary>
    Task<PhoneFacts> PhoneAsync(CancellationToken ct = default);

    /// <summary>Download and enable one ability, reporting progress in words.</summary>
    /// <returns>What happened, for the row to show.</returns>
    Task<string> TurnOnAsync(
        string title, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>What Circle AI is using on disk, broken down for a storage screen.</summary>
    /// <remarks>
    /// A DEFAULT so only the head that can measure a real footprint (the phone,
    /// via the Memory Manager) implements it; the browser inherits
    /// <see cref="StorageReport.None"/> and its storage screen simply shows nothing.
    /// This is the Memory Manager's footprint made visible — distinct from
    /// <see cref="PhoneAsync"/>'s "Space free", which is the DEVICE's free space
    /// from a different reader. The two measure different things and must never be
    /// shown as one number.
    /// </remarks>
    Task<StorageReport> StorageAsync(CancellationToken ct = default)
        => Task.FromResult(StorageReport.None);

    /// <summary>Free the regenerable scratch and say what came back, in a sentence.</summary>
    /// <remarks>Default: nothing to free — only a real footprint can be reclaimed.</remarks>
    Task<string> ReclaimStorageAsync(CancellationToken ct = default)
        => Task.FromResult(string.Empty);
}
