// IShareTarget.cs
//
// Text another app sent us, waiting to be used.
//
// THE SCREEN PROMISED THIS AND THE APP COULD NOT DO IT. "Aim at a job" opens
// with "paste the advert, or share it here from WhatsApp" - and the hybrid's
// MainActivity declared no intent filter at all, so Circle AI never appeared in
// Android's share sheet. Sharing to it was impossible; only the pasting half of
// that sentence was true.
//
// It is not a small half. Job adverts in this market arrive as forwarded
// WhatsApp messages, and the native head has carried the share target since it
// was written precisely because the common case "must not require any typing" -
// its own words. Retyping an advert on a phone keyboard is how somebody decides
// not to bother.
//
// This is the seam that lets the shared UI use it without knowing what an
// Android Intent is: the head catches the share and parks the text, the screen
// takes it.

namespace CircleAI.Samples.It;

/// <summary>Text handed to this app by another one.</summary>
public interface IShareTarget
{
    /// <summary>Whether something is waiting.</summary>
    /// <remarks>
    /// PEEK, because the layout has to decide where to send somebody before the
    /// screen that consumes it exists. Taking it here would swallow the advert on
    /// the way to the screen that wanted it.
    /// </remarks>
    bool HasPending { get; }

    /// <summary>Take what is waiting, leaving nothing behind.</summary>
    /// <remarks>
    /// ONCE. A shared advert that survived being used would reappear the next
    /// time somebody opened the screen, over whatever they were doing.
    /// </remarks>
    string? Take();
}
