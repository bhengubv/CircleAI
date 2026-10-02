// OverviewQuestionTests.cs
//
// The rule that decides which question the app answers itself.
//
// It has to be narrow in both directions. Too wide and a greeting gets a list of
// features; too narrow and the one question people actually ask goes back to the
// model, which measurably will not answer it.

using CircleAI.Core;
using Xunit;

namespace CircleAI.Tests;

public sealed class OverviewQuestionTests
{
    [Theory]
    [InlineData("what can you do")]
    [InlineData("What can you do?")]
    [InlineData("WHAT CAN YOU DO")]
    [InlineData("what do you do")]
    [InlineData("what can you do for me")]
    [InlineData("so what can you do then")]
    [InlineData("and what do you do?")]
    public void Recognises_a_question_about_what_it_can_do(string question)
        => Assert.True(OverviewQuestion.Matches(question));

    [Theory]
    [InlineData("who are you")]
    [InlineData("what are you")]
    [InlineData("how are you")]
    [InlineData("how are you?")]
    public void Leaves_a_question_about_WHO_or_HOW_to_the_model(string question)
    {
        // THESE ALSO REDUCE TO ZERO SIGNIFICANT TERMS, which is exactly why the
        // capability word is required. Answering "how are you" with a list of
        // features is worse than anything the model would have said, and the
        // persona already covers who it is.
        Assert.False(OverviewQuestion.Matches(question));
    }

    [Theory]
    [InlineData("what is the capital of France")]
    [InlineData("tell me a joke")]
    [InlineData("what can a kettle do")]
    [InlineData("can you translate this into Zulu")]
    [InlineData("summarise this for me")]
    public void Leaves_a_real_question_alone(string question)
    {
        // A QUESTION WITH A SUBJECT IS NEVER THIS ONE, however it is phrased.
        // "What can a kettle do" has a kettle in it; "can you translate this into
        // Zulu" is a job, not a survey. Both go to the model.
        Assert.False(OverviewQuestion.Matches(question));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_not_a_question(string? question)
        => Assert.False(OverviewQuestion.Matches(question));

    [Fact]
    public void A_capability_word_alone_is_not_enough()
    {
        // The two tests are an AND. "What can I do about load shedding" carries a
        // capability word and a real subject, and the subject wins.
        Assert.False(OverviewQuestion.Matches("what can I do about load shedding"));
    }
}
