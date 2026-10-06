using Android.App;
using Android.Content.PM;

namespace CircleAI.DeviceTests;

/// <summary>The entry activity for the device-test runner.</summary>
/// <remarks>
/// Deliberately bare. This app exists to host xUnit on the device; it carries none
/// of the sample head's lifecycle work, share-intent filter or memory-pressure
/// handling, because a test runner that evicted models on trim would be changing the
/// thing it is measuring.
/// </remarks>
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize
                         | ConfigChanges.Orientation
                         | ConfigChanges.UiMode
                         | ConfigChanges.ScreenLayout
                         | ConfigChanges.SmallestScreenSize
                         | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
}
