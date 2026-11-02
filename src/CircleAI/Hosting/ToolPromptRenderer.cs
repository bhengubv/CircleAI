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

        // EVERY WORD HERE IS PREFILLED, AND PREFILL IS THE WAIT. Measured on a
        // P30 Lite: this block rendered 1 726 characters — about 500 tokens —
        // against a useful question of thirty. At ~34 ms per prompt token that
        // is 13.7 to 17.5 seconds of silence before the model says anything, and
        // once it enters the conversation's system prefix every later turn
        // carries it.
        //
        // With only three tools registered, the SCHEMAS were the smaller half.
        // The prose around them — an explanation of what tools are, a
        // restatement of the format, and the guard below — was the larger, and
        // it cost the same whether one tool was registered or ten.
        //
        // So it is written for a model, not for a reader. Qwen3 is trained on
        // <tools> and <tool_call>; it does not need to be told that functions
        // exist or that XML tags are XML tags. Everything removed here was
        // ceremony. Nothing removed was information.
        var sb = new StringBuilder();
        sb.AppendLine("# Tools");
        sb.AppendLine("<tools>");

        foreach (var tool in tools)
        {
            if (tool is null) continue;
            sb.AppendLine(JsonSerializer.Serialize(ToFunctionSchema(tool), Compact));
        }

        sb.AppendLine("</tools>");
        sb.AppendLine("To call one, emit exactly:");
        sb.AppendLine("<tool_call>{\"name\": <name>, \"arguments\": <args>}</tool_call>");

        // OVERCLAIM GUARD, KEPT — SHORTER. Observed on a real device: asked "can
        // you make a cv for me", a 0.6B answered "I can assist with generating a
        // CV once you provide the details" — a capability it does not have and
        // cannot get. Small models are agreeable by default and the tool list
        // alone does not stop them promising work they have no tool for.
        //
        // Trimmed from four lines to two. The instruction that did the work was
        // "say you cannot"; the rest was emphasis, and emphasis is priced per
        // token on this phone.
        sb.Append("These are your only actions. If asked for anything else — files, documents, "
                + "images, accounts — say you cannot, and never offer to do it later.");

        return sb.ToString();
    }

    /// <summary>
    /// Maps a <see cref="ToolDefinition"/> to the OpenAI-style function schema
    /// Qwen3 was trained on. Built as objects and serialised rather than string
    /// concatenated, so a quote or brace in a description cannot corrupt the JSON.
    /// </summary>
    /// <remarks>
    /// ONE OWNER, TWO READERS (<see cref="ToolSchema"/>). The C# engine hands these same
    /// schemas to the model's own chat template instead of a note like this one, and a
    /// mapping kept twice would teach the model one shape and show it another.
    /// </remarks>
    private static Dictionary<string, object?> ToFunctionSchema(ToolDefinition tool) => ToolSchema.Function(tool);
}
