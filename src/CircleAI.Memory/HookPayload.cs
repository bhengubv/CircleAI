// HookPayload.cs
//
// Getting the words out of whatever an editor sent.
//
// THIS LIVED IN THE COMMAND, WHERE NO TEST COULD REACH IT. It runs on every
// prompt somebody types, it decides what gets remembered, and its behaviour was
// only ever checked by hand. A claim nothing can test is a claim, not a fact -
// so it moved here.
//
// FORGIVING BY DESIGN, because the shape belongs to somebody else. The payload
// is JSON with a "prompt" field today and that can change. Anything that is not
// that JSON is treated as the words themselves; JSON WITHOUT a prompt is
// treated as nothing at all, because reading the envelope as if it were the
// message would file field names as things somebody said.

using System;
using System.Text.Json;

namespace CircleAI.Memory;

/// <summary>What an editor sends when somebody types something.</summary>
public static class HookPayload
{
    /// <summary>
    /// The words a person typed, out of a hook payload or out of plain text.
    /// </summary>
    /// <param name="raw">Whatever arrived on stdin.</param>
    /// <returns>
    /// What was typed, or empty when the payload carried no words. Empty is an
    /// answer: it means there is nothing to remember, not that something failed.
    /// </returns>
    public static string PromptFrom(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var trimmed = raw.TrimStart();

        // Not an envelope. Take it at face value - a person piping their own
        // notes in is the other half of what this reads.
        if (!trimmed.StartsWith('{')) return raw;

        try
        {
            using var json = JsonDocument.Parse(trimmed);
            if (json.RootElement.ValueKind != JsonValueKind.Object) return raw;

            foreach (var property in json.RootElement.EnumerateObject())
                if (property.NameEquals("prompt"))
                    return property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? ""
                        : "";

            // An envelope with no message in it.
            return "";
        }
        catch (JsonException)
        {
            // Something that starts with a brace and is not JSON is far more
            // likely to be prose than a broken payload.
            return raw;
        }
    }
}
