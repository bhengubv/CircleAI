namespace CircleAI.Voice;

/// <summary>
/// Converts captured audio into text. Implementations are expected to consume
/// PCM 16-bit, 16 kHz mono input as defined by
/// <see cref="AudioFormat.Pcm16Mono16k"/> unless otherwise documented.
/// </summary>
public interface IVoiceTranscriber : IAsyncDisposable
{
    /// <summary>
    /// Transcribe a complete audio buffer (PCM 16-bit, 16 kHz mono).
    /// </summary>
    /// <param name="pcmAudio">
    /// The full audio buffer to transcribe. The buffer must be little-endian
    /// signed 16-bit PCM samples, mono, sampled at 16 kHz.
    /// </param>
    /// <param name="ct">Cancellation token used to abort transcription.</param>
    /// <param name="language">
    /// BCP-47 / ISO 639 code of the language being spoken, when the caller
    /// already knows it; <c>null</c> or <c>"auto"</c> to let the engine detect.
    /// <para>
    /// WORTH PASSING WHENEVER IT IS KNOWN. Detection is a guess made from a few
    /// seconds of audio, and on a small model it is a bad one that leans towards
    /// English: an interpreter screen that had the language printed on its own
    /// microphone button still got Japanese speech written down as English,
    /// because nothing carried the answer down to here.
    /// </para>
    /// </param>
    /// <returns>The recognised text, confidence, and detected language.</returns>
    Task<TranscriptionResult> TranscribeAsync(
        ReadOnlyMemory<byte> pcmAudio, CancellationToken ct = default, string? language = null);

    /// <summary>
    /// Stream audio chunks and receive partial transcriptions as the
    /// underlying engine produces them.
    /// </summary>
    /// <param name="audioChunks">
    /// Asynchronous sequence of PCM 16-bit, 16 kHz mono audio chunks. The
    /// sequence completes when the caller has no more audio to feed.
    /// </param>
    /// <param name="ct">Cancellation token used to abort streaming.</param>
    /// <returns>
    /// An asynchronous sequence of <see cref="PartialTranscription"/>
    /// instances. The final element will have <see cref="PartialTranscription.IsFinal"/>
    /// set to <c>true</c>.
    /// </returns>
    IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(IAsyncEnumerable<ReadOnlyMemory<byte>> audioChunks, CancellationToken ct = default);
}

/// <summary>
/// Final transcription result produced by <see cref="IVoiceTranscriber.TranscribeAsync"/>.
/// </summary>
/// <param name="Text">The recognised text. Empty string if nothing was recognised.</param>
/// <param name="Confidence">Engine-reported confidence in the range [0, 1].</param>
/// <param name="LanguageCode">
/// Detected language as a BCP-47 / ISO 639 code (e.g. "en", "zu", "und" for unknown).
/// </param>
/// <param name="Segments">
/// What was said and WHEN, in order. Empty when the engine cannot report timing.
/// </param>
public sealed record TranscriptionResult(
    string Text,
    float Confidence,
    string LanguageCode,
    IReadOnlyList<TranscriptSegment>? Segments = null)
{
    /// <summary>The timed segments, never null.</summary>
    /// <remarks>
    /// A property rather than a required parameter so every existing caller -
    /// and every other transcriber - keeps compiling and simply reports no
    /// timing, which is the truth for engines that do not produce any.
    /// </remarks>
    public IReadOnlyList<TranscriptSegment> Timed => Segments ?? [];
}

/// <summary>One stretch of speech, and when it was said.</summary>
/// <param name="Text">The words in this stretch.</param>
/// <param name="Start">Offset from the beginning of the audio.</param>
/// <param name="End">Where this stretch stops.</param>
/// <remarks>
/// THE ENGINE ALWAYS KNEW THIS. whisper.cpp reports t0 and t1 for every segment
/// and the P/Invoke for both has been bound in WhisperInterop from the start;
/// the loop that built the transcript read the TEXT of each segment and threw
/// the two timestamps away. So the information was never missing - it was
/// unreachable, which is a different problem with a much smaller fix.
/// <para>
/// SEGMENTS, NOT WORDS, AND THE NAME SAYS SO. whisper.cpp's own unit here is a
/// segment - a phrase-length run, not a word - and calling it a word timing
/// would promise a precision the engine is not reporting. Word-level timing is
/// a further flag (token timestamps) and a further piece of work; this is what
/// the engine hands over today.
/// </para>
/// <para>
/// TimeSpan rather than the raw centiseconds whisper returns: a caller building
/// subtitles should not have to know the unit, and centiseconds are exactly the
/// sort of thing that becomes a factor-of-ten bug in somebody else's code.
/// </para>
/// </remarks>
public sealed record TranscriptSegment(string Text, TimeSpan Start, TimeSpan End)
{
    /// <summary>How long this stretch lasted.</summary>
    public TimeSpan Duration => End - Start;
}

/// <summary>
/// Partial or final transcription produced during streaming recognition.
/// </summary>
/// <param name="Text">The recognised text so far.</param>
/// <param name="IsFinal">
/// <c>true</c> when this is the final transcription for the current utterance;
/// <c>false</c> for in-progress hypotheses that may still change.
/// </param>
/// <param name="Confidence">Engine-reported confidence in the range [0, 1].</param>
public sealed record PartialTranscription(string Text, bool IsFinal, float Confidence);
