// DeviceSettings.cs
//
// What the app is set up to do, kept across launches.

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceSettings : ISettings
{
    private readonly ISpokenLanguage _spoken;
    private readonly SqliteAppStore _store;

    /// <summary>
    /// Takes the language store, because saving has to apply as well, and the one
    /// SQLite file the whole app keeps its state in.
    /// </summary>
    public DeviceSettings(ISpokenLanguage spoken, SqliteAppStore store)
    {
        _spoken = spoken;
        _store = store;
    }

    private const string ModeKey = "app.mode";
    private const string LanguageKey = "app.language";
    private const string PolicyKey = "app.language.policy";
    private const string FixedKey = "app.language.fixed";
    private const string WakeOnKey = "app.wake.enabled";

    private static string DocumentsDir => FileSystem.AppDataDirectory;

    /// <inheritdoc />
    public Task<AppSettings> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(new AppSettings(
            ReadMode(_store.Get(ModeKey, nameof(AppMode.Assistant))!),
            // THE LANGUAGE THE APP WORKS IN. Set first, because everything below
            // reads off it: the wake phrase is whichever one this language has,
            // and answering either follows the speaker or pins a language of its
            // own. English by default because the setup wizard runs in it.
            // THE LAST HARD-CODED ENGLISH, and ISpokenLanguage was already
            // injected one line above it. Nothing had to be plumbed - the answer
            // was in the constructor and the default ignored it.
            _store.Get(LanguageKey, _spoken.Current)!,
            Enum.TryParse<LanguagePolicy>(
                _store.Get(PolicyKey, nameof(LanguagePolicy.FollowTheSpeaker))!, out var p)
                ? p : LanguagePolicy.FollowTheSpeaker,
            _store.Get(FixedKey),
            _store.GetBool(WakeOnKey, true)));

    /// <inheritdoc />
    /// <summary>The stored mode, including the name it used to be saved under.</summary>
    /// <remarks>
    /// THE MODE IS PERSISTED BY NAME, so renaming the enum silently resets every
    /// phone that had it set - the parse fails and falls back to Assistant. The
    /// product calls it translating now; the value on disk may still say
    /// "Interpreter", and that person chose it deliberately.
    /// </remarks>
    private static AppMode ReadMode(string stored) =>
        string.Equals(stored, "Interpreter", StringComparison.OrdinalIgnoreCase)
            ? AppMode.Translator
            : Enum.TryParse<AppMode>(stored, out var m) ? m : AppMode.Assistant;

    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        _store.Set(ModeKey, settings.Mode.ToString());
        _store.Set(LanguageKey, settings.Language);
        _store.Set(PolicyKey, settings.Policy.ToString());
        _store.SetBool(WakeOnKey, settings.WakeEnabled);

        if (settings.FixedLanguage is null) _store.Set(FixedKey, null);
        else _store.Set(FixedKey, settings.FixedLanguage);

        // APPLYING, NOT JUST STORING.
        //
        // The policy is not a note about a preference - it IS the choice the
        // picker writes. Fixed means a person decided, which detection must not
        // overwrite; following the speaker means handing control back, which is
        // exactly what ClearChoice does and what nothing in the UI called until
        // now. Storing the radio button without doing this would leave a screen
        // saying one thing and the app doing another.
        //
        // THE APP LANGUAGE GOES DOWN FIRST, AND THE POLICY ON TOP OF IT. Choose()
        // writes two things - the language in use, and a flag saying a person
        // decided it - and only the first belongs to the app language. So the app
        // language is written as a choice and then, under "follow the speaker",
        // the flag is cleared again: the language it starts in is the one you set,
        // and detection is still free to move off it on the next turn.
        //
        // Without the first call, a phone set to Japanese and left on "follow the
        // speaker" opens in English and stays there until somebody says something
        // it recognises - which reads as the language setting being ignored.
        _spoken.Choose(settings.Language);

        if (settings.Policy == LanguagePolicy.Fixed && settings.FixedLanguage is { } tag)
            _spoken.Choose(tag);
        else if (settings.Policy == LanguagePolicy.FollowTheSpeaker)
            _spoken.ClearChoice();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Namespaced by service, so a new service brings its own settings without
    /// anybody touching this class - "cv/language" and "interpret/from" never
    /// collide, and neither is the app's own language.
    /// </remarks>
    public Task<string?> ServiceSettingAsync(
        string service, string key, string? fallback = null, CancellationToken ct = default)
        => Task.FromResult(_store.Get($"service.{service}.{key}", fallback));

    /// <inheritdoc />
    public Task SetServiceSettingAsync(
        string service, string key, string? value, CancellationToken ct = default)
    {
        _store.Set($"service.{service}.{key}", value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<StoredDocument>> DocumentsAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<StoredDocument>>(() =>
        {
            try
            {
                return Directory.EnumerateFiles(DocumentsDir)
                    .Where(f => Kind(f) is not null)
                    .Select(f =>
                    {
                        var info = new FileInfo(f);
                        return new StoredDocument(
                            Path.GetFileNameWithoutExtension(f).Replace('-', ' '),
                            Kind(f)!, f, info.Length, info.LastWriteTimeUtc);
                    })
                    .OrderByDescending(d => d.Written)
                    .ToList();
            }
            catch
            {
                // A documents list that cannot be read is empty, not a crash: this
                // screen's other sections still work and still matter.
                return [];
            }
        }, ct);

    /// <inheritdoc />
    public Task DeleteDocumentAsync(string path, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                // Only inside our own documents directory. A path from the UI is
                // still a path, and deleting by whatever string arrives is how a
                // list becomes a way to remove something it never listed.
                var full = Path.GetFullPath(path);
                if (!full.StartsWith(Path.GetFullPath(DocumentsDir), StringComparison.Ordinal))
                    return;
                if (Kind(full) is null) return;

                File.Delete(full);
            }
            catch
            {
                // Best effort: a file that will not delete is not worth a crash.
            }
        }, ct);

    /// <summary>
    /// What kind of document a file is, in the person's words - or null when it is
    /// not one of ours.
    /// </summary>
    /// <remarks>
    /// A WHITELIST, NOT A SCAN. The app's data directory also holds databases,
    /// preferences and model files; listing everything would offer somebody the
    /// chance to delete the thing their CV is stored in.
    /// </remarks>
    private static string? Kind(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return null;

        if (name.Contains("cover", StringComparison.OrdinalIgnoreCase)) return "Cover letter";
        if (name.Contains("invoice", StringComparison.OrdinalIgnoreCase)) return "Invoice";
        return "CV";
    }
}
