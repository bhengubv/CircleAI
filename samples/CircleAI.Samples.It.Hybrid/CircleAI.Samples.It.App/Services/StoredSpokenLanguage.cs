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
    /// What to start with before anyone has chosen: what the PHONE says.
    /// </summary>
    /// <remarks>
    /// THIS WAS THE CONSTANT "en", AND IT DEFAULTED EVERY PERSON ON EARTH TO
    /// ENGLISH. The catalogue was never the problem - it holds seventy-five
    /// languages and English is one row of it - but nothing in this app had ever
    /// read the device locale, so a phone set to isiZulu and bought in Soweto
    /// opened in English and invited its owner to go and find their own language
    /// in a list of seventy-five.
    /// <para>
    /// The old note said English "because it is the language the assistant's own
    /// model answers in most reliably". That is a real fact about the model and
    /// it was answering the wrong question: which language somebody SPEAKS is not
    /// which language a model is best at, and letting the second decide the first
    /// is the bias in its politest form.
    /// </para>
    /// <para>
    /// Computed once and remembered: LocaleList is a system call, Current is read
    /// on nearly every screen, and the answer cannot change without the phone
    /// being reconfigured - at which point the app has restarted anyway.
    /// </para>
    /// </remarks>
    public static string Default => _fallback ??= FromPhone();

    private static string? _fallback;

    /// <summary>The phone's own languages, in the order its owner put them.</summary>
    private static string FromPhone()
    {
        try
        {
            var locales = new List<string>();
            var config = Android.App.Application.Context.Resources?.Configuration;

            // The ORDERED list somebody configured, not just the first one:
            // "isiZulu, then English" is a different person from "English only",
            // and Android is the only thing that knows which this is.
            var list = config?.Locales;
            if (list is not null)
                for (var i = 0; i < list.Size(); i++)
                    if (list.Get(i)?.ToLanguageTag() is { Length: > 0 } tag)
                        locales.Add(tag);

            var region = Java.Util.Locale.Default?.Country;
            var choice = LanguageSuggestion.For(locales, region);

            Android.Util.Log.Info("CircleAI.Language",
                $"first language: {string.Join(", ", choice.Tags)} — {choice.Reason}");

            return choice.Tags.FirstOrDefault() ?? LanguageSuggestion.LastResort;
        }
        catch (Exception ex)
        {
            // Never fatal, and never silent: a phone whose locale cannot be read
            // still has to open, and the log says why it opened in English.
            Android.Util.Log.Warn("CircleAI.Language",
                $"could not read the phone's languages, falling back to English — {ex.Message}");
            return LanguageSuggestion.LastResort;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Suggested => _suggested ??= FromPhoneAll();

    private static IReadOnlyList<string>? _suggested;

    /// <summary>Everything the phone suggests, not just the winner.</summary>
    private static IReadOnlyList<string> FromPhoneAll()
    {
        try
        {
            var locales = new List<string>();
            var list = Android.App.Application.Context.Resources?.Configuration?.Locales;
            if (list is not null)
                for (var i = 0; i < list.Size(); i++)
                    if (list.Get(i)?.ToLanguageTag() is { Length: > 0 } tag)
                        locales.Add(tag);

            var choice = LanguageSuggestion.For(locales, Java.Util.Locale.Default?.Country);
            return choice.Tags.Count > 0 ? choice.Tags : [LanguageSuggestion.LastResort];
        }
        catch
        {
            return [LanguageSuggestion.LastResort];
        }
    }

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
