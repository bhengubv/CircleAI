// Transcribing.cs
//
// A recording on disk becomes a transcript. Asked in ONE place.
//
// IVoiceTranscriber takes PCM bytes, which is exactly right for a microphone and
// useless for a file, so anybody holding a recording had to write the bridge
// themselves. tools/stt-hear did: its own RIFF parser, its own downmix, its own
// resampler, sitting beside WavIo which already had all three. That is the
// repo's own documented anti-pattern - one fact with two owners always ends up
// with two answers - and the second owner here would have drifted the moment
// either side learned a new sample format.
//
// PROGRESS IS PART OF THE FEATURE, NOT A NICETY. Tiny runs at roughly real time
// on a P30, so a forty-minute recording is a forty-minute wait, and a wait with
// no number is indistinguishable from a hang. Every caller that transcribes a
// file needs to say how far in it is, so the reporting lives here rather than
// being reinvented, wrongly, by each screen.

namespace CircleAI.Voice;

/// <summary>Turns audio files into transcripts.</summary>
public static class Transcribing
{
    /// <summary>The only rate whisper accepts.</summary>
    public const int WhisperRate = 16_000;

    /// <summary>
    /// How much audio goes into one decode, in seconds.
    /// </summary>
    /// <remarks>
    /// WHY IT IS CHUNKED HERE AT ALL. whisper.cpp does slide its own 30 s window
    /// over longer input, so a whole file WOULD decode - in one call that
    /// returns nothing until it is finished, holding the entire recording as
    /// floats the whole time. An hour is 230 MB of float samples on a phone with
    /// 3,7 GB, on top of the model, and not one word reaches the screen until
    /// the last one is decoded.
    /// <para>
    /// Five minutes a chunk keeps the peak small, lets text appear as it is
    /// recognised, and makes cancelling cost at most one chunk. It is a multiple
    /// of whisper's own 30 s window so no chunk ends mid-window.
    /// </para>
    /// </remarks>
    public const int ChunkSeconds = 300;

    /// <summary>
    /// Overlap between chunks, in seconds.
    /// </summary>
    /// <remarks>
    /// A CUT LANDS MID-WORD AND BOTH SIDES LOSE IT. Splitting on a round number
    /// of seconds takes no notice of speech, so the word straddling the boundary
    /// is half in each chunk and recognised in neither. A few seconds of overlap
    /// means the word is whole in the second chunk; the cost is that the overlap
    /// is heard twice, which <see cref="Merge"/> then has to resolve.
    /// </remarks>
    public const int OverlapSeconds = 3;

    /// <summary>
    /// Transcribe an audio file.
    /// </summary>
    /// <param name="transcriber">An open transcriber. Not disposed here.</param>
    /// <param name="path">The recording.</param>
    /// <param name="decoder">
    /// Reads anything that is not a WAV. <c>null</c> means WAV only, and a
    /// non-WAV file then comes back as a clear <see cref="NotSupportedException"/>
    /// rather than a parse error about RIFF headers.
    /// </param>
    /// <param name="language">BCP-47 code, or null/"auto" to detect.</param>
    /// <param name="progress">
    /// Fraction done, 0 to 1, reported after each chunk.
    /// </param>
    /// <param name="ct">Cancels between chunks and inside a decode.</param>
    public static async Task<TranscriptionResult> FileAsync(
        IVoiceTranscriber transcriber,
        string path,
        IAudioDecoder? decoder = null,
        string? language = null,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transcriber);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            throw new FileNotFoundException($"No audio file at '{path}'.", path);

        var samples = await ReadAsync(path, decoder, ct).ConfigureAwait(false);
        if (samples.Length == 0)
            return new TranscriptionResult(string.Empty, 0f, "und");

        var chunk   = ChunkSeconds * WhisperRate;
        var overlap = OverlapSeconds * WhisperRate;

        // SHORT FILES DO NOT GET CHUNKED, and that is not an optimisation - it
        // is the difference between one decode and two with a seam to merge.
        if (samples.Length <= chunk)
        {
            var whole = await transcriber
                .TranscribeAsync(WavIo.ToPcm16(samples), ct, language)
                .ConfigureAwait(false);
            progress?.Report(1.0);
            return whole;
        }

        var parts   = new List<TranscriptionResult>();
        var offsets = new List<TimeSpan>();

