using Android.App;
using Android.Runtime;

namespace CircleAI.Samples.It.App;

/// <summary>The Android application object.</summary>
/// <remarks>
/// [Application] IS LOad-BEARING, and leaving it off is silent. Without it the
/// manifest names no Application class, Android instantiates the default one,
/// MauiApplication.CreateMauiApp is never called, and MAUI never initialises.
/// The activity still starts and still draws - a blank window whose
/// android:id/content has no child at all. No exception, no logcat entry, no
/// WebView: indistinguishable from a Blazor component that failed to render,
/// which is where two hours went looking.
/// </remarks>
[Application]
public class MainApplication : MauiApplication
{
    /// <summary>Runtime constructor.</summary>
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    /// <inheritdoc />
    public override void OnCreate()
    {
        base.OnCreate();

        // TEACHES DeviceProbe TO READ THE PHONE'S REAL RAM, and without it the app
        // quietly decides it is hardware that cannot run anything.
        //
        // DeviceProbe falls back to the GC HEAP LIMIT - a few hundred MB where the
        // phone has 4 GB - so every model fails its own fit check and every ability
        // reads "Needs more memory". Nothing throws. It happened here exactly as it
        // happened in the native head, which is why that head installs it from
        // Application.OnCreate rather than from whichever screen opens first.
        CircleAI.Device.AndroidDeviceMemory.Install(this);

        // AND THE PHONEMIZER, or this app can hear and translate but never
        // speak. The native head has always called this; the hybrid never did,
        // so ItSpeaker.MobilePhonemizerFactory stayed null and every voice that
        // needs espeak G2P - all of them but Japanese - refused with "on-device
        // phonemizer not wired". Same call, same file, same order as
        // ItApplication.OnCreate.
        CircleAI.Samples.It.Mobile.VoiceWiring.Install(this);

        // Where the phonemiser looks for Open JTalk's dictionary once it has been
        // downloaded. The model store, not the sideload folder: the catalogued
        // entry unpacks into the store, and a registry entry nothing can find is
        // decorative.
        CircleAI.Voice.OpenJTalkPhonemizer.ModelStoreFolder =
            CircleAI.Samples.It.App.Services.ModelStore.Path;

        // Managed voice logging reaches nothing through ILogger on Android, so it
        // goes to logcat directly - the one place it is actually readable.
        CircleAI.Voice.VoiceTrace.Sink = line => Android.Util.Log.Info("ITHYB", line);

        // And what the voice router heard, and where it sent it. Its own tag so
        // a session's routing decisions are one grep, and it logs the MISSES too:
        // the matcher is tuned against typed guesses until real Whisper output
        // is written down somewhere.
        CircleAI.Samples.It.Shared.VoiceTurnRouter.Trace =
            line => Android.Util.Log.Info("CircleAI.Route", line);

        // AND NOW SAY WHAT IS ACTUALLY WIRED. Last, so the trace sink above is
        // already attached and every hook reports through the same channel.
        //
        // THE MUTE BUILD PRODUCED NO LINE ANYWHERE saying the phonemizer was
        // missing - it had to be inferred from a translation that never spoke,
        // days later. Five lines at startup make the next missing wire a grep
        // instead of a day.
        CircleAI.Samples.It.App.Services.DeviceWiringProbe.LogHooks();

        // And a full voice sweep, only when somebody has asked for one. Not
        // awaited: it takes minutes, and startup is not allowed to wait on a
        // diagnostic.
        _ = CircleAI.Samples.It.App.Services.DeviceWiringProbe.SweepVoicesIfRequestedAsync();
    }

    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
