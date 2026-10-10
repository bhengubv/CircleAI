// DecisionLog.cs
//
// Part 3 of the plan: it decides, it acts, and it TELLS YOU.
//
// Everything this device worked out about its own models - that the big brain
// will not fit, that it was killed by one and will not try again, that something
// unusable is taking up room - was reachable only by asking. SelfRequest answers
// "which brain are you using?" perfectly well, and nobody thinks to ask it. The
// bar is Alexa: it says the sentence without being asked.
//
// WHY A TYPE AND NOT A FIELD ON THE SERVICE. The first version was exactly that
// - a static string and an int on CircleNeuronLinkService - and it had the same
// problem as the warm-up loop before ReadinessWait: an Android Service is not
// reachable from the net9/net10 suites, so the one piece with any logic in it
// (do not repeat yourself; never go backwards) could only be checked by running
// a phone. The service keeps one of these and supplies the Android parts.
//
// THE SEQUENCE NUMBER IS WHAT MAKES POLLING SAFE. The link is a binder -
// request/response, no channel back - so a client polls, exactly as it does for
// the wake count. A flag would let a client miss a decision taken between two
// asks, and let a second decision look like nothing had happened. A number that
// only goes up cannot do either.

using System;

namespace CircleAI.Assistant;

/// <summary>The last thing a device decided for itself, and how many times.</summary>
public sealed class DecisionLog
{
    private readonly object _gate = new();
    private string? _last;
    private int _sequence;

    /// <summary>The most recent decision, or null if nothing has been decided.</summary>
    public string? Last { get { lock (_gate) return _last; } }

    /// <summary>
    /// How many distinct decisions this run has recorded. Only ever increases.
    /// </summary>
    /// <remarks>
    /// Resets with the process, which is honest: it counts what THIS run worked
    /// out, not what the device has ever concluded. A client that restarts hears
    /// the current verdict once more, which is right - it is new to that client.
    /// </remarks>
    public int Sequence { get { lock (_gate) return _sequence; } }

    /// <summary>Record a decision. True when it was new and the sequence moved.</summary>
    /// <param name="sentence">
    /// What to say, from <see cref="AssistantPersona"/> and nowhere else. Spoken
    /// text has one owner; a sentence composed at a decision site would be a
    /// second voice for the same product.
    /// </param>
    /// <remarks>
    /// AN IDENTICAL DECISION DOES NOT COUNT AGAIN. The selector re-runs on every
    /// cold start, and on a handset where the phone kills the service several
    /// times a day it would otherwise re-announce "I am using the smaller one"
    /// every time - turning a useful sentence into noise a person learns to
    /// ignore. The same lesson as the notification that posted three thousand
    /// times a day and had every one of them demoted by Android.
    /// <para>
    /// Blank is not a decision. Clearing the verdict would make a client that
    /// polls see the number go up and find nothing to say.
    /// </para>
    /// </remarks>
    public bool Decided(string? sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence)) return false;

        lock (_gate)
        {
            if (string.Equals(sentence, _last, StringComparison.Ordinal)) return false;
            _last = sentence;
            _sequence++;
            return true;
        }
    }

    /// <summary>
    /// Whether a client that last spoke <paramref name="spokenSeq"/> has something new.
    /// </summary>
    /// <remarks>
    /// THE CLIENT'S HALF OF THE CONTRACT, here so that every client cannot get it
    /// subtly wrong in its own way. Strictly greater: a client that has spoken
    /// sequence 3 must not say 3 again, and must say 4.
    /// </remarks>
    public bool HasSomethingNewFor(int spokenSeq)
    {
        lock (_gate) return _last is not null && _sequence > spokenSeq;
    }
}
