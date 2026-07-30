using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Companion.Tests;

/// <summary>
/// Covers the splitter that gives punctuation-free voices their phrase breaks.
/// </summary>
/// <remarks>
/// The bug these guard against is invisible by construction: a mis-split makes no
/// noise, it just puts a pause in the wrong place or none at all, and every
/// acoustic measure still reads healthy. That failure mode already cost this
/// codebase eleven languages' worth of missing syllables, so the boundaries are
/// asserted here rather than left to a listener.
/// </remarks>
public class SentenceSplitterTests
{
    [Fact]
    public void Splits_a_passage_at_sentence_boundaries()
    {
        var segments = SentenceSplitter.Split(
            "Sawubona nonke. Uyangizwa kahle? Ngiyabonga kakhulu.");

        Assert.Equal(3, segments.Count);
        Assert.Equal("Sawubona nonke.", segments[0].Text);
        Assert.Equal("Uyangizwa kahle?", segments[1].Text);
        Assert.Equal("Ngiyabonga kakhulu.", segments[2].Text);
    }

    [Fact]
    public void Keeps_terminating_punctuation_in_the_text()
    {
        // The SA-11 voice's vocabulary contains '?' and can render a real question
        // rise. Stripping it to tidy up diagnostics would silently downgrade every
        // question in eleven languages to a statement.
        var segments = SentenceSplitter.Split("Uyangizwa kahle? Yebo.");

        Assert.EndsWith("?", segments[0].Text);
        Assert.EndsWith(".", segments[1].Text);
    }

    [Fact]
    public void Does_not_split_inside_a_decimal_or_a_domain()
    {
        var segments = SentenceSplitter.Split("The price is 3.5 rand at thegeek.co.za today.");

        Assert.Single(segments);
    }

    [Fact]
    public void Gives_the_last_segment_no_trailing_pause()
    {
        var segments = SentenceSplitter.Split("One. Two. Three.");

        Assert.Equal(0, segments[^1].TrailingPauseMs);
        Assert.All(segments.Take(segments.Count - 1), s => Assert.True(s.TrailingPauseMs > 0));
    }

    [Fact]
    public void Pauses_longer_after_a_full_stop_than_after_a_colon()
    {
        var colon = SentenceSplitter.Split("Asibale ndawonye: kunye. Kubili.");
        var stop = SentenceSplitter.Split("Asibale ndawonye. Kunye. Kubili.");

        Assert.True(colon[0].TrailingPauseMs < stop[0].TrailingPauseMs);
    }

