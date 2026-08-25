// StoredSpokenLanguage.cs
//
// The chosen language, kept across launches.

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
/// <remarks>
/// IN THE SAME SQLITE FILE AS EVERYTHING ELSE, not in SharedPreferences. Which
/// language a person chose is part of how their phone is set up, and setup that
/// lives in four different mechanisms is setup that has to be done four times.
/// </remarks>
public sealed class StoredSpokenLanguage : ISpokenLanguage
{
    private readonly SqliteAppStore _store;

    /// <summary>Takes the app's one state store.</summary>
    public StoredSpokenLanguage(SqliteAppStore store) => _store = store;

    private const string Key = "spoken.language";
    private const string ChosenKey = "spoken.language.chosen";

    /// <summary>
    /// What to fall back to before anyone has chosen: English, because it is the
    /// language the assistant's own model answers in most reliably - not because
    /// it is the most important one here.
    /// </summary>
    public const string Default = "en";

    /// <inheritdoc />
    public string Current => _store.Get(Key, Default)!;

    /// <inheritdoc />
    public string? Chosen
    {
        get
        {
            var v = _store.Get(ChosenKey);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    /// <inheritdoc />
    public void Choose(string tag)
    {
        _store.Set(ChosenKey, tag);
        _store.Set(Key, tag);
    }

    /// <inheritdoc />
    public void ClearChoice() => _store.Set(ChosenKey, null);
}
