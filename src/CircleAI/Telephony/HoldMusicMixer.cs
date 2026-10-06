// HoldMusicMixer.cs
//
// (3.3.0) Background-audio mixer for call-on-hold experiences. Loops
// a music track and mixes the AI's speech on top at adjustable gain.
// Ducks the background automatically when speech frames arrive.

using System;
using System.Buffers.Binary;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Background audio mixer for hold music.</summary>
public sealed class HoldMusicMixer
{
    private readonly byte[] _backgroundLoop;
    private readonly float _backgroundGain;
    private readonly float _duckedGain;
    private int _loopCursor;

    /// <param name="backgroundLoop">PCM-16 mono buffer that the mixer loops over.</param>
    /// <param name="backgroundGain">Gain when no speech (0..1). Default 0.6.</param>
    /// <param name="duckedGain">Gain while speech is being mixed (0..1). Default 0.15.</param>
    public HoldMusicMixer(byte[] backgroundLoop, float backgroundGain = 0.6f, float duckedGain = 0.15f)
    {
        _backgroundLoop = backgroundLoop ?? throw new ArgumentNullException(nameof(backgroundLoop));
        if (backgroundLoop.Length < 2)
        {
            throw new ArgumentException("Background loop must contain at least one PCM-16 sample.", nameof(backgroundLoop));
        }
        if (backgroundGain < 0 || backgroundGain > 1) throw new ArgumentOutOfRangeException(nameof(backgroundGain));
        if (duckedGain     < 0 || duckedGain     > 1) throw new ArgumentOutOfRangeException(nameof(duckedGain));
        _backgroundGain = backgroundGain;
        _duckedGain     = duckedGain;
    }

    /// <summary>Reset the loop cursor to the start.</summary>
    public void Reset() { _loopCursor = 0; }

    /// <summary>
    /// (3.3.0) Mix <paramref name="speechFrame"/> on top of looped
    /// background and write the result into <paramref name="destination"/>.
    /// Pass an empty speech buffer to render plain background.
    /// </summary>
    public int MixFrame(ReadOnlySpan<byte> speechFrame, Span<byte> destination)
    {
        if (destination.Length < 2) return 0;
        bool hasSpeech = speechFrame.Length >= 2;
        int frameLength = hasSpeech ? speechFrame.Length : destination.Length;
        if (destination.Length < frameLength)
        {
            throw new ArgumentException("destination must be at least as long as the speech frame.", nameof(destination));
        }

        var gain = hasSpeech ? _duckedGain : _backgroundGain;

        for (int i = 0; i < frameLength; i += 2)
        {
            short speechSample = hasSpeech
                ? BinaryPrimitives.ReadInt16LittleEndian(speechFrame.Slice(i, 2))
                : (short)0;

            // Pull background sample from the loop, wrapping as needed.
            short bgSample = BinaryPrimitives.ReadInt16LittleEndian(_backgroundLoop.AsSpan(_loopCursor, 2));
            _loopCursor = (_loopCursor + 2) % _backgroundLoop.Length;
            if (_loopCursor % 2 != 0) _loopCursor--; // align to 16-bit boundary

            int mixed = speechSample + (int)(bgSample * gain);
            mixed = Math.Clamp(mixed, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(destination.Slice(i, 2), (short)mixed);
        }
        return frameLength;
    }
}
