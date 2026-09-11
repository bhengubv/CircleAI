// DocumentedCountTests.cs
//
// The numbers written in prose, checked against the tables they describe.
//
// CLAUDE.md's own lesson is that ONE FACT WITH TWO OWNERS ALWAYS ENDS UP WITH
// TWO ANSWERS, and it names the language count as the example. It was right, and
// it was still happening in a place no test could see: comments.
//
// BuiltInWakePhrases opened with "FIVE OF SEVENTY-FIVE. The app speaks
// seventy-five languages and arrives knowing how to be woken in five of them."
// By the time anybody read it again the table held THIRTY-TWO phrases and the
// picker held SEVENTY-EIGHT languages - both numbers wrong, in the header that
// exists to explain the file. IWakePhrases said "seventy of the seventy-five"
// twice, once in public API documentation. LanguageSuggestion said seventy-five
// in two places. The abilities screen told its owner "10 plus languages" over a
// catalogue with a voice for all seventy-eight.
//
// A COMMENT IS THE ONE PLACE A COUNT CAN ROT WITH EVERY TEST GREEN. Adding
// Spanish, Dutch and Portuguese to the picker was a correct change that made
// five paragraphs wrong, and nothing anywhere went red.
//
// So the prose numbers are asserted. If a count moves, this fails and names the
// paragraphs to update - which is the same bargain TargetFrameworkTests makes
// for CLAUDE.md's multi-targeting claim: a sentence that is checked rather than
// believed.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class DocumentedCountTests
{
    [Fact]
    public void The_picker_holds_the_number_the_comments_say_it_does()
    {
        Assert.Equal(78, SampleLanguages.All.Count);

        // If this moved, these say seventy-eight and now do not:
        //   src/CircleAI.Assistant/BuiltInWakePhrases.cs   - header
        //   src/CircleAI.Assistant/IWakePhrases.cs         - header and the
        //                                                    <remarks> on Phrases
        //   src/CircleAI.Assistant/LanguageSuggestion.cs   - two places
        //   docs/OPEN-GAPS.md                              - section A-triple-prime
        // The abilities screen reads this property directly and needs no edit,
        // which is the difference between a number that is counted and one that
        // is typed.
    }

    [Fact]
    public void The_wake_phrase_table_holds_the_number_the_comments_say_it_does()
    {
        Assert.Equal(32, BuiltInWakePhrases.Phrases.Count);

        // If this moved, BuiltInWakePhrases.cs's header says thirty-two.
    }

    [Fact]
    public void The_count_of_languages_that_arrive_with_no_phrase_is_the_documented_one()
    {
        // IWakePhrases states this twice - "forty of the seventy-eight" -
        // and one of them is public API documentation, so it is a promise rather
        // than a note to ourselves.
        //
        // Counted through the ROOT of each tag, because the table keys on
        // language ("ja") while the picker offers regional tags too, and a
        // straight set difference would report Japanese as silent.
        var withPhrase = 0;
        foreach (var language in SampleLanguages.All.Values)
        {
            var tag = language.Tag;
            var dash = tag.IndexOf('-');
            var root = dash > 0 ? tag[..dash] : tag;

            if (BuiltInWakePhrases.Phrases.ContainsKey(tag)
                || BuiltInWakePhrases.Phrases.ContainsKey(root))
            {
                withPhrase++;
            }
        }

        Assert.Equal(78 - 40, withPhrase);

        // 38, from 32 entries: the six regional tags inherit their root, which
        // is why the table size and the coverage are different numbers and why
        // BOTH are asserted rather than one inferred from the other.
    }
}
