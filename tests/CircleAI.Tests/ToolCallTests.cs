// ToolCallTests.cs
//
// The detector that stops the phone reading JSON out loud.
//
// Asked for the weather, the model answers with a tool call rather than words.
// The streaming path does not execute tools, so that call arrives as ordinary
// text - and the head that ships pushed every fragment straight to the speaker.
//
// Two failures matter here and they are not symmetrical. Missing a call means a
// person hears JSON. Tripping on an ordinary answer means SILENCE, with no way
// for them to tell why, which is worse. These pin both directions.

using System.Text;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class ToolCallTests
{
    [Theory]
    [InlineData("<tool_call>{\"name\": \"search\", \"arguments\": {\"q\": \"weather\"}}</tool_call>")]
    [InlineData("<TOOL_CALL>")]
    [InlineData("  <tool_call")]
    [InlineData("{\"name\": \"get_weather\", \"arguments\": {\"city\": \"Johannesburg\"}}")]
    public void A_tool_call_is_recognised(string streamed)
        => Assert.True(ToolCall.Looks(streamed));

    [Theory]
    [InlineData("The weather in Johannesburg is warm today.")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Your name is what I was asked for.")]
    [InlineData("The arguments against it are three.")]
    public void An_ordinary_answer_is_not(string? streamed)
        => Assert.False(ToolCall.Looks(streamed));

    [Fact]
    public void Both_json_keys_are_required_not_either()
    {
        // SILENCE IS THE WORSE FAILURE. An answer containing one of these words
        // must still be spoken; only the pair means the model stopped answering
        // and started calling.
        Assert.False(ToolCall.Looks("She asked for my \"name\" and I told her."));
        Assert.False(ToolCall.Looks("The \"arguments\" were long and dull."));
        Assert.True(ToolCall.Looks("{\"name\":\"x\",\"arguments\":{}}"));
    }

    [Fact]
    public void Only_the_head_of_a_long_answer_is_examined()
    {
        // A tool call is what the model says INSTEAD of an answer, so it comes
        // first. Without this bound, a long correct answer that happens to
        // discuss tool calls near the end would be swallowed after the person had
        // already heard the beginning of it.
        var essay = new string('x', ToolCall.Head + 50)
                  + "{\"name\": \"late\", \"arguments\": {}}";

        Assert.False(ToolCall.Looks(essay));
    }

    [Fact]
    public void It_reads_a_builder_without_copying_the_whole_buffer()
    {
        // The streaming path appends to a StringBuilder per fragment and asks on
        // every one. ToString() on a growing buffer each time is the kind of cost
        // that only shows up on the phone.
        var acc = new StringBuilder();
        foreach (var fragment in new[] { "<tool", "_call>", "{\"name\":\"x\"}" })
            acc.Append(fragment);

        Assert.True(ToolCall.Looks(acc));
    }

    [Fact]
    public void It_catches_the_call_before_the_first_word_is_spoken()
    {
        // THE POINT OF THE HEAD BOUND, FROM THE OTHER SIDE. Detection has to fire
        // on the FIRST fragments, because anything already pushed to the speaker
        // has been heard and cannot be taken back.
        var acc = new StringBuilder();
        acc.Append("<tool_call>");

        Assert.True(ToolCall.Looks(acc));
    }
}
