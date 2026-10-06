// CircleAIToolBridge.cs
//
// Circle AI's tools. The 0.6B will not EMIT a <tool_call> (measured — see
// circleai-06b-wont-toolcall), so the ENGINE decides and seeds these itself for
// the questions whose intent is plain (ToolIntent):
//
//   get_battery_level  — real device state the model cannot know; the host reads
//                        the actual battery, so a battery question answers from
//                        the truth rather than the constant it used to return.
//   web_search         — the one tool that LEAVES THE PHONE: a live search for
//                        something true right now that training cannot hold.
//   calculate          — arithmetic the model gets wrong; a hand-written evaluator,
//                        so a correct sum is proof it ran (ArithmeticIntent answers
//                        most sums before the model, but the tool remains).
//
// Host-neutral by design: the battery reading arrives as a Func so the shared
// project never references Android APIs (same pattern as nativeLibDir).

using CircleAI.Tools;

namespace CircleAI.Assistant;

/// <summary>
/// Circle AI's <see cref="IToolBridge"/> — the tools the engine runs for a
/// question whose intent is plain, since the 0.6B will not call them itself.
/// </summary>
public sealed class CircleAIToolBridge : IToolBridge
{
    private readonly Func<int?>? _batteryPercent;

    /// <param name="batteryPercent">
    /// Host-supplied battery reading (0-100), or <c>null</c> when the host cannot
    /// provide one. Android passes a real reading; the console passes null.
    /// </param>
    public CircleAIToolBridge(Func<int?>? batteryPercent = null)
        => _batteryPercent = batteryPercent;

    /// <summary>Tracks what actually got invoked, so the UI can show it.</summary>
    public List<string> InvocationLog { get; } = new();

    public IReadOnlyList<ToolDefinition> AvailableTools { get; } = new[]
    {
        new ToolDefinition
        {
            Name        = "get_battery_level",
            Description = "Returns the device's current battery charge as a percentage (0-100).",
            Parameters  = new Dictionary<string, ToolParameter>(),
            RequiredParameters = Array.Empty<string>(),
        },
        new ToolDefinition
        {
            // A CALCULATOR, BECAUSE THE MODEL ON THE PHONE CANNOT ADD. Measured
            // on a P30 2026-09-14: the 0.6B answered "what is 12 times 8" with
            // "6". The header of this file calls add_numbers useless as evidence
            // - true of a GOOD model, which would guess right. On this one a
            // correct sum is proof the tool ran, and the only way the person gets
            // a right answer at all. The evaluator (CircleAI.Assistant.Arithmetic) is
            // a hand-written parser over + - * / and brackets: no code escapes it.
            //
            // The description names the operations in the words the Maths tool-cue
            // serves ("arithmetic", "multiply"), so an arithmetic question offers
            // this tool rather than the search.
            Name        = "calculate",
            Description =
                "Works out a numeric expression exactly - arithmetic the model must not do in its " +
                "head. Use it for any sum: multiply, divide, add or subtract numbers, alone or mixed " +
                "in brackets. Turn the words into a standard expression - \"12 times 8\" becomes " +
                "\"12 * 8\", \"half of 40\" becomes \"40 / 2\" - and pass it as 'expression'.",
            Parameters  = new Dictionary<string, ToolParameter>
            {
                ["expression"] = new()
                {
                    Type        = "string",
                    Description = "A standard arithmetic expression using + - * / and brackets, for example \"12 * 8\" or \"(2 + 3) * 4\".",
                },
            },
            RequiredParameters = new[] { "expression" },
        },
        new ToolDefinition
        {
            // THE DESCRIPTION IS THE POLICY. A model decides whether to call a tool
            // by reading this sentence, so it has to say plainly what the tool is
            // for AND when not to bother — otherwise every "what is two plus two"
            // costs a network round trip inside a turn that is already slow.
            Name        = "web_search",
            Description =
                "Searches the internet and returns short result snippets. Use this for anything " +
                "the model cannot know from training: today's news, weather, prices, sports " +
                "results, recent events, or any question about what is true right now. Do not " +
                "use it for arithmetic, definitions, translation, or anything already known.",
            Parameters  = new Dictionary<string, ToolParameter>
            {
                ["query"] = new()
                {
                    Type        = "string",
                    Description = "What to search for, in a few words, as you would type into a search box.",
                },
            },
            RequiredParameters = new[] { "query" },
        },
    };

    public async Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        InvocationLog.Add(
            $"{invocation.ToolName}({string.Join(", ", invocation.Arguments.Select(a => $"{a.Key}={a.Value}"))})");

        switch (invocation.ToolName)
        {
            case "get_battery_level":
            {
                var pct = _batteryPercent?.Invoke();
                return pct is null
                    ? ToolResult.Failure("get_battery_level", "Battery level is unavailable on this host.")
                    : ToolResult.Ok("get_battery_level", pct.Value);
            }

            case "calculate":
            {
                if (!invocation.Arguments.TryGetValue("expression", out var expr) || expr is null)
                    return ToolResult.Failure("calculate", "Missing required argument 'expression'.");

                var text = expr.ToString()!.Trim().Trim('"');

                // The evaluator rejects anything that is not a well-formed sum, so
                // a crafted string is refused, not run. The failure text is a
                // sentence the model can relay - "that is not a sum I can work
                // out" - rather than a stack trace.
                if (!Arithmetic.TryEvaluate(text, out var value))
                    return ToolResult.Failure("calculate", $"'{text}' is not a sum I can work out.");

                return ToolResult.Ok("calculate", Arithmetic.Format(value));
            }

            case "web_search":
            {
                if (!invocation.Arguments.TryGetValue("query", out var q) || q is null)
                    return ToolResult.Failure("web_search", "Missing required argument 'query'.");

                // THE ONLY TOOL HERE THAT LEAVES THE PHONE. Everything above is
                // device state or an in-memory table; this one sends the query
                // words to a search engine. Nothing else about the turn goes with
                // them — not the conversation, not memory, not identity.
                //
                // Returns Ok even when the search failed, carrying the reason as
                // text. A Failure would have the model apologise for a broken tool;
                // the sentence "Could not reach the internet" is something it can
                // simply relay, which is what the person actually needs to hear.
                var text = await WebSearch.SearchAsync(q.ToString()!.Trim().Trim('"'), ct)
                                         .ConfigureAwait(false);
                return ToolResult.Ok("web_search", text);
            }

            default:
                return ToolResult.Failure(invocation.ToolName, $"No such tool '{invocation.ToolName}'.");
        }
    }
}
