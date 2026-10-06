// IBrain.cs
//
// The thing that answers.

namespace CircleAI.Assistant;

/// <summary>One line of a conversation.</summary>
/// <param name="Mine">True when the person said it, false when the phone did.</param>
/// <param name="Text">What was said.</param>
public sealed record Utterance(bool Mine, string Text);

/// <summary>Whether this head can answer, and why not when it cannot.</summary>
/// <param name="Ready">True when a turn can actually be run.</param>
/// <param name="Detail">
/// What is missing, in a sentence a person can act on. "Answering needs a 548 MB
/// model" tells somebody what to do; "not ready" does not.
/// </param>
public sealed record BrainState(bool Ready, string Detail);

/// <summary>Runs a turn of conversation on whichever head is hosting the UI.</summary>
/// <remarks>
/// ONE SESSION, NOT ONE PER SCREEN. Loading a model is expensive enough that a
/// screen which builds its own and disposes it afterwards spends a full load and
/// unload on every question - which is what the job-spec screen used to do, on the
/// screen where somebody is waiting. The answer to two copies is one copy, not a
/// copy loaded and thrown away each time.
/// </remarks>
public interface IBrain
{
    /// <summary>Whether a turn can be run right now.</summary>
    Task<BrainState> StateAsync(CancellationToken ct = default);

    /// <summary>
    /// Ask, and stream the answer as it arrives.
    /// </summary>
    /// <param name="prompt">What to ask.</param>
    /// <param name="token">
    /// Called with each fragment as it is produced. STREAMING IS NOT DECORATION:
    /// on a phone the first word can be seconds away and the whole answer much
    /// longer, and a screen that shows nothing until the end is indistinguishable
    /// from one that has hung.
    /// </param>
    Task<string> AskAsync(
        string prompt, Action<string>? token = null, CancellationToken ct = default);

    /// <summary>Answer a question about an image.</summary>
    /// <remarks>
    /// Its own method rather than a flag on <see cref="AskAsync"/>, because
    /// whether the phone can SEE is a separate selection decision from whether it
    /// can answer - a device may have a chat model and no vision model, and that
    /// has to be reportable rather than discovered by catching an exception thrown
    /// deep inside a session.
    /// </remarks>
    Task<string> SeeAsync(
        string question, byte[] image,
        Action<string>? token = null, CancellationToken ct = default);

    /// <summary>
    /// The longest edge a picture should have before it is handed to SeeAsync.
    /// </summary>
    /// <summary>
    /// Answer a question that needs a tool, running the tool and answering from
    /// its result.
    /// </summary>
    /// <remarks>
    /// THE SECOND PASS, AND WITHOUT IT THE PHONE READS JSON ALOUD.
    /// <para>
    /// AskAsync streams from the raw generator, which does NOT execute tools - so
    /// when the model decides to search, the call arrives as ordinary text and is
    /// spoken verbatim. Somebody asked for the weather and heard a line of JSON.
    /// </para>
    /// <para>
    /// Streaming is still right for the great majority of turns, which need no
    /// tool at all: it is what gets sound out early. So the caller streams,
    /// notices a tool call with <see cref="ToolCall.Looks(string)"/>, says none of
    /// it, and re-runs the turn through here. Two passes, but only for the turns
    /// that genuinely reach the world.
    /// </para>
    /// <para>
    /// NOT STREAMED, deliberately: the tool has to run before there is anything
    /// true to say, so there is nothing to emit early.
    /// </para>
    /// <para>
    /// A head with no tools may return the empty string, which tells the caller
    /// to keep whatever the first pass produced.
    /// </para>
    /// </remarks>
    Task<string> AskWithToolsAsync(string prompt, CancellationToken ct = default);

    /// <summary>
    /// The tool pass, told the RAW question (not the composed prompt) so the engine
    /// can run a tool the 0.6B will not ask for itself — a battery or live-web
    /// question, recognised by <see cref="ToolIntent"/>.
    /// </summary>
    /// <remarks>
    /// A DEFAULT that ignores the question and behaves exactly like the pass above,
    /// so a head that does not route deterministically — and every test fake —
    /// needs no change; the phone (DeviceBrain) overrides it.
    /// </remarks>
    Task<string> AskWithToolsAsync(string prompt, string? question, CancellationToken ct = default)
        => AskWithToolsAsync(prompt, ct);

    /// <remarks>
    /// ASKED, NOT ASSUMED, BECAUSE A SECOND COPY OF THIS NUMBER IS THE BUG THIS
    /// REPO KEEPS FINDING. The value belongs to the inference side - MNN logs the
    /// bundle's own <c>image_size</c> when it loads one - and the shared UI cannot
    /// reference that assembly: CircleAI.Assistant has zero project references
    /// on purpose, because a browser loads it.
    /// <para>
    /// So the contract carries the question and each head answers from what it
    /// actually has. A screen that resizes to a number typed into its own markup
    /// is a screen that will still be sending 1024 the day the models want 512.
    /// </para>
    /// <para>
    /// A head with no vision may return 0, which means "do not resize" - there is
    /// nothing to resize FOR.
    /// </para>
    /// </remarks>
    int MaxImageEdge { get; }
}
