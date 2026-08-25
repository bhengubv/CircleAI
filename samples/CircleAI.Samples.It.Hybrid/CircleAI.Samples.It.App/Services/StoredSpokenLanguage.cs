// StoredSpokenLanguage.cs
//
// The chosen language, kept across launches.

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
/// <remarks>
/// Backed by MAUI Preferences, which is SharedPreferences on Android - the same
/// store the native head writes to, though under its own package. Two apps, two
/// stores; nothing is shared between them and nothing should be.
/// </remarks>
public sealed class StoredSpokenLanguage : ISpokenLanguage
{
    private const string Key = "spoken.language";
    private const string ChosenKey = "spoken.language.chosen";

    /// <summary>
    /// What to fall back to before anyone has chosen: English, because it is the
    /// language the assistant's own model answers in most reliably - not because
    /// it is the most important one here.
    /// </summary>
    public const string Default = "en";

    /// <inheritdoc />
    public string Current => Preferences.Get(Key, Default);

    /// <inheritdoc />
    public string? Chosen
    {
        get
        {
            var v = Preferences.Get(ChosenKey, null as string);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    /// <inheritdoc />
    public void Choose(string tag)
    {
        Preferences.Set(ChosenKey, tag);
        Preferences.Set(Key, tag);
    }

    /// <inheritdoc />
    public void ClearChoice() => Preferences.Remove(ChosenKey);
}
