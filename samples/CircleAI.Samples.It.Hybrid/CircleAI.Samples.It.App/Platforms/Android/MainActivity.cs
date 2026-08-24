using Android.App;
using Android.Content.PM;
using Android.OS;

namespace CircleAI.Samples.It.App;

/// <summary>The Android entry activity.</summary>
/// <remarks>
/// ConfigChanges are listed so a rotation, a font-size change or a keyboard
/// appearing does not tear the activity down and reload the web view - which
/// would drop whatever the person was doing and re-run the splash.
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
