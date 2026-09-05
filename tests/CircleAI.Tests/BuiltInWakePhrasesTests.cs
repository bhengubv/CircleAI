// BuiltInWakePhrasesTests.cs
//
// The screen must not promise a wake phrase the phone does not have.
//
// WHY THIS EARNS ITS PLACE. The settings screen used to fill a "wake phrase
// language" select from the full language table - seventy-five entries - while
// the listener has phrases for five. Somebody could choose Zulu, see Zulu
// sitting in the box, and have the phone go on listening for "Hey B", with
// nothing on screen saying otherwise. That is worse than not offering the
// choice: a person who has already set the thing stops looking for the reason it
// does not work.
//
// That select is gone - the wake phrase follows AppSettings.Language now, which
// is the only shape that cannot express "English app, Japanese wake word". What
// survives is the table of phrases that SHIP, which the shared UI reads to
// answer "does this language arrive with one, and what is it".
//
// The table lives in CircleAI.Samples.It.Contracts, which by design references
// nothing - it is loaded by a browser as well as by a phone - so it cannot read
// WakePhraseBook directly. A hand-kept copy of somebody else's table is a lie
// waiting for the next commit, and this is what stops it.

using System;
using System.Linq;
using CircleAI.Samples.It;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class BuiltInWakePhrasesTests
{
    [Fact]
    public void The_table_matches_the_engines_phrase_book()
    {
        var engine = WakePhraseBook.CandidatesByLanguage
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.Key, Phrases: string.Join('|', p.Value)));

        var shared = BuiltInWakePhrases.Phrases
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.Key, Phrases: string.Join('|', p.Value)));

        // The PHRASES too, not just the languages: a table that agreed on which
        // languages exist while offering different words for them would show a
        // person a phrase the listener has never been told about.
        Assert.Equal(engine, shared);
    }

    [Fact]
    public void A_language_with_no_phrase_returns_empty_rather_than_English()
    {
        // THE WHOLE BUG IN ONE ASSERTION. Falling back to "Hey B" is what the
        // listener does at the bottom of the stack so the phone answers to
        // something; a screen that falls back is a screen that lies, because the
        // person reading it came to find out what to say.
        // "zu" USED TO BE THE EXAMPLE HERE and now ships "Sawubona B", which is
        // the table growing rather than this rule weakening. Picked a language
        // that genuinely has none: Malagasy is in the app's catalogue and has no
        // wake phrase, so the screen must still say so instead of offering "Hey
        // Circle AI" to somebody who asked what to say in Malagasy.
        Assert.Empty(BuiltInWakePhrases.For("mg"));
        Assert.False(BuiltInWakePhrases.Has("mg"));
        Assert.Empty(BuiltInWakePhrases.For(null));
        Assert.Empty(BuiltInWakePhrases.For(""));
    }

    [Fact]
    public void A_regional_tag_finds_its_language()
    {
        // "ja-JP" is a reasonable thing to have stored, and it is Japanese.
        Assert.True(BuiltInWakePhrases.Has("ja-JP"));
        Assert.Equal(BuiltInWakePhrases.For("ja"), BuiltInWakePhrases.For("ja-JP"));
    }

    [Fact]
    public void Japanese_offers_more_than_one_phrase()
    {
        // SEVERAL PER LANGUAGE IS THE POINT, not an accident of the data: the app
        // used to pick one silently, so the phone answered to a name nobody had
        // been told. A language collapsing to a single phrase would quietly undo
        // the screen that lets somebody choose.
        Assert.True(BuiltInWakePhrases.For("ja").Count > 1);
    }

    [Fact]
    public void Every_language_with_a_phrase_has_a_name_to_show()
    {
        // The screen prints the language's name beside the phrase; a tag with no
        // entry in the language table would show as a bare code.
        //
        // COMPARED ON THE LANGUAGE, NOT THE TAG. The catalogue carries regional
        // variants - es-ES and es-MX, no bare "es" - and the lookup strips the
        // region, so a phrase keyed "es" correctly serves both. This assertion
        // used to demand the key appear verbatim and only passed because none of
        // the original five languages had a regional form; it failed the moment
        // Spanish arrived, on a table entry that was right.
        var known = SampleLanguages.All.Keys
            .Select(t => t.Split('-')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in BuiltInWakePhrases.Phrases.Keys)
            Assert.True(known.Contains(tag),
                $"'{tag}' can wake the phone but has no name in SampleLanguages");
    }

    [Fact]
    public void Every_phrase_is_long_enough_to_survive_a_room()
    {
        // THE RULE THE BOOK ALREADY HAD AND THE TABLE DID NOT FOLLOW.
        // MinReliableTokens is 4, and "Hey B" is 3 - measured on a P30 on
        // 2026-09-06 at ONE completed match in six spoken attempts.
        //
        // Tokens need the bundle's own tokenizer, which a unit test has no
        // access to, so this uses words as the honest proxy: every language's
        // FIRST candidate - the one BestFor reaches for - should be a real
        // greeting rather than a syllable. The shorter forms below it are
        // deliberate fallbacks for bundles whose tokenizer cannot represent the
        // longer phrase, and are not held to this.
        var tooShort = BuiltInWakePhrases.Phrases
            .Where(p => p.Value[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            .Select(p => $"{p.Key}: \"{p.Value[0]}\"")
            .ToList();

        Assert.True(tooShort.Count == 0,
            "these languages lead with a phrase too short to be heard across a room:\n  "
            + string.Join("\n  ", tooShort));
    }

    [Fact]
    public void The_best_candidate_comes_first()
    {
        // BestFor takes the FIRST candidate the tokenizer can represent, so the
        // order is the decision. Longest first: the native-script forms at the
        // end are unreachable on an English-subword bundle and exist so the
        // table reads honestly to whoever maintains it.
        foreach (var (tag, phrases) in BuiltInWakePhrases.Phrases)
        {
            var words = phrases.Select(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length).ToList();
            for (var i = 1; i < words.Count; i++)
                Assert.True(words[i] <= words[i - 1],
                    $"'{tag}' lists a longer phrase after a shorter one - "
                    + $"BestFor would settle for \"{phrases[i - 1]}\" and never reach \"{phrases[i]}\"");
        }
    }

    [Fact]
    public void Settings_has_no_wake_language_of_its_own()
    {
        // THE SHAPE IS THE FIX. As long as no such property exists, the settings
        // screen cannot express "run in English, wake in Japanese" - the
        // combination that made this whole thing wrong. A test on behaviour would
        // not catch somebody adding the property back.
        var properties = typeof(AppSettings).GetProperties().Select(p => p.Name).ToList();

        Assert.Contains("Language", properties);
        Assert.DoesNotContain("WakeLanguage", properties);
    }
}
