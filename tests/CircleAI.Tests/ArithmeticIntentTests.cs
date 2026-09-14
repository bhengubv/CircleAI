// ArithmeticIntentTests.cs
//
// The recogniser that answers a plain sum from the engine and, just as
// importantly, DECLINES a sentence that merely contains an operator word. The
// declines are the point: a false positive answers a real question with a
// number, so "what time is it" and "how many times did I call" must fall
// through to the model, every time.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class ArithmeticIntentTests
{
    [Theory]
    [InlineData("what is 12 times 8", "96")]
    [InlineData("347 times 89", "30883")]          // the measured device failure
    [InlineData("12 * 8", "96")]
    [InlineData("12 x 8", "96")]
    [InlineData("12 × 8", "96")]
    [InlineData("5 plus 3", "8")]
    [InlineData("10 minus 3", "7")]
    [InlineData("100 divided by 4", "25")]
    [InlineData("100 over 4", "25")]
    [InlineData("10 divided by 4", "2.5")]
    [InlineData("12 multiplied by 8", "96")]
    [InlineData("(2 + 3) * 4", "20")]
    [InlineData("20% of 80", "16")]
    [InlineData("20 percent of 80", "16")]
    [InlineData("what's 2+2", "4")]
    [InlineData("please calculate 6 * 7", "42")]
    [InlineData("what is 2 + 2 equals", "4")]
    [InlineData("1,000 + 1", "1001")]              // thousands separator
    [InlineData("what is 5 minus 8", "-3")]        // negative result
    // Spoken number words (Whisper transcribes small numbers as words):
    [InlineData("three hundred forty seven times eighty nine", "30883")]
    [InlineData("what is twelve times eight", "96")]
    [InlineData("forty-seven times two", "94")]    // hyphen + words
    [InlineData("one hundred divided by four", "25")]
    [InlineData("five plus three", "8")]
    [InlineData("a hundred minus one", "99")]
    [InlineData("twenty percent of eighty", "16")]
    public void Answers_a_plain_sum(string question, string expected)
    {
        Assert.True(ArithmeticIntent.TryAnswer(question, out var answer),
            $"expected to answer \"{question}\"");
        Assert.Equal(expected, answer);
    }

    [Theory]
    [InlineData("what time is it")]
    [InlineData("how many times did I call")]
    [InlineData("times square")]
    [InlineData("what is the capital of France")]
    [InlineData("tell me a joke")]
    [InlineData("what is love")]
    [InlineData("what is your name")]
    [InlineData("think it over")]
    [InlineData("divide the work between us")]
    [InlineData("2024")]                            // a number, but no operator
    [InlineData("5")]
    [InlineData("hello")]
    [InlineData("5 divided by 0")]                  // the evaluator declines, so this does too
    [InlineData("call me at eight")]                // a number word in ordinary speech
    [InlineData("i have twenty questions")]
    [InlineData("")]
    [InlineData("   ")]
    public void Declines_anything_that_is_not_plainly_a_sum(string question)
    {
        Assert.False(ArithmeticIntent.TryAnswer(question, out var answer),
            $"expected to decline \"{question}\"");
        Assert.Equal("", answer);
    }

    [Fact]
    public void Null_declines()
    {
        Assert.False(ArithmeticIntent.TryAnswer(null, out var answer));
        Assert.Equal("", answer);
    }
}
