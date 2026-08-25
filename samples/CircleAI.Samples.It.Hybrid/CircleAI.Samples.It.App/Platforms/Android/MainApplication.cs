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

        // Where the phonemiser looks for Open JTalk's dictionary once it has been
        // downloaded. The model store, not the sideload folder: the catalogued
        // entry unpacks into the store, and a registry entry nothing can find is
        // decorative.
        CircleAI.Voice.OpenJTalkPhonemizer.ModelStoreFolder =
            CircleAI.Samples.It.App.Services.ModelStore.Path;

        // Managed voice logging reaches nothing through ILogger on Android, so it
        // goes to logcat directly - the one place it is actually readable.
        CircleAI.Voice.VoiceTrace.Sink = line => Android.Util.Log.Info("ITHYB", line);
    }

    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
