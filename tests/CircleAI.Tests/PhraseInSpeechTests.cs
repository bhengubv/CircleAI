// PhraseInSpeechTests.cs
//
// THE WAKE WORD'S DECISION, MOVED SOMEWHERE IT CAN BE ASSERTED.
//
// Every rule in this feature that turned out to be wrong was somewhere no test
// could reach it: a private method on an Android Service, a lambda inside a
// constructor that needs three ONNX graphs on disk, a comment nobody checked
// against audio. Five explanations were offered and measured wrong in a day.
//
// So the rule that decides whether a person gets answered is pure text, and
// these are the cases it has to get right - taken from what the 20M model
// ACTUALLY produced for the owner's voice on 2026-10-12, not from imagination:
//
//     hey circle a      b hey friend     hay bob      hay bill
//     hay friend        hey alexe hey alexa          y cyri
//
// That is a person saying their wake phrases clearly into a phone. Any matching
// rule has to live with that.

using System;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public sealed class PhraseInSpeechTests
{
    // ── shaping the two strings so they can be compared at all ───────────────

    [Theory]
    [InlineData("Hey B!", "hey b")]
    [InlineData("HEY B", "hey b")]
    [InlineData("  hey   b  ", "hey b")]
    [InlineData("Hey, B!", "hey b")]
    [InlineData("Hey Circle AI", "hey circle ai")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_typed_phrase_and_a_transcript_are_brought_into_one_shape(string? raw, string expected)
    {
        // The model emits upper case without punctuation; a person types "Hey B!"
        // with a capital and the exclamation mark that is part of the brand.
        // Neither difference is about what was said.
        Assert.Equal(expected, PhraseInSpeech.Plain(raw));
    }

    [Fact]
    public void Accents_are_kept_because_most_of_these_languages_need_them()
    {
        // Stripping diacritics would be right for English and wrong for most of
        // the two thousand languages this has to serve.
        Assert.Equal("héi bé", PhraseInSpeech.Plain("Héi Bé!"));
        Assert.Equal("sawubona", PhraseInSpeech.Plain("Sawubona"));
    }

    // ── it fires when the phrase has JUST been said ──────────────────────────

    [Fact]
    public void The_phrase_on_its_own_wakes_it()
    {
        Assert.True(PhraseInSpeech.JustSaid("hey b", "Hey B!"));
        Assert.True(PhraseInSpeech.JustSaid("HEY B", "Hey B!"));
    }

    [Fact]
    public void The_phrase_at_the_end_of_a_longer_transcript_wakes_it()
    {
        // A streaming transcript grows, and the wake phrase is at the end of
        // whatever the recogniser has heard when the person stops.
        Assert.True(PhraseInSpeech.JustSaid("so i said to him hey b", "Hey B"));
    }

    [Fact]
    public void The_phrase_in_the_MIDDLE_does_not_wake_it()
    {
        // THE MOMENT HAS PASSED. "hey b what is the weather" contains the
        // phrase, but waking there takes the microphone away mid-question -
        // which is the same defect as not waking at all, wearing a hat.
        Assert.False(PhraseInSpeech.JustSaid("hey b what is the weather", "Hey B"));
    }

    [Fact]
    public void It_matches_whole_words_and_not_substrings()
    {
        // THE MOST OBVIOUS WAY THIS COULD BE WORSE THAN WHAT IT REPLACES. A
        // substring test wakes "hey b" on "they bought", and a wake word that
        // fires during conversation cannot be worked around.
        Assert.False(PhraseInSpeech.JustSaid("they bought", "Hey B"));
        Assert.False(PhraseInSpeech.JustSaid("obey bees", "Hey B"));
        Assert.False(PhraseInSpeech.JustSaid("hey bob", "Hey B"));
    }

    [Fact]
    public void A_transcript_shorter_than_the_phrase_cannot_contain_it()
    {
        Assert.False(PhraseInSpeech.JustSaid("hey", "Hey B"));
        Assert.False(PhraseInSpeech.JustSaid("", "Hey B"));
    }

    [Fact]
    public void An_empty_phrase_never_wakes_anything()
    {
        // Otherwise an unconfigured device wakes on silence, continuously.
        Assert.False(PhraseInSpeech.JustSaid("hey b", ""));
        Assert.False(PhraseInSpeech.JustSaid("hey b", null));
        Assert.False(PhraseInSpeech.JustSaid("anything at all", "   "));
    }

    // ── and when the recogniser mishears a single spoken letter ──────────────

    [Fact]
    public void A_misheard_first_word_is_forgiven_where_the_letter_is_not()
    {
        // "hay" for "hey" is a vowel the recogniser guessed; three letters can
        // carry one edit and still be unmistakably the same word.
        Assert.False(PhraseInSpeech.JustSaid("hay b", "Hey B"));
        Assert.True(PhraseInSpeech.NearlySaid("hay b", "Hey B", slack: 0.4));
    }

    [Fact]
    public void A_SINGLE_LETTER_CANNOT_BE_MATCHED_FUZZILY_AND_THAT_IS_A_PRODUCT_FINDING()
    {
        // MEASURED ON THE OWNER'S VOICE, 2026-10-12. The 20M model transcribed
        // his "Hey B!" as, among others:
        //
        //     hey be      hay bob     hay bill     b hey friend
        //
        // "be" is the letter heard correctly and written longer, so it SHOULD
        // wake. "bob" and "bill" are different words and must NOT. There is no
        // edit-distance or prefix rule that separates them: all three are "b"
        // plus one or two letters.
        //
        // So a one-letter wake word is not reliably matchable in a transcript -
        // and the keyword spotter reached the same wall from the other side,
        // where "_B" was the rarest of three spellings and never once won a
        // frame. Two completely different engines, the same conclusion.
        //
        // This is pinned as a TEST rather than written in a comment somewhere,
        // because the next person to look at this will otherwise spend a day
        // inventing the fuzzy rule that cannot exist.
        Assert.False(PhraseInSpeech.NearlySaid("hey be", "Hey B", slack: 0.4),
            "if this ever passes, check that 'hay bob' still fails");
        Assert.False(PhraseInSpeech.NearlySaid("hay bob", "Hey B", slack: 0.4));
        Assert.False(PhraseInSpeech.NearlySaid("hay bill", "Hey B", slack: 0.4));

        // Whereas a phrase with a real word in it survives the same mishearing.
        Assert.True(PhraseInSpeech.NearlySaid("hey circle a i", "Hey Circle A I", slack: 0.4));
    }

    [Fact]
    public void But_fuzziness_is_opt_in_and_bounded()
    {
        // A fuzzy wake word wakes at things that merely sound close, and the
        // cost is a phone that interrupts a conversation. At the default slack
        // a one-letter word must still match exactly.
        Assert.False(PhraseInSpeech.NearlySaid("hey d", "Hey B"));
        Assert.False(PhraseInSpeech.NearlySaid("hey bob", "Hey B"));
        Assert.False(PhraseInSpeech.NearlySaid("hay friend", "Hey B"));
    }

    [Fact]
    public void A_longer_word_may_lose_a_letter_where_a_short_one_may_not()
    {
        // Per word rather than over the whole string: the SHORT word is usually
        // the one carrying the identity, so it is the one that must be exact.
        Assert.True(PhraseInSpeech.NearlySaid("hey circl ai", "Hey Circle AI"));
        Assert.False(PhraseInSpeech.NearlySaid("hey circle ab", "Hey Circle AI"));
    }

    [Fact]
    public void What_the_model_really_produced_does_not_wake_it_by_accident()
    {
        // The actual transcript of the owner testing several phrases. Only the
        // ones that ARE the phrase may wake it.
        foreach (var heard in new[]
                 {
                     "hey circle a", "b hey friend", "hay friend", "hay bob",
                     "hay bill", "hay she la", "hay elector", "y cyri",
                     "hey alexe hey alexa", "hay gou",
                 })
        {
            Assert.False(PhraseInSpeech.JustSaid(heard, "Hey B"), heard);
            Assert.False(PhraseInSpeech.NearlySaid(heard, "Hey B"), heard);
        }
    }

    // ── inside one endpointed utterance, position stops mattering ────────────

    [Fact]
    public void WITHIN_ONE_UTTERANCE_THE_PHRASE_COUNTS_WHEREVER_IT_IS()
    {
        // THE TAIL RULE WAS RIGHT AND HAS STOPPED BEING RIGHT.
        //
        // JustSaid matches only at the end because the transcript it was
        // written for GREW without bound, so "hey b what is the weather"
        // contained the phrase long after the moment to wake had passed.
        //
        // The wake loop now endpoints: the gate closes on a silence, the
        // recogniser is asked to finish, the transcript is forgotten. What
        // reaches a matcher is ONE BURST of a few seconds - and inside a burst
        // there is no "the moment has passed".
        //
        // MEASURED ON THE OWNER'S VOICE, 2026-10-12. Saying "Hey Circle AI"
        // decoded as one burst reading:
        //
        //     A B HAY CIRCLE HE CIRCLE A CIRCLE A
        //
        // The phrase is plainly in there. A tail-only rule scores it zero
        // because he carried on talking for another two words.
        const string burst = "A B HAY CIRCLE HE CIRCLE A CIRCLE A";

        Assert.False(PhraseInSpeech.NearlySaid(burst, "Hey Circle"));
        Assert.True(PhraseInSpeech.SaidAnywhere(burst, "Hey Circle"));
    }

    [Fact]
    public void But_matching_anywhere_keeps_every_protection_that_matters()
    {
        // THE RECALL IS BOUGHT FROM POSITION, NOT FROM THE WORD RULES. A fuzzy
        // wake word that also ignores position would fire across a
        // conversation, and a phone that interrupts people cannot be worked
        // around.
        Assert.False(PhraseInSpeech.SaidAnywhere("they bought a loaf", "Hey B"));
        Assert.False(PhraseInSpeech.SaidAnywhere("obey bees in the garden", "Hey B"));

        // The decoys this very recogniser produced on the owner's voice.
        foreach (var heard in new[]
                 {
                     "HE HAY BOB HAY BOB", "HAY SHE LA", "TABLE", "A JARVIS",
                     "HAY FRIEND HAY FRIEND HAY FRIEND HEY AI HE HE AI",
                     "HAIG HEY GOOBU HEY GOUGU HAY ALEXAY JARVIS PAGE AGE OF",
                 })
            Assert.False(PhraseInSpeech.SaidAnywhere(heard, "Hey B"), heard);
    }

    [Fact]
    public void A_phrase_longer_than_the_utterance_cannot_be_in_it()
    {
        Assert.False(PhraseInSpeech.SaidAnywhere("circle", "Hey Circle AI"));
        Assert.False(PhraseInSpeech.SaidAnywhere("", "Hey B"));
        Assert.False(PhraseInSpeech.SaidAnywhere("hey b", ""));
    }

    [Fact]
    public void BOTH_OF_THE_OWNERS_PHRASES_WAKE_IT_AND_NONE_OF_THE_DECOYS_DO()
    {
        // EVERY UTTERANCE OF THE OWNER'S 56-SECOND RECORDING, 2026-10-12, as
        // the recogniser decoded it once the stream was actually being reset
        // between bursts. He is testing his own two phrases AND the competition:
        //
        //     HAY B                                      <- "Hey B"
        //     HAY CIRCLED AY HAY CIRCLE A HEY CIRCLE AI  <- "Hey Circle AI"
        //     FRIEND HAY FRIEND HAY FRIEND HEY AY HEY A  <- "Hey Friend"
        //     HAY BOB HAY BOB                            <- "Hey Bob"
        //     ELECTOR HEY ALEXA HEY ALEXAY CYRI          <- "Hey Alexa", "Siri"
        //     GU HEY GOOBU HEY GOO HEY ALEXEY JARVIS ... <- "Hey Google", "Jarvis"
        //
        // AN EARLIER VERSION OF THIS TEST CONCLUDED THE OPPOSITE, and the
        // conclusion was pinned here as a "product finding": that the model
        // wrote "A B" for "Hey B" three times in five, so "Hey B" could never
        // be a wake phrase. That was measured on a CORRUPTED stream -
        // StreamingTranscriber.Reset cleared the transcript and left the
        // encoder's cache untouched, so every burst was decoded as a
        // continuation of the one before it. "A B" was the corruption, not the
        // owner's voice and not the model.
        //
        // A measurement taken through a broken pipe is not evidence about the
        // thing at the far end of it, and calling it a product finding nearly
        // cost the product its name.
        Assert.True(PhraseInSpeech.SaidAnywhere("HAY B", "Hey B"));
        Assert.True(PhraseInSpeech.SaidAnywhere(
            "HAY CIRCLED AY HAY CIRCLE A HEY CIRCLE AI", "Hey Circle AI"));

        // AND NOT ONE DECOY FIRES, which is the half that makes it usable. Six
        // other wake phrases were spoken into the same microphone in the same
        // minute and none of them is ours.
        foreach (var decoy in new[]
                 {
                     "FRIEND HAY FRIEND HAY FRIEND HEY AY HEY A",
                     "HAY BOB HAY BOB",
                     "ELECTOR HEY ALEXA HEY ALEXAY CYRI",
                     "GU HEY GOOBU HEY GOO HEY ALEXEY JARVIS PAGE AGE OF",
                     "NOW", "JARVIS",
                 })
        {
            Assert.False(PhraseInSpeech.SaidAnywhere(decoy, "Hey B"), decoy);
            Assert.False(PhraseInSpeech.SaidAnywhere(decoy, "Hey Circle AI"), decoy);

            // AND THE SHORTER PHRASE MEASURED AS 4,5x MORE RELIABLE. Scaling
            // the owner's own recording down in four steps, wakes per phrase:
            //
            //     Hey B            2      Hey Circle   9
            //     Hey Circle AI    2      Circle       9
            //
            // Those nine come from the SAME utterances where he said "Hey
            // Circle AI" - the tail is what the model renders differently every
            // time ("CIRCLE AY", "CIRCLE A", "CIRCLE E I"), so asking for less
            // of it catches more. Recall is only half of a wake word though,
            // and this is the other half: the shorter phrase must still refuse
            // every competitor he said into the same microphone.
            Assert.False(PhraseInSpeech.SaidAnywhere(decoy, "Hey Circle"), decoy);
        }
    }

    [Fact]
    public void A_mishearing_of_one_letter_is_forgiven_and_two_is_not()
    {
        // WHERE THE LINE SITS, independent of any one recording. "hay" and
        // "he" are each one edit from "hey" and are real mishearings of it;
        // "a" and "the" are not reachable without a budget that would also
        // start matching words which are not the phrase at all.
        Assert.True(PhraseInSpeech.SaidAnywhere("HAY B", "Hey B"));
        Assert.True(PhraseInSpeech.SaidAnywhere("HE B", "Hey B"));

        foreach (var heard in new[] { "A B", "THE B" })
            Assert.False(PhraseInSpeech.SaidAnywhere(heard, "Hey B"),
                $"if this ever passes, check that 'HAY BOB' still fails: {heard}");

        // And the single letter still has to be exactly itself, or "bob" and
        // "bill" wake the phone - which is pinned on its own above.
        Assert.False(PhraseInSpeech.SaidAnywhere("HAY BOB", "Hey B"));
        Assert.False(PhraseInSpeech.SaidAnywhere("HAY BILL", "Hey B"));
    }

    // ── the distance function the fuzzy path rests on ────────────────────────

    [Theory]
    [InlineData("b", "b", 0)]
    [InlineData("b", "be", 1)]
    [InlineData("b", "d", 1)]
    [InlineData("hey", "hay", 1)]
    [InlineData("circle", "circl", 1)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    public void Edit_distance_counts_what_it_should(string a, string b, int expected)
    {
        Assert.Equal(expected, PhraseInSpeech.Distance(a, b));
        Assert.Equal(expected, PhraseInSpeech.Distance(b, a));   // symmetric
    }

    [Fact]
    public void Distance_is_not_given_nulls()
    {
        Assert.Throws<ArgumentNullException>(() => PhraseInSpeech.Distance(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => PhraseInSpeech.Distance("a", null!));
    }

    // ── and it serves a language that is not English ─────────────────────────

    [Fact]
    public void A_phrase_in_another_language_works_the_same_way()
    {
        // THE WHOLE REASON THIS APPROACH WAS CHOSEN. Any phrase, any language,
        // set at runtime, with no model trained for it.
        Assert.True(PhraseInSpeech.JustSaid("sawubona bhuti", "Sawubona Bhuti"));
        Assert.True(PhraseInSpeech.JustSaid("molo mfowethu", "Molo Mfowethu"));
        Assert.False(PhraseInSpeech.JustSaid("sawubona sisi", "Sawubona Bhuti"));
    }

    [Fact]
    public void The_exact_transcript_the_phone_produced_wakes_it()
    {
        // MEASURED ON THE P30 AT 04:05 ON 2026-10-12, the first time the
        // transcribing wake path ever ran on the device. The owner said his
        // phrase and the 20M model wrote:
        //
        //     ANY TIME NOW HAY B SIGNING OUT
        //
        // It did not wake. The phrase is right there, and the loop was wired to
        // the EXACT matcher while the forgiving one sat beside it, already
        // written and already tested - the same "built and never connected"
        // defect this feature has produced four times before. "hay" for "hey"
        // is one vowel in a three-letter word with no context to constrain it.
        Assert.False(PhraseInSpeech.JustSaid("any time now hay b", "Hey B"));
        Assert.True(PhraseInSpeech.NearlySaid("any time now hay b", "Hey B"));

        // And the same recogniser's other guesses at that syllable still must
        // not wake it - those came off the same device in the same session.
        Assert.False(PhraseInSpeech.NearlySaid("any time now hay bob", "Hey B"));
        Assert.False(PhraseInSpeech.NearlySaid("any time now hay bill", "Hey B"));
    }
}
