// ToolCallReader.cs
//
// Reading a tool call out of what a small model actually emitted.
//
// A 0.6B model asked to produce a JSON function call produces ALMOST one. It
// wraps it in a markdown fence. It puts the arguments in a STRING instead of an
// object. It forgets the closing tag when it hits the token limit. It writes a
// trailing comma. It calls the tool "get_weather()" with the brackets still on.
// Every one of those is a call the person asked for and did not get.
//
// THE OLD BEHAVIOUR WAS SILENCE, WHICH IS THE WORST OF THE THREE OUTCOMES.
// AIService.ParseToolCall returned null on any of them, and the agentic loop
// reads null as "no tool call - we are done". So the model tried to look
// something up, produced a call that was one character off, and the loop
// treated that as the model having decided not to use a tool at all. Nothing
// was logged, nothing was retried, and the answer came back without the
// information. From the outside that is indistinguishable from a model that is
// simply bad at its job.
//
// AND ONE OF THEM WAS WORSE THAN SILENCE. When "arguments" arrived as a JSON
// STRING rather than an object - which Qwen does regularly - the old parser did
// not fail. It took the tool name, found no object to read, and invoked the tool
// with NO ARGUMENTS AT ALL. A search for nothing, a reminder with no time, a
// message with no recipient: a wrong action taken confidently.
//
// THIS IS NOT GRAMMAR-CONSTRAINED DECODING AND DOES NOT PRETEND TO BE.
// Constraining the sampler so an invalid token cannot be emitted needs logit
// access, and the MNN bridge exposes generate-and-stream with no hook into
// sampling - so it is not available from managed code today, at any amount of
// effort short of changing the native bridge. What IS achievable is to accept
// every spelling of a correct intention, and to turn a genuinely wrong call
// into a sentence the model can act on rather than a null.

using System.Text.Json;

namespace CircleAI.Tools;

/// <summary>Extracts and validates tool calls from a model's output.</summary>
public static class ToolCallReader
{
    private const string Open  = "<tool_call>";
    private const string Close = "</tool_call>";

    /// <summary>
    /// Every tool call in a response, in the order the model emitted them.
    /// </summary>
    /// <remarks>
    /// MORE THAN ONE IS NORMAL AND ONLY THE FIRST WAS EVER READ. Asked to do two
    /// things, a model emits two blocks; the loop ran the first, re-prompted
    /// with its result, and the second was gone - so "remind me at six and text
    /// Sipho" quietly became just the reminder. A caller that only wants one can
    /// still take the first, but it can no longer do so by accident.
    /// </remarks>
    public static IReadOnlyList<ToolInvocation> ReadAll(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return [];

        var calls = new List<ToolInvocation>();
        var at = 0;

        while (true)
        {
            var start = response.IndexOf(Open, at, StringComparison.Ordinal);
            if (start < 0) break;

            var from = start + Open.Length;
            var end  = response.IndexOf(Close, from, StringComparison.Ordinal);

            // AN UNCLOSED TAG IS A TRUNCATED REPLY, NOT A MISSING CALL. Hitting
            // the token ceiling mid-call leaves the opening tag and a complete
            // enough JSON body; the close never arrives. Reading to the end of
            // the response recovers the call the model had already decided on.
            var body = end < 0 ? response[from..] : response[from..end];

            var call = ReadOne(body);
            if (call is not null) calls.Add(call);

            if (end < 0) break;
            at = end + Close.Length;
        }

        return calls;
    }

    /// <summary>The first tool call in a response, or <c>null</c>.</summary>
    public static ToolInvocation? Read(string? response)
        => ReadAll(response) is [var first, ..] ? first : null;

