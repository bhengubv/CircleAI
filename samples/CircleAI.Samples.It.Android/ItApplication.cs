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
/// read from this product was "Allow com.bhengubv.circleai to record audio?".
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

        // THE VOICE LAYER CAN SAY WHERE ITS TIME GOES, ONCE SOMETHING IS
        // LISTENING. It is a plain assembly with no Android reference and no
        // logger, so by default it reports into a null sink and every timing it
        // knows is thrown away — which is how transcription came to be a single
        // unattributed number measured from outside. Pointed at logcat under the
        // same tag as the turn itself, so one filter shows the whole chain in
        // order rather than half of it here and half somewhere else.
        CircleAI.Voice.VoiceTrace.Sink = line => Log.Info(Tag, line);

        // WHERE A VOICE COPIED ONTO THIS PHONE WOULD BE. The app's own external
        // files directory: readable with no storage permission, and writable over
        // a cable, which is what makes an int8 voice testable before it is
        // published. The importer still checks every byte against the hash the
        // catalogue publishes, so this is a delivery route, not a trust hole.
        CircleAI.Samples.It.Voice.ItSpeaker.SideloadFolder =
            GetExternalFilesDir(null)?.AbsolutePath;

        // WHERE OPEN JTALK'S DICTIONARY LIVES. Same directory, because it
        // arrives the same way — but it is NOT a voice: 104 MB of compiled
        // morphology (sys.dic alone is 100 MB) shared by every Japanese voice,
        // so it is registered once here rather than bundled into any one of
        // them. Without it the Japanese voice refuses to speak rather than
        // falling back to characters, which would be confident noise.
        CircleAI.Voice.OpenJTalkPhonemizer.DictionaryFolder =
            GetExternalFilesDir(null)?.AbsolutePath;

        // And where a DOWNLOADED one lands, which is a different place: the
        // catalogue entry (OpenJTalk-Dic-ja) unpacks into the model store, not
        // into the sideload folder.
        CircleAI.Voice.OpenJTalkPhonemizer.ModelStoreFolder = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "CircleAI", "Models");

        Log.Info(Tag, "process wiring installed");
    }
}
