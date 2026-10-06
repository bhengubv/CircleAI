// ToolCall.cs
//
// Spotting the moment the model asks for a tool instead of answering.
//
// A PERSON ASKED FOR THE WEATHER AND THE PHONE RECITED JSON AT THEM.
//
// The streaming path is the raw generator - it does not execute tools - so when
// the model decides to search, the call arrives as ordinary text and is spoken
// verbatim. The fix is two-pass: notice the call mid-stream, say none of it, and
// re-run the turn through the agentic path that actually executes the tool and
// answers from its result.
//
// ONE HEAD HAD THIS AND THE OTHER DID NOT, which is the whole reason it is here.
// It lived as a private static inside an Activity in a sample that is being
// retired; the head that ships pushed every fragment straight to the speaker.
//
// BCL ONLY. This assembly is loaded by a browser, so the detector is a pure
// function over the text and the turn logic stays with whoever owns the brain.

using System.Text;

namespace CircleAI.Assistant;

/// <summary>Recognising a tool call in a stream that was supposed to be an answer.</summary>
public static class ToolCall
{
    /// <summary>
    /// How much of the stream is worth examining.
    /// </summary>
    /// <remarks>
    /// ONLY THE HEAD MATTERS. A tool call is what the model says INSTEAD of an
    /// answer, so it comes first. Scanning the whole buffer forever would let a
    /// long answer that merely MENTIONS the words trip this - and a suppressed
    /// answer is worse than a spoken tool call, because the person gets silence
    /// and no way to tell why.
    /// </remarks>
    public const int Head = 400;

    /// <summary>Does this look like a tool call rather than an answer?</summary>
    public static bool Looks(StringBuilder accumulated)
    {
        ArgumentNullException.ThrowIfNull(accumulated);

        return Looks(accumulated.Length <= Head
            ? accumulated.ToString()
            : accumulated.ToString(0, Head));
    }

    /// <summary>Does this look like a tool call rather than an answer?</summary>
    /// <remarks>
    /// TWO SHAPES, BECAUSE MODELS EMIT BOTH. Qwen wraps it in a
    /// <c>&lt;tool_call&gt;</c> tag; others emit bare JSON with a name and an
    /// argument object. Requiring BOTH keys for the JSON form is what stops an
    /// answer that happens to contain the word "name" being swallowed.
    /// </remarks>
    public static bool Looks(string? streamed)
    {
        if (string.IsNullOrEmpty(streamed)) return false;

        var head = streamed.Length <= Head ? streamed : streamed[..Head];

        if (head.Contains("<tool_call", StringComparison.OrdinalIgnoreCase)) return true;

        return head.Contains("\"name\"", StringComparison.Ordinal)
            && head.Contains("\"arguments\"", StringComparison.Ordinal);
    }
}
