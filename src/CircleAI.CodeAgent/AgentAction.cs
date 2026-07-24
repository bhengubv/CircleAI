// AgentAction.cs
//
// The wire protocol between the brain and the loop. The model replies with a
// SINGLE JSON action object per turn; this parses the first balanced {...} out
// of the reply (models wrap JSON in prose / fences often enough that a strict
// parse is brittle) and maps it to a strongly-typed action. Anything it cannot
// understand becomes Unknown with the raw text preserved, so the loop can feed
// the confusion back rather than crash.

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace CircleAI.CodeAgent;

/// <summary>The kind of step the agent asked to take this turn.</summary>
public enum AgentActionKind
{
    /// <summary>Reply could not be parsed into a known action.</summary>
    Unknown,
    /// <summary>Read a file's current text.</summary>
    ReadFile,
    /// <summary>Apply a character-range edit to a file.</summary>
    EditFile,
    /// <summary>Run an allow-listed command and observe its output.</summary>
    RunCommand,
    /// <summary>Search the codebase for a query.</summary>
    SearchCode,
    /// <summary>Declare the task complete.</summary>
    Finish,
}

/// <summary>
/// A parsed agent action. Only the fields relevant to <see cref="Kind"/> are
/// populated; <see cref="Raw"/> keeps the source JSON (or the whole reply, when
/// unparsed) for diagnostics and re-prompting.
/// </summary>
public sealed record AgentAction(
    AgentActionKind        Kind,
    string?                Path        = null,
    int                    RangeStart  = 0,
    int                    RangeEnd    = 0,
    string?                Replacement = null,
    string?                Executable  = null,
    IReadOnlyList<string>? Args        = null,
    string?                Query       = null,
    int                    TopK        = 10,
    string?                Summary     = null,
    string?                Raw         = null);

/// <summary>Parses a model reply into an <see cref="AgentAction"/>.</summary>
public static class AgentActionParser
{
    /// <summary>
    /// Parse the first JSON action object in <paramref name="modelText"/>.
    /// Never throws — an unrecognised or malformed reply yields
    /// <see cref="AgentActionKind.Unknown"/> with the raw text attached.
    /// </summary>
    public static AgentAction Parse(string? modelText)
    {
        if (string.IsNullOrWhiteSpace(modelText))
            return new AgentAction(AgentActionKind.Unknown, Raw: modelText);

        var json = ExtractFirstJsonObject(modelText);
        if (json is null)
            return new AgentAction(AgentActionKind.Unknown, Raw: modelText);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new AgentAction(AgentActionKind.Unknown, Raw: modelText);

            var action = GetString(root, "action")?.Trim().ToLowerInvariant();
            return action switch
            {
                "read_file" => new AgentAction(
                    AgentActionKind.ReadFile,
                    Path: GetString(root, "path"),
                    Raw:  json),

                "edit_file" => new AgentAction(
                    AgentActionKind.EditFile,
                    Path:        GetString(root, "path"),
                    RangeStart:  GetInt(root, "range_start"),
                    RangeEnd:    GetInt(root, "range_end"),
                    Replacement: GetString(root, "replacement") ?? "",
                    Raw:         json),

                "run_command" => new AgentAction(
                    AgentActionKind.RunCommand,
                    Executable: GetString(root, "executable"),
                    Args:       GetStringArray(root, "args"),
                    Path:       GetString(root, "cwd"),
                    Raw:        json),

                "search_code" => new AgentAction(
                    AgentActionKind.SearchCode,
                    Query: GetString(root, "query"),
                    TopK:  GetInt(root, "top_k", 10),
                    Raw:   json),

                "finish" => new AgentAction(
                    AgentActionKind.Finish,
                    Summary: GetString(root, "summary") ?? "",
                    Raw:     json),

                _ => new AgentAction(AgentActionKind.Unknown, Raw: json),
            };
        }
        catch (JsonException)
        {
            return new AgentAction(AgentActionKind.Unknown, Raw: modelText);
        }
    }

    // Extract the first balanced {...} run, respecting string literals and
    // escapes so a brace inside a JSON string value never ends the object early.
    private static string? ExtractFirstJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var depth    = 0;
        var inString = false;
        var escape   = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (escape) { escape = false; continue; }
            if (inString)
            {
                if (c == '\\') escape = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return text.Substring(start, i - start + 1);
                    break;
            }
        }
        return null;
    }

    private static string? GetString(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int GetInt(JsonElement o, string name, int fallback = 0) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n
            : fallback;

    private static IReadOnlyList<string> GetStringArray(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String) continue;
            var s = e.GetString();
            if (s is not null) list.Add(s);
        }
        return list;
    }
}
