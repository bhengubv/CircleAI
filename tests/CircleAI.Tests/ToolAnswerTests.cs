// ToolAnswerTests.cs
//
// The engine's own words for a tool result — because the 0.6B will not phrase one
// (measured: the battery tool returned 82 and the model still said "I can't access
// this information"). So the engine formats it, and this pins the wording.

using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class ToolAnswerTests
{
    [Theory]
    [InlineData(82, "Your battery is at 82%.")]
    [InlineData(0, "Your battery is at 0%.")]
    [InlineData(100, "Your battery is at 100%.")]
    public void Battery_reads_the_percent(int percent, string expected)
        => Assert.Equal(expected, ToolAnswer.Battery(percent));

    [Theory]
    [InlineData(null)]
    [InlineData(-1)]
    [InlineData(101)]
    public void A_battery_that_cannot_be_read_says_so(int? percent)
        => Assert.Equal("I could not read the battery.", ToolAnswer.Battery(percent));

    [Theory]
    [InlineData("It's 24 degrees in Durban.", "It's 24 degrees in Durban.")]
    [InlineData("Could not reach the internet.", "Could not reach the internet.")]
    [InlineData("  needs a trim  ", "needs a trim")]
    public void Web_relays_the_search_text(string text, string expected)
        => Assert.Equal(expected, ToolAnswer.Web(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Web_with_nothing_says_so(string? text)
        => Assert.Equal("I could not find anything on that.", ToolAnswer.Web(text));
}
