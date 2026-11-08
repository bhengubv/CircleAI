// SpeechLines.cs
//
// WHERE THE EARS CLOSE ONE LINE OF WHAT IS BEING SAID AND START THE NEXT.
//
// THE EARS WERE MEANT TO WORK IN LITTLE CHUNKS AND DID NOT. Every update reads the words again from the
// first one, because the memory the decoder attends to has grown, so an update costs whatever has been said
// so far. Measured on the P30 on 2026-11-08, the brain idle and 33 s of speech fed at the pace it is spoken:
//
//     1 s said    an update takes 0,2 s
//     7 s said    0,3 s, and it is keeping up
//    16 s said    1,4 s, and it is 5 s behind
//    21 s said    2,5 s, and it is 15 s behind
//    33 s said    3,6 s, and it is 77 s behind
//
// On 2026-11-07 that was somebody waiting 77 and 96 s for an answer. So a line is closed while it is still
// cheap: what it said is written down for good, and the next update starts on nothing but what came after.

namespace CircleAI.Voice;

/// <summary>Finds where a line of speech can be closed.</summary>
internal static class SpeechLines
{
    /// <summary>The shortest line, in frames of 20 ms: a sentence's worth, so the model has something to read.</summary>
    public const int Least = 150;

    /// <summary>The longest line, in frames of 20 ms: under the 7 s at which an update stops keeping up on the P30.</summary>
    public const int Most = 300;

    /// <summary>A quiet this many frames long is somebody drawing breath: 240 ms.</summary>
    public const int Pause = 12;

    /// <summary>How far above the bottom of a line's levels still counts as quiet.</summary>
    private const float QuietOverFloor = 2f;

    /// <summary>Where in a line's levels, lowest first, its floor is read off.</summary>
    private const double FloorQuantile = 0.1;

    /// <summary>With no pause, how far back from the end of the line the quietest place is looked for: 2 s.</summary>
    private const int Reach = 100;

    /// <summary>With no pause, the frames taken together to find the quietest place: the gap between two words.</summary>
    private const int Gap = 5;

    /// <summary>The frame to close the line at; zero to carry on with it.</summary>
    /// <param name="levels">The level of each frame of the line so far.</param>
    /// <remarks>
    /// BY THE LINE'S OWN QUIET, NOT THE GATE'S. A gate reads a room and on 2026-11-07 it read this one wrong for
    /// every turn; between two words a line is at its own bottom whatever the room is doing.
    /// <para>
    /// AND WITH NO PAUSE IT IS CLOSED ANYWAY, at the quietest place going. A word cut in two costs that word.
    /// Going on costs everything said after it.
    /// </para>
    /// </remarks>
    public static int CutIn(ReadOnlySpan<float> levels)
    {
        if (levels.Length < Least) return 0;

        var sorted = levels.ToArray();
        Array.Sort(sorted);
        var quiet = sorted[(int)(sorted.Length * FloorQuantile)] * QuietOverFloor;

        var longest = 0;
        var longestEnds = 0;
        var run = 0;
        for (var i = Least; i < levels.Length; i++)
        {
            run = levels[i] <= quiet ? run + 1 : 0;
            if (run <= longest) continue;
            longest = run;
            longestEnds = i + 1;
        }

        if (longest >= Pause) return longestEnds - longest / 2;
        return levels.Length < Most ? 0 : Quietest(levels);
    }

    private static int Quietest(ReadOnlySpan<float> levels)
    {
        var from = Math.Max(Least, levels.Length - Reach);
        var least = float.MaxValue;
        var at = levels.Length - Gap / 2 - 1;

        for (var i = from; i + Gap <= levels.Length; i++)
        {
            var sum = 0f;
            for (var j = i; j < i + Gap; j++) sum += levels[j];
            if (sum >= least) continue;
            least = sum;
            at = i + Gap / 2;
        }

        return at;
    }
}
