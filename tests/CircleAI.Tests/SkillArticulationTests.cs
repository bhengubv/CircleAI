using CircleAI.Career;
using Xunit;

namespace CircleAI.Tests;

// The wording-help engine for the CV interview. Pure: a prompt builder and a
// forgiving parser, so the one part that can be checked without a phone is.
//
// The guarantee under test is the honest default - nothing usable means the
// person keeps their own words, never a blank and never something worse.
public class SkillArticulationTests
{
    [Theory]
    [InlineData(ProfileField.Headline)]
    [InlineData(ProfileField.WorkDid)]
    [InlineData(ProfileField.Skills)]
    [InlineData(ProfileField.Summary)]
    public void Offers_help_where_wording_is_the_work(ProfileField field)
        => Assert.True(SkillArticulation.Applies(field));

    [Theory]
    [InlineData(ProfileField.FullName)]
    [InlineData(ProfileField.Phone)]
    [InlineData(ProfileField.Location)]
    [InlineData(ProfileField.WorkWhen)]
    [InlineData(ProfileField.WorkRole)]
    [InlineData(ProfileField.WorkOrganisation)]
    [InlineData(ProfileField.Education)]
    [InlineData(ProfileField.Certification)]
    [InlineData(ProfileField.Languages)]
    public void Leaves_facts_exactly_as_given(ProfileField field)
        => Assert.False(SkillArticulation.Applies(field));

    [Fact]
    public void Prompt_carries_the_persons_words_and_forbids_inventing()
    {
        var q = new InterviewQuestion(ProfileField.Skills,
            "What are you good at?", "advert match");

        var prompt = SkillArticulation.BuildPrompt(q, "  i fix cars and bakkies  ");

        Assert.Contains("i fix cars and bakkies", prompt);   // their words, trimmed in
        Assert.Contains("What are you good at?", prompt);     // the question they answered
        Assert.Contains("Do not add", prompt);                // the no-invention rule
        Assert.Contains("comma-separated", prompt);           // the Skills shape
    }

    [Fact]
    public void Prompt_shape_follows_the_field()
    {
        var did = SkillArticulation.BuildPrompt(
            new InterviewQuestion(ProfileField.WorkDid, "What did you do?", "the part that gets read"),
            "swept up, served customers, closed the till");

        Assert.Contains("two or three short lines", did);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_reply_means_keep_their_words(string? reply)
        => Assert.Null(SkillArticulation.Parse(reply, "i fix cars"));

    [Fact]
    public void Strips_the_quotes_a_small_model_adds()
        => Assert.Equal("Vehicle maintenance and repair",
            SkillArticulation.Parse("\"Vehicle maintenance and repair\"", "i fix cars"));

    [Fact]
    public void The_same_words_back_are_not_a_suggestion()
        => Assert.Null(SkillArticulation.Parse("I Fix Cars", "i fix cars"));

    [Fact]
    public void A_runaway_answer_is_dropped()
        => Assert.Null(SkillArticulation.Parse(new string('x', 500), "short"));

    [Fact]
    public void A_clean_rewrite_comes_back_trimmed()
        => Assert.Equal("Motor mechanic",
            SkillArticulation.Parse("  Motor mechanic\n", "i fix cars"));
}
