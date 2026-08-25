// DeviceSettings.cs
//
// What the app is set up to do, kept across launches.

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceSettings : ISettings
{
    private readonly ISpokenLanguage _spoken;

    /// <summary>Takes the language store, because saving has to apply as well.</summary>
    public DeviceSettings(ISpokenLanguage spoken) => _spoken = spoken;

    private const string ModeKey = "app.mode";
    private const string PolicyKey = "app.language.policy";
    private const string FixedKey = "app.language.fixed";
    private const string WakeLangKey = "app.wake.language";
    private const string WakeOnKey = "app.wake.enabled";
    private const string FromKey = "app.interpret.from";
    private const string ToKey = "app.interpret.to";

    private static string DocumentsDir => FileSystem.AppDataDirectory;

    /// <inheritdoc />
    public Task<AppSettings> LoadAsync(CancellationToken ct = default)
        => Task.FromResult(new AppSettings(
            Enum.TryParse<AppMode>(Preferences.Get(ModeKey, nameof(AppMode.Assistant)), out var m)
                ? m : AppMode.Assistant,
            Enum.TryParse<LanguagePolicy>(
                Preferences.Get(PolicyKey, nameof(LanguagePolicy.FollowTheSpeaker)), out var p)
                ? p : LanguagePolicy.FollowTheSpeaker,
            Preferences.Get(FixedKey, null as string),
            // ENGLISH BY DEFAULT, AND SEPARATELY FROM THE ANSWERING LANGUAGE.
            // "Hey B" is an English phrase; defaulting this to whatever language
            // somebody last spoke is how the wake word silently changed under them.
            Preferences.Get(WakeLangKey, "en"),
            Preferences.Get(FromKey, "en"),
            Preferences.Get(ToKey, "zu"),
            Preferences.Get(WakeOnKey, true)));

    /// <inheritdoc />
    public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Preferences.Set(ModeKey, settings.Mode.ToString());
        Preferences.Set(PolicyKey, settings.Policy.ToString());
        Preferences.Set(WakeLangKey, settings.WakeLanguage);
        Preferences.Set(WakeOnKey, settings.WakeEnabled);
        Preferences.Set(FromKey, settings.InterpretFrom);
        Preferences.Set(ToKey, settings.InterpretTo);

        if (settings.FixedLanguage is null) Preferences.Remove(FixedKey);
        else Preferences.Set(FixedKey, settings.FixedLanguage);

        // APPLYING, NOT JUST STORING.
        //
        // The policy is not a note about a preference - it IS the choice the
        // picker writes. Fixed means a person decided, which detection must not
        // overwrite; following the speaker means handing control back, which is
        // exactly what ClearChoice does and what nothing in the UI called until
        // now. Storing the radio button without doing this would leave a screen
        // saying one thing and the app doing another.
        if (settings.Policy == LanguagePolicy.Fixed && settings.FixedLanguage is { } tag)
            _spoken.Choose(tag);
        else if (settings.Policy == LanguagePolicy.FollowTheSpeaker)
            _spoken.ClearChoice();

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
