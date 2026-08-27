using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CircleAI.Samples.It.App.Services;

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
//
// THE SHARE SHEET. Without this filter Circle AI does not appear when somebody
// shares text from WhatsApp - and "Aim at a job" opens by telling them to do
// exactly that. The native head has carried the same filter on JobSpecActivity
// since it was written, with the reason in its own comment: a forwarded advert
// is the common case and "must not require any typing". Retyping an advert on a
// phone keyboard is how somebody decides not to bother.
//
// SingleTop above is what makes the second half work: an app already open gets
// OnNewIntent rather than a fresh activity, so a share arriving mid-session
// lands in the same session instead of restarting it.
//
[IntentFilter(
    new[] { Intent.ActionSend },
    Categories = new[] { Intent.CategoryDefault },
    DataMimeType = "text/plain")]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Park(Intent);
    }

    /// <summary>A share that arrived while the app was already open.</summary>
    /// <remarks>
    /// SetIntent as well as parking it: without that, Intent still returns the
    /// one this activity was created with, and the next thing to read it gets a
    /// stale advert.
    /// </remarks>
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent is not null) Intent = intent;
        Park(intent);
    }

    /// <summary>
    /// Put shared text where the shared UI can reach it.
    /// </summary>
    /// <remarks>
    /// Parked rather than navigated to from here: routing belongs to the Blazor
    /// app, and an activity reaching into it would be a second router that can
    /// disagree with the first. MainLayout notices and goes.
    /// </remarks>
    static void Park(Intent? intent)
    {
        if (intent?.Action != Intent.ActionSend) return;
        if (intent.GetStringExtra(Intent.ExtraText) is { Length: > 0 } shared)
            AndroidShareTarget.Parked = shared;
    }
}
