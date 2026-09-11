// IAnnounces.cs
//
// Keeping the person in the loop while the assistant acts on their phone.
//
// WHY THIS IS NOT A LUXURY. Every capability up to now opened one of this app's
// own screens, so "who did that" was never in question - the app was in front of
// you when it happened. A capability that acts on the PHONE breaks that: "play
// Coldplay" hands the request to whatever plays music here, that app comes to
// the front, and Circle AI is behind it with nothing on screen. What somebody
// sees is a music player opening by itself, which is indistinguishable from a
// bug, a mis-tap, or somebody else driving the phone.
//
// AND IT GETS WORSE AS THE CAPABILITIES GET BETTER. Playing music is the mildest
// thing on the roadmap. An assistant that puts an appointment in a calendar,
// sends somebody a text, or works down a list of tasks is taking actions that
// have consequences for OTHER PEOPLE, one after another, while the phone may be
// face down on a table. Told afterwards is not in the loop; told at the end of
// six steps is not in the loop either. Each step says what it is doing as it
// does it.
//
// ANNOUNCE. DO NOT CONFIRM. This is the answer to the obvious next question -
// "shouldn't it ASK before it texts somebody?" - and the answer is no. Somebody
// spoke the instruction; that IS the authorisation, and a confirmation step
// re-asks a question they have already answered. On a voice assistant it also
// doubles the turns for every useful action, and it fails the premise of the
// product three times over: hands free, phone across the room, screen owned by
// another app. Being in the loop is what this file does. It is not a prompt.
//
// ASKING FOR A MISSING DETAIL IS NOT CONFIRMING, and it stays. "Play music"
// names nothing and gets "Play what?" - that is a parameter the instruction did
// not carry. "Shall I play Coldplay?" after somebody said "play Coldplay" is
// re-asking, and is the thing this paragraph forbids.
//
// SPOKEN FIRST, BECAUSE THE SCREEN IS THE ONE THING YOU CANNOT COUNT ON. The
// premise of the whole product is that the phone is across the room or in a
// pocket. By the time a hand-off happens the screen belongs to another app
// anyway, so the voice is the channel that actually reaches somebody and the
// notification shade is what they find when they look later. A toast would
// satisfy neither: three seconds, behind whatever just launched.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Assistant;

/// <summary>Tells the person what the assistant is doing to their phone, as it does it.</summary>
public interface IAnnounces
{
    /// <summary>
    /// Say out loud, and leave on the shade, what is about to happen.
    /// </summary>
    /// <param name="what">
    /// One short sentence in the present tense, as somebody glancing up would
    /// want it — "Playing coldplay", "Adding Thursday at three to your calendar",
    /// "Texting Sipho". Never an id, an intent name or a stack trace.
    /// </param>
    /// <remarks>
    /// AWAITED, AND CALLED BEFORE THE STEP IT DESCRIBES. Two reasons it is a
    /// Task rather than fire-and-forget: the words should be out before the
    /// thing happens, and on a hand-off the app that launches may take the audio
    /// focus — so an announcement started too late is an announcement nobody
    /// hears.
    /// <para>
    /// ONE CALL PER STEP. A capability that does three things says three
    /// sentences. Summarising them into one at the end is the behaviour this
    /// interface exists to prevent.
    /// </para>
    /// </remarks>
    Task SayingAsync(string what, CancellationToken ct = default);

    /// <summary>Nothing is in progress any more; take the status down.</summary>
    /// <remarks>
    /// Best-effort on purpose. The shade expires an announcement on its own
    /// precisely because a caller can be cancelled or throw before reaching
    /// here, and a status claiming something the phone is not doing is worse
    /// than no status at all.
    /// </remarks>
    void Done();
}

/// <summary>
/// A head with nowhere to announce — the browser, and every test.
/// </summary>
/// <remarks>
/// A NULL OBJECT RATHER THAN A NULLABLE. Announcing sits on top of an action and
/// is never a condition of it, so calling code should not have to ask whether it
/// can. Doing nothing is the honest behaviour for a head with no voice and no
/// notification shade: nothing is claimed, because nothing can be shown.
/// </remarks>
public sealed class AnnouncesNothing : IAnnounces
{
    /// <inheritdoc />
    public Task SayingAsync(string what, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public void Done() { }
}
