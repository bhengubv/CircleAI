// OutLoud.cs
//
// WHETHER THE ASSISTANT IS TALKING RIGHT NOW, SO IT DOES NOT LISTEN TO ITSELF.
//
// MEASURED, AND IT IS THE REASON THIS EXISTS. On 2026-10-12 the wake loop's own
// microphone capture was pulled off the P30 and run through the recogniser. It
// read:
//
//     " I AM USING THE SMALLER BRAIN THE BIG ONE STOPPED THIS PHONE SO I LEFT
//       IT ALONE"
//
// Nobody said that to the phone. That is the PHONE, spoken by its own
// text-to-speech out of its own speaker and picked up by its own microphone, and
// the wake recogniser transcribed it perfectly. Three things follow and none of
// them is acceptable:
//
//   - IT CAN WAKE ITSELF. The assistant says its own name - "I'm B", "ask B
//     again in a moment" - and the wake word is listening. A phone that
//     triggers on its own voice has a feedback loop, not a wake word.
//   - IT SPENDS THE BATTERY THE GATE JUST SAVED. Every sentence it speaks is
//     also a sentence it decodes.
//   - IT MAKES THE LOG LIE. "last heard" showing the assistant's words reads
//     exactly like the microphone working, which is how this went unnoticed.
//
// AndroidAudioPlayer already carried a comment about this - it waits out the
// playback "so the loop does not start listening again while the assistant is
// still talking (which is how a device hears its own voice and answers
// itself)". That guard is real and it covers the TURN's microphone. The wake
// loop is a DIFFERENT microphone, always on, in the service, and it was never
// told. The fact had one place that knew it and one place that needed it, and
// no wire between them - which is this repo's signature defect, found on this
// one feature for the sixth time.
//
// SO IT IS ONE FACT WITH ONE OWNER, and deliberately not an event: a listener
// that misses an event stays wrong for ever, whereas something ASKING "is the
// mouth open" cannot drift. It is static because there is exactly one speaker
// and one pair of ears on a phone, and threading an instance through four
// layers to say so is how the previous five of these got skipped.
//
// FRAMEWORK-NEUTRAL, so both legs assert it instead of it being discovered on a
// device.

using System;
using System.Threading;

namespace CircleAI.Voice;

/// <summary>Whether the assistant's own voice is coming out of the speaker.</summary>
public static class OutLoud
{
    /// <summary>How long after speaking the ears stay shut.</summary>
    /// <remarks>
    /// THE ROOM KEEPS THE SOUND FOR A MOMENT AFTER THE SPEAKER STOPS. The tail
    /// of a sentence reaches the microphone off the walls, and a recogniser fed
    /// that tail hears a fragment of the assistant with nothing in front of it -
    /// which is worse than hearing the whole sentence, because a fragment is
    /// what a wake phrase is.
    /// <para>
    /// Four hundred milliseconds. Long enough for a small room, short enough
    /// that somebody answering the assistant immediately is still heard.
    /// </para>
    /// </remarks>
    public static TimeSpan Settle { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Where the time comes from, so this is testable without waiting.</summary>
    public static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    private static int _mouths;
    private static long _stoppedAt;
    private static long _busyUntil;

    /// <summary>True while the assistant is speaking, or has just stopped.</summary>
    public static bool Now
    {
        get
        {
            if (Volatile.Read(ref _mouths) > 0) return true;

            // Audio this process handed to ANOTHER process to play, for as long as
            // it lasts plus the room's settle. See For.
            var busy = Interlocked.Read(ref _busyUntil);
            if (busy != 0 && Clock().UtcTicks < busy + Settle.Ticks) return true;

            var stopped = Interlocked.Read(ref _stoppedAt);
            if (stopped == 0) return false;

            return Clock().UtcTicks - stopped < Settle.Ticks;
        }
    }

    /// <summary>How many sentences have been spoken, for reporting.</summary>
    /// <remarks>
    /// A COUNT RATHER THAN A FLAG, because a wake loop reporting "0 blocks
    /// skipped while speaking" could mean the assistant never spoke or could
    /// mean this was never wired - and those two look identical. A silent zero
    /// is not a quiet room.
    /// </remarks>
    public static long Spoken { get; private set; }

    /// <summary>Marks the mouth open until the returned handle is disposed.</summary>
    /// <remarks>
    /// NESTED CALLS ARE COUNTED, not overwritten. Two sentences overlapping -
    /// which happens the moment anything speaks in parallel - would otherwise
    /// have the first one's end re-open the ears while the second is still
    /// playing.
    /// </remarks>
    public static IDisposable While()
    {
        Interlocked.Increment(ref _mouths);
        Spoken++;
        return new Mouth();
    }

    /// <summary>Marks the mouth open for audio that will play somewhere else.</summary>
    /// <param name="length">How long the audio lasts.</param>
    /// <remarks>
    /// FOR SPEECH THIS PROCESS RENDERS AND ANOTHER PROCESS PLAYS - and that is how
    /// every answer reaches a person on this product.
    /// <para>
    /// The service renders; the app it is linked to plays, because the app is the
    /// one somebody is looking at. <see cref="While"/> is the right mark for
    /// playback in this process, and that is the trouble: the app's player marks
    /// the APP's copy of this class, which is static per process, and the wake
    /// loop lives in the service. Measured on the P30 on 2026-10-16, an answer
    /// played by the app was transcribed straight back by the service's wake
    /// loop - "I AM HAPPY AND THE TEMPERATURE IS WARM ... TWENTY FIVE DEGREES" -
    /// while it reported "0 sentences". A self-wake is one answer away whenever the
    /// assistant says its own name.
    /// </para>
    /// <para>
    /// The service cannot see the app's speaker, but at the moment it hands the
    /// audio over it knows exactly how long that audio is, and the app plays it at
    /// once. So it marks itself deaf for that long. It only ever extends: a second
    /// sentence handed over while the first is playing must not shorten the first.
    /// </para>
    /// </remarks>
    public static void For(TimeSpan length)
    {
        if (length <= TimeSpan.Zero) return;

        var until = Clock().UtcTicks + length.Ticks;
        long seen;
        do
        {
            seen = Interlocked.Read(ref _busyUntil);
            if (until <= seen) break;
        }
        while (Interlocked.CompareExchange(ref _busyUntil, until, seen) != seen);

        Spoken++;
    }

    /// <summary>Forgets everything. For tests, and for a fresh process.</summary>
    public static void Reset()
    {
        Volatile.Write(ref _mouths, 0);
        Interlocked.Exchange(ref _stoppedAt, 0);
        Interlocked.Exchange(ref _busyUntil, 0);
        Spoken = 0;
    }

    private sealed class Mouth : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            // ONCE, however many times a using block is unwound. Disposing
            // twice would drop the count below zero and leave the ears shut
            // for ever, which is the one failure here that cannot be noticed
            // by listening.
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;

            Interlocked.Exchange(ref _stoppedAt, Clock().UtcTicks);
            Interlocked.Decrement(ref _mouths);
        }
    }
}
