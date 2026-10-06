// AppLifecycle.cs
//
// The moments an Android activity knows about and a Razor page does not.
//
// A PAGE COMPUTES ITS STATE ONCE AND NEVER AGAIN. Home reads readiness in
// OnInitializedAsync; grant the microphone from somewhere else and come back,
// and the headline is still the one from before you granted it. The other head
// re-checked on every OnResume and again after a permission result, and the head
// that ships had no lifecycle hook at all - its activity is OnCreate and
// OnNewIntent and nothing else.
//
// WHY AN EVENT RATHER THAN A POLL. "Ask again every few seconds" spends battery
// on a question whose answer changes twice a day, and a page that polls is a
// page that still shows stale state for however long the interval is. The
// platform already knows the exact moment; it just had nowhere to say it.
//
// WHY STATIC. The activity is constructed by Android, not by the container - it
// cannot be given a dependency - and the pages are constructed by Blazor. A
// static is the one thing both can reach. It matches CircleAISession.Remembers,
// CircleAISpeaker.SideloadFolder and AndroidShareTarget.Parked, which solved the
// same problem the same way.
//
// BCL ONLY, because this assembly is loaded by a browser. A head that has no
// lifecycle simply never raises anything, and every subscriber is correct by
// doing nothing.

namespace CircleAI.Assistant;

/// <summary>What the host platform noticed, for the UI that cannot see it.</summary>
public static class AppLifecycle
{
    /// <summary>
    /// The app came back to the front.
    /// </summary>
    /// <remarks>
    /// A PERMISSION MAY HAVE BEEN GRANTED, a model may have finished downloading
    /// in another screen, the owner may have turned the microphone off in system
    /// settings. Anything that read the device once is now possibly wrong.
    /// </remarks>
    public static event Action? Resumed;

    /// <summary>
    /// The OS is short of memory and this process is a candidate.
    /// </summary>
    /// <remarks>
    /// THE ALTERNATIVE IS THE LOW-MEMORY KILLER TAKING THE WHOLE PROCESS.
    /// Answering it by evicting the specialist and keeping the generalist is a
    /// brownout: the assistant gets worse at one thing instead of disappearing
    /// mid-sentence.
    /// </remarks>
    public static event Action? MemoryIsShort;

    /// <summary>Raised by the host when the app returns to the foreground.</summary>
    public static void RaiseResumed() => Raise(Resumed);

    /// <summary>The app went off screen.</summary>
    /// <remarks>
    /// ONLY ONE THING NEEDS THIS AND IT IS THE MICROPHONE. The screen-up wake
    /// fallback holds the mic for as long as somebody is looking at the app; a
    /// Resumed with no Paused to answer it would leave that open behind whatever
    /// they opened next, which is the exact promise about privacy this product
    /// is making.
    /// </remarks>
    public static event Action? Paused;

    /// <summary>Tell everything the app went off screen.</summary>
    public static void RaisePaused() => Raise(Paused);

    /// <summary>Raised by the host when the OS reports memory pressure.</summary>
    public static void RaiseMemoryIsShort() => Raise(MemoryIsShort);

    /// <summary>
    /// Call every handler, and let none of them stop the others.
    /// </summary>
    /// <remarks>
    /// ONE PAGE THROWING MUST NOT COST THE REST THEIR NOTIFICATION. These fire
    /// from a platform callback - `OnResume`, `OnTrimMemory` - where an escaping
    /// exception is an ANR or a crash rather than a handled error, and the
    /// subscriber that threw is usually the least important one.
    /// </remarks>
    private static void Raise(Action? handlers)
    {
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try { handler(); }
            catch { /* a stale headline is not worth an ANR */ }
        }
    }
}
