namespace CircleAI.Samples.It;

/// <summary>
/// The phase every brand mark in the app is showing, in one place.
/// </summary>
/// <remarks>
/// THERE ARE TWO MICROPHONE BUTTONS AND THERE WAS NO SHARED STATE.
///
/// Home's circle and the middle of the tab bar are the same control offered
/// twice, and each kept its own <see cref="MarkState"/> field with its own copy
/// of the same phase switch. So a turn started from one of them left the other
/// sitting idle: press the circle and the bar goes on showing "talk to it" while
/// it is already listening to you, press the bar and the circle - the largest
/// thing on the screen - does nothing at all.
///
/// Measured on a P30, reading the live DOM rather than the pixels: tapping the
/// hero put <c>bm-listening</c> on the hero alone, and tapping the bar put it on
/// the bar alone. Neither ever moved the other. Both marks animate correctly;
/// they were just being told about different turns.
///
/// <para>
/// A field per component cannot fix that, because the thing being described is
/// not per component. There is ONE microphone and ONE turn, so there is one
/// answer to "what is it doing", and every mark reads it here.
/// </para>
/// <para>
/// It also gives the resident wake word somewhere to report to. When the phone
/// wakes on its own nobody pressed anything, so no component's private field is
/// in a position to know - which is why waking currently animates nothing at
/// all. That path publishes here like any other.
/// </para>
/// </remarks>
public sealed class VoiceMark
{
    /// <summary>What the microphone is doing right now.</summary>
    public MarkState State { get; private set; } = MarkState.Idle;

    /// <summary>
    /// A real microphone level in 0..1, or zero when nothing measures one.
    /// </summary>
    /// <remarks>
    /// ZERO IS THE HONEST DEFAULT, and BrandMark is built to accept it: the
    /// listening arcs breathe on their own and the level only widens the spread
    /// on top, so a head with no level says "I am listening" without also
    /// claiming to hear you. Never fabricate one to make the mark move.
    /// </remarks>
    public double Level { get; private set; }

    /// <summary>Whether a turn is under way, from wherever it was started.</summary>
    /// <remarks>
    /// The guard both buttons need. Each used to test its own field, so the two
    /// could start a second turn on top of the first - one microphone, two
    /// callers, and whichever lost the race reported the failure.
    /// </remarks>
    public bool Busy => State != MarkState.Idle;

    /// <summary>Raised whenever <see cref="State"/> or <see cref="Level"/> moves.</summary>
    /// <remarks>
    /// MAY ARRIVE ON ANY THREAD. A turn reports from wherever the audio loop
    /// runs, and the wake word reports from a background service, so a component
    /// handling this must marshal - <c>InvokeAsync(StateHasChanged)</c> - rather
    /// than touching its own state directly. Every subscriber here does.
    /// </remarks>
    public event Action? Changed;

    /// <summary>Say what the microphone is doing now.</summary>
    /// <remarks>
    /// Silent when nothing actually changed. A turn reports its level many times
    /// a second, and re-rendering both marks for a value identical to the one
    /// they already have is work with nothing to show for it.
    /// </remarks>
    public void Report(MarkState state, double level = 0)
    {
        // Comparing doubles exactly is right here rather than sloppy: this is a
        // "did the value I was handed differ from the one I stored" test, not a
        // question about how close two measurements are.
        if (state == State && level.Equals(Level)) return;

        State = state;
        Level = level;
        Changed?.Invoke();
    }

    /// <summary>Say what a turn just reported.</summary>
    /// <remarks>
    /// THE MAPPING LIVED IN BOTH BUTTONS, character for character. Two copies of
    /// one switch is two places to forget a phase, and the phases are the whole
    /// point of the mark - listening breathes, thinking travels, speaking fires -
    /// so a missed case draws the wrong thing rather than nothing, which is
    /// harder to notice. One turn, one translation of it.
    /// </remarks>
    public void Report(TurnState turn) => Report(
        turn.Phase switch
        {
            TurnPhase.Listening => MarkState.Listening,
            TurnPhase.Thinking => MarkState.Thinking,
            TurnPhase.Speaking => MarkState.Speaking,
            _ => MarkState.Idle,
        },
        turn.Level);

    /// <summary>Back to nothing happening.</summary>
    /// <remarks>
    /// Its own method because every caller ends the same way and the level has to
    /// go with it. A turn that finished while the arcs were still wide would
    /// leave them wide - idle drawn as though it were listening.
    /// </remarks>
    public void Clear() => Report(MarkState.Idle);
}
