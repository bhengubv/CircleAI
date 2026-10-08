// PersonalSpeech.cs
//
// How THIS person says borrowed words, learned from listening to them.
//
// WHAT IT IS. A phone that can speak isiZulu still says "WiFi", "data",
// "WhatsApp" - English words that arrived with the things. Everybody pronounces
// them slightly differently, the curated table has one opinion, and the
// difference between a voice that sounds like a machine reading isiZulu and one
// that sounds like a person is mostly in those words.
//
// THE LEARNING IS FREE, AND THAT IS THE POINT. Every turn already produces a
// transcript of what this person said, in their own spelling - so a borrowed
// word arrives written the way THEY say it at no cost to them. Nobody corrects
// anything, nobody fills in a form: they asked their phone to do something, and
// the answer to "how do you say WiFi" came with it.
//
// IT NEVER LEAVES THE PHONE and is never merged with anybody else's, so two
// handsets pronounce the same word differently. That is correct.
//
// WHY IT IS HERE. The other head held this in an Activity - a table field, a
// path property, a learn method and a wiring line, spread over eighty lines of
// MainActivity - and it went with that head. This is the same behaviour as an
// object, so the head that ships can use it and neither has to know how it
// works.
//
// HOW IT REACHES THE VOICE, which differs between the heads. The other head
// spoke through an ITtsEngine and wrapped it with CircleAISpeaker.Respelling-
// Engine. This head speaks through CircleAITtsProbe, which takes text and a
// path - so the same Respeller is applied to the TEXT here instead. It is the
// identical transform: RespellingTtsEngine does nothing but call
// Respeller.Rewrite before handing the text on.

using CircleAI.Voice;

namespace CircleAI.Assistant.Device;

/// <summary>The one personal pronunciation table this process uses.</summary>
/// <remarks>
/// STATIC BECAUSE THE EAR AND THE MOUTH ARE THE SAME TABLE. Learning happens
/// where the transcript is - DeviceConversation - and speaking happens in
/// DeviceVoiceHost, and two instances would mean the phone learned a word and
/// then did not say it that way.
/// </remarks>
public static class PersonalSpeech
{
    private const string Tag = "CircleAI.Respell";
    private static readonly object Gate = new();
    private static PersonalRespellings? _table;

    /// <summary>Where the learned spellings live. App-private, never synced.</summary>
    public static string Path
        => System.IO.Path.Combine(AppPaths.Data, "respellings.json");

    private static PersonalRespellings Table
    {
        get
        {
            // LOAD NEVER THROWS. A missing file and an unreadable one both yield
            // an empty table, by contract: losing the learning is bad, refusing
            // to start because of it is worse, and the person can teach it again
            // simply by talking.
            lock (Gate)
            {
                return _table ??= PersonalRespellings.Load(Path);
            }
        }
    }

    /// <summary>What this person's spellings do to a sentence before it is spoken.</summary>
    /// <remarks>
    /// UNCHANGED FOR MOST LANGUAGES, AND THAT IS CORRECT. These are Nguni and
    /// Sotho letter values; applying them to an English or Japanese sentence
    /// would produce nonsense, so the respeller declines and the text goes
    /// through as written.
    /// </remarks>
    public static string Rewrite(string? languageTag, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var host = Root(languageTag);
        if (!LoanwordRespeller.IsNguniOrSotho(host)) return text;

        try
        {
            var respeller = new Respeller
            {
                HostLanguage = host,
                Personal = Table,
                EnglishPhonemizer = EnglishPhonemizer(),
            };

            return respeller.Rewrite(text);
        }
        catch (Exception ex)
        {
            // Never a reason to go silent. The unrespelt sentence is still the
            // right sentence; it just sounds slightly less like this person.
            Android.Util.Log.Warn(Tag, "could not respell: " + ex.Message);
            return text;
        }
    }

    /// <summary>Learn from one thing this person said, and keep it across restarts.</summary>
    /// <remarks>
    /// WRITTEN ONLY WHEN SOMETHING CHANGED. The commonest transcript by far
    /// contains no borrowed word at all, and re-serialising the table on every
    /// utterance would put a flash write on the critical path of every turn - on
    /// a phone whose storage is the slowest thing in it.
    /// <para>
    /// Saved on ANY change, not only a changed spelling: the sixth hearing
    /// confirms without altering anything, and saving only on a changed spelling
    /// meant a word could never reach a persisted Confirmed state.
    /// </para>
    /// </remarks>
    /// <returns>The words whose spelling changed because of this utterance.</returns>
    public static IReadOnlyList<string> LearnFrom(string? heard, string? languageTag)
    {
        var none = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(heard)) return none;

        try
        {
            var host = Root(languageTag);
            var spellings = LoanwordRespeller.Table(host);

            // Not a language these spellings fit. A phone set to English learns
            // nothing, which is right: these are isiZulu letter values.
            if (spellings.Count == 0) return none;

            var table = Table;
            var changed = table.LearnFrom(heard, spellings);

            if (!table.HasUnsavedChanges) return none;
            table.Save(Path);

            if (changed.Count == 0) return none;

            foreach (var word in changed)
                Android.Util.Log.Info(Tag, $"learned: {word} -> {table.Respell(word)}");

            return changed;
        }
        catch (Exception ex)
        {
            // Learning is a bonus, never a reason to lose a turn. A full disk or
            // a locked file must not take the conversation down with it.
            Android.Util.Log.Warn(Tag, "could not learn: " + ex.Message);
            return none;
        }
    }

    /// <summary>Everything learned so far, for a screen that wants to show it.</summary>
    public static IReadOnlyList<LearnedWord> Learned()
    {
        try { return Table.All(); }
        catch { return []; }
    }

    /// <summary>
    /// English pronunciation for words no table has, or null when unavailable.
    /// </summary>
    /// <remarks>
    /// OUT-OF-PROCESS espeak - it is GPL-3.0 and CircleAI never links it. Absent
    /// (the separate app is not installed) the curated table still works and
    /// unknown words are left as written.
    /// </remarks>
    private static IPhonemizer? EnglishPhonemizer()
    {
        try { return CircleAI.Assistant.Voice.CircleAISpeaker.MobilePhonemizerFactory?.Invoke("en-us"); }
        catch { return null; }
    }

    /// <summary>"zu-ZA" and "zu_ZA" are both isiZulu.</summary>
    private static string Root(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        var t = tag.Trim();
        var cut = t.IndexOfAny(['-', '_']);
        return cut > 0 ? t[..cut] : t;
    }
}
