// SpokenTurnClock.cs
//
// How long a spoken turn really took, from the only moment a person counts from.
//
// THE NUMBER THAT MATTERS WAS IN NO LOG. Every stage kept its own time - the ears, the brain, the
// voice - and on 2026-11-06 the owner's 70 seconds had to be pieced together from their timestamps
// by hand, which is how a turn could be called fast because its text was. A person does not count
// from the question reaching a queue. They count from the moment they stopped talking, and they
// stop counting at the first sound. So that is what this holds, stage by stage, for every turn.

using System;
using System.Diagnostics;
using System.Globalization;

namespace CircleAI.Assistant;

/// <summary>Marks when each part of a spoken turn happened, from the last word said to the first sound.</summary>
public sealed class SpokenTurnClock
{
    private readonly Func<long> _now;
    private long _stopped = -1, _closed = -1, _heard = -1, _firstText = -1, _firstPiece = -1, _firstSound = -1;
    private long _soundEnds = -1, _longestSilence;
    private int _firstPieceChars;

    /// <param name="now">The clock, in <see cref="Stopwatch"/> ticks; null reads the real one.</param>
    public SpokenTurnClock(Func<long>? now = null) => _now = now ?? Stopwatch.GetTimestamp;

    /// <summary>The microphone is closed, <paramref name="afterLastWord"/> after the last word.</summary>
    public void MicrophoneClosed(TimeSpan afterLastWord)
    {
        _closed = _now();
        _stopped = _closed - (long)(afterLastWord.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>The question's words are known. A second call means the first were not the question: everything after them starts over.</summary>
    public void Heard()
    {
        _firstText = _firstPiece = _firstSound = -1;
        _heard = _now();
    }

    /// <summary>The brain's first words have come back. Only the first call counts.</summary>
    public void FirstText()
    {
        if (_firstText < 0) _firstText = _now();
    }

    /// <summary>The voice has started on a piece; true when it is the first. Only the first call counts.</summary>
    public bool FirstPiece(int chars)
    {
        if (_firstPiece >= 0) return false;
        _firstPiece = _now();
        _firstPieceChars = chars;
        return true;
    }

    /// <summary>The first sound is going out. Only the first call counts.</summary>
    public void FirstSound()
    {
        if (_firstSound < 0) _firstSound = _now();
    }

    /// <summary><paramref name="audio"/> of speech is starting, or queued behind what is playing.</summary>
    public void Playing(TimeSpan audio)
    {
        var now = _now();
        if (_soundEnds >= 0 && now > _soundEnds) _longestSilence = Math.Max(_longestSilence, now - _soundEnds);
        _soundEnds = Math.Max(now, _soundEnds) + (long)(audio.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>From the last word said to the first sound; null until both have happened.</summary>
    public TimeSpan? ToFirstSound => _stopped >= 0 && _firstSound >= 0 ? Between(_stopped, _firstSound) : null;

    /// <summary>The turn in one line, in the order it happened. Holds no words anybody said.</summary>
    public string Report()
    {
        if (ToFirstSound is not { } total) return "spoken turn: no sound came out";
        return string.Create(CultureInfo.InvariantCulture,
            $"spoken turn: {total.TotalMilliseconds:F0} ms from the last word to the first sound: "
            + $"microphone {Ms(_stopped, _closed)}, ears {Ms(_closed, _heard)}, brain {Ms(_heard, _firstText)}, "
            + $"to the voice {Ms(_firstText, _firstPiece)}, voice {Ms(_firstPiece, _firstSound)} ({_firstPieceChars} chars first); "
            + $"longest silence inside the answer {Between(0, _longestSilence).TotalMilliseconds:F0} ms");
    }

    private static TimeSpan Between(long from, long to) =>
        TimeSpan.FromSeconds((to - from) / (double)Stopwatch.Frequency);

    private static string Ms(long from, long to) =>
        from < 0 || to < 0 ? "?" : Between(from, to).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture);
}
