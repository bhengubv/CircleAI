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

/// <summary>One thing CircleAI is, or is not, allowed to do on this device.</summary>
/// <param name="Title">What it lets CircleAI do, in a person's words.</param>
/// <param name="Why">Why CircleAI asks for it — one sentence, concrete.</param>
/// <param name="Granted">Whether the person has allowed it.</param>
/// <param name="Runtime">
/// True when it is a runtime permission somebody can change, false when it is granted
/// at install and cannot be withdrawn. The difference decides whether a screen should
/// offer to do anything about it.
/// </param>
/// <remarks>
/// THE SERVICE HAS NO LAUNCHER ICON, SO NOTHING COULD SHOW THIS. CircleAI holds the
/// microphone, the foreground service and the biometric prompt, and the only way to
/// see any of that was to find it in Android's own app list — which a person has no
/// reason to know exists, because it never appears in a launcher. An engine somebody
/// cannot inspect is an engine they have to take on trust.
/// <para>
/// NAMED FOR WHAT IT DOES, NOT FOR THE CONSTANT. "android.permission.RECORD_AUDIO"
/// tells somebody nothing they did not already fear; "Hear you, when you speak to it"
/// and a reason tells them what they are agreeing to.
/// </para>
/// </remarks>
public sealed record PermissionRow(string Title, string Why, bool Granted, bool Runtime);

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
public sealed record StorageReport(
    IReadOnlyList<StorageLine> Lines, string Total, string Freeable)
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

    /// <summary>What CircleAI is allowed to do on this device, and what it is not.</summary>
    /// <remarks>
    /// A DEFAULT OF NOTHING, so a head with no engine behind it — the browser — shows
    /// an empty section rather than a list of permissions it invented. The head that
    /// can ask the service answers properly; everything else says nothing, which is
    /// true.
    /// </remarks>
    Task<IReadOnlyList<PermissionRow>> PermissionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PermissionRow>>(System.Array.Empty<PermissionRow>());

    /// <summary>Open the place a person can change those permissions.</summary>
    /// <remarks>
    /// THE APP CANNOT GRANT THE SERVICE'S PERMISSIONS, and it should not pretend to.
    /// A runtime permission belongs to the package that holds it, so the honest action
    /// is to put the person in front of CircleAI's own permission screen — which is
    /// reachable by intent from any app, needs no permission to launch, and is the
    /// only place the decision can actually be made.
    /// <para>
    /// False when this head has no way to open it, so a button is not drawn.
    /// </para>
    /// </remarks>
    Task<bool> OpenPermissionsAsync(CancellationToken ct = default)
        => Task.FromResult(false);
}
