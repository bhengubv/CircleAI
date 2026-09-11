// CircleNeuronService.cs
//
// The resident device service: one process that owns the models, so the apps
// don't each own their own.
//
// WHY A SERVICE AND NOT A LIBRARY CALL. Three things force it, and only the
// third is about convenience:
//
//   1. Android 8 kills plain background work. An always-listening wake word only
//      survives as a FOREGROUND service with the microphone type declared. There
//      is no version of "Hey B" that is not this.
//   2. Loading is the expensive part. A 122 MB voice takes 13-23 s to build a
//      session on a P30 Lite, measured. Per-app, that cost is paid again in every
//      app. Once, in a shared service, it is paid at boot.
//   3. Fifteen apps on a 3 GB phone cannot each hold their own copy. That is
//      arithmetic, not preference.
//
// WHAT "RESIDENT" DOES NOT MEAN. It does not mean the models stay loaded. On the
// cheapest phone that is exactly what makes the device unusable for everything
// else. The service stays alive and nearly weightless — wake word, routing,
// the per-person tables — while ResidentSlotManager keeps the generalist warm and
// hot-swaps the specialist, evicting the specialist first under pressure. High-end
// hardware can afford to keep more resident; the P30 keeps almost nothing. That
// decision belongs to DeviceProbe's tier, which is why this service installs the
// memory probe before it builds anything.

using Android.App;
using Android.Content;
using Android.OS;
using CircleAI.Core;
using CircleAI.Hosting;
using CircleAI.Hosting.Neuron;

namespace CircleAI.Device;

/// <summary>
/// Foreground service hosting one <see cref="NeuronNode"/> for the device.
/// </summary>
/// <remarks>
/// Start it with <see cref="Start"/> and reach the brain with
/// <see cref="CircleNeuronConnection"/>. Sticky: Android restarts it after an
/// out-of-memory kill, which on the phones this targets is a matter of when.
/// </remarks>
// BOTH TYPES DECLARED, ONE OR TWO USED. A service may start with any SUBSET of
// the types it declares, and the two jobs here are genuinely different: holding
// models resident is dataSync, holding the microphone open for a wake word is
// microphone. Declaring only dataSync and then opening a microphone is the kind
// of thing Android 14 refuses outright.
//
// Which types are actually claimed is decided at StartForeground, from whether a
// listener has been supplied — so the chat-only build, which has no speech stack
// and no RECORD_AUDIO, never claims the microphone type and never has to hold
// the permission that comes with it.
[Service(
    Name                  = "ai.circle.CircleNeuronService",
    Exported              = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync
                          | global::Android.Content.PM.ForegroundService.TypeMicrophone)]
public sealed partial class CircleNeuronService : Service
{
    /// <summary>logcat tag for this service. Filter on it to see the whole lifecycle.</summary>
    private const string LogTag = "CircleAI.Neuron";

    /// <summary>Notification channel id for the resident-service notification.</summary>
    public const string ChannelId = "circleai-neuron";

    /// <summary>Channel name, as the user sees it in Android's notification settings.</summary>
    public const string ChannelName = "Circle AI";

    /// <summary>Notification id for the ongoing foreground notification.</summary>
    public const int NotificationId = 0xC1A2;

    /// <summary>
    /// Builds the options the hosted Neuron runs with. A host sets this ONCE,
    /// before <see cref="Start"/>; left null the service runs but reports that it
    /// has no brain, rather than inventing one.
    /// </summary>
    /// <remarks>
    /// A static hook rather than a constructor argument because Android owns this
    /// object's construction — the OS news it up on restart, and nothing we do can
    /// pass it a parameter at that moment.
    /// </remarks>
    public static Func<AIOptions>? OptionsFactory { get; set; }

    /// <summary>The live node, or null before the service has finished starting.</summary>
    public static NeuronNode? Node { get; private set; }

    /// <summary>What the service is doing, safe to show a user.</summary>
    public static string Status { get; private set; } = "not started";

    /// <summary>Where the service has got to.</summary>
    public enum ServiceState
    {
        /// <summary>Not started, or stopped.</summary>
        Idle,

        /// <summary>Started; the model is being read off storage.</summary>
        Loading,

