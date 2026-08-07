#if IT_VOICE_ANDROID
#nullable enable

// SpokenLanguage.cs
//
// The language this phone talks in, chosen by the person, remembered.
//
// AUTO-DETECTION WAS TOO NOISY TO TRUST. The transcriber reports a language and
// the turn followed it, which is elegant right up until it is wrong: whisper tiny
// cannot reliably separate the Nguni languages from one another, so isiXhosa
// comes back as "zu", siSwati comes back as "zu", and a good deal of everything
// comes back as "und". Answering a person in the wrong language is worse than
// asking them once which one they want — it is the specific insult this product
// exists to avoid.
//
// So the person says, and the phone remembers. One tap, eleven options, no
// settings screen and no account.
//
// KNOWING ALSO MAKES THE EARS BETTER. Whisper runs in "auto" only because nobody
// had told it otherwise; given a language it decodes with that constraint instead
// of inferring it, which is more accurate on short utterances — exactly the ones a
// wake-word assistant hears. So the choice improves recognition as well as the
// reply, and both halves stop guessing.

using System;
using Android.Content;

namespace CircleAI.Samples.It.Mobile;

/// <summary>The eleven languages the voice speaks, and which one is chosen.</summary>
public static class SpokenLanguage
{
    const string Prefs = "circleai.voice";
    const string Key   = "spoken.language";

    /// <summary>
    /// What to fall back to before anyone has chosen.
    /// </summary>
    /// <remarks>
    /// English, because it is the language the assistant's own model answers in
    /// most reliably — not because it is the most important one here.
    /// </remarks>
    public const string Default = "en";

    /// <summary>Name as a speaker of it would recognise it, and the code to use.</summary>
    /// <remarks>
    /// ENDONYMS, not English names for other people's languages. A person choosing
    /// their own language should see it written the way they write it — "isiZulu",
    /// not "Zulu". English is listed first only because it is the default; the rest
    /// follow in their own alphabetical order.
    /// </remarks>
    public static readonly (string Code, string Name)[] All =
    {
        ("en",  "English"),
        ("af",  "Afrikaans"),
        ("nr",  "isiNdebele"),
        ("xh",  "isiXhosa"),
        ("zu",  "isiZulu"),
        ("nso", "Sepedi"),
        ("st",  "Sesotho"),
        ("tn",  "Setswana"),
        ("ss",  "siSwati"),
        ("ve",  "Tshivenda"),
        ("ts",  "Xitsonga"),
    };

    /// <summary>The chosen language code, or <see cref="Default"/>.</summary>
    public static string Current(Context c)
    {
        try
        {
            var p = c.GetSharedPreferences(Prefs, FileCreationMode.Private);
            return p?.GetString(Key, Default) ?? Default;
        }
        catch { return Default; }
    }

    /// <summary>Remembers the choice across launches.</summary>
    public static void Set(Context c, string code)
    {
        try
        {
            using var e = c.GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit();
            e?.PutString(Key, code);
            e?.Apply();
        }
        catch { /* a preference that will not save is not worth a crash */ }
    }

    /// <summary>The display name for a code, for putting on a button.</summary>
    public static string NameOf(string? code)
    {
        foreach (var (c, n) in All)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return n;
        return "English";
    }
}
#endif
