// NumberWordsTests.cs
//
// Spoken numbers to digits, so the voice path can answer a sum a person said.
// The "leaves alone" cases matter as much as the conversions: a number word in
// ordinary speech ("a joke", "call me at eight") must not turn the sentence into
// a sum on its own - the conservative gate in ArithmeticIntent still has the
// final say, but this must not hand it a false digit run either.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class NumberWordsTests
{
    [Theory]
    [InlineData("twelve", "12")]
    [InlineData("eighty nine", "89")]
    [InlineData("three hundred forty seven", "347")]
    [InlineData("one hundred", "100")]
    [InlineData("a hundred", "100")]
    [InlineData("a hundred and one", "101")]
    [InlineData("one thousand two hundred thirty four", "1234")]
    [InlineData("two million", "2000000")]
    [InlineData("forty seven", "47")]   // hyphens are split by ArithmeticIntent, before this runs
    [InlineData("three hundred forty seven times eighty nine", "347 times 89")]
    [InlineData("twenty percent of eighty", "20 percent of 80")]
    public void Digitises_number_words(string words, string expected)
        => Assert.Equal(expected, NumberWords.Digitise(words));

    [Theory]
    [InlineData("tell me a joke", "tell me a joke")]   // "a" not before a scale
    [InlineData("what time is it", "what time is it")]
    [InlineData("call me at eight", "call me at 8")]   // digit run is fine; the gate rejects the sentence
    [InlineData("347 times 89", "347 times 89")]       // digits pass straight through
    public void Leaves_ordinary_words_alone(string text, string expected)
        => Assert.Equal(expected, NumberWords.Digitise(text));
}
