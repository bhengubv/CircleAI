// Transcript.cs
//
// A recording, written down - what was said, when, and by whom.
//
// ITS OWN TYPE BECAUSE THIS ASSEMBLY CANNOT SEE CircleAI.Voice. Contracts has
// zero ProjectReferences on purpose - it is loaded by a WASM head that must not
// drag ONNX, MNN or whisper into the browser - so TranscriptSegment, which lives
// beside the transcriber that produces it, is not reachable from here. The
// Device implementation maps one to the other, which is a dozen lines and the
// price of the boundary holding.
//
// WHY A HEAD NEEDS THIS AT ALL. Transcribing.FileAsync was written, tested and
// committed with no caller: IConversation had DictateAsync and SessionAsync, and
// both are microphones. There was no way for a screen to hand over a recording
// somebody already had - which is most of what people want a transcriber for.

namespace CircleAI.Assistant;

/// <summary>A recording, written down.</summary>
/// <param name="Text">The whole thing, as one piece of text.</param>
/// <param name="Lines">
/// The same words in timed pieces, in order. Empty when the engine that
/// produced this cannot report timing.
/// </param>
/// <param name="Language">Detected BCP-47 code, or "und" when unknown.</param>
/// <param name="Confidence">
/// What the engine thought of it, 0 to 1.
/// <para>
/// WORTH LOOKING AT NOW, WHICH IT WAS NOT BEFORE. This was a constant zero for
/// the life of the voice library because the whisper processor was never built
/// asking for probabilities, so every caller that might have gated on it would
/// have rejected perfect transcripts.
/// </para>
/// </param>
public sealed record Transcript(
    string Text,
    IReadOnlyList<TranscriptLine> Lines,
    string Language = "und",
    float Confidence = 0f)
{
    /// <summary>An empty transcript, for a file with nothing in it.</summary>
    public static readonly Transcript Nothing = new(string.Empty, []);

    /// <summary>How long the recording ran, as far as the timings say.</summary>
    public TimeSpan Length => Lines.Count == 0 ? TimeSpan.Zero : Lines[^1].End;

    /// <summary>How many distinct speakers were made out, or zero if none were.</summary>
    public int Speakers => Lines.Where(l => l.Speaker is not null)
                                .Select(l => l.Speaker!)
                                .Distinct(StringComparer.Ordinal)
                                .Count();
}

/// <summary>One stretch of a recording.</summary>
/// <param name="Text">The words.</param>
/// <param name="Start">Offset from the beginning of the recording.</param>
/// <param name="End">Where the stretch stops.</param>
/// <param name="Speaker">
/// Who said it - "Speaker 1", "Speaker 2" - or <c>null</c> when that could not
/// be worked out.
/// <para>
/// NULL STAYS NULL. A stretch too short to characterise cannot be attributed,
/// and the tempting repair - give it whoever spoke last - is wrong often enough
/// to matter: a one-word "yeah" mid-turn is usually the other person. An
/// unlabelled line is honest; a wrongly labelled one is a quote in the wrong
/// mouth, which in minutes of a meeting is the error that does damage.
/// </para>
/// </param>
public sealed record TranscriptLine(
    string Text, TimeSpan Start, TimeSpan End, string? Speaker = null)
{
    /// <summary>How long this stretch lasted.</summary>
    public TimeSpan Duration => End - Start;
}

/// <summary>Which subtitle format to write.</summary>
/// <remarks>
/// The two are not interchangeable and the difference is silent. SubRip
/// separates seconds from milliseconds with a COMMA and numbers every cue;
/// WebVTT uses a FULL STOP and requires its own header line. A player handed the
/// wrong one shows no subtitles at all and reports nothing, so this is an
/// explicit choice rather than something guessed from a file extension.
/// </remarks>
public enum SubtitleFormat
{
    /// <summary>SubRip (.srt) — the one most players and phones take.</summary>
    SubRip = 0,

    /// <summary>WebVTT (.vtt) — what a browser's &lt;track&gt; element needs.</summary>
    WebVtt,
}
