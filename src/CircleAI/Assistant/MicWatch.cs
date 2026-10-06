// MicWatch.cs
//
// WHEN TO SAY THE MICROPHONE IS ALIVE, AND WHEN TO SHUT UP ABOUT IT.
//
// The wake word logged "mic alive: N chunks, peak 0.015, mean 0.0018" every five
// seconds, forever. On a P30 with 637 running tasks on 2026-10-04 that was the only
// thing left in the ring buffer: reading a setup line printed thirty seconds earlier
// took clearing the log and restarting the service twice, because 720 heartbeats an
// hour had scrolled past it. A diagnostic that hides every other diagnostic has
// stopped being one.
//
// THE QUESTION IT ANSWERS IS STILL WORTH ANSWERING. Without it, a phone that is
// listening and hearing nothing logs exactly the same as a phone that is not
// listening at all - and that is the state the other head was in when the wake word
// went deaf and every structural check said it was fine. So this backs off rather
// than going away, and reports a CHANGE immediately: going silent or coming back is
// the news, and a heartbeat is not.
//
// AND IT LIVES HERE, NOT BESIDE THE WAKE WORD, FOR A REASON THIS REPO HAS WRITTEN
// DOWN. ScreenUpWakeWord is in a net10.0-android library, and not one of the test
// projects can reference one of those - docs/OPEN-GAPS.md records exactly this about
// ModelChoice: "IT HAD NO TESTS BECAUSE NOTHING COULD REFERENCE IT... the one rule
// that decides what a person is offered was the one rule nothing asserted." This
// decision is arithmetic over a float and a counter, with no Android in it, so it
// belongs in the project whose whole job is data and pure decisions.

using System;

namespace CircleAI.Assistant;

/// <summary>What the watch wants said about the microphone, if anything.</summary>
public enum MicSay
{
    /// <summary>The window is not over yet.</summary>
    Nothing = 0,

    /// <summary>Sound is arriving, and it was arriving last time too.</summary>
    StillAlive,

    /// <summary>Sound is arriving, and this is the first word on it.</summary>
    Alive,

    /// <summary>Nothing at all is arriving, and this is the first word on it.</summary>
    WentSilent,

    /// <summary>Still nothing arriving. Worth repeating, at the backed-off cadence.</summary>
    StillSilent,

    /// <summary>Sound is back after a silence.</summary>
    CameBack,
}

/// <summary>Decides how often the microphone's state is worth a log line.</summary>
public sealed class MicWatch
{
    /// <summary>
    /// How often the microphone's state is CHECKED, at 16 kHz: every 5 s, always.
    /// </summary>
    /// <remarks>
    /// THE BACKOFF IS ON THE REASSURANCE, NOT ON THE DETECTION, and getting that
    /// backwards is a flaw a test caught before a device did. The first version
    /// backed off the whole check, so after an hour of quiet the window was ten
    /// minutes - and a microphone taken away by another app would not have been
    /// noticed for up to ten minutes. The one failure this whole mechanism exists to
    /// catch would have been the one it was slowest to report.
    /// </remarks>
    private const long ProbeSamples = 80_000;

    /// <summary>
    /// Samples between repeated "still alive" lines: 5 s, 30 s, 2 min, then 10 min
    /// forever. Six to nine lines an hour instead of 720.
    /// </summary>
    private static readonly long[] Backoff = [80_000, 480_000, 1_920_000, 9_600_000];

    /// <summary>Below this peak amplitude, nothing is arriving at all.</summary>
    /// <remarks>
    /// MEASURED, NOT PICKED. A quiet room on a P30 reads a peak of 0.012 to 0.023
    /// with a mean near 0.0018; a microphone delivering nothing reads zero. Two
    /// orders of magnitude apart, so this separates "quiet" from "dead" without
    /// anybody having to guess where the line goes.
    /// </remarks>
    public const float SilenceThreshold = 0.001f;

    private int _step;
    private long _sinceProbe;
    private long _sinceLine;
    private float _probePeak;
    private bool? _wasSilent;

    /// <summary>Samples until the state is next checked. Always the probe window.</summary>
    public long Probe => ProbeSamples;

    /// <summary>Samples that must pass before an unchanged state is repeated.</summary>
    public long Quiet => Backoff[_step];

    /// <summary>
    /// Feed a chunk's worth of samples and its loudest value; say what to log.
    /// </summary>
    /// <param name="samples">How many samples arrived.</param>
    /// <param name="peak">The loudest absolute amplitude in them.</param>
    /// <remarks>
    /// THE PEAK IS CARRIED ACROSS THE PROBE WINDOW, not judged per chunk: one loud
    /// chunk in five seconds is a microphone that works, and asking about a single
    /// chunk would report silence every time somebody stopped talking.
    /// </remarks>
    public MicSay Observe(int samples, float peak)
    {
        if (samples > 0)
        {
            _sinceProbe += samples;
            _sinceLine += samples;
        }
        if (peak > _probePeak) _probePeak = peak;

        if (_sinceProbe < ProbeSamples) return MicSay.Nothing;

        var silent = _probePeak < SilenceThreshold;
        var first = _wasSilent is null;
        var changed = first || _wasSilent != silent;

        _sinceProbe = 0;
        _probePeak = 0f;
        _wasSilent = silent;

        if (changed)
        {
            // A transition is news and is said at once, whatever the cadence had
            // backed off to, and it restarts that cadence.
            _step = 0;
            _sinceLine = 0;
            return silent ? MicSay.WentSilent
                 : first  ? MicSay.Alive
                 :          MicSay.CameBack;
        }

        // Unchanged: say it only when the backed-off interval has actually passed.
        if (_sinceLine < Quiet) return MicSay.Nothing;
        _sinceLine = 0;

        // A DEAD MICROPHONE KEEPS SAYING SO, at the backed-off cadence rather than
        // once. "It went quiet an hour ago and has not come back" is the useful
        // reading, and a single line an hour earlier would have scrolled away.
        if (_step < Backoff.Length - 1) _step++;
        return silent ? MicSay.StillSilent : MicSay.StillAlive;
    }
}
