// ToolPromptRenderer.cs
//
// Renders registered ToolDefinitions into the system prompt so the model
// actually KNOWS the tools exist.
//
// Why this exists
// ---------------
// AIService could parse <tool_call> blocks (ParseToolCall) and invoke the
// bridge (InvokeToolAsync), but nothing ever told the model a single tool
// name, parameter or schema. AvailableTools / GetAvailableToolsAsync were
// referenced NOWHERE in CircleAI.Hosting. So a real model could only ever
// emit a valid tool call by guessing a name it was never given.
//
// Circle33ToolCallingTests passed regardless, because its fake generator
// emits a canned <tool_call> block unconditionally — the test exercised the
// parse-and-invoke half and silently assumed the describe half existed.
//
// This renderer is deliberately in the same assembly, and directly above the
// parser it must agree with: a renderer and parser that drift apart reproduce
// exactly the failure this file was written to fix. ToolPromptRendererTests
// pins the round trip — render a definition, parse the documented example
// back, assert the tool name survives.
//
// Format is Qwen3's native tool convention (the catalogued models are the
// Qwen3 and Qwen2.5-Instruct ladders), matching AIService.ParseToolCall:
// a JSON object with "name" and "arguments" inside <tool_call></tool_call>.

using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using CircleAI.Tools;

namespace CircleAI.Hosting;

/// <summary>
/// Turns <see cref="ToolDefinition"/>s into the system-prompt block that tells a
/// model which tools it may call and the exact shape to emit.
/// </summary>
internal static class ToolPromptRenderer
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>
    /// Renders the tools block, or <see cref="string.Empty"/> when there are no
    /// tools — callers append unconditionally, so empty must be safe.
    /// </summary>
    internal static string Render(IReadOnlyList<ToolDefinition>? tools)
    {
        if (tools is null || tools.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("# Tools");
        sb.AppendLine();
        sb.AppendLine("You may call one or more functions to assist with the user query.");
        sb.AppendLine();
        sb.AppendLine("You are provided with function signatures within <tools></tools> XML tags:");
        sb.AppendLine("<tools>");

        foreach (var tool in tools)
        {
            if (tool is null) continue;
            sb.AppendLine(JsonSerializer.Serialize(ToFunctionSchema(tool), Compact));
        }

        sb.AppendLine("</tools>");
        sb.AppendLine();
        sb.AppendLine(
            "For each function call, return a json object with function name and arguments " +
            "within <tool_call></tool_call> XML tags:");
        sb.AppendLine("<tool_call>");
        sb.AppendLine("{\"name\": <function-name>, \"arguments\": <args-json-object>}");
        sb.AppendLine("</tool_call>");

        // OVERCLAIM GUARD. Observed on a real device: asked "can you make a cv
        // for me", a 0.6B answered "I can assist with generating a CV once you
        // provide the details" — a capability it does not have and cannot get.
        // Small models are agreeable by default; the tool list alone does not
        // stop them promising work they have no tool for. Naming the boundary
        // explicitly is the cheapest correction available at this model size.
        sb.AppendLine();
        sb.AppendLine("Those are the ONLY actions you can perform. You have no other tools, and no");
        sb.AppendLine("ability to create files, documents, images, or accounts. If the user asks for");
        sb.AppendLine("something outside that list, say plainly that you cannot do it — do NOT offer");
        sb.Append("to do it \"once they provide details\". Answering from your own knowledge is fine.");

        return sb.ToString();
    }

    /// <summary>
    /// Maps a <see cref="ToolDefinition"/> to the OpenAI-style function schema
    /// Qwen3 was trained on. Built as objects and serialised rather than string
    /// concatenated, so a quote or brace in a description cannot corrupt the JSON.
    /// </summary>
    private static Dictionary<string, object?> ToFunctionSchema(ToolDefinition tool)
    {
        var properties = new Dictionary<string, object?>();

        foreach (var (paramName, param) in tool.Parameters)
        {
            var schema = new Dictionary<string, object?>
            {
                ["type"]        = param.Type,
                ["description"] = param.Description,
            };

            if (param.Enum is { Length: > 0 })
                schema["enum"] = param.Enum;

            properties[paramName] = schema;
        }

        return new Dictionary<string, object?>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>
            {
                ["name"]        = tool.Name,
                ["description"] = tool.Description,
                ["parameters"]  = new Dictionary<string, object?>
                {
                    ["type"]       = "object",
                    ["properties"] = properties,
                    ["required"]   = tool.RequiredParameters,
                },
            },
        };
    }
}
