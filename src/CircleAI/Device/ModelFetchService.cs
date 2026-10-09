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

        // OWED NOW MEANS "MISSING", NOT ONLY "HALF DOWN". Unfinished() reads
        // loader.InstalledIds(), which lists folders that EXIST - so a phone with
        // nothing downloaded has no ids, owes nothing, and never starts. That is
        // exactly the P30 on 2026-10-09: "setup: no engine components on this
        // device", four abilities reading "Nothing for this yet", and a fetch
        // service that correctly concluded there was nothing to resume.
        //
        // The check stays BEFORE the notification, which is the part of the
        // original rule still worth keeping: a device with everything down shows
        // no sign this exists.
        if (ModelSetup.Unfinished(loader).Count > 0) { Launch(context); return; }

        // Nothing partial. Ask whether anything is missing entirely - the
        // first-run plan, the same one the setup screen would have shown.
        try
        {
            var registry = new Core.Models.ModelRegistryService();
            var plan = CircleAI.Assistant.FirstRun
                .Plan(registry, loader, CircleAI.Core.DeviceProbe.Snapshot(), speech: true)
                .Select(s => (Name: s.Model.Name, Bytes: s.Model.TotalBytes));

            if (ModelSetup.NotStarted(loader, plan).Count == 0) return;
        }
        catch (Exception ex)
        {
            // Could not tell. Do NOT start on a guess: this spends somebody's
            // data, and the one thing worse than a missed fetch is an unasked-for
            // one begun because a plan would not compute.
            global::Android.Util.Log.Warn(LogTag, "cannot tell what is missing, not starting: " + ex.Message);
            return;
        }

        Launch(context);
    }

    /// <summary>Starts the foreground fetch service.</summary>
    private static void Launch(Context context)
    {

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
            // STICKY HERE TOO, AND THIS IS NOT COSMETIC. Android keeps the LAST
            // value OnStartCommand returned as the service's restart mode, so an
            // ordinary "already fetching, ignore this one" return of NotSticky
            // would quietly cancel the Sticky set by the start that is actually
            // downloading - and the kill it is meant to survive would then end
            // the download for good. The bug would only ever show up on a phone
            // that got a second start AND was later killed, which is the P30.
            if (_busy) return StartCommandResult.Sticky;
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

                var registry = new Core.Models.ModelRegistryService();
                using var loader = new BundleModelLoader(root, registry);

                // FINISH WHAT IS HALF DOWN FIRST. Bytes already on disk are the
                // nearest thing to a completed promise, and leaving them to start
                // something new is how a device ends up with six half-models.
                await ModelSetup.ResumeUnfinishedAsync(loader, Say, ct).ConfigureAwait(false);

                // THEN START WHAT WAS NEVER STARTED - the change the owner asked
                // for on 2026-10-09. Every ability is meant to be on from first
                // run, and one reading "Nothing for this yet" until somebody finds
                // the Start button is not on by anything.
                //
                // The plan is the same one the setup screen shows, computed the
                // same way, so this fetches exactly what that screen would have
                // offered - including the fit rules that keep a 22.8 GB model off
                // a phone that cannot hold it. What is gone is the tap, not the
                // judgement.
                //
                // Smallest first inside FetchNotStartedAsync, so the wake word, a
                // voice and the ears land in the first few minutes and the phone
                // can hear its name long before the 2.8 GB brain arrives.
                try
                {
                    var plan = CircleAI.Assistant.FirstRun
                        .Plan(registry, loader, CircleAI.Core.DeviceProbe.Snapshot(), speech: true)
                        .Select(s => (Name: s.Model.Name, Bytes: s.Model.TotalBytes));

                    await ModelSetup.FetchNotStartedAsync(loader, plan, Say, ct).ConfigureAwait(false);
                }
                catch (System.OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A plan that cannot be computed must not lose the resume above.
                    global::Android.Util.Log.Warn(LogTag, "first-run plan failed: " + ex.Message);
                }
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

        // STICKY, AS OF 2026-10-09, AND THE OLD REASONING WAS WRONG.
        //
        // It used to say: "NOT sticky: a restart with a null intent would start a
        // second fetch, and two gigabyte downloads at once is the state this whole
        // effort exists to clear rather than create. The next app launch starts it
        // again."
        //
        // The second-fetch fear does not survive reading the top of this method.
        // A redelivery arrives as a fresh OnStartCommand, which takes the Gate and
        // returns immediately if _busy - and after a process KILL the static is
        // gone with the process, so there is nothing to double up with. The intent
        // is never read here, so a null one changes nothing.
        //
        // What the old comment got wrong was the other half. "The next app launch
        // starts it again" puts a multi-gigabyte download behind a person
        // remembering to open the app. This phone is a P30 where iAware kills
        // FOREGROUND services - measured, adj 100 on a service running at
        // adj:-10000 - and where the swap runs out. So the case this must survive
        // is precisely the one NotSticky refused to: killed at 60% of a 2.8 GB
        // brain, with nobody opening anything for a day.
        //
        // The honest cost: after a kill on a phone that owes nothing, Android
        // restarts this, it goes foreground, the loop finds nothing and stops - a
        // notification visible for under a second. That is a far smaller price
        // than a download that only resumes when somebody happens to launch the
        // app, and the bytes already on disk are what make the resume cheap.
        return StartCommandResult.Sticky;
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