        /// <summary>The node is up and answering.</summary>
        Ready,

        /// <summary>It tried and could not. <see cref="Status"/> says why.</summary>
        Failed,
    }

    /// <summary>
    /// The memory manager. Live once the service has started.
    /// </summary>
    /// <remarks>
    /// Exposed so a host can call <see cref="AndroidMemoryPressure.Touch"/> on real
    /// use and so a screen can show what pressure the phone is under. Wired by the
    /// service rather than left to the host, because "release memory you are not
    /// using" is not a feature an app should have to opt into.
    /// </remarks>
    public static AndroidMemoryPressure? Memory { get; private set; }

    /// <summary>The current state. A caller waits on this, not on a clock.</summary>
    /// <remarks>
    /// Failed exists because without it a client can only ask "ready yet?" and has
    /// no way to hear "I have given up". Measured on the P30: the service reported
    /// a terminal failure in about two seconds and the connection still waited its
    /// full ninety-second timeout, because polling a ready-flag cannot tell the
    /// difference between still-loading and never-going-to. To a person that is an
    /// app that hangs.
    /// </remarks>
    public static ServiceState State { get; private set; } = ServiceState.Idle;

    private static readonly object Gate = new();

    /// <summary>Starts the resident service if it is not already running.</summary>
    public static void Start(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;
        var intent = new Intent(app, typeof(CircleNeuronService));

        // O+ refuses startService from the background; startForegroundService
        // promises a notification within a few seconds, which OnStartCommand posts
        // before it touches anything slow.
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O) app.StartForegroundService(intent);
        else                                            app.StartService(intent);
    }

    /// <summary>
    /// Which foreground-service types this start is actually claiming.
    /// </summary>
    /// <remarks>
    /// The microphone is claimed only when a listener has been supplied AND the
    /// permission is held. Claiming it otherwise is worse than useless: from
    /// Android 14 a microphone-typed service without RECORD_AUDIO is refused, so
    /// a chat-only build would fail to go foreground at all — and the failure
    /// would look like the model host being broken rather than a type it never
    /// needed.
    /// </remarks>
    private global::Android.Content.PM.ForegroundService ClaimedTypes()
    {
        var types = global::Android.Content.PM.ForegroundService.TypeDataSync;

        // API 30 OR LATER FOR THE MICROPHONE TYPE. TypeMicrophone is 0x80 and
        // was introduced in API 30; Android 10 does not know that bit, rejects
        // a StartForeground that claims it, and then kills the process for not
        // having gone foreground. Measured on a P30 (API 29): the wake word was
        // listening and the app died ten seconds later.
        //
        // Claiming it is a DECLARATION to the OS about what this service does.
        // Not claiming it on Android 10 does not close the microphone - the
        // listener still holds it; the service simply declares dataSync, which
        // is the whole vocabulary that platform has.
        if (Listener is not null &&
            Build.VERSION.SdkInt >= BuildVersionCodes.R &&
            CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio)
                == global::Android.Content.PM.Permission.Granted)
        {
            types |= global::Android.Content.PM.ForegroundService.TypeMicrophone;
        }

        return types;
    }

    /// <summary>Stops the resident service and releases the models it holds.</summary>
    public static void Stop(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;
        app.StopService(new Intent(app, typeof(CircleNeuronService)));
    }

    /// <summary>The running service, so a static caller can refresh its notification.</summary>
    /// <remarks>
    /// THE SHADE SAID "READY" WHILE THE MICROPHONE WAS OPEN. The notification is
    /// only ever written from BuildNodeAsync, which finishes BEFORE the listener
    /// is installed - so the line somebody reads all day was decided at a moment
    /// when there was nothing to report, and never revisited.
    /// <para>
    /// That is the opposite of what this service says it is for: "the
    /// notification is not an apology for holding the microphone; it is the
    /// honest disclosure that we are". A shade reading "Ready" while an app holds
    /// the microphone is not disclosure, it is the absence of it.
    /// </para>
    /// <para>
    /// A static handle because StartListeningAsync is static - Android owns this
    /// object and hands it to nobody - and cleared in OnDestroy so a torn-down
    /// service is never notified through.
    /// </para>
    /// </remarks>
    private static CircleNeuronService? _running;

    /// <summary>Puts what the service is actually doing back on the shade.</summary>
    internal static void RefreshNotification()
    {
        try { _running?.Notify(ListeningNotificationText()); }
        catch { /* a notification is never worth taking the service down for */ }
    }

    // ── what the assistant is doing to this phone ────────────────────────────
    //
    // WHY THIS EXISTS. A capability that acts on the device hands off to another
    // app - "play Coldplay" starts whatever plays music here - and the moment it
    // does, Circle AI is behind that app with nothing on screen. From the
    // outside a music player simply opened on its own, and there is no way to
    // tell the assistant did it from a bug, a mis-tap, or somebody else driving
    // the phone over adb. Announced here because the shade is the one surface
    // that survives another app coming to the front.

    private static string? _doing;
    private static DateTimeOffset _doingUntil;

    /// <summary>How long an announcement stands before the shade stops repeating it.</summary>
    /// <remarks>
    /// SELF-HEALING ON PURPOSE. A caller that announces and then never clears -
    /// because it threw, or was cancelled, or somebody added a return - would
    /// otherwise leave the shade claiming something the phone is not doing, and
    /// a stale claim is worse than no claim. Thirty seconds outlives the hand-off
    /// it describes and expires long before anybody could be misled by it.
    /// </remarks>
    private static readonly TimeSpan AnnouncementLasts = TimeSpan.FromSeconds(30);

    /// <summary>Say on the shade what the assistant is doing to this phone, or null to stop.</summary>
    /// <remarks>
    /// IT GOES IN THE TITLE, NOT OVER THE TEXT. The content line is the
    /// microphone disclosure - "Listening for ... - nothing is kept or sent" -
    /// and replacing it would hide that this app holds the microphone in order
    /// to announce something far less important. The title is the honest place:
    /// prominent, short, and it costs the disclosure nothing.
    /// </remarks>
    public static void Announce(string? what)
    {
        _doing = string.IsNullOrWhiteSpace(what) ? null : what!.Trim();
        _doingUntil = _doing is null ? default : DateTimeOffset.UtcNow + AnnouncementLasts;
        RefreshNotification();
    }

    /// <summary>The title line: the app, plus what it is doing when it is doing something.</summary>
    private static string NotificationTitle()
    {
        if (_doing is null) return "Circle AI";
        if (DateTimeOffset.UtcNow > _doingUntil) { _doing = null; return "Circle AI"; }
        return "Circle AI · " + _doing;
    }

    public override IBinder OnBind(Intent? intent) => new CircleNeuronBinder(this);

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        _running = this;

        // Notification FIRST. Android gives a startForegroundService caller a few
        // seconds to call StartForeground or it kills the process with an ANR —
        // and building a Neuron takes far longer than that budget.
        try
        {
            EnsureChannel();
            var notification = BuildNotification("starting…");

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                try
                {
                    StartForeground(NotificationId, notification, ClaimedTypes());
                }
                catch (Exception typed)
                {
                    // GOING FOREGROUND MATTERS MORE THAN THE TYPES. If the
                    // platform refuses the types - a constant it does not know,
                    // a permission it wants first - the untyped call still puts
                    // this service in the foreground, and a narrower declaration
                    // is far better than the ANR that follows not going
                    // foreground at all.
                    global::Android.Util.Log.Warn(LogTag,
                        "StartForeground refused the claimed types (" + typed.Message
                        + ") - going foreground untyped");
                    StartForeground(NotificationId, notification);
                }
            }
            else
            {
                StartForeground(NotificationId, notification);
            }
        }
        catch (Exception ex)
        {
            // LOUD. This used to set a Status string that nothing reads and log
            // nothing at all, so a service the platform had rejected looked
            // exactly like a service that had never been asked to start - and
            // Android kills the process seconds later, which is where the only
            // visible evidence appeared.
            global::Android.Util.Log.Error(LogTag, "could not go foreground: " + ex);
            Status = $"could not go foreground: {ex.Message}";
            State  = ServiceState.Failed;
            return StartCommandResult.Sticky;
        }

        // ONLY ONCE THE SERVICE IS ACTUALLY IN THE FOREGROUND. Renewing a
        // notification for a service that failed to start would keep the shade
        // claiming a microphone that was refused - the same lie this mechanism
        // exists to end, told from the other direction.
        KeepNotificationAlive();

        // The probe before the brain. Everything the Neuron decides about which
        // models fit is read off DeviceProbe, so measuring the phone has to happen
        // before anything asks — otherwise the first answers are made on the GC
        // heap limit and the device looks like a wearable.
        AndroidDeviceMemory.Install(this);

        // The memory manager, before the models exist — so the first thing that
        // gets loaded is already being watched. Registered as a ComponentCallbacks2
        // on the APPLICATION: onTrimMemory is delivered to registered callbacks,
        // and a Service that does not register simply never hears Android ask.
        if (Memory is null)
        {
            Memory = new AndroidMemoryPressure();
            try { ApplicationContext?.RegisterComponentCallbacks(Memory); }
            catch (Exception ex)
            {
                // Without this we are deaf to pressure and will be killed rather
                // than asked. Worth saying out loud, not worth refusing to start.
                Status = $"memory signals unavailable: {ex.Message}";
            }
        }

        _ = System.Threading.Tasks.Task.Run(BuildNodeAsync);
        return StartCommandResult.Sticky;
    }

    private async System.Threading.Tasks.Task BuildNodeAsync()
    {
        try
        {
            var factory = OptionsFactory;
            if (factory is null)
            {
                // NO FACTORY MEANS NOBODY ASKED FOR A BRAIN, NOT THAT ONE BROKE.
                //
                // This said "no brain configured — set CircleNeuronService
                // .OptionsFactory before Start", marked the service Failed, and
                // PUT THAT ON THE NOTIFICATION SHADE. Reported from a Redmi 12 on
                // 2026-09-07, where it is the only thing the owner of the phone
                // ever sees this service say - a stack-trace sentence addressed to
                // a developer, sitting under the app's name, on a service that is
                // working perfectly.
                //
                // The hybrid head hosts its brain in the app process - DeviceBrain
                // owns an ItSession - and uses this service for ONE thing: holding
                // the microphone open while the screen is off. It never sets a
                // factory because it never wants a node. The native head does want
                // one and sets it. So a null factory is a caller saying "just hold
                // the microphone", and answering that with a failure is the service
                // reporting a decision as a fault.
                //
                // Idle rather than Failed: nothing failed, and Failed is read
                // elsewhere as a reason to give up. The notification goes back to
                // saying what the service is actually doing.
                Status = "listening only — no brain was asked for";
                State  = ServiceState.Idle;
                global::Android.Util.Log.Info(LogTag, Status);
                Notify(ListeningNotificationText());
                return;
            }

            Status = "loading the model…";
            State  = ServiceState.Loading;
            Notify(Status);

            NeuronNode node;
            lock (Gate)
            {
                if (Node is not null) { Status = "ready"; State = ServiceState.Ready; Notify(Status); return; }

                // The memory manager goes IN, not alongside. Passed here it reaches
                // AIService's brownout path — the one that downshifts and releases —
                // so Android's onTrimMemory and the idle timer both end up pulling
                // the same lever the design already built.
                node = new NeuronNode(new AIService(
                    factory(),
                    modelLoader:          null,
                    generatorFactory:     null,
                    modelSelector:        null,
                    modelRegistry:        null,
                    memoryPressureSource: Memory));
                Node = node;
            }

            // Warm it here, not on the first question. The whole reason this is a
            // service is so nobody waits 13-23 s mid-sentence.
            await node.Brain.StartAsync().ConfigureAwait(false);

            Status = node.IsReady ? "ready" : node.StatusMessage;
            State  = node.IsReady ? ServiceState.Ready : ServiceState.Failed;

            // Only once there is something worth releasing. Started earlier it
            // would spend the whole cold load counting the model as idle.
            if (node.IsReady) Memory?.StartIdleWatch();

            Notify(Status);
        }
        catch (Exception ex)
        {
            // A service that dies takes every app's AI with it. Report and stay up:
            // the models may still load on a later attempt, and a bound client can
            // read Status and say something true instead of hanging.
            Status = $"failed to load: {ex.Message}";
            State  = ServiceState.Failed;
            Notify(Status);
        }
    }

    public override void OnDestroy()
    {
        // THE CPU FIRST, because this is the path a wake lock leaks down. The
        // service can be destroyed without anyone calling StopListening - the
        // platform tears it down, the task is swiped away, the process is
        // reclaimed - and a partial wake lock that outlives its service holds
        // the CPU up with nothing listening on the other end. It is released
        // here as well as there, and releasing twice is harmless.
        LetTheCpuSleep();

        lock (Gate)
        {
            (Node?.Brain as IDisposable)?.Dispose();
            Node = null;
        }

        // Hand the callback back. A ComponentCallbacks2 left registered against a
        // destroyed service is a leak Android holds for the life of the process —
        // the exact sin this class exists to avoid committing.
        try { if (Memory is not null) ApplicationContext?.UnregisterComponentCallbacks(Memory); }
        catch { /* already torn down */ }
        Memory?.Dispose();
        Memory = null;
        Status = "stopped";
        State  = ServiceState.Idle;

        // Nothing to notify through any more, and a stale handle would have a
        // torn-down service posting about a microphone it no longer holds.
        _running = null;

        // Stop renewing BEFORE removing, or a timer that fires between the two
        // re-posts the notification this line is about to take away - and it
        // would then sit there with a fresh lease and nothing behind it, which
        // is precisely the bug.
        _renew?.Dispose();
        _renew = null;
        _doing = null;

        // AN ORDERLY STOP TAKES THE NOTICE DOWN ITSELF, so the alarm has nothing
        // left to do. Left armed it would wake a process two minutes later to
        // look at a notification that is already gone - harmless, and still
        // waste nobody asked for.
        DisarmReaper(this);

        StopForeground(StopForegroundFlags.Remove);
        base.OnDestroy();
    }

    // ── the notification ─────────────────────────────────────────────────────

    private void EnsureChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        if (GetSystemService(NotificationService) is not NotificationManager nm) return;
        if (nm.GetNotificationChannel(ChannelId) is not null) return;

        // Low importance: this notification is a legal requirement of running in
        // the foreground, not news. It should sit silently in the shade.
        nm.CreateNotificationChannel(
            new NotificationChannel(ChannelId, ChannelName, NotificationImportance.Low)
            {
                Description = "Keeps Circle AI's models loaded so apps answer instantly.",
            });
    }

    // ── the dead-man's switch ────────────────────────────────────────────────
    //
    // THE SHADE OUTLIVED THE PROCESS, AND SAID THE MICROPHONE WAS OPEN.
    //
    // Measured on a P30 on 2026-09-11: no process, no service record, nothing in
    // ActivityManager - and this notification still sitting on the shade saying
    // "Listening for Hey Circle AI - nothing is kept or sent". The phone was
    // telling its owner an app held the microphone when no such app existed.
    //
    // Android is supposed to take a foreground-service notification away with
    // the service. EMUI did not, and the same four vendors that kill these
    // services on their own schedule are the ones least likely to tidy up after
    // doing it. OnDestroy's StopForeground(Remove) only runs on an ORDERLY stop;
    // a process killed by lowmemorykiller never reaches it.
    //
    // A FALSE DISCLOSURE IS WORSE THAN A MISSING ONE. Every other notice in this
    // app can be late. This one is the microphone disclosure, and a phone that
    // claims to be listening when it is not teaches its owner that the notice
    // means nothing - which is exactly the habit that makes a real one useless.
    //
    // SO SOMETHING OUTSIDE THE PROCESS HOLDS THE STOPWATCH.
    //
    // THE FIRST ATTEMPT AT THIS DID NOT WORK, AND IT IS WORTH SAYING WHY. It put
    // the lease on the notification itself - SetTimeoutAfter, renewed every
    // thirty seconds by the live service - on the reasoning that a dead process
    // stops renewing and the platform reaps it. Measured on a P30 on 2026-09-11:
    // force-stopped, no pid, no service record, and the notice still on the
    // shade three minutes into a two-minute lease.
    //
    // Android does not apply timeoutAfter to a notification carrying
    // FLAG_FOREGROUND_SERVICE. It holds those for as long as the service runs,
    // deliberately - so the one mechanism that could have expired this is the
    // one that flag switches off, and an orphaned record keeps the flag.
    //
    // AN ALARM IS THE ONLY CLOCK THAT OUTLIVES THE PROCESS. AlarmManager keeps
    // the schedule in the system and starts a fresh process to deliver the
    // broadcast. The living service pushes the alarm forward on every heartbeat
    // so it never fires; when nothing is left to push it, NotificationReaper
    // runs and takes the notice down. The absence of a heartbeat is still the
    // signal - what changed is who is holding the stopwatch.

    /// <summary>How long the shade may stand without the service pushing the alarm out.</summary>
    /// <remarks>
    /// Two minutes is the window in which the shade may be wrong after an
    /// unannounced kill. Shorter would narrow it; it would also mean a phone
    /// that briefly deprioritises this service drops the microphone disclosure
    /// while the microphone is still open, which is the opposite failure and the
    /// worse one. The reaper re-checks rather than trusting the alarm, so a
    /// premature firing costs nothing.
    /// </remarks>
    private static readonly TimeSpan NotificationLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Whether a service is alive in THIS process. The reaper's liveness test.</summary>
    internal static bool IsRunning => _running is not null;

    /// <summary>Pushes the dead-man's alarm out by one full lease.</summary>
    /// <remarks>
    /// ONE PendingIntent, UpdateCurrent, so every call REPLACES the pending
    /// alarm rather than stacking another. A service renewing every thirty
    /// seconds therefore has exactly one alarm outstanding at all times, always
    /// two minutes out, and it only ever arrives if the renewals stop.
    /// <para>
    /// INEXACT AND NON-WAKING, both on purpose. `Set` needs no permission -
    /// exact alarms are gated from Android 12 and this does not need to be
    /// exact. ElapsedRealtime rather than the wakeup variants because nobody is
    /// misled by a shade they are not looking at: firing the moment the phone is
    /// next awake is both soon enough and free.
    /// </para>
    /// </remarks>
    internal static void ArmReaper(Context context)
    {
        try
        {
            if (context.GetSystemService(AlarmService) is not AlarmManager alarms) return;

            var intent = new Intent(context.ApplicationContext ?? context, typeof(NotificationReaper))
                .SetAction(NotificationReaper.Action);

            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;

            var pending = PendingIntent.GetBroadcast(
                context.ApplicationContext ?? context, ReaperRequestCode, intent, flags);
            if (pending is null) return;

            alarms.Set(
                AlarmType.ElapsedRealtime,
                (long)(SystemClock.ElapsedRealtime() + NotificationLifetime.TotalMilliseconds),
                pending);
        }
        catch { /* a missing dead-man's switch must never take the service down */ }
    }

    /// <summary>Cancels the dead-man's alarm, for an ORDERLY stop that removes the notice itself.</summary>
    private static void DisarmReaper(Context context)
    {
        try
        {
            if (context.GetSystemService(AlarmService) is not AlarmManager alarms) return;

            var intent = new Intent(context.ApplicationContext ?? context, typeof(NotificationReaper))
                .SetAction(NotificationReaper.Action);

            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;

            var pending = PendingIntent.GetBroadcast(
                context.ApplicationContext ?? context, ReaperRequestCode, intent, flags);
            if (pending is not null) alarms.Cancel(pending);
        }
        catch { /* nothing here is worth failing a shutdown for */ }
    }

    /// <summary>Request code for the reaper alarm. Fixed, so one alarm is ever outstanding.</summary>
    private const int ReaperRequestCode = 0xC1A3;

    /// <summary>How often a living service renews it.</summary>
    /// <remarks>
    /// A QUARTER OF THE LIFETIME, NOT HALF. Three renewals may be missed - to a
    /// busy phone, a doze transition, a slow main thread - before the disclosure
    /// is at risk. Re-posting a notification is cheap and this service already
    /// holds a wake lock, so the margin costs nothing worth counting.
    /// </remarks>
    private static readonly TimeSpan NotificationRenewal = TimeSpan.FromSeconds(30);

    // Fully qualified: Android.OS and System.Timers both have a Timer, and
    // this file imports Android.OS.
    private System.Threading.Timer? _renew;

    /// <summary>What was last posted, so a renewal repeats it rather than guessing.</summary>
    /// <remarks>
    /// The renewal must not decide what the notification SAYS. During model
    /// loading the text is "loading the model…"; a renewal that rebuilt the line
    /// from listening state would overwrite it with something else halfway
    /// through. Its only job is to reset the clock on whatever is true now.
    /// </remarks>
    private string _lastText = "starting…";

    /// <summary>Starts the heartbeat that keeps the reaper away while this lives.</summary>
    /// <remarks>
    /// TWO JOBS, AND THE SECOND IS THE ONE THAT WORKS. Re-posting keeps the
    /// shade's text current; pushing the alarm out is what actually makes the
    /// notice mortal. Armed once immediately as well as on the interval, so the
    /// dead-man's switch is live from the moment the service goes foreground
    /// rather than thirty seconds later.
    /// </remarks>
    private void KeepNotificationAlive()
    {
        _renew?.Dispose();
        ArmReaper(this);
        _renew = new System.Threading.Timer(
            _ =>
            {
                try
                {
                    Notify(_lastText);
                    ArmReaper(this);
                }
                catch { /* never take the service down for a notice */ }
            },
            null, NotificationRenewal, NotificationRenewal);
    }

    private Notification BuildNotification(string text)
    {
        var builder = new Notification.Builder(this, ChannelId)
            .SetContentTitle(NotificationTitle())
            .SetContentText(text)
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuManage)
            .SetOngoing(true);

        // NO SetTimeoutAfter HERE, AND THAT IS A MEASUREMENT RATHER THAN A
        // PREFERENCE. It was here, it looked right, and it does nothing: Android
        // does not apply a timeout to a notification carrying
        // FLAG_FOREGROUND_SERVICE. Verified on a P30 - the notice sat on the
        // shade three minutes into a two-minute lease with no process behind it.
        // The lease lives in AlarmManager instead; see ArmReaper.

        // TAPPABLE, BECAUSE THE DESIGN ALREADY DEPENDED ON IT. After a reboot the
        // microphone cannot restart itself - from Android 14 a microphone-typed
        // foreground service may not be started from BOOT_COMPLETED - so
        // BootReceiver brings the models back and leaves listening to "one
        // deliberate tap", with this notification named as the place that tap
        // happens. It had no content intent, so tapping it did nothing at all and
        // the only route back was to go and find the app.
        var open = OpenTheApp();
        if (open is not null) builder.SetContentIntent(open);

        return builder.Build();
    }

    /// <summary>A tap on the notification opens the app.</summary>
    /// <remarks>
    /// Asked of the package manager rather than naming an Activity, because this
    /// assembly is shared by every head and must not know which one it is inside.
    /// Null when the package has no launcher - a service-only build - and then the
    /// notification is simply not tappable, which is honest.
    /// </remarks>
    private PendingIntent? OpenTheApp()
    {
        try
        {
            var launch = PackageManager?.GetLaunchIntentForPackage(PackageName!);
            if (launch is null) return null;

            launch.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);

            // IMMUTABLE FROM API 31, where a PendingIntent must declare one or the
            // other or the call throws outright.
            var flags = Build.VERSION.SdkInt >= BuildVersionCodes.S
                ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable
                : PendingIntentFlags.UpdateCurrent;

            return PendingIntent.GetActivity(this, 0, launch, flags);
        }
        catch
        {
            // A notification that cannot be tapped is a smaller loss than a
            // service that will not start.
            return null;
        }
    }

    private void Notify(string text)
    {
        try
        {
            // Remembered so a renewal can repeat it rather than re-deriving it.
            _lastText = text;

            if (GetSystemService(NotificationService) is NotificationManager nm)
                nm.Notify(NotificationId, BuildNotification(text));
        }
        catch { /* the notification is a courtesy; never take the service down for it */ }
    }
}

/// <summary>Binder handing a same-process client the live node.</summary>
public sealed class CircleNeuronBinder : Binder
{
    internal CircleNeuronBinder(CircleNeuronService service) => Service = service;

    /// <summary>The service instance.</summary>
    public CircleNeuronService Service { get; }

    /// <summary>The hosted node, or null while it is still loading.</summary>
    public NeuronNode? Node => CircleNeuronService.Node;
}
