// TranscribeFileTests.cs
//
// Transcribing a FILE, and turning what comes back into subtitles.
//
// None of this needs a model. What it pins is the arithmetic and the formatting
// - the two places where a transcript that looks perfect in a debugger produces
// a subtitle file no player will show.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

public class TranscribeFileTests
{
    // ── Chunk offsets ───────────────────────────────────────────────────

    [Fact]
    public void A_single_chunk_keeps_its_timings()
    {
        var merged = Transcribing.Merge(
            [Result("hello", (0, 1))],
            [TimeSpan.Zero]);

        Assert.Equal(TimeSpan.Zero, merged.Timed[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(1), merged.Timed[0].End);
    }

    [Fact]
    public void The_second_chunk_is_shifted_by_where_it_began()
    {
        // THE BUG THIS EXISTS FOR. Each chunk is decoded as if it started at
        // zero, so without the offset every segment after the first five
        // minutes is timed from the wrong origin - a subtitle file that is
        // right up to the seam and nonsense afterwards.
        var merged = Transcribing.Merge(
        [
            Result("first", (0, 10)),
            Result("second", (0, 10)),
        ],
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(297),      // 300 s chunk less 3 s overlap
        ]);

        Assert.Equal(2, merged.Timed.Count);
        Assert.Equal(TimeSpan.FromSeconds(297), merged.Timed[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(307), merged.Timed[1].End);
    }

    [Fact]
    public void A_segment_heard_twice_in_the_overlap_is_written_once()
    {
        // The tail of chunk one and the head of chunk two are the same audio.
        // Both decode it; only one may reach the transcript, or the speaker
        // appears to repeat himself at every seam.
        var merged = Transcribing.Merge(
        [
            Result("the same words", (295, 299)),        // ends at 299 absolute
            Result("the same words", (0, 2)),            // 297 + 0 = 297 absolute
        ],
        [
            TimeSpan.Zero,
            TimeSpan.FromSeconds(297),
        ]);

        Assert.Single(merged.Timed);
        Assert.Equal("the same words", merged.Text);
    }

    [Fact]
    public void Merged_segments_never_go_backwards()
    {
        var merged = Transcribing.Merge(
        [
            Result("a", (0, 5)), Result("b", (0, 5)), Result("c", (0, 5)),
        ],
        [
            TimeSpan.Zero, TimeSpan.FromSeconds(297), TimeSpan.FromSeconds(594),
        ]);

        for (var i = 1; i < merged.Timed.Count; i++)
            Assert.True(merged.Timed[i].Start >= merged.Timed[i - 1].End,
                $"segment {i} starts before segment {i - 1} ends");
    }

    [Fact]
    public void An_engine_without_timings_still_merges_its_text()
    {
        // Not every transcriber reports segments. Merging on segments alone
        // would return an empty transcript for those, which is worse than no
        // timings at all.
        var merged = Transcribing.Merge(
        [
            new TranscriptionResult("part one", 1f, "en"),
            new TranscriptionResult("part two", 1f, "en"),
        ],
        [TimeSpan.Zero, TimeSpan.FromSeconds(297)]);

        Assert.Equal("part one part two", merged.Text);
        Assert.Empty(merged.Timed);
    }

    [Fact]
    public void A_detected_language_survives_a_chunk_that_could_not_tell()
    {
        var merged = Transcribing.Merge(
        [
            new TranscriptionResult("", 0f, "und"),
            new TranscriptionResult("konnichiwa", 1f, "ja"),
        ],
        [TimeSpan.Zero, TimeSpan.FromSeconds(297)]);

        Assert.Equal("ja", merged.LanguageCode);
    }

    [Fact]
    public void Merge_refuses_a_mismatched_offset_count()
    {
        Assert.Throws<ArgumentException>(() =>
            Transcribing.Merge([Result("x", (0, 1))], []));
    }

    // ── Subtitles ───────────────────────────────────────────────────────

    [Fact]
    public void Srt_separates_milliseconds_with_a_comma_and_vtt_with_a_full_stop()
    {
        // THE SILENT ONE. A player handed the wrong separator shows no
        // subtitles and reports nothing; the file opens fine in an editor.
        IReadOnlyList<TranscriptSegment> segs =
            [new("hello", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3))];

        Assert.Contains("00:00:01,500 --> 00:00:03,000", Subtitles.ToSrt(segs));
        Assert.Contains("00:00:01.500 --> 00:00:03.000", Subtitles.ToVtt(segs));
    }

    [Fact]
    public void Vtt_starts_with_its_header_and_srt_does_not()
    {
        IReadOnlyList<TranscriptSegment> segs =
            [new("hello", TimeSpan.Zero, TimeSpan.FromSeconds(1))];

        Assert.StartsWith("WEBVTT", Subtitles.ToVtt(segs), StringComparison.Ordinal);
        Assert.StartsWith("1", Subtitles.ToSrt(segs), StringComparison.Ordinal);
    }

