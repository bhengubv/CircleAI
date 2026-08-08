#nullable enable

// ItApplication.cs
//
// The process wiring, at the one point that cannot be skipped.
//
// TWO PROCESS-WIDE STATICS HAD TO BE SET BEFORE ANYTHING USEFUL HAPPENED, AND BOTH
// WERE SET FROM AN ACTIVITY. Whichever screen the phone happened to open first
// decided whether the app worked:
//
//   AndroidDeviceMemory.Install  — teaches DeviceProbe to read the phone's real
//                                  RAM. Called from MainActivity only.
//   VoiceWiring.Install          — the phonemizer factory. Called from MainActivity,
//                                  then from HomeActivity after it broke.
//
// MainActivity is the TEXT screen. The launcher is HomeActivity. So on a normal
// run — tap the icon, use the circle — AndroidDeviceMemory.Install had never run,
// and DeviceProbe fell back to reading the GC heap limit: about 100 MB where the
// phone has 4 GB. Every model then failed its own fit check, so the device looked
// to itself like hardware that could not run anything at all. Nothing threw. It
// simply decided, quietly and wrongly, that nothing fits.
//
// AndroidDeviceMemory's own documentation says to call it "in Application.OnCreate
// or the launcher activity's OnCreate", and neither was being done.
//
// VoiceWiring.cs already tells this story once, about the phonemizer, and ends with
// "a static that must be set before use, from whichever entry point happens to run
// first, is a rule no one can keep by remembering". That was right, and the fix it
// chose — call it from both activities — only moved the rule. The second static
// proves it: the same mistake reappeared in a different place within weeks.
//
// So the rule is not remembered any more. Application.OnCreate runs before any
// activity, service, receiver or provider in the process, on every entry path
// there is: launcher icon, wake word, notification, share sheet, adb. There is no
// route into this app that skips it.

using Android.App;
using Android.Runtime;
using Android.Util;

namespace CircleAI.Samples.It.Mobile;

/// <summary>Process-wide setup, before any screen exists.</summary>
/// <remarks>
/// THE LABEL IS NOT DECORATION. Android takes the APPLICATION label — not the
/// activity's — for the permission sheet, so the first sentence a new person ever
/// read from this product was "Allow com.bhengubv.itsample to record audio?".
/// Being asked for your microphone by a package name is how an app looks like
/// something that got onto your phone rather than something you chose.
/// </remarks>
[Application(Label = "Circle AI")]
public class ItApplication : Application
{
    const string Tag = "CircleAI.It";

    protected ItApplication(nint handle, JniHandleOwnership transfer)
        : base(handle, transfer) { }

    /// <inheritdoc/>
    public override void OnCreate()
    {
        base.OnCreate();

        // FIRST, because the selector is asked "does this fit?" by the home screen
        // within a second of launch, and an unwired probe answers "nothing fits".
        CircleAI.Device.AndroidDeviceMemory.Install(this);

#if IT_VOICE_ANDROID
        VoiceWiring.Install(this);
#endif

        Log.Info(Tag, "process wiring installed");
    }
}
