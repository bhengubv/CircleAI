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
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
