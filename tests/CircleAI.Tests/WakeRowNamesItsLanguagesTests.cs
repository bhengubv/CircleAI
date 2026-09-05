// WakeRowNamesItsLanguagesTests.cs
//
// "the wake word — here", on a capability that answers in thirty-two languages.
//
// Once the voice rows started naming what they speak, this became the vaguest
// line on the screen: it said the bytes had arrived and nothing else. What
// somebody wants to know is whether the phone will answer THEM, which is a
// question about language, and it is the same question the voice rows answer.
//
// The number was reported as "32 voices are warmed up but the loading screen
// doesn't say so". It is not voices - there were four of those - it is the wake
// phrase table, which covers thirty-two languages. The complaint was right and
// aimed one row over.
//
// READ FROM THE SHIPPED TABLE, NOT FROM THE OWNER'S CHOICE. The chosen phrase
// lives in the app's own store, and a census reaching for it would become the
// fifth owner of one fact - which is exactly what made the phone answer to
// "Hey B" while every screen said "Hey Circle AI". Coverage is a property of the
// build and cannot drift under it.

using System.Linq;
using CircleAI.Core.Models;
using CircleAI.Samples.It;
using Xunit;

namespace CircleAI.Tests;

public class WakeRowNamesItsLanguagesTests
{
    [Fact]
    public void The_table_covers_more_than_a_handful_of_languages()
    {
        // A GUARD ON THE PREMISE. If the phrase table shrank back to the five it
        // started at, the row below would still "pass" while saying much less
        // than the screen is claiming.
        Assert.True(BuiltInWakePhrases.Phrases.Count >= 20,
            $"the wake phrase table has shrunk to {BuiltInWakePhrases.Phrases.Count} languages");
    }

    [Fact]
    public void Every_wake_language_can_be_named()
    {
        // The row prints names, not tags. A language the phone can be woken in
        // but which the app cannot name would appear as a bare code beside
        // "isiZulu" and "Japanese", which reads as a bug rather than as coverage.
        //
        // THIS FOUND THREE. es, pt and nl have no bare entry in the table - the
        // catalogue carries es-ES, es-MX, pt-BR, pt-PT, nl-NL, nl-BE - so an
        // exact lookup returned nothing and the screen would have printed the
        // codes. Matched on the language root now, with the region trimmed off
        // the name: a phrase covering Spanish generally is not Spanish (Spain).
        var known = SampleLanguages.All.Keys
            .Select(t => t.Split('-')[0])
            .ToHashSet(System.StringComparer.OrdinalIgnoreCase);

        var unnamed = BuiltInWakePhrases.Phrases.Keys
            .Where(tag => !known.Contains(tag.Split('-')[0]))
            .ToList();

        Assert.True(unnamed.Count == 0,
            "these languages can wake the phone and have no name to show:\n  "
            + string.Join("\n  ", unnamed));
    }

    [Fact]
    public void A_regionalised_language_is_named_without_its_region()
    {
        // The three the test above found. Spanish is in the table only as
        // "Spanish (Spain)" and "Spanish (Mexico)", and a wake phrase keyed "es"
        // covers neither one country nor the other.
        var row = FirstRun.OtherVoices(
            [new ModelEntry(Name: "V", Version: "1.0", Quantization: "none")
             {
                 Modality = CircleAI.Core.ModelModality.Tts,
                 Language = "es,pt,nl",
                 TotalBytes = 1024,
             }],
            FirstRun.WantedFor(speech: true));

        Assert.NotNull(row);
        Assert.Contains("Spanish", row!.Value.Detail);
        Assert.DoesNotContain("(Spain)", row.Value.Detail);
        Assert.DoesNotContain("(Brazil)", row.Value.Detail);
    }

    [Fact]
    public void The_wake_languages_are_a_superset_of_what_it_can_speak()
    {
        // WAKING AND SPEAKING ARE DIFFERENT CAPABILITIES and the screen now shows
        // both, so the difference has to be real rather than an accident. Being
        // able to answer to its name in a language it cannot yet speak is the
        // right way round: the wake word is six megabytes and a voice is sixty,
        // so coverage arrives first and the voice follows.
        //
        // This is also the distinction that was missed in the report - "32
        // voices" was thirty-two WAKE languages next to four voices.
        Assert.Contains("ja", BuiltInWakePhrases.Phrases.Keys);
        Assert.Contains("sw", BuiltInWakePhrases.Phrases.Keys);
        Assert.Contains("ar", BuiltInWakePhrases.Phrases.Keys);
    }
}