    [Fact]
    public void Srt_numbers_its_cues_from_one_and_vtt_numbers_nothing()
    {
        IReadOnlyList<TranscriptSegment> segs =
        [
            new("one", TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            new("two", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
        ];

        var srt = Subtitles.ToSrt(segs);
        Assert.Contains("1\n00:00:00,000", srt);
        Assert.Contains("2\n00:00:01,000", srt);

        var vtt = Subtitles.ToVtt(segs);
        Assert.DoesNotContain("\n1\n", vtt);
    }

    [Fact]
    public void Hours_are_always_written()
    {
        // Both formats permit HH and some players require it. A file that works
        // everywhere beats one that is three characters shorter.
        IReadOnlyList<TranscriptSegment> segs =
            [new("late", new TimeSpan(0, 1, 2, 3, 4), new TimeSpan(0, 1, 2, 5, 0))];

        Assert.Contains("01:02:03.004 --> 01:02:05.000", Subtitles.ToVtt(segs));
        Assert.Contains("01:02:03,004 --> 01:02:05,000", Subtitles.ToSrt(segs));
    }

    [Fact]
    public void Past_twenty_four_hours_the_hour_count_keeps_going()
    {
        // TimeSpan.Hours rolls over at a day; TotalHours does not. A recording
        // longer than a day would otherwise produce cues that jump back to 00 -
        // and the archive footage this is aimed at is exactly that long.
        IReadOnlyList<TranscriptSegment> segs =
            [new("day two", TimeSpan.FromHours(25), TimeSpan.FromHours(25).Add(TimeSpan.FromSeconds(2)))];

        Assert.Contains("25:00:00,000 --> 25:00:02,000", Subtitles.ToSrt(segs));
    }

    [Fact]
    public void A_cue_that_overlaps_the_next_is_trimmed_rather_than_dropped()
    {
        // Two cues on screen at once is a rendering fault the player cannot
        // fix. Trimming the earlier one keeps every word.
        IReadOnlyList<TranscriptSegment> segs =
        [
            new("first",  TimeSpan.Zero,               TimeSpan.FromSeconds(5)),
            new("second", TimeSpan.FromSeconds(3),     TimeSpan.FromSeconds(7)),
        ];

        var srt = Subtitles.ToSrt(segs);
        Assert.Contains("00:00:00,000 --> 00:00:03,000", srt);
        Assert.Contains("first", srt);
        Assert.Contains("second", srt);
    }

    [Fact]
    public void Empty_and_instantaneous_segments_are_not_written()
    {
        IReadOnlyList<TranscriptSegment> segs =
        [
            new("   ",   TimeSpan.Zero,            TimeSpan.FromSeconds(1)),
            new("blip",  TimeSpan.FromSeconds(1),  TimeSpan.FromSeconds(1.01)),
            new("real",  TimeSpan.FromSeconds(2),  TimeSpan.FromSeconds(4)),
        ];

        var srt = Subtitles.ToSrt(segs);
        Assert.DoesNotContain("blip", srt);
        Assert.Contains("real", srt);
        Assert.StartsWith("1\n", srt, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_timed_produces_an_empty_srt_and_a_bare_vtt()
    {
        Assert.Equal(string.Empty, Subtitles.ToSrt([]));
        Assert.Equal("WEBVTT\n\n", Subtitles.ToVtt([]));
    }

    // ── Reading the file ────────────────────────────────────────────────

    [Fact]
    public void A_wav_is_recognised_by_its_header_not_its_name()
    {
        // Phones write .m4a and call recorders write ADPCM into .wav. The
        // extension is not the answer.
        var dir = Temp();
        try
        {
            var named = Path.Combine(dir, "recording.m4a");
            File.WriteAllBytes(named, Wav(16_000, 1, 16, 1600));
            Assert.True(WavIo.IsWave(named));

            var lying = Path.Combine(dir, "recording.wav");
            File.WriteAllBytes(lying, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
            Assert.False(WavIo.IsWave(lying));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void IsWave_says_no_for_a_file_too_short_to_have_a_header()
    {
        var dir = Temp();
        try
        {
            var tiny = Path.Combine(dir, "t.wav");
            File.WriteAllBytes(tiny, "RIFF"u8.ToArray());
            Assert.False(WavIo.IsWave(tiny));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Reading_at_sixteen_kilohertz_resamples_from_whatever_the_file_is()
    {
        var dir = Temp();
        try
        {
            // One second of 44.1 kHz stereo.
            var path = Path.Combine(dir, "a.wav");
            File.WriteAllBytes(path, Wav(44_100, 2, 16, 44_100));

            var samples = WavIo.ReadMono(path, 16_000);

            // Mono, at 16 kHz, one second of it - within a sample of rounding.
            Assert.InRange(samples.Length, 15_999, 16_001);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Reading_for_transcription_does_not_truncate_at_thirty_seconds()
    {
        // THE CAP BELONGS TO THE CALLER. ReadMono24k stops at 30 s because a
        // voice-cloning reference should; a transcript that stops at 30 s looks
        // like the recogniser gave up.
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "long.wav");
            File.WriteAllBytes(path, Wav(16_000, 1, 16, 16_000 * 40));   // 40 seconds

            Assert.Equal(16_000 * 40, WavIo.ReadMono(path, 16_000).Length);
            Assert.Equal(24_000 * 30, WavIo.ReadMono24k(path).Length);   // still capped
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── The file path end to end ────────────────────────────────────────

    [Fact]
    public async Task A_missing_file_is_a_file_not_found_not_a_parse_error()
    {
        await using var fake = new CountingTranscriber();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Transcribing.FileAsync(fake, Path.Combine(Temp(), "nope.wav")));
    }

    [Fact]
    public async Task A_non_wav_with_no_decoder_says_so_rather_than_blaming_the_file()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "memo.m4a");
            File.WriteAllBytes(path, new byte[64]);

            await using var fake = new CountingTranscriber();
            var ex = await Assert.ThrowsAsync<NotSupportedException>(() =>
                Transcribing.FileAsync(fake, path));

            // The message must be about the BUILD, not about their recording.
            Assert.Contains("decoder", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_short_file_is_one_decode_and_reports_complete()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "short.wav");
            File.WriteAllBytes(path, Wav(16_000, 1, 16, 16_000 * 10));   // 10 seconds

            await using var fake = new CountingTranscriber();
            var seen = new List<double>();
            await Transcribing.FileAsync(fake, path,
                progress: new Progress<double>(seen.Add));

            Assert.Equal(1, fake.Calls);
            await WaitFor(() => seen.Contains(1.0));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_long_file_is_decoded_in_chunks_that_advance()
    {
        var dir = Temp();
        try
        {
            // Twelve minutes: three 5-minute chunks less the overlap.
            var path = Path.Combine(dir, "long.wav");
            File.WriteAllBytes(path, Wav(16_000, 1, 16, 16_000 * 720));

            await using var fake = new CountingTranscriber();
            await Transcribing.FileAsync(fake, path);

            Assert.True(fake.Calls >= 3, $"expected at least 3 chunks, got {fake.Calls}");

            // Every chunk but the last is a full window; none is empty.
            Assert.All(fake.Lengths, n => Assert.True(n > 0));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Cancelling_stops_between_chunks()
    {
        var dir = Temp();
        try
        {
            var path = Path.Combine(dir, "long.wav");
            File.WriteAllBytes(path, Wav(16_000, 1, 16, 16_000 * 1800));  // 30 minutes

            using var cts = new CancellationTokenSource();
            await using var fake = new CountingTranscriber { OnCall = () => cts.Cancel() };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                Transcribing.FileAsync(fake, path, ct: cts.Token));

            Assert.True(fake.Calls <= 2, $"kept decoding after cancel: {fake.Calls} chunks");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>A result with one segment at the given second offsets.</summary>
    private static TranscriptionResult Result(string text, (double Start, double End) at)
        => new(text, 1f, "en",
            [new TranscriptSegment(text, TimeSpan.FromSeconds(at.Start), TimeSpan.FromSeconds(at.End))]);

    private static string Temp()
    {
        var dir = Path.Combine(Path.GetTempPath(), "circleai-tx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Progress is reported through a synchronisation context; give it a beat.</summary>
    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "progress never reached 1.0");
    }

    /// <summary>A minimal PCM WAV of <paramref name="frames"/> frames of silence.</summary>
    private static byte[] Wav(int rate, int channels, int bits, int frames)
    {
        var blockAlign = channels * bits / 8;
        var dataBytes  = frames * blockAlign;

        using var ms = new MemoryStream();
        using var w  = new BinaryWriter(ms);

        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1);                      // PCM
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * blockAlign);
        w.Write((short)blockAlign);
        w.Write((short)bits);
        w.Write("data"u8);
        w.Write(dataBytes);
        w.Write(new byte[dataBytes]);

        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Counts decodes and records how much audio each was handed.</summary>
    private sealed class CountingTranscriber : IVoiceTranscriber
    {
        public int Calls { get; private set; }
        public List<int> Lengths { get; } = [];
        public Action? OnCall { get; init; }

        public Task<TranscriptionResult> TranscribeAsync(
            ReadOnlyMemory<byte> pcmAudio, CancellationToken ct = default, string? language = null)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            Lengths.Add(pcmAudio.Length);
            OnCall?.Invoke();

            return Task.FromResult(new TranscriptionResult(
                $"chunk {Calls}", 1f, "en",
                [new TranscriptSegment($"chunk {Calls}", TimeSpan.Zero, TimeSpan.FromSeconds(1))]));
        }

        public async IAsyncEnumerable<PartialTranscription> StreamTranscribeAsync(
            IAsyncEnumerable<ReadOnlyMemory<byte>> audioChunks,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield return new PartialTranscription(string.Empty, true, 0f);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
