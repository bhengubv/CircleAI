// ToolIntentTests.cs
//
// The engine's decision to run a tool the 0.6B will not call itself (battery,
// live web). The DECLINES are the point: a false positive reads the battery
// nobody asked about, or sends a query off the phone uninvited — so a sentence
// that merely contains "charge" or "search" must fall through to the model.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class ToolIntentTests
{
    [Theory]
    [InlineData("what's my battery", ToolNeed.Battery)]
    [InlineData("how much battery do I have left", ToolNeed.Battery)]
    [InlineData("battery level", ToolNeed.Battery)]
    [InlineData("is the battery low", ToolNeed.Battery)]
    [InlineData("what's the weather like today", ToolNeed.Web)]
    [InlineData("what's the weather", ToolNeed.Web)]
    [InlineData("give me the latest news", ToolNeed.Web)]
    [InlineData("any headlines this morning", ToolNeed.Web)]
    [InlineData("what's the forecast for tomorrow", ToolNeed.Web)]
    [InlineData("search the web for the exchange rate", ToolNeed.Web)]
    [InlineData("can you look it up online", ToolNeed.Web)]
    [InlineData("find that on the internet", ToolNeed.Web)]
    public void Recognises_a_clear_tool_need(string question, ToolNeed expected)
        => Assert.Equal(expected, ToolIntent.Classify(question));

    [Theory]
    [InlineData("tell me a joke")]
    [InlineData("what is the capital of France")]
    [InlineData("charge my account")]              // "charge" is not the battery
    [InlineData("who is in charge here")]
    [InlineData("search my memory for Thabo")]      // "search" without the web is local
    [InlineData("whether I should go out")]         // "whether" is not "weather"
    [InlineData("the newsagent on the corner")]     // "news" only word-bounded
    [InlineData("what is 12 times 8")]              // arithmetic, handled before this
    [InlineData("")]
    [InlineData("   ")]
    public void Leaves_everything_else_to_the_model(string question)
        => Assert.Equal(ToolNeed.None, ToolIntent.Classify(question));

    [Fact]
    public void Null_is_none()
        => Assert.Equal(ToolNeed.None, ToolIntent.Classify(null));
}
