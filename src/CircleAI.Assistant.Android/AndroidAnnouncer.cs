// AndroidAnnouncer.cs
//
// The phone's half of IAnnounces: said out loud, and left on the shade.
//
// TWO CHANNELS, BECAUSE THEY FAIL AT DIFFERENT TIMES.
//
//   THE VOICE reaches somebody who is not looking - which is the premise of the
//   whole product, and is exactly the case when this matters most. It is also
//   the only channel that still works once another app owns the screen.
//
//   THE SHADE is what they find afterwards. A voice announcement is gone the
//   moment it finishes; somebody who was out of the room when their phone texted
//   a colleague needs to be able to see that it did.
//
// Deliberately NOT a toast: three seconds, and it renders behind the activity
// that just launched - so it would fail in precisely the case it was added for.
//
// THE MICROPHONE DISCLOSURE IS NEVER COVERED. CircleNeuronService.Announce puts
// this in the notification TITLE and leaves the content line alone, because that
// line is how this app discloses that it is holding the microphone.

using CircleAI.Device;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class AndroidAnnouncer : IAnnounces
{
    private readonly IVoiceHost _voice;
    private readonly ISpokenLanguage _spoken;

    public AndroidAnnouncer(IVoiceHost voice, ISpokenLanguage spoken)
    {
        _voice = voice;
        _spoken = spoken;
    }

    /// <inheritdoc />
    public async Task SayingAsync(string what, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(what)) return;

        // THE SHADE FIRST, BECAUSE IT CANNOT FAIL SLOWLY. Writing a notification
        // is immediate; synthesising a sentence is not, and if the voice is
        // missing or the audio device is busy the shade has still been told.
        try { CircleNeuronService.Announce(what); }
        catch { /* a notification is a courtesy, never a precondition */ }

        try
        {
            // IN THE LANGUAGE THE PERSON IS USING, like every other spoken line.
            // An assistant that answers in isiZulu and then announces what it is
            // about to do in English has changed who it is mid-sentence.
            await _voice.SayAsync(_spoken.Current, what, ct: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A voice that will not play is a reason to be quiet, never a reason
            // to abandon the thing somebody asked for. The shade still says it.
            Android.Util.Log.Warn("CircleAI.Turn", "announce could not speak: " + ex.Message);
        }
    }

    /// <inheritdoc />
    public void Done()
    {
        try { CircleNeuronService.Announce(null); }
        catch { /* the shade expires it on its own regardless */ }
    }
}
