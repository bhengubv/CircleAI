// NOT BEHIND IT_VOICE_ANDROID, deliberately.
//
// This file is SharedPreferences and two string keys — `using System;` and
// `using Android.Content;`, no voice API anywhere in it. It was wrapped in
// #if IT_VOICE_ANDROID all the same, which took the type out of the chat-only
// build and left LanguagePickerActivity.Choose calling something that was not
// there. Which language a person chose is worth remembering whether or not the
// assistant can speak it aloud — it answers in that language either way.
#nullable enable

// SpokenLanguage.cs
//
// The language of the LAST turn, remembered, plus the names of the eleven.
//
// THIS WAS A PICKER AND IS NOT ANY MORE. The person chose once and the phone
// held onto it, which reads as respectful and is in fact the wrong shape: a
// persistent control for a property that is not persistent. South African
// households do not hold one language for a whole conversation. A clan moves
// through two or three of them inside a single exchange — English for the
// numbers, isiZulu for the argument, Afrikaans for the joke — so a language
// chosen on the first turn is wrong by the third, and being answered in the one
// you have just stopped speaking is the exact insult this exists to avoid.
//
// LanguageGuess now decides per turn from the transcript. What survives here is
// the memory of what it decided last, and only as a FALLBACK: when a turn is too
// short to carry evidence ("yes", "thanks") the guess returns null and the
// conversation keeps the language it already had, rather than snapping back to
// English mid-exchange.
//
// AND WHISPER STAYS ON "auto" — DO NOT PIN IT. It is tempting to feed the
// remembered language to the transcriber, and it would genuinely decode short
// utterances better. But the whole premise here is that the next turn may be a
// different language from this one, and a pinned decoder cannot hear the switch:
// it would transcribe isiZulu as though it were English and hand LanguageGuess a
// transcript with the evidence already destroyed. Slightly worse recognition that
// can still change its mind beats better recognition that cannot.

using System;
using Android.Content;

namespace CircleAI.Samples.It.Mobile;

/// <summary>The eleven languages the voice speaks, and which one is chosen.</summary>
public static class SpokenLanguage
{
    const string Prefs = "circleai.voice";
    const string Key   = "spoken.language";
    const string ChosenKey = "spoken.language.chosen";

    /// <summary>
    /// What to fall back to before anyone has chosen.
    /// </summary>
    /// <remarks>
    /// English, because it is the language the assistant's own model answers in
    /// most reliably — not because it is the most important one here.
    /// </remarks>
    public const string Default = "en";

    /// <summary>The language of the last turn, or <see cref="Default"/>.</summary>
    public static string Current(Context c)
    {
        try
        {
            var p = c.GetSharedPreferences(Prefs, FileCreationMode.Private);
            return p?.GetString(Key, Default) ?? Default;
        }
        catch { return Default; }
    }

    /// <summary>
    /// Remembers the language of the last turn — unless a person chose one.
    /// </summary>
    /// <remarks>
    /// A DETECTION MUST NOT UNDO A CHOICE. Every turn called this with whatever
    /// it heard, so picking Japanese on the languages screen survived exactly
    /// until the next English sentence, which silently wrote "en" back. Observed
    /// on the phone: Japanese chosen, one English question asked, and the next
    /// Japanese turn resolved to English because the stored value had been
    /// overwritten — while the screen still said Japanese.
    /// <para>
    /// Detection keeps its purpose, which is a household that moves between
    /// languages mid-conversation. It just no longer overrules the person who
    /// went to a screen and said which language this is.
    /// </para>
    /// </remarks>
    public static void Set(Context c, string code)
    {
        if (Chosen(c) is not null) return;
        Write(c, Key, code);
    }

    /// <summary>The language a person picked, or null if they never did.</summary>
    public static string? Chosen(Context c)
    {
        try
        {
            var p = c.GetSharedPreferences(Prefs, FileCreationMode.Private);
            var v = p?.GetString(ChosenKey, null);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    /// <summary>Records an explicit choice, which detection will not overwrite.</summary>
    public static void Choose(Context c, string code)
    {
        Write(c, ChosenKey, code);
        Write(c, Key, code);
    }

    /// <summary>Forgets the explicit choice, handing control back to detection.</summary>
    public static void ClearChoice(Context c) => Write(c, ChosenKey, null);

    static void Write(Context c, string key, string? value)
    {
        try
        {
            using var e = c.GetSharedPreferences(Prefs, FileCreationMode.Private)?.Edit();
            if (value is null) e?.Remove(key); else e?.PutString(key, value);
            e?.Apply();
        }
        catch { /* a preference that will not save is not worth a crash */ }
    }

    /// <summary>The display name for a code, for putting on the screen.</summary>
    /// <remarks>
    /// ONE TABLE, NOT THREE. The endonyms lived here, again in ItSpeaker, and were
    /// about to be copied a third time for the typed screen — three lists of the
    /// same eleven facts, each free to drift. They now sit with the detector, which
    /// is the thing that produces the codes and is compiled into every build.
    /// </remarks>
    public static string NameOf(string? code) => CircleAI.Samples.It.LanguageGuess.NameOf(code);
}