    /// <summary>
    /// Whether a call names a registered tool and carries what it requires.
    /// </summary>
    /// <param name="call">The parsed call.</param>
    /// <param name="tools">What is actually registered.</param>
    /// <param name="problem">
    /// A sentence addressed TO THE MODEL when the call cannot be run.
    /// </param>
    /// <remarks>
    /// THE SENTENCE IS THE POINT. An unrunnable call used to be dropped, so the
    /// model never learned it had got the name wrong and would produce the same
    /// wrong name on the next turn. Handed back as a tool-role message, a name
    /// that does not exist becomes a correction with the real names in it, and
    /// the second attempt is usually right.
    /// <para>
    /// Unknown ARGUMENTS are not an error. Small models add a plausible extra
    /// field; refusing the call over one would throw away a call that is
    /// otherwise exactly right, and the bridge ignores what it does not read.
    /// </para>
    /// </remarks>
    public static bool IsRunnable(
        ToolInvocation call, IReadOnlyList<ToolDefinition>? tools, out string problem)
    {
        ArgumentNullException.ThrowIfNull(call);

        if (tools is null || tools.Count == 0)
        {
            problem = "No tools are available.";
            return false;
        }

        var definition = tools.FirstOrDefault(
            t => string.Equals(t.Name, call.ToolName, StringComparison.OrdinalIgnoreCase));

        if (definition is null)
        {
            problem =
                $"There is no tool called \"{call.ToolName}\". " +
                $"The tools are: {string.Join(", ", tools.Select(t => t.Name))}.";
            return false;
        }

        // PRESENT BUT NULL IS MISSING. A model that knows it needs a city and
        // does not have one writes "city": null, and a bare key check would let
        // that through to a tool asked to look up nowhere.
        var missing = definition.RequiredParameters
            .Where(required => Argument(call, required) is null)
            .ToList();

        if (missing.Count > 0)
        {
            problem =
                $"\"{definition.Name}\" needs {string.Join(" and ", missing.Select(m => $"\"{m}\""))}, " +
                "which the call did not give.";
            return false;
        }

        // An enum parameter with a value outside the list is a call that will
        // fail inside the tool, further away, with a worse message.
        foreach (var (name, value) in call.Arguments)
        {
            if (value is null) continue;
            if (!definition.Parameters.TryGetValue(name, out var parameter)) continue;
            if (parameter.Enum is not { Length: > 0 } allowed) continue;

            var text = value.ToString();
            if (allowed.Any(a => string.Equals(a, text, StringComparison.OrdinalIgnoreCase))) continue;

            problem =
                $"\"{name}\" must be one of {string.Join(", ", allowed)}, not \"{text}\".";
            return false;
        }

        problem = string.Empty;
        return true;
    }

    /// <summary>An argument's value, matched without regard to case.</summary>
    /// <remarks>
    /// Not an indexer lookup: the map a caller hands in may have been built with
    /// an ordinal comparer, and a required "city" would then be reported missing
    /// because the model wrote "City".
    /// </remarks>
    private static object? Argument(ToolInvocation call, string name)
    {
        foreach (var (key, value) in call.Arguments)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    // ── Reading one block ───────────────────────────────────────────────

    private static ToolInvocation? ReadOne(string body)
    {
        var json = Unwrap(body);
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException)
        {
            // ONE REPAIR, AND ONLY THE ONE THAT IS UNAMBIGUOUS. A trailing comma
            // before a closing brace or bracket has exactly one correct reading,
            // so fixing it cannot change what the model meant. Anything cleverer
            // - balancing braces, quoting bare keys - starts guessing at intent,
            // and a tool invoked on a guess is worse than one not invoked.
            var repaired = DropTrailingCommas(json);
            if (ReferenceEquals(repaired, json)) return null;
            try { doc = JsonDocument.Parse(repaired); }
            catch (JsonException) { return null; }
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var name = Text(root, "name") ?? Text(root, "tool_name") ?? Text(root, "function");
            if (string.IsNullOrWhiteSpace(name)) return null;

            // "get_weather()" - the model wrote a call rather than a name.
            name = name.Trim().TrimEnd('(', ')').Trim();
            if (name.Length == 0) return null;

            return new ToolInvocation
            {
                ToolName  = name,
                Arguments = Arguments(root),
            };
        }
    }

    /// <summary>The arguments object, wherever the model put it.</summary>
    private static Dictionary<string, object?> Arguments(JsonElement root)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (!root.TryGetProperty("arguments", out var element) &&
            !root.TryGetProperty("parameters", out element) &&
            !root.TryGetProperty("args", out element))
            return args;

        // THE ONE THAT CALLED TOOLS WITH NOTHING. Models emit the arguments as a
        // JSON STRING about as often as as an object - "{\"city\":\"Durban\"}"
        // rather than {"city":"Durban"} - and the old check for an object
        // silently produced an empty argument list. The tool then ran, with no
        // city, and reported whatever a search for nothing returns.
        if (element.ValueKind == JsonValueKind.String)
        {
            var inner = element.GetString();
            if (string.IsNullOrWhiteSpace(inner)) return args;
            try
            {
                using var nested = JsonDocument.Parse(inner);
                Fill(args, nested.RootElement);
            }
            catch (JsonException)
            {
                // Not JSON inside the string either. An empty argument list is
                // then the truthful answer, and IsRunnable turns it into a
                // sentence about what is missing.
            }
            return args;
        }

        Fill(args, element);
        return args;
    }

    /// <summary>Copy an object's properties into the argument map.</summary>
    private static void Fill(Dictionary<string, object?> args, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
            args[property.Name] = Value(property.Value);
    }

    /// <summary>
    /// A JSON value as the nearest CLR type.
    /// </summary>
    /// <remarks>
    /// TYPED, NOT STRINGIFIED. Everything that was not a string used to arrive
    /// as its raw JSON text, so a tool expecting a number got "5" and one
    /// expecting a flag got "true" - and whether that worked depended entirely
    /// on how forgiving the individual tool happened to be about parsing.
    /// </remarks>
    private static object? Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        // BOXED SEPARATELY ON PURPOSE. Written as `? l : element.GetDouble()` the
        // conditional operator unifies long and double to DOUBLE, so a whole
        // number arrives as 250d and a tool doing `is long` never matches - the
        // exact stringly-typed problem this switch exists to remove, wearing a
        // different hat. Caught by ToolCallReaderTests, which asserted 250L and
        // was told "Expected: 250, Actual: 250".
        JsonValueKind.Number => element.TryGetInt64(out var l) ? (object)l : element.GetDouble(),
        _ => element.GetRawText(),
    };

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString()
            : null;

    /// <summary>Strip a markdown fence and anything either side of the JSON.</summary>
    /// <remarks>
    /// Models trained to show their work put ```json around the call, and some
    /// add a line of prose before it. The braces are the call; what surrounds
    /// them is packaging.
    /// </remarks>
    private static string Unwrap(string body)
    {
        var text = body.Trim();

        var first = text.IndexOf('{');
        var last  = text.LastIndexOf('}');

        return first >= 0 && last > first ? text[first..(last + 1)] : text;
    }

    /// <summary>Remove commas that sit immediately before a <c>}</c> or <c>]</c>.</summary>
    private static string DropTrailingCommas(string json)
    {
        if (!json.Contains(',')) return json;

        var sb = new System.Text.StringBuilder(json.Length);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < json.Length; i++)
        {
            var c = json[i];

            if (inString)
            {
                sb.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; sb.Append(c); continue; }

            if (c == ',')
            {
                // Look ahead past whitespace for a closer.
                var j = i + 1;
                while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                if (j < json.Length && (json[j] == '}' || json[j] == ']')) continue;
            }

            sb.Append(c);
        }

        var repaired = sb.ToString();
        return repaired.Length == json.Length ? json : repaired;
    }
}
