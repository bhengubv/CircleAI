// ModelFetchService.cs
//
// THE DOWNLOAD WAS BEING FROZEN, AND NO AMOUNT OF RETRY LOGIC COULD HAVE HELPED.
//
// Measured on a Circle OS device (Android 16), 2026-10-04. The 21.3 GB weight file
// of Qwen3.6-35B-A3B climbed to 13 666 174 991 bytes and then stopped dead, three
// separate times. Every time: process alive, sockets silent, marker untouched, and
// not one line in the log. ActivityManager said why:
//
//   curProcState=15   cached=true   Proc #4: cch  +20 b/ /LAST
//   mScreenOn=true  mCharging=true  mState=ACTIVE        (not doze, not idle)
//
// The resume runs in the service process, whose only lifeline is the app's binding.
// The moment the app is not in front, that process goes CACHED, and Android's
// freezer suspends its threads. A frozen thread cannot return from a socket read -
// and cannot run the CancellationTokenSource.CancelAfter timer that the stall guard
// depends on either. That is why the guard never fired: the code that was supposed
// to notice the stall was itself frozen.
//
// A multi-gigabyte download needs the process held up for the duration, which on
// Android means a foreground service. This is it, and it does nothing else.
//
// WHY IT IS ITS OWN SERVICE AND NOT A FLAG ON CircleNeuronService. That service
// claims TypeMicrophone ALONE while listening, deliberately: from Android 14 the
// combination with dataSync is what made it fail to go foreground at all, and
// unpicking that cost a day. Adding dataSync back for the sake of downloads would
// reintroduce exactly that. A separate service claims dataSync alone, lives only
// while bytes are moving, and cannot affect the resident one.
//
// AND IT MUST SURVIVE BEING REFUSED. dataSync is the one foreground type Android 14+
// puts on a daily time budget, and a spent budget means startForeground THROWS and
// the service never starts - already hit on this device. So going foreground is
// attempted and not required: refused, it carries on unprotected, which is exactly
// what happened before this existed. Worse than foreground, better than nothing,
// and the bytes are resumable either way.

using Android.App;
using Android.Content;
using Android.OS;
using CircleAI.Inference;


namespace CircleAI.Device;


[Service(
    Name                  = "ai.circle.ModelFetchService",
    Exported              = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class ModelFetchService : Service
{
    private const string LogTag      = "CircleAI.Fetch";
    private const string ChannelId   = "circleai.fetch";
    private const string ChannelName = "Setting up";
    private const int    NotifyId    = 0x0C1F;

    /// <summary>Where the models live. Set before <see cref="Start"/>.</summary>
    public static string? StorageDirectory { get; set; }

    private static readonly object Gate = new();
    private static bool _busy;

    private CancellationTokenSource? _cancel;

    /// <summary>
    /// Start fetching, if anything is unfinished and nothing is already fetching.
    /// </summary>
    /// <remarks>
    /// THE CHECK HAPPENS BEFORE THE NOTIFICATION, so a device with nothing owing
    /// never shows one. A person whose models are all down should see no sign this
    /// exists - the notification is the disclosure for work actually being done, and
    /// a permanent one for no work is the kind of thing people turn off.
    /// </remarks>
    public static void StartIfAnythingIsOwed(Context context, BundleModelLoader loader)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(loader);

        if (ModelSetup.Unfinished(loader).Count == 0) return;

        lock (Gate) { if (_busy) return; }

        var app = context.ApplicationContext ?? context;
        var intent = new Intent(app, typeof(ModelFetchService));
        try
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O) app.StartForegroundService(intent);
            else                                            app.StartService(intent);
        }
        catch (Exception ex)
        {
            // A background start can be refused outright. The resume still happens
            // on the next launch; it is only unprotected until then.
            global::Android.Util.Log.Warn(LogTag, "could not start: " + ex.Message);
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        lock (Gate)
        {
            if (_busy) return StartCommandResult.NotSticky;
            _busy = true;
        }

        // ATTEMPTED, NOT REQUIRED. See the header: dataSync is budgeted, and a spent
        // budget throws here. Without the foreground state the process is freezable
        // again - which is the bug - but the alternative is not downloading at all.
        var protectedRun = GoForeground("Setting up Circle AI…");
        if (!protectedRun)
            global::Android.Util.Log.Warn(LogTag,
                "running without foreground protection; Android may freeze this mid-download");

        _cancel = new CancellationTokenSource();
        var ct = _cancel.Token;
        var root = StorageDirectory;

        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    global::Android.Util.Log.Warn(LogTag, "no storage directory; nothing to do");
                    return;
                }

                using var loader = new BundleModelLoader(root, new Core.Models.ModelRegistryService());
                await ModelSetup.ResumeUnfinishedAsync(loader, Say, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(LogTag, "fetch failed: " + ex.Message);
            }
            finally
            {
                lock (Gate) _busy = false;
                try { StopForeground(StopForegroundFlags.Remove); } catch { }
                try { StopSelf(); } catch { }
            }
        }, ct);

        // NOT sticky: a restart with a null intent would start a second fetch, and
        // two gigabyte downloads at once is the state this whole effort exists to
        // clear rather than create. The next app launch starts it again.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        try { _cancel?.Cancel(); } catch { }
        _cancel?.Dispose();
        _cancel = null;
        lock (Gate) _busy = false;
        base.OnDestroy();
    }

    /// <summary>Report a line to the log AND to the shade, which is the honest surface.</summary>
    private void Say(string line)
    {
        global::Android.Util.Log.Info(LogTag, line);

        // "carrying on with X: 1234 of 5678 bytes already here" is the log's phrasing,
        // not a person's. The shade gets the short version.
        if (line.StartsWith("carrying on with ", StringComparison.Ordinal))
        {
            var rest = line["carrying on with ".Length..];
            var name = rest.Split(':')[0];
            Notify($"Setting up {name}…");
        }
        else if (line.StartsWith("finished ", StringComparison.Ordinal))
        {
            Notify("Finishing up…");
        }
    }

    private bool GoForeground(string text)
    {
        try
        {
            EnsureChannel();
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotifyId, Build_(text),
                    global::Android.Content.PM.ForegroundService.TypeDataSync);
            else
                StartForeground(NotifyId, Build_(text));
            return true;
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(LogTag, "startForeground refused: " + ex.Message);
            return false;
        }
    }

    private void Notify(string text)
    {
        try
        {
            if (GetSystemService(NotificationService) is NotificationManager nm)
                nm.Notify(NotifyId, Build_(text));
        }
        catch { /* the shade is a courtesy */ }
    }

    private Notification Build_(string text) =>
        new Notification.Builder(this, ChannelId)
            .SetContentTitle("Circle AI")
            .SetContentText(text)
            .SetSmallIcon(global::Android.Resource.Drawable.StatSysDownload)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .Build();

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        if (GetSystemService(NotificationService) is not NotificationManager nm) return;
        if (nm.GetNotificationChannel(ChannelId) is not null) return;

        // Low importance: this is the disclosure for a download, not news.
        nm.CreateNotificationChannel(
            new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
            {
                Description = "Shown while Circle AI finishes downloading a model.",
            });
    }
}
