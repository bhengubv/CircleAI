// WhatAQuestionReducesToProbe.cs
//
// A MEASUREMENT, NOT A RULE: how many distinguishing words each real question
// reduces to, because the skill store's coverage rule is set by that count and
// the first version of it was set without looking.

using CircleAI.Core;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class WhatAQuestionReducesToProbe
{
    private readonly ITestOutputHelper _out;

    public WhatAQuestionReducesToProbe(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData("Happy, how are you doing today? What is the general temperature today?")]
    [InlineData("I have a health question")]
    [InlineData("Help me plan meals")]
    [InlineData("I have a legal question")]
    [InlineData("Help me deal with a government office")]
    [InlineData("I have a question about my job or my rights at work")]
    [InlineData("threat modeling for a mobile app")]
    [InlineData("Hey, B. What date is it today?")]
    public void Prints_the_words_a_question_is_searched_on(string question)
    {
        var terms = SearchTerms.Significant(question);
        _out.WriteLine($"{terms.Count}  [{string.Join(", ", terms)}]  <- \"{question}\"");
    }
}
