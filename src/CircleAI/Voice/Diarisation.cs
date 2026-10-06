// Diarisation.cs
//
// Who spoke, and when. Not who they ARE.
//
// THE HALF THAT WAS MISSING LANDED TODAY. OnnxSpeakerIdentity has been in this
// library for a long time and it answers a different question: it compares a
// stretch of audio against people who have ENROLLED, and returns a name or
// nothing. That is identification, and it is useless for a meeting recording
// where nobody has enrolled and the point is simply to tell the voices apart.
//
// Diarisation needs two things: a way to cut the audio into stretches that each
// contain one person talking, and a way to say whether two stretches are the
// same voice. The second has existed all along inside OnnxSpeakerIdentity. The
// first is exactly what TranscriptSegment's Start and End now give - whisper was
// always reporting them and the library was throwing them away, so a transcript
// could say WHAT was said and never WHEN, and without when there is nothing to
// cut on.
//
// AN UNLABELLED SEGMENT IS HONEST; A WRONGLY LABELLED ONE IS A QUOTE PUT IN
// SOMEBODY ELSE'S MOUTH. Short segments produce no usable embedding - the
// embedder needs about a second - and the tempting repair is to give them the
// previous speaker. That is wrong often enough to matter: a one-word "yeah" in
// the middle of somebody's turn is usually the OTHER person agreeing. So a
// segment that cannot be attributed is left with no speaker, and a reader can
// see which lines are certain.

namespace CircleAI.Voice;

/// <summary>Turns a stretch of speech into a vector that identifies the voice.</summary>
/// <remarks>
/// Separate from <see cref="ISpeakerIdentity"/> on purpose. Identity answers
/// "which enrolled person is this"; this answers "what does this voice look
/// like", which is the only thing diarisation needs and the only thing that can
/// be asked of a recording of strangers.
/// </remarks>
public interface ISpeakerEmbedder
{
    /// <summary>
    /// A voice vector for PCM-16 mono audio, or <c>null</c> when the stretch is
    /// too short or too quiet to characterise.
    /// </summary>
    /// <remarks>
    /// NULL IS A REAL ANSWER AND MUST NOT BE A ZERO VECTOR. A zero vector has a
    /// cosine similarity of zero with everything, which reads as "a voice unlike
    /// any other" and reliably invents an extra speaker.
    /// </remarks>
    ValueTask<float[]?> EmbedAsync(
        ReadOnlyMemory<byte> pcm16, int sampleRateHz, CancellationToken ct = default);
}

/// <summary>Groups timed segments by who was speaking.</summary>
public static class Diarisation
{
    /// <summary>
    /// Cosine similarity at or above which two stretches are the same voice.
    /// </summary>
    /// <remarks>
    /// The same 0,55 <c>SpeakerIdentityConfig.MatchThreshold</c> uses, and
    /// deliberately so: the two are asking the same question of the same
    /// embeddings, and two thresholds for one judgement is how the enrolment
    /// screen and the transcript end up disagreeing about whether two clips are
    /// the same person.
    /// </remarks>
    public const double SameVoice = 0.55;

    /// <summary>
    /// Assign each embedding a speaker number, in order of first appearance.
    /// </summary>
    /// <param name="embeddings">
    /// One per stretch, in time order. <c>null</c> for a stretch that could not
    /// be characterised; it gets <c>-1</c>.
    /// </param>
    /// <param name="threshold">Similarity for "same voice".</param>
    /// <returns>A speaker index per embedding, counting from 0; -1 for unknown.</returns>
    /// <remarks>
    /// GREEDY, IN TIME ORDER, AGAINST A RUNNING CENTROID. Each stretch is
    /// compared with the average of every stretch already assigned to each
    /// speaker, joins the best if it clears the threshold, and starts a new
    /// speaker if it does not.
    /// <para>
    /// The alternative, agglomerative clustering, is more accurate and is cubic
    /// in the number of segments: an hour of conversation is several hundred of
    /// them on a phone that is also holding a model. This is linear in segments
    /// and in speakers, and it matches the shape of the problem - people take
    /// turns, so time order carries real information that a symmetric distance
    /// matrix throws away.
    /// </para>
    /// <para>
    /// WHAT IT COSTS, HONESTLY: it is order-dependent. A first stretch that is
    /// unusually noisy sets a poor centroid for that speaker, and the error does
    /// not get revisited. Averaging in every later stretch pulls the centroid
    /// back, so the damage is early and shrinking rather than permanent.
    /// </para>
    /// </remarks>
    public static int[] Cluster(IReadOnlyList<float[]?> embeddings, double threshold = SameVoice)
    {
        ArgumentNullException.ThrowIfNull(embeddings);

        var labels    = new int[embeddings.Count];
        var centroids = new List<float[]>();
        var counts    = new List<int>();

        for (var i = 0; i < embeddings.Count; i++)
        {
            var e = embeddings[i];
            if (e is null || e.Length == 0) { labels[i] = -1; continue; }

            var best = -1;
            var bestSimilarity = double.MinValue;

            for (var c = 0; c < centroids.Count; c++)
            {
                if (centroids[c].Length != e.Length) continue;
                var similarity = Similarity(e, centroids[c]);
                if (similarity > bestSimilarity) { bestSimilarity = similarity; best = c; }
            }

            if (best >= 0 && bestSimilarity >= threshold)
            {
                labels[i] = best;
                Absorb(centroids[best], e, counts[best]);
                counts[best]++;
            }
            else
            {
                labels[i] = centroids.Count;
                centroids.Add((float[])e.Clone());
                counts.Add(1);
            }
        }

        return labels;
    }

