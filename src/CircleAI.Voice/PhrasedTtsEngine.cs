#nullable enable

// PhrasedTtsEngine.cs
//
// Wraps any ITtsEngine so a passage is spoken sentence by sentence, with a real
// pause where each full stop was.
//
// This is a decorator rather than a change inside OnnxTtsEngine because the
// problem it solves belongs to every voice we ship, not to one engine: MMS,
// guymandude SA-11 and ToucanTTS were all trained on punctuation-stripped text,
// so none of them can encode a pause. Putting it here means one implementation
// serves all of them, and a future engine whose model DOES speak punctuation can
// simply not be wrapped.
//
// It also fixes a latency problem that turns out to be the same problem. Feeding
// a whole paragraph to the model means every word of it must render before the
// first word can play — on a phone that is the difference between a pause and a
// stall. Synthesising per sentence lets StreamSynthesiseAsync emit sentence one
// while sentence two is still being made.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Voice;

/// <summary>
/// An <see cref="ITtsEngine"/> that speaks text one sentence at a time, joining
/// the results with silence so sentence breaks are audible.
/// </summary>
public sealed class PhrasedTtsEngine : ITtsEngine, ITtsFrontEndDiagnostics, IDisposable
{
    private readonly ITtsEngine _inner;
    private readonly bool _ownsInner;
    private readonly List<string> _skippedSymbols = new();
    private readonly List<string> _approximatedSymbols = new();

    /// <param name="inner">The engine that actually synthesises each sentence.</param>
    /// <param name="ownsInner">
    /// When true, disposing this engine disposes <paramref name="inner"/> too.
    /// Defaults to false: callers commonly keep a warm engine alive across many
    /// utterances, and on a phone rebuilding one costs minutes, not milliseconds.
    /// </param>
    public PhrasedTtsEngine(ITtsEngine inner, bool ownsInner = false)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _ownsInner = ownsInner;
    }

    /// <summary>Segments the last call produced — 1 means nothing was split.</summary>
    public int LastSegmentCount { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// Summed over every segment. Reading the inner engine directly would report
    /// only the LAST sentence, so a passage that lost sound in its opening lines
    /// would look clean.
    /// </remarks>
    public int LastSkippedCount { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<string> LastSkippedSymbols => _skippedSymbols;

    /// <inheritdoc />
    public IReadOnlyList<string> LastApproximatedSymbols => _approximatedSymbols;

    /// <summary>Accumulates one segment's diagnostics into the running total.</summary>
    private void CollectDiagnostics()
    {
        if (_inner is not ITtsFrontEndDiagnostics d) return;

        LastSkippedCount += d.LastSkippedCount;
        foreach (var s in d.LastSkippedSymbols)
            if (!_skippedSymbols.Contains(s)) _skippedSymbols.Add(s);
        foreach (var s in d.LastApproximatedSymbols)
            if (!_approximatedSymbols.Contains(s)) _approximatedSymbols.Add(s);
    }

    private void ResetDiagnostics()
    {
        LastSkippedCount = 0;
        _skippedSymbols.Clear();
        _approximatedSymbols.Clear();
    }

    public async Task<TtsSynthesisResult> SynthesiseAsync(
        string text, CancellationToken cancellationToken = default)
    {
        var segments = SentenceSplitter.Split(text);
        LastSegmentCount = segments.Count;
        ResetDiagnostics();

        if (segments.Count == 0)
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, 16000, 1, 16);

        // One sentence needs no joining — hand the inner result back untouched so
        // a single-sentence utterance is byte-identical to the unwrapped engine.
        if (segments.Count == 1)
        {
            var only = await _inner.SynthesiseAsync(segments[0].Text, cancellationToken).ConfigureAwait(false);
            CollectDiagnostics();
            return only;
        }

        var buffers = new List<ReadOnlyMemory<byte>>(segments.Count * 2);
        TtsSynthesisResult? format = null;
        var total = 0;

        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var part = await _inner.SynthesiseAsync(segment.Text, cancellationToken).ConfigureAwait(false);
            CollectDiagnostics();
            if (part.AudioData.Length == 0) continue;

            format ??= part;
            buffers.Add(part.AudioData);
            total += part.AudioData.Length;

            var gap = Silence(part, segment.TrailingPauseMs);
            if (gap.Length > 0)
            {
                buffers.Add(gap);
                total += gap.Length;
            }
        }

        if (format is null)
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, 16000, 1, 16);

        var joined = new byte[total];
        var offset = 0;
        foreach (var b in buffers)
        {
            b.Span.CopyTo(joined.AsSpan(offset));
            offset += b.Length;
        }

        return format with { AudioData = joined };
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> StreamSynthesiseAsync(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var segments = SentenceSplitter.Split(text);
        LastSegmentCount = segments.Count;
        ResetDiagnostics();

        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Synthesise per sentence rather than delegating to the inner stream:
            // the inner engine renders whatever it is given in one pass, so passing
            // the whole passage would reinstate exactly the stall this avoids.
            var part = await _inner.SynthesiseAsync(segment.Text, cancellationToken).ConfigureAwait(false);
            CollectDiagnostics();
            if (part.AudioData.Length == 0) continue;

            yield return part.AudioData;

            var gap = Silence(part, segment.TrailingPauseMs);
            if (gap.Length > 0) yield return gap;
        }
    }

    /// <summary>PCM silence of <paramref name="milliseconds"/> in the result's format.</summary>
    private static ReadOnlyMemory<byte> Silence(TtsSynthesisResult format, int milliseconds)
    {
        if (milliseconds <= 0) return ReadOnlyMemory<byte>.Empty;

        var bytesPerFrame = Math.Max(1, format.Channels * (format.BitsPerSample / 8));
        var frames = (int)((long)format.SampleRate * milliseconds / 1000);

        // Signed PCM is silent at zero, which is also the default for a new array.
        return new byte[frames * bytesPerFrame];
    }

    public void Dispose()
    {
        if (_ownsInner && _inner is IDisposable d) d.Dispose();
    }
}
