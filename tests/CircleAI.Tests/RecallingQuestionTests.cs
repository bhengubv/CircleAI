// RecallingQuestionTests.cs
//
// Retrieval is not prompting, and one string was doing both.
//
// Recalling.Ask builds what the MODEL should read: remembered facts about the
// person, then the question. That whole thing was also handed to the skill
// store and the RAG index as the thing to SEARCH FOR - so a P30 that had
// remembered one note about deploying searched 1,378 skills for the words in
// that note alongside the actual question. Measured 2026-09-13.
//
// The facts are the least relevant possible search terms: they are about the
// person, and what is being looked for is about the subject.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class RecallingQuestionTests
{
    static IReadOnlyList<Remembered> Known(params string[] facts)
        => [.. facts.Select(f => new Remembered(f))];

    [Fact]
    public void The_exact_prompt_the_phone_built_gives_back_only_the_question()
    {
        // VERBATIM FROM THE DEVICE LOG, including the fact it had learned:
        //   CIRCLEAI-SKILLS full q="Things you already know about them:
        //   - Never deploy with -t:Install on this phone, it wipes the models every time
        //   What is the weather like today?"
        var asked = Recalling.Ask(
            "What is the weather like today?",
            Known("Never deploy with -t:Install on this phone, it wipes the models every time"));

        Assert.Equal("What is the weather like today?", Recalling.Question(asked));
    }

    [Fact]
    public void Nothing_about_the_person_survives_into_the_search()
    {
        // THE POINT OF THE WHOLE CHANGE. A single leaked word here is a term the
        // index ranks on, and these are words about somebody's phone habits.
        var asked = Recalling.Ask(
            "How do I plant maize?",
            Known("Her name is Thandi", "She works in Durban", "Rent is due on the 3rd"));

        var question = Recalling.Question(asked);

        Assert.Equal("How do I plant maize?", question);
        Assert.DoesNotContain("Thandi", question, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Durban", question, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Rent", question, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("already know", question, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_question_that_was_never_composed_passes_through_untouched()
    {
        // Ask returns `heard` unchanged when nothing is remembered, so most
        // turns never carry the heading at all - and a head that composes
        // nothing must be completely unaffected.
        Assert.Equal("What is the capital of Kenya",
            Recalling.Question("What is the capital of Kenya"));

        Assert.Equal("What is the capital of Kenya",
            Recalling.Question(Recalling.Ask("What is the capital of Kenya", Known())));
    }

    [Fact]
    public void A_question_of_several_paragraphs_keeps_all_of_them()
    {
        // SPLIT ON THE FIRST BLANK LINE, NOT THE LAST. Somebody pasting a job
        // advert into the box sends paragraphs; taking only the final one would
        // search on its last sentence and silently drop the rest.
        var pasted = "Senior plumber wanted." + Environment.NewLine + Environment.NewLine
                   + "Must own tools." + Environment.NewLine + Environment.NewLine
                   + "Start Monday.";

        var asked = Recalling.Ask(pasted, Known("He is a plumber"));

        Assert.Equal(pasted, Recalling.Question(asked));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_is_nothing_out(string? asked)
        => Assert.True(string.IsNullOrWhiteSpace(Recalling.Question(asked)));

    [Fact]
    public void Text_that_merely_mentions_the_heading_is_not_treated_as_composed()
    {
        // The heading has to be at the START. Somebody asking about the feature
        // itself - "what does Things you already know about them: mean" - must
        // not have their question cut in half.
        const string asking = "What does Things you already know about them: mean?";
        Assert.Equal(asking, Recalling.Question(asking));
    }
}
