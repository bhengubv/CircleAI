// DiarisationTests.cs
//
// Telling voices apart, without a model.
//
// The embedder is the part that needs ONNX weights and a real recording. The
// clustering, the slicing and the numbering are arithmetic, and they are where
// a diarised transcript goes wrong in ways nobody notices - a speaker silently
// split in two halfway through, a slice taken off the end of the buffer, a
// "Speaker 2" appearing before "Speaker 1".

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class DiarisationTests
{
    // ── Clustering ──────────────────────────────────────────────────────

    [Fact]
    public void One_voice_throughout_is_one_speaker()
    {
        var labels = Diarisation.Cluster([Voice(1), Voice(1), Voice(1)]);

        Assert.Equal([0, 0, 0], labels);
    }

    [Fact]
    public void Two_voices_taking_turns_are_two_speakers()
    {
        var labels = Diarisation.Cluster([Voice(1), Voice(2), Voice(1), Voice(2)]);

        Assert.Equal(labels[0], labels[2]);
        Assert.Equal(labels[1], labels[3]);
        Assert.NotEqual(labels[0], labels[1]);
    }

    [Fact]
    public void A_stretch_that_could_not_be_characterised_is_unknown_not_a_new_speaker()
    {
        // A null embedding treated as a vector would be a voice unlike any
        // other, and would invent a speaker for every short "mm".
        var labels = Diarisation.Cluster([Voice(1), null, Voice(1)]);

        Assert.Equal(-1, labels[1]);
        Assert.Equal(labels[0], labels[2]);
    }

    [Fact]
    public void Nothing_to_cluster_produces_nothing()
    {
        Assert.Empty(Diarisation.Cluster([]));
    }

    [Fact]
    public void A_speaker_is_not_split_in_two_as_their_segments_accumulate()
    {
        // THE ONE THAT WOULD HAVE LOOKED LIKE A MODEL PROBLEM. A centroid is an
        // average of unit vectors, and an average of unit vectors is SHORTER
        // than one - by exactly how much the samples disagree. Compared with a
        // raw dot product, similarity therefore decays as a speaker accumulates
        // segments, and partway through a long recording the same person stops
        // matching themselves and becomes "Speaker 3".
        var noisy = new List<float[]?>();
        for (var i = 0; i < 40; i++) noisy.Add(Voice(1, jitter: 0.25f, seed: i));

        var labels = Diarisation.Cluster(noisy);

        Assert.All(labels, l => Assert.Equal(labels[0], l));
        Assert.Equal(0, labels[^1]);
    }

    [Fact]
    public void A_higher_threshold_separates_more_readily()
    {
        List<float[]?> similar = [Voice(1), Voice(1, jitter: 0.6f, seed: 7)];

        Assert.Single(Diarisation.Cluster(similar, threshold: 0.1).Distinct());
        Assert.Equal(2, Diarisation.Cluster(similar, threshold: 0.999).Distinct().Count());
    }

    // ── Slicing the audio ───────────────────────────────────────────────

    [Fact]
    public void A_segment_maps_to_the_right_bytes()
    {
        // 10 seconds of 16 kHz PCM-16 = 320 000 bytes.
        var pcm = new byte[16_000 * 2 * 10];
        var seg = new TranscriptSegment("x", TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3));

        var slice = Diarisation.Slice(pcm, seg, 16_000);

        Assert.Equal(16_000 * 2, slice.Length);
    }

    [Fact]
    public void A_segment_running_past_the_end_is_clamped_rather_than_throwing()
    {
        // Whisper pads its last window and a merged transcript's offsets come
        // from arithmetic, so the final segment routinely claims to end after
        // the audio does. Unclamped that is an exception at the very end of a
        // long recording, after every expensive thing has already been done.
        var pcm = new byte[16_000 * 2 * 5];
        var seg = new TranscriptSegment("x", TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(9));

        var slice = Diarisation.Slice(pcm, seg, 16_000);

        Assert.Equal(16_000 * 2, slice.Length);
    }

    [Fact]
    public void A_segment_entirely_past_the_end_is_empty_not_negative()
    {
        var pcm = new byte[16_000 * 2];
        var seg = new TranscriptSegment("x", TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(10));

        Assert.True(Diarisation.Slice(pcm, seg, 16_000).IsEmpty);
    }

    [Fact]
    public void A_slice_always_starts_on_a_whole_sample()
    {
        // An odd byte offset reads the high byte of one sample as the low byte
        // of the next. That is not a quiet voice, it is white noise, and the
        // embedder would characterise the noise.
        var pcm = new byte[16_000 * 2 * 4];

        for (var ms = 1; ms < 200; ms += 7)
        {
            var seg = new TranscriptSegment("x",
                TimeSpan.FromMilliseconds(ms), TimeSpan.FromMilliseconds(ms + 1000));
            Assert.Equal(0, Diarisation.Slice(pcm, seg, 16_000).Length % 2);
        }
    }

    // ── End to end, with a fake embedder ────────────────────────────────

    [Fact]
    public async Task Segments_come_back_labelled_in_order_of_first_appearance()
    {
        var segments = Segments(("hello", 0, 2), ("hi there", 2, 4), ("how are you", 4, 6));
        var embedder = new ScriptedEmbedder([Voice(2), Voice(1), Voice(2)]);

        var named = await Diarisation.OfAsync(segments, Audio(10), 16_000, embedder);

        // The first voice heard is Speaker 1 whatever the clusterer numbered it.
        Assert.Equal("Speaker 1", named[0].Speaker);
        Assert.Equal("Speaker 2", named[1].Speaker);
        Assert.Equal("Speaker 1", named[2].Speaker);
    }

    [Fact]
    public async Task A_segment_the_embedder_could_not_read_keeps_no_speaker()
    {
        // An unlabelled line is honest. A wrongly-labelled one is a quote put
        // in somebody else's mouth, which in a meeting transcript is the kind
        // of error that matters.
        var segments = Segments(("hello", 0, 2), ("mm", 2, 3), ("carry on", 3, 5));
        var embedder = new ScriptedEmbedder([Voice(1), null, Voice(1)]);

        var named = await Diarisation.OfAsync(segments, Audio(10), 16_000, embedder);

        Assert.Equal("Speaker 1", named[0].Speaker);
        Assert.Null(named[1].Speaker);
        Assert.Equal("Speaker 1", named[2].Speaker);
    }

    [Fact]
    public async Task The_text_and_the_timings_are_untouched()
    {
        var segments = Segments(("hello", 0, 2), ("hi", 2, 4));
        var embedder = new ScriptedEmbedder([Voice(1), Voice(2)]);

        var named = await Diarisation.OfAsync(segments, Audio(10), 16_000, embedder);

        Assert.Equal("hello", named[0].Text);
        Assert.Equal(TimeSpan.FromSeconds(2), named[0].End);
        Assert.Equal(TimeSpan.FromSeconds(2), named[1].Start);
    }

    [Fact]
    public async Task Nothing_to_diarise_returns_what_it_was_given()
    {
        var embedder = new ScriptedEmbedder([]);
        Assert.Empty(await Diarisation.OfAsync([], Audio(1), 16_000, embedder));
    }

    [Fact]
    public async Task Cancelling_stops_before_every_segment_is_embedded()
    {
        var segments = Segments(("a", 0, 2), ("b", 2, 4), ("c", 4, 6), ("d", 6, 8));
        using var cts = new CancellationTokenSource();
        var embedder = new ScriptedEmbedder([Voice(1), Voice(1), Voice(1), Voice(1)])
        {
            OnCall = () => cts.Cancel(),
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Diarisation.OfAsync(segments, Audio(10), 16_000, embedder, ct: cts.Token));

        Assert.True(embedder.Calls <= 2, $"kept embedding after cancel: {embedder.Calls}");
    }

    [Fact]
    public async Task The_speaker_count_is_what_a_reader_would_count()
    {
        var segments = Segments(("a", 0, 2), ("b", 2, 4), ("c", 4, 6));
        var embedder = new ScriptedEmbedder([Voice(1), Voice(2), null]);

        var named = await Diarisation.OfAsync(segments, Audio(10), 16_000, embedder);

        Assert.Equal(2, Diarisation.SpeakerCount(named));
    }

    // ── Subtitles carry the speaker ─────────────────────────────────────

    [Fact]
    public void A_labelled_segment_names_its_speaker_in_the_subtitle()
    {
        IReadOnlyList<TranscriptSegment> segs =
        [
            new("hello", TimeSpan.Zero, TimeSpan.FromSeconds(2)) { Speaker = "Speaker 1" },
            new("hi",    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)) { Speaker = "Speaker 2" },
        ];

        var srt = Subtitles.ToSrt(segs);

        Assert.Contains("Speaker 1: hello", srt);
        Assert.Contains("Speaker 2: hi", srt);
    }

    [Fact]
    public void An_unlabelled_segment_is_written_as_it_always_was()
    {
        IReadOnlyList<TranscriptSegment> segs =
            [new("hello", TimeSpan.Zero, TimeSpan.FromSeconds(2))];

        Assert.Contains("\nhello\n", Subtitles.ToSrt(segs));
        Assert.DoesNotContain(":", Subtitles.ToSrt(segs).Split('\n')[2]);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>A unit vector standing in for a voice, optionally jittered.</summary>
    /// <remarks>
    /// Each voice occupies its OWN block of dimensions, so two different voices
    /// are exactly orthogonal and two samples of one voice differ only by the
    /// jitter. Sine waves at different frequencies would have been prettier and
    /// would have made these tests depend on an incidental similarity between
    /// two arbitrary waveforms - which is a property of the helper, not of the
    /// clustering being tested.
    /// </remarks>
    private static float[] Voice(int which, float jitter = 0f, int seed = 0)
    {
        const int Dim = 32;
        const int Block = 8;

        var rng = new Random(seed * 31 + which);
        var v = new float[Dim];

        var from = ((which - 1) * Block) % Dim;
        for (var i = from; i < from + Block; i++)
            v[i] = 1f + (jitter > 0 ? (float)((rng.NextDouble() - 0.5) * 2 * jitter) : 0f);

        var norm = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        for (var i = 0; i < Dim; i++) v[i] /= norm;
        return v;
    }

    private static TranscriptSegment[] Segments(params (string Text, double From, double To)[] spec)
        => [.. spec.Select(s => new TranscriptSegment(
                s.Text, TimeSpan.FromSeconds(s.From), TimeSpan.FromSeconds(s.To)))];

    private static byte[] Audio(int seconds) => new byte[16_000 * 2 * seconds];

    /// <summary>Hands back prepared embeddings in order.</summary>
    private sealed class ScriptedEmbedder(IReadOnlyList<float[]?> script) : ISpeakerEmbedder
    {
        public int Calls { get; private set; }
        public Action? OnCall { get; init; }

        public ValueTask<float[]?> EmbedAsync(
            ReadOnlyMemory<byte> pcm16, int sampleRateHz, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var next = Calls < script.Count ? script[Calls] : null;
            Calls++;
            OnCall?.Invoke();
            return ValueTask.FromResult(next);
        }
    }
}
