// Trouble.cs
//
// One sentence a person can act on, from an exception.
//
// SCREENS WERE PRINTING `{ex.GetType().Name}: {ex.Message}` INTO A CHAT BUBBLE.
// Four of them - Chat, Seeing, Home and the layout - all with the same line, all
// putting a type name and an internal file path in front of somebody who wanted
// to know whether their phone was broken.
//
// The other head summarised instead, and had done since it was written. That
// summariser was a private static in a sample that is being retired, so the
// behaviour would have gone with it.
//
// WHAT IT IS NOT: a log. The exception still belongs in logcat verbatim, because
// a developer needs the type and the stack. This is what the SCREEN says.
//
// ORDER MATTERS AND IS NOT ALPHABETICAL. The MNN message also contains the word
// "RAM" and a full internal path, so matching "memory" first let that path
// through onto the first screen a person ever sees. Specific before general.

namespace CircleAI.Assistant;

/// <summary>Turning a failure into something a person can do something about.</summary>
public static class Trouble
{
    /// <summary>
    /// How much of an unrecognised message is worth showing.
    /// </summary>
    /// <remarks>
    /// A raw exception message can be a paragraph of native diagnostics. Cutting
    /// it keeps a screen readable; showing none of it would leave somebody with
    /// nothing to quote when they ask for help.
    /// </remarks>
    public const int Longest = 160;

    /// <summary>
    /// An optional sink notified of every exception that passes through <see cref="Say"/>.
    /// A head points this at the self-heal loop, so a caught failure is diagnosed and —
    /// if safe — fixed, without Trouble taking any dependency (it stays browser-loadable,
    /// which is why this is a plain delegate, not an injected service). Set once at
    /// startup; a throwing observer is swallowed.
    /// </summary>
    public static Action<Exception>? Observer { get; set; }

    /// <summary>One sentence a non-developer can act on.</summary>
    /// <remarks>
    /// PHRASED AS A CAUSE PLUS A FIX, in that order, because "not enough free
    /// memory" alone tells somebody they have a problem and not what to do about
    /// it. Every branch that has a known remedy says it.
    /// </remarks>
    public static string Say(Exception? ex)
    {
        if (ex is null) return "Something went wrong.";

        // THE SENTENCE IS FOR THE PERSON. THIS IS FOR WHOEVER HAS TO FIX IT.
        //
        // This method is the funnel where an exception stops being an exception
        // and becomes one line of plain English - and until now that was the
        // ONLY record of it. A P30 showed "Object reference not set to an
        // instance of an object" in a chat bubble and logcat held nothing at
        // all: no type, no stack, no frame. Three separate guesses at the cause
        // were made from reading source and all three were wrong, each costing a
        // full Release build to disprove.
        //
        // Console rather than a logger, and for the reason AIService already
        // records beside CIRCLEAI-PROMPT: hosts wire a logger provider or they
        // do not, and these samples do not - a LogError here would print
        // nowhere and cost a build to discover. On Android this lands under the
        // DOTNET tag; in a browser it lands in the JS console.
        try
        {
            Console.WriteLine("CIRCLEAI-TROUBLE " + ex);
        }
        catch
        {
            // A diagnostic that throws would replace the failure being reported
            // with its own, which is the one thing this must never do.
        }

        // Hand the failure to whoever is healing (a head wires this to the self-heal
        // loop). Fire-and-forget, and it never changes what the screen is told.
        if (Observer is { } sink)
        {
            try { sink(ex); }
            catch { /* the healer's own failure must not replace the one being reported */ }
        }

        var msg = ex.GetBaseException().Message ?? "";

        // FIRST, because the MNN failure message also contains "RAM" and an
        // internal file path - matching "memory" ahead of it let that path
        // through onto the screen.
        if (msg.Contains("MNN model load failed", StringComparison.OrdinalIgnoreCase))
            return "The model file on this phone could not be opened. "
                 + "Re-downloading it usually fixes it.";

        if (msg.Contains("memory", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase))
            return "Not enough free memory. Closing other apps usually fixes it.";

        if (msg.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "The phone blocked a file or network request.";

        if (msg.Contains("No such file", StringComparison.OrdinalIgnoreCase)
         || msg.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "Part of the model is missing - it may still be downloading.";

        if (ex is System.Net.Http.HttpRequestException
               or System.Net.Sockets.SocketException)
            return "The download could not reach the internet.";

        // NOTHING RECOGNISED, SO SAY WHAT HAPPENED RATHER THAN INVENT A CAUSE.
        // A wrong diagnosis is worse than an unexplained one: it sends somebody
        // to clear storage for a problem that was never about storage.
        if (msg.Length == 0) return "Something went wrong.";

        return msg.Length > Longest ? string.Concat(msg.AsSpan(0, Longest), "…") : msg;
    }
}