    /// <summary>
    /// Work out who was speaking in each segment and return them labelled.
    /// </summary>
    /// <param name="segments">Timed segments, in order, from a transcription.</param>
    /// <param name="pcm16">The same audio the transcript came from, PCM-16 mono.</param>
    /// <param name="sampleRateHz">Its sample rate.</param>
    /// <param name="embedder">What turns a stretch into a voice vector.</param>
    /// <param name="threshold">Similarity for "same voice".</param>
    /// <param name="ct">Cancels between segments.</param>
    /// <returns>
    /// The same segments, in the same order, with <see cref="TranscriptSegment.Speaker"/>
    /// set where it could be determined and left null where it could not.
    /// </returns>
    public static async Task<IReadOnlyList<TranscriptSegment>> OfAsync(
        IReadOnlyList<TranscriptSegment> segments,
        ReadOnlyMemory<byte> pcm16,
        int sampleRateHz,
        ISpeakerEmbedder embedder,
        double threshold = SameVoice,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRateHz);

        if (segments.Count == 0) return segments;

        var embeddings = new List<float[]?>(segments.Count);

        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();
            var slice = Slice(pcm16, segment, sampleRateHz);
            embeddings.Add(slice.IsEmpty
                ? null
                : await embedder.EmbedAsync(slice, sampleRateHz, ct).ConfigureAwait(false));
        }

        var labels = Cluster(embeddings, threshold);

        // NUMBERED BY FIRST APPEARANCE, NOT BY CLUSTER INDEX. They happen to
        // agree today because Cluster walks in time order, but a reader seeing
        // "Speaker 2" before "Speaker 1" would rightly distrust the whole file,
        // and that should not depend on an implementation detail of the
        // clustering staying the way it is.
        var numbers = new Dictionary<int, int>();
        var named   = new List<TranscriptSegment>(segments.Count);

        for (var i = 0; i < segments.Count; i++)
        {
            if (labels[i] < 0) { named.Add(segments[i]); continue; }

            if (!numbers.TryGetValue(labels[i], out var number))
            {
                number = numbers.Count + 1;
                numbers[labels[i]] = number;
            }

            named.Add(segments[i] with { Speaker = $"Speaker {number}" });
        }

        return named;
    }

    /// <summary>How many distinct speakers a labelled transcript holds.</summary>
    public static int SpeakerCount(IReadOnlyList<TranscriptSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return segments.Where(s => s.Speaker is not null)
                       .Select(s => s.Speaker!)
                       .Distinct(StringComparer.Ordinal)
                       .Count();
    }

    // ── Internals ───────────────────────────────────────────────────────

    /// <summary>The bytes of one segment, clamped to the audio that exists.</summary>
    /// <remarks>
    /// CLAMPED, NOT TRUSTED. A segment's End can run past the end of the buffer -
    /// whisper pads its last window, and a merged transcript's offsets come from
    /// arithmetic rather than from the file - and an unclamped slice is an
    /// ArgumentOutOfRangeException at the very end of a long recording, after
    /// all the work is done.
    /// <para>
    /// PUBLIC BECAUSE CUTTING A SEGMENT OUT OF THE AUDIO IS A THING CALLERS
    /// WANT. Tapping a line of a transcript to hear it said is the obvious one,
    /// and so is re-running a single stretch through a better model. Left
    /// private, each of those callers writes this arithmetic again and gets the
    /// odd-offset or the past-the-end case wrong.
    /// </para>
    /// </remarks>
    public static ReadOnlyMemory<byte> Slice(
        ReadOnlyMemory<byte> pcm16, TranscriptSegment segment, int sampleRateHz)
    {
        const int BytesPerSample = 2;

        var from = (long)(segment.Start.TotalSeconds * sampleRateHz) * BytesPerSample;
        var to   = (long)(segment.End.TotalSeconds   * sampleRateHz) * BytesPerSample;

        // Whole samples only: an odd offset reads the high byte of one sample as
        // the low byte of another, which is white noise rather than a voice.
        from -= from % BytesPerSample;
        to   -= to   % BytesPerSample;

        from = Math.Clamp(from, 0, pcm16.Length);
        to   = Math.Clamp(to,   from, pcm16.Length);

        return to <= from ? ReadOnlyMemory<byte>.Empty : pcm16[(int)from..(int)to];
    }

    /// <summary>Cosine similarity, which for L2-normalised vectors is the dot product.</summary>
    internal static double Similarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return -1;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na  += a[i] * a[i];
            nb  += b[i] * b[i];
        }

        // NORMALISED HERE TOO, THOUGH THE EMBEDDER ALREADY DOES IT. A centroid
        // is an AVERAGE of unit vectors and an average of unit vectors is not a
        // unit vector - it is shorter, by exactly how much the voices disagree.
        // Taking the raw dot product against one would therefore drift below the
        // threshold as a speaker accumulates segments, and the same person would
        // be split into two partway through the recording.
        if (na <= 0 || nb <= 0) return -1;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    /// <summary>Fold a new member into a running mean, in place.</summary>
    private static void Absorb(float[] centroid, float[] member, int count)
    {
        for (var i = 0; i < centroid.Length && i < member.Length; i++)
            centroid[i] = (centroid[i] * count + member[i]) / (count + 1);
    }
}
