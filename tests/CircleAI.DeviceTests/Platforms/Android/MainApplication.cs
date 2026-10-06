using Android.App;
using Android.Runtime;

namespace CircleAI.DeviceTests;

/// <summary>The Android application object for the device-test runner.</summary>
/// <remarks>
/// [Application] IS LOAD-BEARING and leaving it off is silent — the same trap the
/// sample head documents. Without it the manifest names no Application class,
/// MauiApplication.CreateMauiApp is never called, MAUI never initialises, and the
/// activity draws an empty window with no exception and no logcat entry. For a test
/// runner that failure reads as "zero tests", which is indistinguishable from
/// everything passing.
/// </remarks>
[Application]
public class MainApplication : MauiApplication
{
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
