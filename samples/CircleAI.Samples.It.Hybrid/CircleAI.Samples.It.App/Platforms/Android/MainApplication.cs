using Android.App;
using Android.Runtime;

namespace CircleAI.Samples.It.App;

/// <summary>The Android application object.</summary>
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
