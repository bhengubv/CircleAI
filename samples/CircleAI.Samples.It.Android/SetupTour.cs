#nullable enable

// SetupTour.cs
//
// What a person does with the forty-five minutes.
//
// THE WAIT IS NOT ONE LENGTH AND THAT IS THE WHOLE DESIGN. The same bundle is a
// few minutes on a premium handset and most of an hour on a P30 Lite over
// 48 Mbps — measured on the device, and unchanged by opening eight sockets
// instead of one, so it is the link and it differs per person. A flow that
// assumes the fast phone insults the slow one; a flow that assumes the slow one
// wastes the fast one's time. So the tour is chosen from the ETA the download is
// already computing.
//
// NONE OF IT IS FILLER. Every step is something the person has to do anyway —
// choose a language, set a wake phrase, grant a microphone, learn what leaves
// the phone — and every step works with ONLY THE VOICE PRESENT, which is the
// first ~110 MB of a 22.8 GB fetch. That ordering is not luck: FirstRun fetches
// voice, then ears, then the wake word, then the brain, precisely so the phone
// becomes able to teach you about itself long before it can think.
//
// The alternative was a spinner and a percentage, which on the slow phone is
// forty-five minutes of a screen that looks broken. This is the same forty-five
// minutes spent arriving somewhere.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Samples.It.Mobile;

/// <summary>What a tour step needs before it can be shown.</summary>
[Flags]
public enum StepNeeds
{
    /// <summary>Nothing — can run the moment the app opens.</summary>
    Nothing = 0,
    /// <summary>A text-to-speech voice on disk.</summary>
    Voice = 1,
    /// <summary>A wake-word bundle on disk.</summary>
    WakeWord = 2,
}

/// <summary>One thing worth doing while the rest downloads.</summary>
/// <param name="Title">The step, in the words a person would use.</param>
/// <param name="Body">One line saying why it is worth doing now.</param>
/// <param name="Action">The button. Empty when the step is only to be read.</param>
/// <param name="Needs">What must already be on the phone for this to work.</param>
/// <param name="Seconds">
/// Roughly how long this takes someone. Used to fit the tour to the wait rather
/// than to pace it — a step nobody has time for is worse than no step.
/// </param>
public sealed record TourStep(
    string Title, string Body, string Action, StepNeeds Needs, int Seconds);

/// <summary>Chooses what to offer, given how long there is and what has landed.</summary>
public static class SetupTour
{
    /// <summary>
    /// Below this the tour is not offered at all.
    /// </summary>
    /// <remarks>
    /// On a fast connection the brain arrives before anybody has read the first
    /// card, and being walked through a tutorial you did not need — while the
    /// thing you came for sits ready behind it — is its own kind of rude. Under
    /// two minutes, show the bar and get out of the way.
    /// </remarks>
    public static readonly TimeSpan NotWorthIt = TimeSpan.FromMinutes(2);

    /// <summary>Everything worth doing, in the order it is worth doing it.</summary>
    /// <remarks>
    /// Ordered by what it buys the person, not by what is cheapest to show. The
    /// language comes first because it is the one choice that changes every
    /// screen and every sentence after it, and because hearing the phone speak
    /// your own language is the moment this stops being another chat app.
    /// </remarks>
    public static IReadOnlyList<TourStep> All { get; } = new[]
    {
        new TourStep(
            "Your language",
            "Hear it speak, and pick the one you want to be answered in.",
            "Choose a language", StepNeeds.Voice, Seconds: 90),

        new TourStep(
            "Let it hear you",
            "The microphone is only used when you talk to it. Nothing is recorded.",
            "Allow the microphone", StepNeeds.Nothing, Seconds: 20),

        new TourStep(
            "Say “Hey B”",
            "Wake it without touching the phone. Try it now and see it light up.",
            "Try the wake word", StepNeeds.WakeWord, Seconds: 60),

        // THE STEP THAT DECIDES WHETHER ANY OF THE REST SURVIVES. Huawei, Xiaomi,
        // Oppo and Vivo all kill foreground services on their own schedule, no
        // matter what Android says, and only the owner can exempt an app. Skip
        // this and the assistant goes deaf an hour later for reasons nobody can
        // see — the phone will not say it did it.
        new TourStep(
            "Keep it awake",
            "This phone stops apps in the background to save battery. Allow Circle AI "
          + "to keep running, or it will stop listening when you put it down.",
            "Allow it to keep running", StepNeeds.Nothing, Seconds: 45),

        // THE ONE WORTH AN HOUR, and the reason the tour exists at all. Everything
        // above is setup; this is the product doing something for somebody while
        // it is still downloading itself. It needs the voice (to ask out loud) and
        // nothing else — no brain — so it can start about two minutes in and run
        // for as long as the person wants.
        //
        // Given a long budget it is offered early, because a CV is worth more than
        // a wake-word demonstration to somebody who needs work.
        new TourStep(
            "Build your CV while you wait",
            "Answer some questions and watch your CV write itself. You can send it to "
          + "an employer before this download even finishes.",
            "Start my CV", StepNeeds.Voice, Seconds: 900),

        new TourStep(
            "What leaves the phone",
            "The conversation, your memory and your identity stay here. Only a web "
          + "search you ask for ever goes out.",
            "", StepNeeds.Nothing, Seconds: 30),

        new TourStep(
            "Things to ask it",
            "“How do I get to the clinic?”  “Write me a CV.”  "
          + "“What is this photo?”  It answers out loud.",
            "", StepNeeds.Voice, Seconds: 40),
    };

    /// <summary>
    /// The steps worth offering right now.
    /// </summary>
    /// <remarks>
    /// Filtered by what has actually landed, so nothing offers to demonstrate a
    /// capability the phone cannot yet perform — a wake-word card on a device
    /// with no wake bundle is a promise that fails the moment it is pressed.
    /// <para>
    /// Then trimmed to the time available. The budget is deliberately generous
    /// against the ETA rather than exact: somebody who finishes early gets the
    /// bar back, which is fine, whereas somebody cut off mid-step has been
    /// interrupted by their own phone.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TourStep> For(TimeSpan remaining, bool voice, bool wake)
    {
        if (remaining < NotWorthIt) return Array.Empty<TourStep>();

        var have = StepNeeds.Nothing;
        if (voice) have |= StepNeeds.Voice;
        if (wake)  have |= StepNeeds.WakeWord;

        var budget = remaining.TotalSeconds;
        var picked = new List<TourStep>();

        foreach (var s in All)
        {
            if ((s.Needs & ~have) != 0) continue;     // needs something not here yet
            if (s.Seconds > budget) break;
            picked.Add(s);
            budget -= s.Seconds;
        }

        return picked;
    }
}
