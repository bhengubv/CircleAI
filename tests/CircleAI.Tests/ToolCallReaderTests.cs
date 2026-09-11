// ToolCallReaderTests.cs
//
// What a small model actually emits when asked for a tool call.
//
// Every malformed case below is a real shape a 0.6B model produces, not an
// invented one. Under the old parser each of them returned null, the agentic
// loop read null as "the model chose not to use a tool", and the person got an
// answer missing the thing they asked for - with nothing anywhere saying why.
//
// One of them was worse than null, and it has its own test.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Tools;
using Xunit;

namespace CircleAI.Tests;

public class ToolCallReaderTests
{
    // ── The shapes that used to be dropped ──────────────────────────────

    [Fact]
    public void The_plain_correct_form_still_reads()
    {
        var call = ToolCallReader.Read(
            "<tool_call>{\"name\":\"get_weather\",\"arguments\":{\"city\":\"Durban\"}}</tool_call>");

        Assert.NotNull(call);
        Assert.Equal("get_weather", call!.ToolName);
        Assert.Equal("Durban", call.Arguments["city"]);
    }

    [Fact]
    public void Arguments_as_a_json_string_are_read_rather_than_silently_dropped()
    {
        // THE ONE THAT WAS WORSE THAN SILENCE. Qwen emits the arguments as a
        // STRING containing JSON about as often as it emits an object. The old
        // parser checked for an object, found none, and invoked the tool with an
        // EMPTY argument list - so the search ran with no query and reported
        // whatever a search for nothing returns. A wrong action, taken
        // confidently, with no error anywhere.
        var call = ToolCallReader.Read(
            "<tool_call>{\"name\":\"search\",\"arguments\":\"{\\\"query\\\":\\\"load shedding\\\"}\"}</tool_call>");

        Assert.NotNull(call);
        Assert.Equal("load shedding", call!.Arguments["query"]);
    }

    [Fact]
    public void A_truncated_reply_still_yields_the_call_it_had_decided_on()
    {
        var call = ToolCallReader.Read("<tool_call>{\"name\":\"search\",\"arguments\":{\"query\":\"x\"}}");

        Assert.NotNull(call);
        Assert.Equal("search", call!.ToolName);
    }

    [Fact]
    public void A_markdown_fence_around_the_call_is_packaging_not_content()
    {
        var call = ToolCallReader.Read(
            "<tool_call>\n```json\n{\"name\":\"search\",\"arguments\":{\"query\":\"x\"}}\n```\n</tool_call>");

        Assert.NotNull(call);
        Assert.Equal("search", call!.ToolName);
    }

    [Fact]
    public void A_trailing_comma_is_repaired_because_it_has_one_reading()
    {
        var call = ToolCallReader.Read(
            "<tool_call>{\"name\":\"search\",\"arguments\":{\"query\":\"x\",},}</tool_call>");

        Assert.NotNull(call);
        Assert.Equal("x", call!.Arguments["query"]);
    }

    [Fact]
    public void A_comma_inside_a_string_is_not_a_trailing_comma()
    {
        // The repair walks strings rather than scanning text, or "Durban, KZN"
        // loses its comma and the tool is called with a different place.
        var call = ToolCallReader.Read(
            "<tool_call>{\"name\":\"go\",\"arguments\":{\"to\":\"Durban, KZN\"}}</tool_call>");

        Assert.Equal("Durban, KZN", call!.Arguments["to"]);
    }

    [Fact]
    public void A_name_written_as_a_call_loses_its_brackets()
    {
        var call = ToolCallReader.Read("<tool_call>{\"name\":\"get_weather()\"}</tool_call>");

        Assert.Equal("get_weather", call!.ToolName);
    }

    [Theory]
    [InlineData("tool_name")]
    [InlineData("function")]
    public void The_other_spellings_of_the_name_field_are_accepted(string field)
    {
        var call = ToolCallReader.Read(
            $"<tool_call>{{\"{field}\":\"search\",\"arguments\":{{}}}}</tool_call>");

        Assert.Equal("search", call!.ToolName);
    }

    [Theory]
    [InlineData("parameters")]
    [InlineData("args")]
    public void The_other_spellings_of_the_arguments_field_are_accepted(string field)
    {
        var call = ToolCallReader.Read(
            $"<tool_call>{{\"name\":\"search\",\"{field}\":{{\"query\":\"x\"}}}}</tool_call>");

        Assert.Equal("x", call!.Arguments["query"]);
    }

    [Fact]
    public void Values_keep_their_types_instead_of_becoming_text()
    {
        // A tool expecting a number used to receive the string "5", and whether
        // that worked depended on how forgiving that particular tool was.
        var call = ToolCallReader.Read(
            "<tool_call>{\"name\":\"bid\",\"arguments\":" +
            "{\"amount\":250,\"ratio\":1.5,\"proxy\":true,\"note\":null}}</tool_call>");

        Assert.Equal(250L, call!.Arguments["amount"]);
        Assert.Equal(1.5d, call.Arguments["ratio"]);
        Assert.Equal(true, call.Arguments["proxy"]);
        Assert.Null(call.Arguments["note"]);
    }

    [Fact]
    public void Argument_names_are_matched_without_regard_to_case()
    {
        var call = ToolCallReader.Read(
            "<tool_call>{\"name\":\"search\",\"arguments\":{\"Query\":\"x\"}}</tool_call>");

        Assert.Equal("x", call!.Arguments["query"]);
    }

