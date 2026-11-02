// ToolSchema.cs
//
// A TOOL AS THE FUNCTION SCHEMA CHAT TEMPLATES ARE TRAINED ON - ONE OWNER, TWO READERS.
//
// ToolPromptRenderer writes the schemas into a note for an engine that cannot take tools any
// other way; the C# engine hands the same schemas to the model's own chat template, whose tools
// block prints each one with tojson. Two copies of this mapping would drift - a renamed key in
// one would teach the model one shape and show it another - so both read it here.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace CircleAI.Tools;

/// <summary>Tools as the OpenAI-style function schema (<c>{"type": "function", "function": {...}}</c>).</summary>
public static class ToolSchema
{
    /// <summary>
    /// One tool's schema. Built as objects and serialised rather than string concatenated, so a
    /// quote or brace in a description cannot corrupt the JSON; keys in the order the templates print.
    /// </summary>
    public static Dictionary<string, object?> Function(ToolDefinition tool)
    {
        System.ArgumentNullException.ThrowIfNull(tool);
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

        var function = new Dictionary<string, object?>
        {
            ["name"]        = tool.Name,
            ["description"] = tool.Description,
        };

        // NOTHING FOR A TOOL THAT TAKES NOTHING. get_battery_level was emitting
        // "parameters":{"type":"object","properties":{},"required":[]} — sixty
        // characters saying there is nothing to say, prefilled on every turn
        // once the block latches. Omitted entirely; a function with no
        // parameters is called with none.
        if (properties.Count > 0)
        {
            var parameters = new Dictionary<string, object?>
            {
                ["type"]       = "object",
                ["properties"] = properties,
            };

            // Likewise: an empty required list is not information.
            if (tool.RequiredParameters is { Count: > 0 })
                parameters["required"] = tool.RequiredParameters;

            function["parameters"] = parameters;
        }

        return new Dictionary<string, object?>
        {
            ["type"]     = "function",
            ["function"] = function,
        };
    }

    /// <summary>The tools as one JSON array of schemas - what a chat template's <c>tools</c> variable holds.</summary>
    public static string Json(IEnumerable<ToolDefinition?> tools)
    {
        System.ArgumentNullException.ThrowIfNull(tools);
        return JsonSerializer.Serialize(tools.Where(t => t is not null).Select(t => Function(t!)).ToList());
    }
}
