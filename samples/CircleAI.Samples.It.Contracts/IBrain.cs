// IBrain.cs
//
// The thing that answers.

namespace CircleAI.Samples.It;

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
}
