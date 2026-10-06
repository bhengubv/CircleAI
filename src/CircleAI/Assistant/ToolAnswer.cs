// ToolAnswer.cs
//
// Formatting a tool's result INTO the answer, in the engine — because the 0.6B
// will not. Measured on a P30 (circleai-06b-wont-toolcall): the battery tool ran
// and returned 82, and the model still answered "I can't access this
// information". The 0.6B neither emits a <tool_call> nor reads a tool result back.
// So for a plainly tool-needing question (ToolIntent) the result IS the answer,
// formatted here, rather than a prompt for a model that will not read it.
//
// Primitives in, string out — no CircleAI.Tools dependency, so this stays in the
// browser-safe core beside ToolIntent and ArithmeticIntent. The engine (Runtime)
// pulls the value out of the ToolResult and hands it here.

namespace CircleAI.Assistant;

/// <summary>Turns a tool's result into the words a person hears, in the engine.</summary>
public static class ToolAnswer
{
    /// <summary>The battery answer from a reading (0-100), or a plain failure note.</summary>
    public static string Battery(int? percent)
        => percent is >= 0 and <= 100
            ? $"Your battery is at {percent}%."
            : "I could not read the battery.";

    /// <summary>The web answer: the search text as it came back, or a note when empty.</summary>
    /// <remarks>
    /// Relayed, not summarised. The 0.6B will not summarise a result it will not
    /// read, and the search text — a snippet, or "Could not reach the internet" — is
    /// already a sentence a person can use. A capable model would summarise better;
    /// this is the honest best for the model that runs on the phone.
    /// </remarks>
    public static string Web(string? text)
    {
        var t = text?.Trim() ?? "";
        return t.Length == 0 ? "I could not find anything on that." : t;
    }
}
