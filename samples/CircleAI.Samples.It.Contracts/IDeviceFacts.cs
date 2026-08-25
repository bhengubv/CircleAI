// IDeviceFacts.cs
//
// What the "What it can do" screen needs, and only a head can answer.
//
// Abilities are read off the model registry and the device probe: which models
// are catalogued, which are actually on disk, which would fit in this phone's
// memory, and what the phone is. None of that exists in a browser tab, and none
// of it belongs in the shared UI - the screen renders rows, it does not decide
// what a row says.

namespace CircleAI.Samples.It;

/// <summary>What state an ability is in on this device.</summary>
public enum AbilityState
{
    /// <summary>A model is on disk and the code that uses it is in this build.</summary>
    On,

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
}
