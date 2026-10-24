// ModelReview.cs
//
// "IS THIS THE BEST MODEL GIVEN THE RESOURCES WE HAVE?" - asked again, on a clock.
//
// THE ANSWER WAS DECIDED ONCE AND THEN BELIEVED FOR EVER. Setup ran at first launch,
// chose what this phone could hold at that moment, and nothing asked again. But every
// term in the question moves:
//
//   the resources   free RAM is what the gate reads, and on a 3.6 GB phone it is a
//                   different number at nine in the morning and at midnight; storage
//                   is freed by the person deleting things, and by our own
//                   housekeeping;
//   the models      rows arrive from the device's own discovery, from a peer over
//                   the mesh, from a side-load - the catalogue is not a build
//                   artefact;
//   what is here    a download finishes, a file is corrupted, a model is reclaimed.
//
// Measured on the P30: it ran Qwen3-0.6B for a week while Qwen3.5-0.8B was missing,
// and the only thing that would have fetched the better one was somebody reinstalling
// the app. A phone that can only improve when a person reinstalls it is a phone that
// does not improve.
//
// WHEN, AND WHY IT IS NOT "WHENEVER". The review can SPEND somebody's data and fill
// their storage, so it waits for the moment that costs them nothing to notice: the
// phone idle, once an hour. Not on every launch (a download beginning as somebody
// opens the app is the app taking the phone over), not continuously (the question is
// worth asking as often as its answer can change, which is not every minute).
//
// This is the schedule and nothing else. Whether the device is idle is the head's to
// say - it needs the platform - and what to fetch is the plan's. Both of those have
// been wrong before in ways no test could see, which is why the part that can be
// tested lives here.

namespace CircleAI.Assistant;

/// <summary>When to ask again whether this device has the models it should.</summary>
public static class ModelReview
{
    /// <summary>How often the question is worth asking.</summary>
    /// <remarks>
    /// AN HOUR, the owner's number (2026-10-24). It is the scale the answer actually
    /// changes on: free RAM settles, a download finishes, a catalogue refresh lands.
    /// Faster buys nothing a person could see and spends battery walking the model
    /// store; slower and a phone that could have had a better brain at lunchtime does
    /// not get one until the evening.
    /// </remarks>
    public static readonly TimeSpan Every = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether to ask the question now.
    /// </summary>
    /// <param name="deviceIdle">
    /// True when the person is not using the phone. The head decides what that means;
    /// on Android it is the screen being off.
    /// </param>
    /// <param name="alreadyFetching">
    /// True while bytes are already moving. A second review would compute the same
    /// plan and start nothing, but it would walk the model store to find that out.
    /// </param>
    /// <param name="sinceLast">
    /// How long since the last review. <see cref="TimeSpan.MaxValue"/> for "never".
    /// </param>
    /// <remarks>
    /// THE CLOCK DOES NOT RUN WHILE THE PHONE IS IN USE, and that is deliberate. A
    /// review skipped because somebody was using their phone is not a review owed
    /// twice over; it is one that happens as soon as they put it down. So the timer
    /// may tick many times inside one hour and this still says no, and it says yes on
    /// the first idle tick after the hour has passed - whenever that lands.
    /// </remarks>
    public static bool Due(bool deviceIdle, bool alreadyFetching, TimeSpan sinceLast)
        => deviceIdle && !alreadyFetching && sinceLast >= Every;
}
