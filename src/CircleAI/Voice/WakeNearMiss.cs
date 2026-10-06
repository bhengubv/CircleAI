// WakeNearMiss.cs
//
// How close the listener came, for the one screen whose job is to say so.
//
// WHY THIS EXISTS. "The wake word did not fire" has several causes that are the
// same silence from outside the app:
//
//   - no audio is arriving at all
//   - audio is arriving but too quiet to score
//   - the phrase is scoring, partially, and never completing
//   - the phrase completed and stage two turned it down
//
// The detector already distinguishes all four in its log. The SCREEN could not,
// because the only thing that ever reached it was "it fired". So somebody
// standing in front of the wake screen saying the phrase over and over saw
// exactly the same thing whether the microphone was dead or whether they were
// missing by one token.
//
// Measured on a P30 on 2026-09-06. The log said
// `closest="Hey Circle AI" 1/8 tokens p=0` — one token of eight, which means
// "it can hear you, you are too far away". The screen said "Listening", which
// means nothing, for as long as anybody cared to keep trying.
//
// ONE CHANNEL, TWO CAUSES. A partial match and a refused match are both "nearly"
// from the person's side, and they want different words back: one says come
// closer, the other says pause before you say it. Refused tells them apart.

using System;

namespace CircleAI.Voice;

/// <summary>The closest the listener came to waking, without waking.</summary>
/// <param name="Phrase">The phrase it was tracking.</param>
/// <param name="Matched">How many of the phrase's tokens were matched.</param>
/// <param name="Total">How many tokens the phrase has.</param>
/// <param name="Score">What that partial match scored.</param>
/// <param name="Refused">
/// Why stage two turned down a COMPLETE match, or null when the phrase simply
/// never completed. The distinction is the whole value of the type: a person who
/// was refused said the phrase perfectly well and needs different advice from
/// one who is standing too far away.
/// </param>
public sealed record WakeNearMiss(
    string Phrase, int Matched, int Total, double Score, string? Refused = null)
{
    /// <summary>True when the phrase was heard in full and then turned down.</summary>
    public bool WasRefused => Refused is { Length: > 0 };

    /// <summary>How much of the phrase landed, 0 to 1.</summary>
    /// <remarks>
    /// Guarded against a zero Total: a phrase with no tokens cannot arrive from
    /// the spotter, but this is read by a screen and a divide-by-zero on the UI
    /// thread is a worse outcome than a meaningless zero.
    /// </remarks>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Matched / (double)Total, 0, 1);
}

/// <summary>A detector that can say how close it came without waking.</summary>
/// <remarks>
/// SEPARATE FROM <see cref="IWakeWordDetector"/> ON PURPOSE. Seven types
/// implement that interface — a null one, an energy one, a legacy one, two test
/// fakes — and only the zipformer has a beam whose depth means anything. Putting
/// this on the main interface would make six implementations declare an event
/// they can never raise, which reads as a promise rather than as a capability.
/// <para>
/// A caller asks: <c>if (detector is IReportsNearMisses n) n.NearMiss += ...</c>.
/// </para>
/// </remarks>
public interface IReportsNearMisses
{
    /// <summary>Raised when the phrase was nearly heard, and was not.</summary>
    /// <remarks>
    /// Raised on the CAPTURE thread and at the heartbeat's cadence rather than
    /// per frame — a partial hypothesis moves many times a second and a screen
    /// re-rendering at that rate would cost more than the wake word does.
    /// </remarks>
    event EventHandler<WakeNearMiss>? NearMiss;
}
