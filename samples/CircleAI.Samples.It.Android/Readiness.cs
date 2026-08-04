#nullable enable

// Readiness.cs
//
// What the assistant can do RIGHT NOW, said in words, and never as one flag.
//
// THE LANDING SCREEN NEVER TOLD YOU WHETHER IT WAS READY. It showed a circle, a
// tagline and three claims, and whether pressing anything would do something was
// a mystery you resolved by pressing and waiting. Measured on the P30 with every
// model already downloaded: THIRTY-FIVE SECONDS from launch to the first thing it
// would answer. Half a minute of a screen that looks finished and is not.
//
// READY IS NOT ONE THING, which is the whole fix. The parts have wildly different
// costs and were being treated as a single gate:
//
//   the voice        ~1s     a few MB of ONNX
//   the wake word    ~1s     three small graphs, 6.7 MB
//   the ears        ~3s      whisper tiny, 78 MB
//   the brain       ~30s     Qwen 1.5B, 433 MB — all of the wait, by itself
//
// Blocking on the slowest makes a phone that could have greeted you in a second
// sit mute for half a minute. So readiness is staged: it can HEAR and ANSWER
// ALOUD almost at once, and thinking arrives when it arrives — which is how a
// person experiences talking to anyone. You do not wait for someone to finish
// booting before you say their name.
//
// This is also why voice comes first. A text box is an implicit promise that an
// answer is a keystroke away; a voice that says "one moment" while it thinks is
// the oldest interface there is and nobody has to be taught it.

using System;

namespace CircleAI.Samples.It.Mobile;

/// <summary>How far along the assistant is, coarsely, for the person watching.</summary>
public enum ReadyStage
{
    /// <summary>Nothing usable yet.</summary>
    Waking,
    /// <summary>It can hear you and speak back, but cannot think yet.</summary>
    CanListen,
    /// <summary>Everything is up.</summary>
    Ready,
    /// <summary>Something needed is missing and will not arrive on its own.</summary>
    NeedsSetup,
}

/// <summary>Readiness as a person would describe it.</summary>
/// <param name="Stage">Coarse state, for choosing what to show.</param>
/// <param name="Headline">The big line. An instruction wherever possible.</param>
/// <param name="Caption">One quieter line under it, or empty.</param>
/// <param name="CanTalk">True when pressing the circle will do something.</param>
public readonly record struct Readiness(
    ReadyStage Stage, string Headline, string Caption, bool CanTalk)
{
    /// <summary>
    /// Turns the parts into one honest description.
    /// </summary>
    /// <remarks>
    /// EVERY LINE HERE IS AN INSTRUCTION OR A FACT, never a status word. "Ready",
    /// "Initialising", "Loading model" all describe the machine's inner life and
    /// leave the person to work out what to do about it. "Tap and talk" tells them
    /// what to do; "Getting ready — you can talk in a moment" tells them what is
    /// happening AND that waiting is the right move.
    /// <para>
    /// The wording is deliberately short and concrete, because it has to work for
    /// someone who is seven and someone who is eighty, on the first read, with no
    /// help available.
    /// </para>
    /// </remarks>
    /// <param name="wake">
    /// True when the wake word is present AND listening, so the phrase is a real
    /// instruction rather than an aspiration.
    /// </param>
    public static Readiness From(bool voice, bool ears, bool brain, bool anythingInstalled,
                                 bool wake = false)
    {
        if (!anythingInstalled)
            return new(ReadyStage.NeedsSetup,
                "Let's set it up",
                "It needs a few things first. Tap to start.",
                CanTalk: false);

        // SAY THE NAME, DON'T TAP. When the phone is genuinely listening, the
        // headline says so — telling someone to tap while a microphone is already
        // open teaches them the slower half of the interface and hides the half
        // that makes it worth building. The tap still works; it is just no longer
        // the thing being advertised.
        var lead = wake ? "Say “Hey B”" : "Tap and talk";

        // CAN TALK BEFORE IT CAN THINK — the point of the whole file. As soon as
        // it can hear and speak, pressing the circle does something useful, even
        // if the answer takes a few seconds longer while the brain finishes.
        if (voice && ears && !brain)
            return new(ReadyStage.CanListen,
                lead,
                "Still waking up — the first answer may take a moment.",
                CanTalk: true);

        if (voice && ears && brain)
            return new(ReadyStage.Ready, lead,
                wake ? "or tap the circle" : "", CanTalk: true);

        if (voice && !ears)
            return new(ReadyStage.Waking,
                "Getting ready",
                "You can talk to it in a moment.",
                CanTalk: false);

        return new(ReadyStage.Waking, "Getting ready", "", CanTalk: false);
    }
}