    // ── More than one ───────────────────────────────────────────────────

    [Fact]
    public void Every_call_in_a_reply_is_read_not_only_the_first()
    {
        // "Remind me at six and text Sipho" is two blocks. Running the first and
        // re-prompting lost the second, and nothing reported a dropped action.
        var calls = ToolCallReader.ReadAll(
            "<tool_call>{\"name\":\"remind\",\"arguments\":{\"at\":\"18:00\"}}</tool_call>" +
            "I will also message him.\n" +
            "<tool_call>{\"name\":\"text\",\"arguments\":{\"who\":\"Sipho\"}}</tool_call>");

        Assert.Equal(2, calls.Count);
        Assert.Equal("remind", calls[0].ToolName);
        Assert.Equal("text", calls[1].ToolName);
    }

    [Fact]
    public void A_reply_with_no_call_reads_as_none()
    {
        Assert.Empty(ToolCallReader.ReadAll("Here is your answer."));
        Assert.Empty(ToolCallReader.ReadAll(""));
        Assert.Empty(ToolCallReader.ReadAll(null));
        Assert.Null(ToolCallReader.Read("Here is your answer."));
    }

    [Fact]
    public void Genuine_nonsense_is_still_refused()
    {
        // The repairs stop where guessing would start. A body with no JSON in it
        // is not a call, and inventing one would be worse than reading none.
        Assert.Null(ToolCallReader.Read("<tool_call>not valid json</tool_call>"));
        Assert.Null(ToolCallReader.Read("<tool_call>{}</tool_call>"));
        Assert.Null(ToolCallReader.Read("<tool_call>{\"arguments\":{\"a\":1}}</tool_call>"));
    }

    // ── Checking it against the registry ────────────────────────────────

    [Fact]
    public void A_call_that_matches_a_registered_tool_is_runnable()
    {
        var call = Call("get_weather", ("city", "Durban"));

        Assert.True(ToolCallReader.IsRunnable(call, Tools, out var problem));
        Assert.Equal(string.Empty, problem);
    }

    [Fact]
    public void An_unknown_tool_name_comes_back_with_the_real_names_in_it()
    {
        // The model can only correct itself if it is told what the names are.
        var call = Call("weather", ("city", "Durban"));

        Assert.False(ToolCallReader.IsRunnable(call, Tools, out var problem));
        Assert.Contains("weather", problem);
        Assert.Contains("get_weather", problem);
        Assert.Contains("set_alarm", problem);
    }

    [Fact]
    public void A_missing_required_argument_names_the_argument()
    {
        var call = Call("get_weather");

        Assert.False(ToolCallReader.IsRunnable(call, Tools, out var problem));
        Assert.Contains("city", problem);
    }

    [Fact]
    public void A_required_argument_present_but_null_counts_as_missing()
    {
        var call = new ToolInvocation
        {
            ToolName = "get_weather",
            Arguments = new Dictionary<string, object?> { ["city"] = null },
        };

        Assert.False(ToolCallReader.IsRunnable(call, Tools, out var problem));
        Assert.Contains("city", problem);
    }

    [Fact]
    public void A_value_outside_an_enum_is_caught_here_rather_than_inside_the_tool()
    {
        var call = Call("set_alarm", ("when", "07:00"), ("repeat", "fortnightly"));

        Assert.False(ToolCallReader.IsRunnable(call, Tools, out var problem));
        Assert.Contains("fortnightly", problem);
        Assert.Contains("daily", problem);
    }

    [Fact]
    public void An_extra_argument_the_schema_does_not_mention_is_not_an_error()
    {
        // Small models add a plausible extra field. Refusing an otherwise
        // correct call over one would lose a call the person asked for, and the
        // bridge ignores what it does not read.
        var call = Call("get_weather", ("city", "Durban"), ("units", "celsius"));

        Assert.True(ToolCallReader.IsRunnable(call, Tools, out _));
    }

    [Fact]
    public void With_no_tools_registered_nothing_is_runnable()
    {
        Assert.False(ToolCallReader.IsRunnable(Call("anything"), [], out var problem));
        Assert.False(ToolCallReader.IsRunnable(Call("anything"), null, out _));
        Assert.NotEqual(string.Empty, problem);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<ToolDefinition> Tools =
    [
        new ToolDefinition
        {
            Name = "get_weather",
            Description = "Current weather for a place.",
            Parameters = new Dictionary<string, ToolParameter>
            {
                ["city"] = new() { Type = "string", Description = "Where." },
            },
            RequiredParameters = ["city"],
        },
        new ToolDefinition
        {
            Name = "set_alarm",
            Description = "Set an alarm.",
            Parameters = new Dictionary<string, ToolParameter>
            {
                ["when"]   = new() { Type = "string", Description = "Time." },
                ["repeat"] = new()
                {
                    Type = "string",
                    Description = "How often.",
                    Enum = ["once", "daily", "weekly"],
                },
            },
            RequiredParameters = ["when"],
        },
    ];

    private static ToolInvocation Call(string name, params (string Key, object? Value)[] args)
        => new()
        {
            ToolName = name,
            Arguments = args.ToDictionary(a => a.Key, a => a.Value, StringComparer.OrdinalIgnoreCase),
        };
}
