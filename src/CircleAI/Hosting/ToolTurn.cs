// ToolTurn.cs
//
// ONE TURN THAT MAY RUN A TOOL, DECIDED IN ONE PLACE FOR EVERY HOST OF A BRAIN.
//
// The 0.6B neither emits a call for a battery or live-web question nor reads the result
// back - measured on a P30, get_battery_level ran and returned 82 and the model still
// answered "I can't access this information" (circleai-06b-wont-toolcall). So for those the
// engine runs the tool itself and words the answer (ToolAnswer); any other question goes to
// the agentic loop, where a model that does call tools - the C# engine holds its call to the
// template's form (ToolCallGrammar) - has them run and answers from the results.
//
// THE SESSION DID THIS IN-PROCESS AND NOTHING ELSE COULD. The service's inference process
// answered every question its queue held as a plain one, tools allowed or not (OPEN-GAPS
// A16), so an app's tool turn through the link came back as the call it had already seen.
// A second copy of this decision is the defect this repo keeps finding - two owners of one
// behaviour drift - so the session and the inference process both come here.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Inference;
using CircleAI.Tools;

namespace CircleAI.Hosting;

/// <summary>A turn that may run a tool: the engine's own for a plain intent, else the agentic loop.</summary>
public static class ToolTurn
{
    /// <summary>The answer to <paramref name="prompt"/>, with tools.</summary>
    /// <param name="brain">The brain, started.</param>
    /// <param name="prompt">The turn as composed for the model.</param>
    /// <param name="question">The question alone, as the person asked it - what the intent is read from.</param>
    /// <param name="options">The turn's own options for the agentic loop; null takes the brain's.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<string> RunAsync(
        IAIService brain, string prompt, string? question, GenerationOptions? options = null, CancellationToken ct = default)
    {
        var need = ToolIntent.Classify(question);
        if (InvocationFor(need, question) is { } invocation)
        {
            var result = await brain.InvokeToolAsync(invocation, ct).ConfigureAwait(false);
            return AnswerFrom(need, result);
        }

        // A bigger model that DOES emit its own calls: the agentic loop, unchanged.
        return await brain.AgenticChatAsync(prompt, options, ct).ConfigureAwait(false);
    }

    /// <summary>The concrete tool for a plain intent, or null to leave it to the model.</summary>
    internal static ToolInvocation? InvocationFor(ToolNeed need, string? question) => need switch
    {
        ToolNeed.Battery => new ToolInvocation
        {
            ToolName = "get_battery_level",
            Arguments = new Dictionary<string, object?>(),
        },
        ToolNeed.Web => new ToolInvocation
        {
            ToolName = "web_search",
            Arguments = new Dictionary<string, object?> { ["query"] = question! },
        },
        _ => null,
    };

    /// <summary>The engine's own words for a tool result - the 0.6B will not phrase it.</summary>
    private static string AnswerFrom(ToolNeed need, ToolResult result) => need switch
    {
        ToolNeed.Battery => ToolAnswer.Battery(AsPercent(result)),
        ToolNeed.Web => ToolAnswer.Web(result.Success ? result.Result?.ToString() : result.Error),
        _ => result.Result?.ToString() ?? string.Empty,
    };

    private static int? AsPercent(ToolResult r)
        => r.Success && r.Result is not null && int.TryParse(r.Result.ToString(), out var n) ? n : null;
}
