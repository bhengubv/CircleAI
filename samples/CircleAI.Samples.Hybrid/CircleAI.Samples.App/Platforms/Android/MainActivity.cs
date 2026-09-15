using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using CircleAI.Assistant.Device;

namespace CircleAI.Samples.App;

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

#if DEBUG
        // DEBUG ONLY, AND OFF THE UI THREAD. Everything about the memory's
        // design assumes a handset - SQLite because it is the only option on a
        // phone, FTS5 with a LIKE floor because a build flag is not something
        // to assume here - and none of it had ever run on one. This says what
        // is actually true on this device. `adb logcat -s CircleMemory`.
        MemoryProbe.Start(FilesDir?.AbsolutePath ?? CacheDir!.AbsolutePath);
#endif
    }

    /// <summary>
    /// The app came back to the front, so anything that read the device once is
    /// now possibly wrong.
    /// </summary>
    /// <remarks>
    /// THIS ACTIVITY HAD NO LIFECYCLE HOOK AT ALL - OnCreate and OnNewIntent and
    /// nothing else - so a page that computed readiness in OnInitializedAsync
    /// kept that answer forever. Grant the microphone from the bar and come back
    /// and the headline still says it needs granting; finish a download in
    /// Settings and Home still says "Let's set it up".
    ///
    /// The other head re-ran its readiness check on every OnResume and again
    /// after a permission result, and said so in its own comment: without it
    /// "the person granted permission and then had to work out for themselves
    /// that the screen would only change if they left it and came back".
    /// </remarks>
    /// <summary>The app went off screen.</summary>
    /// <remarks>
    /// THE MICROPHONE IS THE WHOLE REASON. On a phone whose vendor kills the
    /// foreground service the wake word falls back to a loop that runs in this
    /// process, and a loop nobody stops is a microphone left open behind the
    /// next app somebody opens.
    /// </remarks>
    protected override void OnPause()
    {
        base.OnPause();
        CircleAI.Assistant.AppLifecycle.RaisePaused();
    }

    protected override void OnResume()
    {
        base.OnResume();
        CircleAI.Assistant.AppLifecycle.RaiseResumed();
    }

    /// <summary>
    /// The OS is short of memory and would rather this process gave some back.
    /// </summary>
    /// <remarks>
    /// A BROWNOUT BEATS BEING KILLED. Answering this evicts the admitted
    /// specialist and keeps the warm generalist serving, so the assistant gets
    /// worse at one thing instead of vanishing mid-sentence - which is what the
    /// low-memory killer does instead, on the 1.4 GB handsets this is for.
    ///
    /// The other head has answered onTrimMemory since it was written. This one
    /// ignored it entirely.
    /// </remarks>
    public override void OnTrimMemory([Android.Runtime.GeneratedEnum] TrimMemory level)
    {
        base.OnTrimMemory(level);

        if (level is TrimMemory.RunningLow or TrimMemory.RunningCritical
                  or TrimMemory.Complete or TrimMemory.Background)
        {
            Android.Util.Log.Info("CircleAI.Mem",
                $"OS memory pressure ({level}) - evicting the specialist, keeping the generalist");
            CircleAI.Assistant.AppLifecycle.RaiseMemoryIsShort();

            // AND SHED THE DISPOSABLE CACHE. The eviction above frees RAM; this
            // frees the regenerable scratch on disk - the same brownout, one tier
            // down, and free to rebuild. The worse the pressure, the more we let
            // go: a severe signal clears all of it, a milder one only what the
            // person's own keep/cap would (aged-out and over-cap). Never the models
            // or the person's memory - TrimCache and ReclaimCaches touch the cache
            // alone.
            var severe = level is TrimMemory.RunningCritical or TrimMemory.Complete;

            // Read the keep/cap on THIS (main) thread: the app store's single SQLite
            // connection is not safe to touch from the worker below while the UI may
            // be using it, so the values are in hand before the disk work goes
            // off-thread. Defaults stand if the store is not resolvable.
            var keep = CircleAI.Assistant.CacheEviction.KeepDefault;
            var cap = CircleAI.Assistant.CacheEviction.MaxCacheDefaultBytes;
            if (!severe)
            {
                var store = Microsoft.Maui.IPlatformApplication.Current?.Services?
                    .GetService(typeof(CircleAI.Assistant.Device.SqliteAppStore))
                    as CircleAI.Assistant.Device.SqliteAppStore;
                if (store is not null)
                    (keep, cap) = CircleAI.Assistant.Device.CachePolicySettings.Resolve(store);
            }

            // Off the main thread, because this callback runs on it and the work is
            // disk I/O.
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var manager = new CircleAI.Assistant.Device.MemoryManager(
                        new CircleAI.Assistant.Device.DeviceResourcesReader());
                    var freed = severe ? manager.ReclaimCaches() : manager.TrimCache(cap, keep);
                    if (freed > 0)
                        Android.Util.Log.Info("CircleAI.Mem",
                            $"pressure cache tidy ({level}): freed {CircleAI.Assistant.MemoryBudget.Human(freed)}");
                }
                catch (System.Exception ex)
                {
                    Android.Util.Log.Warn("CircleAI.Mem", "pressure cache tidy failed: " + ex.Message);
                }
            });
        }
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
