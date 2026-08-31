// ISettings.cs
//
// What the app is set up to do, and how.
//
// WHY THIS EXISTS AT ALL, given the screen next door argues against settings.
// That argument was about ONE property: the conversation language. A household
// here moves through two or three languages inside a single exchange, so a
// persistent control for it is wrong and the picker is a read-out instead.
//
// It was never an argument against configuration. Everything below IS persistent
// - nobody changes their wake phrase mid-sentence, or where their CV is filed, or
// whether the app is being used as an assistant or an interpreter. Those were
// simply never given anywhere to live, and "This phone" is a diagnostics readout
// wearing a settings tab's clothes: it reports, it configures nothing.

namespace CircleAI.Samples.It;

/// <summary>What the app is being used for.</summary>
/// <remarks>
/// The shell decides what the app IS. The libraries underneath serve translation,
/// documents, vision and more; a shell that only ever offers a conversation and a
/// CV makes a versatile thing look like a narrow one.
/// </remarks>
public enum AppMode
{
    /// <summary>Ask it things and have it answer. The default.</summary>
    Assistant,

    /// <summary>
    /// Translate between two languages, rather than answering in one.
    /// </summary>
    Translator,
}

/// <summary>How the conversation language is decided.</summary>
public enum LanguagePolicy
{
    /// <summary>
    /// Every turn is answered in the language it was spoken in.
    /// </summary>
    /// <remarks>
    /// THE RIGHT DEFAULT HERE, and the reason the picker is a read-out: a clan
    /// moves through two or three languages inside one exchange, so a language
    /// fixed on the first turn is wrong by the third.
    /// </remarks>
    FollowTheSpeaker,

    /// <summary>Always answer in one chosen language.</summary>
    Fixed,
}

/// <summary>Everything the app remembers about how it should behave.</summary>
/// <param name="Mode">Assistant or interpreter.</param>
/// <param name="Language">
/// THE LANGUAGE THE APP WORKS IN - what you speak or type to it.
/// </param>
/// <param name="Policy">How the answering language is decided.</param>
/// <param name="FixedLanguage">
/// The language to answer in when <see cref="LanguagePolicy.Fixed"/>.
/// </param>
/// <param name="WakeEnabled">Whether to listen for the wake phrase at all.</param>
/// <remarks>
/// THERE IS NO WakeLanguage, AND THERE MUST NOT BE ONE.
/// <para>
/// There was. The wake phrase had its own language setting, on the reasoning that
/// it is a different property from the answering language - which was true, and
/// produced a control that let somebody run the app in English and wake it with
/// ビーさん. Nobody wants that. The phrase you say to wake a phone is the language
/// you are already speaking to it in; it is not a choice, it is a consequence of
/// <paramref name="Language"/>.
/// </para>
/// <para>
/// What IS a choice is WHICH phrase, because a language can have several -
/// Japanese has ビーさん, ビーさま and Bee san - and whether there is one at all,
/// because most languages have none yet and the honest answer is to let somebody
/// add theirs. That lives in <see cref="IWakePhrases"/>, keyed by language.
/// </para>
/// <para>
/// The bug this replaced is still real and still guarded: picking a language used
/// to silently change the wake phrase with nothing on screen saying so. The fix
/// is to SHOW the phrase that the language implies, not to detach the two.
/// </para>
/// </remarks>
public sealed record AppSettings(
    AppMode Mode = AppMode.Assistant,
    string Language = "en",
    LanguagePolicy Policy = LanguagePolicy.FollowTheSpeaker,
    string? FixedLanguage = null,
    bool WakeEnabled = true);

/// <summary>One document the app has produced.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Kind">"CV", "Cover letter", "Invoice" - in the person's words.</param>
/// <param name="Path">Where it is on disk.</param>
/// <param name="Bytes">How big.</param>
/// <param name="Written">When it was last written, UTC.</param>
public sealed record StoredDocument(
    string Name, string Kind, string Path, long Bytes, DateTime Written);

/// <summary>Reads and writes what the app is set up to do.</summary>
public interface ISettings
{
    /// <summary>The current settings.</summary>
    Task<AppSettings> LoadAsync(CancellationToken ct = default);

    /// <summary>Store new settings, and apply anything that takes effect at once.</summary>
    /// <remarks>
    /// APPLYING MATTERS AS MUCH AS STORING. The resident wake listener is built
    /// once and keeps running; changing the wake language without rebuilding it
    /// leaves the microphone waiting for the old phrase, with nothing on screen to
    /// say so. That has already happened.
    /// </remarks>
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);

    /// <summary>Everything the app has written for this person.</summary>
    Task<IReadOnlyList<StoredDocument>> DocumentsAsync(CancellationToken ct = default);

    /// <summary>
    /// A setting belonging to ONE service, rather than to the app.
    /// </summary>
    /// <remarks>
    /// LANGUAGE IS NOT ONE SETTING. Somebody can use the app in English, want
    /// their CV written in Japanese, and want interpreting between Korean and
    /// Mandarin - three different answers to "which language", each belonging to
    /// the service that asks. A single global value forces all three to agree,
    /// which is wrong for every person who needs more than one.
    /// <para>
    /// Keyed by service so a new service brings its own settings without touching
    /// this contract or the settings screen.
    /// </para>
    /// </remarks>
    Task<string?> ServiceSettingAsync(
        string service, string key, string? fallback = null, CancellationToken ct = default);

    /// <summary>Change a setting belonging to one service.</summary>
    Task SetServiceSettingAsync(
        string service, string key, string? value, CancellationToken ct = default);

    /// <summary>Delete one document.</summary>
    /// <remarks>
    /// A CV IS THE MOST PERSONAL THING THIS APP HOLDS. Being able to see what was
    /// kept and remove it is not a convenience, it is the other half of a promise
    /// that nothing leaves the device.
    /// </remarks>
    Task DeleteDocumentAsync(string path, CancellationToken ct = default);
}
