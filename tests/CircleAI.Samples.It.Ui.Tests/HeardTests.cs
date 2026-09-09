// HeardTests.cs
//
// The rules that decide whether the assistant answers at all.
//
// Every case here is a measured transcript from a real phone on 2026-09-09, not
// an invented one. The owner's report was that the replies were "so wrong anyone
// would get fired", and the log showed why: the transcriber was handed the tail
// of the wake phrase, invented a word out of it, and the answering model then
// invented a statement of law to go with the invented word.
//
// These functions are the gate between those two inventions. They were private
// inside an Android-only class and therefore untestable, which is how a
// fabricated legal claim reached a speaker.

using CircleAI.Samples.It;

namespace CircleAI.Samples.It.Ui.Tests;

public class HeardTests
{
    [Fact]
    public void A_made_up_word_still_gets_through_and_that_is_deliberate()
    {
        // MEASURED ON A P30. The wake fired at 3 of 8 tokens - part way through
        // "Hey Circle AI" - and the turn recorded the rest of the phrase, which
        // came back as this and was answered as a question about cryptocurrency.
        //
        // It is NOT filtered, and the first attempt to filter it is why this test
        // says so out loud. The rule was "a single token of ten characters or
        // more is garbage", which also swallowed "Johannesburg". Nothing here can
        // tell an invented word from a real one without a dictionary.
        //
        // The fault is fixed where it is caused: the turn no longer opens its
        // microphone until the wake phrase has finished, so this transcript has
        // no way to be produced. Pinned as a deliberate limit rather than left as
        // an assumption somebody re-discovers.
        Assert.Equal("Placeculeeai.", Heard.Speech("Placeculeeai."));
    }

    [Fact]
    public void The_repetition_loop_is_not_speech()
    {
        // MEASURED ON A REDMI 12, same build, same minute. Whisper's well-known
        // failure on junk audio: one fragment, over and over.
        var loop = string.Concat(Enumerable.Repeat("Heset Lea. ", 15));
        Assert.Null(Heard.Speech(loop));
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("Stop.")]
    [InlineData("no")]
    [InlineData("Johannesburg")]
    public void Short_real_answers_survive(string said)
    {
        // THE HALF THAT MUST NOT BREAK. People answer an assistant in one word
        // constantly, and "Johannesburg" is twelve letters with no spaces - the
        // length test alone would eat it, which is why it is paired with the
        // punctuation trim rather than used on its own.
        Assert.Equal(said.Trim('.'), Heard.Speech(said)?.Trim('.'));
    }

    [Fact]
    public void A_real_sentence_survives()
    {
        const string asked = "What is the weather in Durban tomorrow?";
        Assert.Equal(asked, Heard.Speech(asked));
    }

    [Fact]
    public void A_meeting_that_repeats_a_phrase_keeps_the_rest()
    {
        // The repetition test must not eat a transcript that merely contains a
        // repeated line among real content.
        const string minutes =
            "Right, let us begin. Thank you. Thank you. The budget is approved. "
            + "We will reconvene on Thursday.";
        Assert.Equal(minutes, Heard.Speech(minutes));
    }

    [Fact]
    public void Non_speech_labels_still_go()
    {
        Assert.Null(Heard.Speech("[BLANK_AUDIO]"));
        Assert.Null(Heard.Speech("[Music]"));
        Assert.Equal("forklift (code 14)", Heard.Speech("forklift (code 14)"));
    }

    [Fact]
    public void The_console_marker_is_not_part_of_the_answer()
    {
        // It reached the caption AND the voice: the first thing synthesised was a
        // four-character chunk, so the assistant said "IT!" out loud before
        // anything it had been asked.
        Assert.Equal(
            "I cannot place your cryptocurrency.",
            Heard.Answer("IT! > I cannot place your cryptocurrency."));
    }

    [Fact]
    public void The_marker_is_stripped_only_from_the_front()
    {
        const string mid = "The app is called IT! > and it listens.";
        Assert.Equal(mid, Heard.Answer(mid));
    }

    [Fact]
    public void A_partial_marker_is_left_alone()
    {
        // Streaming means the caption is rebuilt from a growing string, so the
        // marker arrives a character at a time and must not be half-eaten.
        Assert.Equal("IT!", Heard.Answer("IT!"));
        Assert.Equal("IT! >", Heard.Answer("IT! >"));
    }
}
