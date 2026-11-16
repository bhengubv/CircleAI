#nullable enable

// FeatureChunks.cs
//
// What a streaming zipformer front end still needs of the audio it has heard, and nothing older.
//
// THE WAKE WORD KEPT ALL OF IT. ZipformerKwsSpotter added every feature frame it made to a list and never dropped
// one, and its KaldiFbank kept every sample behind them, because nothing called Consume: 16,000 floats a second and
// 100 frames of 80, about 100 KB a second for as long as the phone listens. Measured on the P30 on 2026-11-15 from
// the phone's own memory samples (am_pss), with the wake word the only thing running: the service grew from 356 MB
// to 621 MB in 53 minutes - 5 MB a minute - after reaching 1.05 GB the day before, and Huawei's iAware killed it as
// an abnormal process each time (am_kill iAwareK[abnormProc]), two hours into listening. StreamingTranscriber had
// found and fixed the same list on 2026-10-17 ("the service process was measured at 904 MB"); the spotter, whose
// Drain was last touched in August, never got the fix, and the spotter is what a phone without the transcribing
// bundle listens with.
//
// SO THE BOOKKEEPING LIVES HERE, where a test can drive it without a model: frames are taken from the extractor as
// they become ready, which lets the audio behind them go (KaldiFbank.Consume); handed to the encoder a chunk at a
// time; and dropped once no later chunk can reach them. Chunks overlap - 45 frames read, 32 advanced - so what is
// held is the overlap and the frames not yet read, never the past.

using System;
using System.Collections.Generic;

namespace CircleAI.Voice;

/// <summary>
/// The feature frames a streaming encoder has not finished with: taken from a <see cref="KaldiFbank"/> as they become
/// ready, handed out a chunk at a time, and dropped once no later chunk can reach them.
/// </summary>
internal sealed class FeatureChunks
{
    private readonly KaldiFbank _fbank;
    private readonly int _chunkFrames;
    private readonly int _advanceFrames;
    private readonly List<float[]> _frames = new();
    private int _cursor;

    /// <summary>The frames of <paramref name="fbank"/>, a chunk at a time.</summary>
    /// <param name="fbank">The extractor the frames come from. It is consumed as they are taken.</param>
    /// <param name="chunkFrames">The frames each encoder call reads - 45 for the zipformer bundles shipped.</param>
    /// <param name="advanceFrames">How far each call moves on - 32, so 13 frames overlap.</param>
    public FeatureChunks(KaldiFbank fbank, int chunkFrames, int advanceFrames)
    {
        ArgumentNullException.ThrowIfNull(fbank);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkFrames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(advanceFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(advanceFrames, chunkFrames);
        _fbank = fbank;
        _chunkFrames = chunkFrames;
        _advanceFrames = advanceFrames;
    }

    /// <summary>Takes every frame the extractor has ready, and lets it forget the audio behind them.</summary>
    public void TakeReady()
    {
        var ready = _fbank.FramesReady;
        for (var f = 0; f < ready; f++) _frames.Add(_fbank.GetFrame(f));
        if (ready > 0) _fbank.Consume(ready);
    }

    /// <summary>Whether a whole chunk is held, ready for the encoder.</summary>
    public bool HasChunk => _cursor + _chunkFrames <= _frames.Count;

    /// <summary>Frame <paramref name="t"/> of the chunk the encoder reads next, from 0 to the chunk's width less one.</summary>
    public float[] this[int t]
    {
        get
        {
            if ((uint)t >= (uint)_chunkFrames || !HasChunk)
                throw new ArgumentOutOfRangeException(nameof(t), $"frame {t} of a {_chunkFrames}-frame chunk, with {_frames.Count - _cursor} held");
            return _frames[_cursor + t];
        }
    }

    /// <summary>Moves to the next chunk, and drops the frames no later chunk can reach.</summary>
    public void Advance()
    {
        _cursor += _advanceFrames;
        var drop = Math.Min(_cursor, _frames.Count);
        if (drop <= 0) return;
        _frames.RemoveRange(0, drop);
        _cursor -= drop;
    }

    /// <summary>Forgets every frame: with the extractor's own Reset, a new stream.</summary>
    public void Clear()
    {
        _frames.Clear();
        _cursor = 0;
    }

    /// <summary>The frames held now: a chunk's overlap and what the extractor made ready since, never the past.</summary>
    public int Held => _frames.Count;
}