    [Fact]
    public void Cuts_an_over_long_run_at_a_word_boundary()
    {
        // No punctuation at all: on a phone the whole run must render before any of
        // it can play, so it is cut for latency — but never mid-word.
        var text = string.Join(' ', Enumerable.Repeat("ngiyabonga", 60));

        var segments = SentenceSplitter.Split(text);

        Assert.True(segments.Count > 1);
        Assert.All(segments, s => Assert.DoesNotContain("ngiyabongangiyabonga", s.Text));
        Assert.All(segments, s => Assert.Equal(s.Text.Trim(), s.Text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("... ??? ...")]
    public void Produces_nothing_for_input_with_no_speech_in_it(string? text)
    {
        Assert.Empty(SentenceSplitter.Split(text));
    }

    [Fact]
    public void Treats_a_line_break_as_a_paragraph_boundary()
    {
        var segments = SentenceSplitter.Split("Sawubona nonke\nNgiyabonga");

        Assert.Equal(2, segments.Count);
        Assert.True(segments[0].TrailingPauseMs > 280);
    }
}

/// <summary>
/// Covers the decorator that joins those segments back into one utterance.
/// </summary>
public class PhrasedTtsEngineTests
{
    /// <summary>A stand-in that reports what it was asked to say, and how often.</summary>
    private sealed class RecordingEngine : ITtsEngine, ITtsFrontEndDiagnostics
    {
        public List<string> Requests { get; } = new();
        public int LastSkippedCount { get; private set; }
        public IReadOnlyList<string> LastSkippedSymbols { get; private set; } = Array.Empty<string>();

        // 100 ms of 16-bit mono at 16 kHz per call, so byte counts are predictable.
        public Task<TtsSynthesisResult> SynthesiseAsync(string text, CancellationToken ct = default)
        {
            Requests.Add(text);

            // Pretend this engine cannot say the letter 'q' — one distinct symbol
            // per call, so the aggregation across segments is observable.
            var missing = text.Contains('q') ? new[] { "q" } : Array.Empty<string>();
            LastSkippedCount = missing.Length;
            LastSkippedSymbols = missing;

            return Task.FromResult(new TtsSynthesisResult(new byte[3200], 16000, 1, 16));
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
            string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return (await SynthesiseAsync(text, ct)).AudioData;
        }
    }

    [Fact]
    public async Task Synthesises_each_sentence_separately()
    {
        var inner = new RecordingEngine();
        using var engine = new PhrasedTtsEngine(inner);

        await engine.SynthesiseAsync("Sawubona. Ngiyabonga. Sala kahle.");

        Assert.Equal(3, inner.Requests.Count);
        Assert.Equal(3, engine.LastSegmentCount);
    }

    [Fact]
    public async Task Inserts_silence_between_sentences()
    {
        var inner = new RecordingEngine();
        using var engine = new PhrasedTtsEngine(inner);

        var joined = await engine.SynthesiseAsync("Sawubona. Ngiyabonga.");

        // Two segments of 3200 bytes, plus one 280 ms gap at 16 kHz / 16-bit mono.
        const int gapBytes = 16000 * 280 / 1000 * 2;
        Assert.Equal(3200 * 2 + gapBytes, joined.AudioData.Length);
    }

    [Fact]
    public async Task Leaves_a_single_sentence_byte_identical()
    {
        var inner = new RecordingEngine();
        using var engine = new PhrasedTtsEngine(inner);

        var result = await engine.SynthesiseAsync("Sawubona nonke.");

        Assert.Single(inner.Requests);
        Assert.Equal(3200, result.AudioData.Length);   // no gap appended
    }

    [Fact]
    public async Task Sums_skipped_symbols_across_every_segment()
    {
        // Reading the inner engine directly reports only the LAST sentence, so a
        // passage losing sound in its opening lines would look clean.
        var inner = new RecordingEngine();
        using var engine = new PhrasedTtsEngine(inner);

        await engine.SynthesiseAsync("Aqua one. Aqua two. Clean three.");

        Assert.Equal(0, inner.LastSkippedCount);       // last segment was clean
        Assert.Equal(2, engine.LastSkippedCount);      // but two segments lost sound
        Assert.Contains("q", engine.LastSkippedSymbols);
    }

    [Fact]
    public async Task Streams_the_first_sentence_before_the_rest_are_made()
    {
        var inner = new RecordingEngine();
        using var engine = new PhrasedTtsEngine(inner);

        await using var e = engine.StreamSynthesiseAsync("Sawubona. Ngiyabonga. Sala kahle.")
            .GetAsyncEnumerator();
        await e.MoveNextAsync();

        // Playback can start once ONE sentence exists — that is the whole point of
        // splitting on a device where a paragraph takes seconds to render.
        Assert.Single(inner.Requests);
    }

    [Fact]
    public async Task Does_not_dispose_a_shared_inner_engine_by_default()
    {
        // A warm engine costs minutes to rebuild on the phone; disposing one that
        // the caller still owns would be an expensive surprise.
        var inner = new DisposalTracker();
        new PhrasedTtsEngine(inner).Dispose();
        Assert.False(inner.Disposed);

        new PhrasedTtsEngine(inner, ownsInner: true).Dispose();
        Assert.True(inner.Disposed);
    }

    private sealed class DisposalTracker : ITtsEngine, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;

        public Task<TtsSynthesisResult> SynthesiseAsync(string text, CancellationToken ct = default)
            => Task.FromResult(new TtsSynthesisResult(new byte[2], 16000, 1, 16));

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
            string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return (await SynthesiseAsync(text, ct)).AudioData;
        }
    }
}
