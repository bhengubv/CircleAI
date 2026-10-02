// PlainReplyTests.cs
//
// The asterisks a small model sprinkles on an answer, and what they do when nobody
// removes them.
//
// Measured on a P30 on 2026-10-02: "Tell me a joke" came back as
//     **Joke:** Why did the lobster jump into a swimming pool?
// rendered exactly like that in the bubble - and the same string is what "Read it
// out" hands to the voice.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class PlainReplyTests
{
    [Fact]
    public void Takes_the_asterisks_off_the_answer_that_was_on_screen()
    {
        var said = PlainReply.Clean(
            "**Joke:** Why did the lobster jump into a swimming pool?\n\n"
            + "*(The lobster jumped in because he thought if he got wet, he'd drown too!)*");

        Assert.DoesNotContain("*", said);
        Assert.StartsWith("Joke: Why did the lobster", said);
    }

    [Theory]
    [InlineData("**bold**", "bold")]
    [InlineData("*italic*", "italic")]
    [InlineData("__bold__", "bold")]
    [InlineData("_italic_", "italic")]
    [InlineData("# Heading", "Heading")]
    [InlineData("### Smaller", "Smaller")]
    public void Removes_the_markers_a_voice_would_read_aloud(string given, string expected)
        => Assert.Equal(expected, PlainReply.Clean(given));

    [Fact]
    public void A_code_fence_is_left_exactly_as_it_was()
    {
        // "Answers questions and helps you write" includes code, and inside a fence
        // these characters are the content. Stripping there would corrupt the one
        // kind of answer whose punctuation is load-bearing.
        const string code = "Try this:\n```\nvar x = a * b;\nname_of_thing = 1;\n```\ndone";

        Assert.Equal(code, PlainReply.Clean(code));
    }

    [Fact]
    public void An_underscore_inside_a_word_is_a_name_not_emphasis()
    {
        // file_name and snake_case are not italics, and a voice saying "file name"
        // is right while one saying "file underscore name" is not - but mangling the
        // identifier on screen would be worse than either.
        Assert.Equal("Open file_name.txt", PlainReply.Clean("Open file_name.txt"));
    }

    [Fact]
    public void A_hash_in_a_sentence_is_a_number_sign()
    {
        // Only a line can start a heading.
        Assert.Equal("Issue #42 is open", PlainReply.Clean("Issue #42 is open"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_in_nothing_out(string? given)
        => Assert.Equal(string.Empty, PlainReply.Clean(given));

    [Fact]
    public void Plain_prose_is_untouched()
    {
        const string prose = "Here is one for you. Why did the chicken cross the road?";
        Assert.Equal(prose, PlainReply.Clean(prose));
    }
}
