// CircleAIToolBridge.cs
//
// IT!'s tools. Deliberately chosen so a correct answer PROVES the tool ran.
//
// A tool like add_numbers would be useless as evidence: a model can do
// arithmetic in its head, so a right answer would not distinguish "called the
// tool" from "ignored the tool and guessed correctly". Both tools here return
// values the model cannot possibly know:
//
//   get_battery_level  — real device state, supplied by the host. On the phone
//                        this is checkable against the actual battery.
//   lookup_price       — an arbitrary in-memory table. R249.99 for SKU-1001 is
//                        unguessable; if it appears in the answer, the tool ran
//                        AND the argument was passed correctly.
//
// Host-neutral by design: the battery reading arrives as a Func so the shared
// project never references Android APIs (same pattern as nativeLibDir).

using CircleAI.Tools;

namespace CircleAI.Assistant;

/// <summary>
/// A minimal <see cref="IToolBridge"/> for the sample — two tools whose results
/// a model cannot fabricate.
/// </summary>
public sealed class CircleAIToolBridge : IToolBridge
{
    private readonly Func<int?>? _batteryPercent;

    /// <summary>Unguessable by construction — that is the point.</summary>
    private static readonly Dictionary<string, decimal> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SKU-1001"] = 249.99m,
        ["SKU-2002"] = 1849.50m,
        ["SKU-3003"] = 79.00m,
    };

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
            Name        = "lookup_price",
            Description = "Looks up the retail price in rand for a product SKU.",
            Parameters  = new Dictionary<string, ToolParameter>
            {
                ["sku"] = new()
                {
                    Type        = "string",
                    Description = "The product SKU, for example SKU-1001",
                },
            },
            RequiredParameters = new[] { "sku" },
        },
        new ToolDefinition
        {
            // A CALCULATOR, BECAUSE THE MODEL ON THE PHONE CANNOT ADD. Measured
            // on a P30 2026-09-14: the 0.6B answered "what is 12 times 8" with
            // "6". The header of this file calls add_numbers useless as evidence
            // - true of a GOOD model, which would guess right. On this one a
            // correct sum is proof the tool ran, and the only way the person gets
            // a right answer at all. The evaluator (CircleAI.Tools.Arithmetic) is
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

            case "lookup_price":
            {
                if (!invocation.Arguments.TryGetValue("sku", out var raw) || raw is null)
                    return ToolResult.Failure("lookup_price", "Missing required argument 'sku'.");

                var sku = raw.ToString()!.Trim().Trim('"');
                return Prices.TryGetValue(sku, out var price)
                    ? ToolResult.Ok("lookup_price", price)
                    : ToolResult.Failure("lookup_price", $"Unknown SKU '{sku}'.");
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
