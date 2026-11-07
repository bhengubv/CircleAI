// ClipCapture.cs
//
// A recording in the microphone's place, said at the speed it was said.
//
// A SPOKEN TURN COULD ONLY BE MEASURED BY SOMEBODY SPEAKING, so it was hardly measured at all: on
// 2026-11-06 the logs held one real spoken question in three days. Everything after the microphone
// is the same code whether the words come from a throat or a file, and a file says the same words
// every time, so a change can be compared with the one before it. Blocks come out at their own
// pace, because the turn's ears are meant to work WHILE somebody talks; a file read as fast as it
// will go would hide exactly that.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Voice;

/// <summary>Plays PCM 16-bit 16 kHz mono in as a microphone would, then a silent room.</summary>
public sealed class ClipCapture : IAudioCapture
{
    private const int BlockMs = 100;
    private const int BlockBytes = 16_000 * 2 * BlockMs / 1000;

    private readonly ReadOnlyMemory<byte> _pcm;

    /// <param name="pcm">The recording, PCM 16-bit 16 kHz mono.</param>
    public ClipCapture(ReadOnlyMemory<byte> pcm) => _pcm = pcm;

    /// <inheritdoc />
    public AudioFormat Format => AudioFormat.Pcm16Mono16k;

    /// <inheritdoc />
    /// <remarks>
    /// THE ROOM GOES ON AFTER THE CLIP ENDS, as a room does: whoever is listening decides when
    /// the question is over, and a capture that simply stopped would decide it for them.
    /// </remarks>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var silence = new byte[BlockBytes];
        for (var block = 0L; !ct.IsCancellationRequested; block++)
        {
            // Against the clock, not by a delay per block: delays drift late, and late blocks would
            // make the recording slower than it was said.
            var due = TimeSpan.FromMilliseconds((block + 1) * BlockMs) - clock.Elapsed;
            if (due > TimeSpan.Zero)
            {
                try { await Task.Delay(due, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }

            var at = block * BlockBytes;
            yield return at >= _pcm.Length ? silence : _pcm.Slice((int)at, (int)Math.Min(BlockBytes, _pcm.Length - at));
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