        for (var start = 0; start < samples.Length; start += chunk - overlap)
        {
            ct.ThrowIfCancellationRequested();

            var length = Math.Min(chunk, samples.Length - start);
            var slice  = samples.AsSpan(start, length).ToArray();

            var part = await transcriber
                .TranscribeAsync(WavIo.ToPcm16(slice), ct, language)
                .ConfigureAwait(false);

            parts.Add(part);

            // THE OFFSET IS THE WHOLE POINT. Every chunk is decoded as if it
            // began at zero, so segment three of chunk two is reported at 00:12
            // when it was said at 05:12. Adding the chunk's own start back is
            // the only thing standing between a subtitle file and one that is
            // right for the first five minutes and nonsense after.
            offsets.Add(TimeSpan.FromSeconds(start / (double)WhisperRate));

            progress?.Report(Math.Min(1.0, (start + length) / (double)samples.Length));

            if (start + length >= samples.Length) break;
        }

        progress?.Report(1.0);
        return Merge(parts, offsets);
    }

    /// <summary>
    /// Stitch per-chunk results into one, shifting each chunk's timings by where
    /// that chunk began.
    /// </summary>
    /// <remarks>
    /// PUBLIC BECAUSE CHUNKING IS NOT ONLY DONE HERE. A screen that transcribes
    /// while recording splits the audio on its own terms - at a pause, at the
    /// end of a take - and then has exactly this problem: each piece is timed
    /// from its own zero. Left private, that caller writes the shift itself and
    /// gets one of the two ways it goes wrong.
    /// </remarks>
    public static TranscriptionResult Merge(
        IReadOnlyList<TranscriptionResult> parts, IReadOnlyList<TimeSpan> offsets)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(offsets);
        if (parts.Count != offsets.Count)
            throw new ArgumentException("One offset per part.", nameof(offsets));
        if (parts.Count == 0) return new TranscriptionResult(string.Empty, 0f, "und");

        var segments = new List<TranscriptSegment>();
        var text     = new System.Text.StringBuilder();
        double confidence = 0;
        var language = "und";

        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var at   = offsets[i];

            if (!string.IsNullOrEmpty(part.LanguageCode) && part.LanguageCode != "und")
                language = part.LanguageCode;
            confidence += part.Confidence;

            foreach (var seg in part.Timed)
            {
                var shifted = new TranscriptSegment(seg.Text, seg.Start + at, seg.End + at);

                // THE OVERLAP IS HEARD TWICE AND MUST BE WRITTEN ONCE. The last
                // few seconds of a chunk are also the first few of the next, so
                // without this the transcript stutters - the same phrase twice,
                // seconds apart, which reads as the speaker repeating himself.
                //
                // Dropped by TIME rather than by text: whisper does not
                // necessarily decode the overlap identically on both sides, so
                // comparing strings would let a near-miss through, and a near
                // miss is the worst of the three outcomes - it looks deliberate.
                if (segments.Count > 0 && shifted.Start < segments[^1].End) continue;

                segments.Add(shifted);
                if (text.Length > 0) text.Append(' ');
                text.Append(shifted.Text);
            }
        }

        // The segments are the authority once they exist: they have had the
        // overlap removed and the plain text has not. A part that reported no
        // segments at all still contributes its text, or an engine without
        // timings would merge to nothing.
        var joined = segments.Count > 0
            ? text.ToString().Trim()
            : string.Join(" ", parts.Select(p => p.Text)
                                    .Where(t => !string.IsNullOrWhiteSpace(t))).Trim();

        return new TranscriptionResult(
            joined, (float)(confidence / parts.Count), language, segments);
    }

    /// <summary>Read a file to mono float samples at whisper's rate.</summary>
    private static async Task<float[]> ReadAsync(
        string path, IAudioDecoder? decoder, CancellationToken ct)
    {
        if (WavIo.IsWave(path))
            return WavIo.ReadMono(path, WhisperRate);

        if (decoder is not null && decoder.CanRead(path))
            return await decoder.DecodeAsync(path, WhisperRate, ct).ConfigureAwait(false);

        throw new NotSupportedException(
            $"'{Path.GetFileName(path)}' is not a WAV file and no decoder on this build reads it. " +
            "Pass an IAudioDecoder that can (Android: MediaExtractor + MediaCodec).");
    }
}
