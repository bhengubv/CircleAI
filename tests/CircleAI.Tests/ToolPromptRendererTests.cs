// ToolPromptRendererTests.cs
//
// Guards the half of tool calling that did not exist until 2026-07-20: telling
// the model which tools it may call.
//
// The bug these tests exist to prevent recurring: AIService could PARSE
// <tool_call> and INVOKE the bridge, but AvailableTools was referenced nowhere
// in CircleAI.Hosting, so a real model was never given a tool name to emit.
// Circle33ToolCallingTests stayed green throughout because its fake generator
// emits a canned <tool_call> block regardless of the prompt — it exercised the
// back half and assumed the front half.
//
// So the load-bearing test here is the ROUND TRIP: what the renderer tells the
// model to emit must be what ParseToolCall can read back. A renderer and parser
// that drift apart reproduce the original failure exactly.

using System.Collections.Generic;
using CircleAI.Hosting;
using CircleAI.Tools;
using Xunit;

namespace CircleAI.Tests;

public sealed class ToolPromptRendererTests
{
    private static ToolDefinition GetTime() => new()
    {
        Name        = "get_time",
        Description = "Returns the current local time for a city.",
        Parameters  = new Dictionary<string, ToolParameter>
        {
            ["city"] = new() { Type = "string", Description = "City name, e.g. Durban" },
        },
        RequiredParameters = new[] { "city" },
    };

    [Fact]
    public void Render_NoTools_IsEmpty()
    {
        // Callers append unconditionally, so empty must be safe.
        Assert.Equal(string.Empty, ToolPromptRenderer.Render(null));
        Assert.Equal(string.Empty, ToolPromptRenderer.Render(new List<ToolDefinition>()));
    }

    [Fact]
    public void Render_NamesTheToolAndItsParameters()
    {
        var block = ToolPromptRenderer.Render(new[] { GetTime() });

        Assert.Contains("get_time", block);
        Assert.Contains("city", block);
        Assert.Contains("<tools>", block);
        Assert.Contains("<tool_call>", block);
    }

    [Fact]
    public void RenderedInstruction_IsParseableByParseToolCall()
    {
        // THE drift guard. The renderer instructs the model to emit
        //   <tool_call>{"name": ..., "arguments": {...}}</tool_call>
        // so a model that obeys must produce something ParseToolCall reads.
        var block = ToolPromptRenderer.Render(new[] { GetTime() });
        Assert.Contains("{\"name\": <function-name>, \"arguments\": <args-json-object>}", block);

        var asAModelWouldEmit =
            "<tool_call>\n{\"name\": \"get_time\", \"arguments\": {\"city\": \"Durban\"}}\n</tool_call>";

        var invocation = AIService.ParseToolCall(asAModelWouldEmit);

        Assert.NotNull(invocation);
        Assert.Equal("get_time", invocation!.ToolName);
        Assert.Equal("Durban", invocation.Arguments["city"]);
    }

    [Fact]
    public void Render_DescriptionWithQuotes_StaysValidJson()
    {
        // Built via JsonSerializer rather than string concatenation precisely so
        // a quote in a description cannot corrupt the schema.
        var awkward = new ToolDefinition
        {
            Name        = "echo",
            Description = "Repeats the \"input\" back, {verbatim}.",
            Parameters  = new Dictionary<string, ToolParameter>
            {
                ["text"] = new() { Type = "string", Description = "Say \"hello\"" },
            },
            RequiredParameters = new[] { "text" },
        };

        var block = ToolPromptRenderer.Render(new[] { awkward });

        // The serialised function line must survive a JSON round trip.
        // LastIndexOf, not IndexOf: the instruction sentence above the block
        // ("...function signatures within <tools></tools> XML tags:") contains
        // both tags literally, so IndexOf matches the prose and extracts an
        // empty span. The real payload is always the LAST pair.
        var open  = block.LastIndexOf("<tools>", System.StringComparison.Ordinal) + "<tools>".Length;
        var close = block.LastIndexOf("</tools>", System.StringComparison.Ordinal);
        var json  = block[open..close].Trim();

        var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(
            "echo",
            parsed.RootElement.GetProperty("function").GetProperty("name").GetString());
    }
}
