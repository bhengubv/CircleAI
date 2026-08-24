// MarkState.cs
//
// Which part of the exchange the mark is showing.
//
// Shared, because the mark is now drawn in two languages - C# on a Canvas in the
// native Android head, SVG plus CSS in the Razor component - and both need the
// same vocabulary. A head that reports "Thinking" must mean the same thing in
// either.
//
// THE STATES ARE TOLD APART BY MOTION, NOT BRIGHTNESS. That was learned the hard
// way in the native mark: they were once distinguished by alpha alone, and
// Speaking was not animated at all, so "I am hearing you", "I am thinking" and
// "I am answering" all looked like the same glowing circle. The one moment a
// person most needs feedback - the long wait - was the least legible.

namespace CircleAI.Samples.It;

/// <summary>What the brand mark is currently saying without words.</summary>
public enum MarkState
{
    /// <summary>Nothing happening. Static, and costs no frames.</summary>
    Idle,

    /// <summary>
    /// Hearing you. The arcs SCALE with the microphone level - reactive, because
    /// you are the one moving it.
    /// </summary>
    Listening,

    /// <summary>
    /// Working. A bright band TRAVELS outward through the arcs, repeating:
    /// directional, and making no claim about how much is left.
    /// </summary>
    Thinking,

    /// <summary>
    /// Answering. The arcs FIRE in sequence from the inside out - sound leaving
    /// the device, the mirror image of Listening pulling it in.
    /// </summary>
    Speaking,
}
