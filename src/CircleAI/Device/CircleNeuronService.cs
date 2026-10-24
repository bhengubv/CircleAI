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
using CircleAI.Core.Models;
using CircleAI.Inference;
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

    /// <summary>
    /// What this device has learned about its own models. Set by the host; null
    /// means it has learned nothing and refuses nothing.
    /// </summary>
    public static CircleAI.Core.Models.IModelCatalog? Catalogue { get; set; }

    /// <summary>The model this service actually chose, or null before it has.</summary>
    /// <remarks>
    /// IT EXISTED IN ONE LOG LINE AND NOWHERE ELSE. "model: Qwen3.5-2B-MNN" went to
    /// logcat and the decision was then unavailable to anything that might have to
    /// say it out loud - so a person asking which brain it was using could only be
    /// answered by a developer with a cable. Set where the choice is pinned.
    /// </remarks>
    public static string? RunningModel { get; private set; }

    /// <summary>Where the crash breadcrumb is written. Set by the host.</summary>
    /// <remarks>
    /// NOT the model store: InstalledIds enumerates that directory and would read a
    /// state folder as a model called ".state".
    /// </remarks>
    public static string? StateDirectory { get; set; }

    /// <summary>
    /// Completes when the catalogue has been refreshed, assessed and tidied. Set by
    /// the host; null means there is nothing to wait for.
    /// </summary>
    /// <remarks>
    /// THE BRAIN CHOSE FROM A CATALOGUE THAT WAS ABOUT TO CHANGE. Measured on the
    /// P30 on 2026-10-16: the selector picked at 02:12:00, the refresh that raised
    /// Qwen3.5-0.8B's rank landed at 02:12:08, and the assessment at 02:12:15 - so
    /// it ranked yesterday's numbers, tied the 0.8B with Qwen2.5-1.5B and took the
    /// wrong one. Waiting costs the bootstrap's time on a cold start only, and the
    /// bootstrap now writes through a write-ahead log instead of syncing the disk
    /// some 180 times.
    /// </remarks>
    public static System.Threading.Tasks.Task? CatalogueReady { get; set; }

    /// <summary>
    /// Completes when start-up housekeeping has finished - the abandoned-download sweep
    /// and the catalogue's refresh, assessment and tidy. Set by the host; every model pull
    /// waits for it (ModelSetup.WaitForHousekeepingAsync). Null means nothing to wait for.
    /// </summary>
    /// <remarks>
    /// NOT <see cref="CatalogueReady"/>, which the brain waits on and which stays free of
    /// the sweep: the sweep walks gigabytes, and the brain's wait is bounded at 30 s.
    /// </remarks>
    public static System.Threading.Tasks.Task? HousekeepingDone { get; set; }

    /// <summary>The longest a brain start waits for <see cref="CatalogueReady"/>.</summary>
    /// <remarks>
    /// Bounded because a catalogue that never finishes must not cost the phone its
    /// brain; past this it selects from whatever the catalogue holds, and says so.
    /// </remarks>
    internal static readonly TimeSpan CatalogueWait = TimeSpan.FromSeconds(30);

    /// <summary>Whether <see cref="RecordLastLoad"/> has run in this process.</summary>
    private static volatile bool _lastLoadRecorded;

    /// <summary>
    /// Read the breadcrumb the last load left, if it died there, and turn it into a
    /// strike against that model - once per process.
    /// </summary>
    /// <param name="context">This process, for Android's record of why it last ended.</param>
    /// <param name="catalogue">Where verdicts are kept. Null records nothing.</param>
    /// <param name="stateDirectory">Where the breadcrumb is written.</param>
    /// <remarks>
    /// A breadcrumb that survived is proof the process died inside a load, and it
    /// names the model. A native abort runs no managed handler, so this is the ONLY
    /// place that failure can be noticed.
    /// <para>
    /// A HOST CALLS THIS BEFORE THE CATALOGUE BOOTSTRAP, and the order matters. The
    /// bootstrap's housekeeping removes a model in favour of one that has loaded here
    /// and is not refused - so a death recorded AFTER it could let a model on its
    /// second strike serve as the replacement, delete what it replaced, and then be
    /// refused itself. It used to run at the top of the brain's start, which raced
    /// the catalogue being assigned at all: on the P30 on 2026-10-16 the catalogue won
    /// by about 130 ms.
    /// </para>
    /// </remarks>
    public static void RecordLastLoad(Context context, CircleAI.Core.Models.IModelCatalog? catalogue, string stateDirectory)
    {
        if (_lastLoadRecorded) return;
        _lastLoadRecorded = true;

        try
        {
            // FIRST, THE REFUSALS THE OLD ONE-STRIKE RULE GOT WRONG. They are
            // already written and nothing re-reads them, so changing the rule alone
            // would leave Qwen3.5-0.8B refused on the P30 for ever. Each comes back
            // on one strike; idempotent - see CrashVerdict.Reconsider.
            CircleAI.Inference.CrashVerdict.Reconsider(
                catalogue, line => global::Android.Util.Log.Warn(LogTag, line));

            var crumb = CircleAI.Assistant.DeviceDiagnostics.PreviousCrash(stateDirectory);
            CircleAI.Inference.CrashVerdict.Record(
                catalogue, crumb, line => global::Android.Util.Log.Warn(LogTag, line),
                killedByTheSystem: LastDeathWasTheSystems(context));

            // Cleared either way: a breadcrumb read twice would refuse a second model
            // on the strength of the first one's death.
            CircleAI.Assistant.DeviceDiagnostics.EndRisky(stateDirectory);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(LogTag, "could not read the last load's breadcrumb: " + ex.Message);
        }
    }

    /// <summary>
    /// Wait, bounded, for <see cref="CatalogueReady"/>, saying how long it took.
    /// </summary>
    /// <remarks>
    /// A selection made from the catalogue as it stood before this launch refreshed
    /// it is a selection made from stale ranks. Never throws: a catalogue that is
    /// late or broken costs the phone its freshest ranking, never its brain.
    /// </remarks>
    private static async System.Threading.Tasks.Task WaitForCatalogueAsync()
    {
        if (CatalogueReady is not { } ready)
        {
            global::Android.Util.Log.Warn(LogTag, "no catalogue bootstrap to wait for; selecting from what is stored");
            return;
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await ready.WaitAsync(CatalogueWait).ConfigureAwait(false);
            global::Android.Util.Log.Info(LogTag,
                $"catalogue ready after {waited.ElapsedMilliseconds} ms; selecting");
        }
        catch (TimeoutException)
        {
            global::Android.Util.Log.Warn(LogTag,
                $"catalogue still settling after {CatalogueWait.TotalSeconds:0} s; selecting from what it holds");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn(LogTag, "catalogue bootstrap failed: " + ex.Message);
        }
    }

    /// <summary>The live node, or null before the service has finished starting.</summary>
    /// <remarks>
    /// Only for a host that runs the brain IN this process. The CircleAI service runs it
    /// in its ":inference" process instead and sets <see cref="Brain"/>; there this stays
    /// null and every question goes through <see cref="Asks"/>.
    /// </remarks>
    public static NeuronNode? Node { get; private set; }

    /// <summary>
    /// The supervised brain, when the host runs it in another process. Set by the host
    /// before <see cref="Start"/>; when set, this service holds the foreground and the
    /// microphone and asks the brain to load, but builds no node of its own.
    /// </summary>
    public static CircleAI.Assistant.Requests.IBrainWorker? Brain { get; set; }

    /// <summary>The one door questions come in by (the request coordinator). Set by the host.</summary>
    public static CircleAI.Assistant.Requests.IAskFront? Asks { get; set; }

    /// <summary>What the service reports about itself. Set by the host.</summary>
    public static CircleAI.Assistant.Lifecycle.IReadinessTracker? Readiness { get; set; }

    private static bool _watchingReadiness;

    /// <summary>Ask the supervised brain to load, and keep the shade saying what it is doing.</summary>
    /// <remarks>
    /// THE PRELOAD, NOT THE LOAD. The brain loads in its own process; this only asks
    /// for it early - at start and on every wake - so the 13-23 s are paid before the
    /// question rather than after it. A failure here is reported by the supervisor to
    /// readiness, and the question that follows waits in the queue.
    /// </remarks>
    private async System.Threading.Tasks.Task PreloadAsync()
    {
        var brain = Brain;
        if (brain is null) return;

        if (Readiness is { } readiness && !_watchingReadiness)
        {
            _watchingReadiness = true;
            readiness.Changed += (_, snapshot) => OnReadiness(snapshot);
            OnReadiness(readiness.Current);
        }

        try
        {
            await brain.EnsureReadyAsync(System.Threading.CancellationToken.None).ConfigureAwait(false);
            Memory?.StartIdleWatch();
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Info(LogTag, "the brain is not up yet: " + ex.GetType().Name);
        }
    }

    private static void OnReadiness(CircleAI.Assistant.Lifecycle.ReadinessSnapshot snapshot)
    {
        var brain = snapshot.Brain;
        Status = CircleAI.Assistant.Lifecycle.ReadinessSentences.For(brain);
        State = brain.Phase switch
        {
            CircleAI.Assistant.Lifecycle.BrainPhase.Ready   => ServiceState.Ready,
            CircleAI.Assistant.Lifecycle.BrainPhase.Loading => ServiceState.Loading,
            CircleAI.Assistant.Lifecycle.BrainPhase.Failed  => ServiceState.Failed,
            _                                               => ServiceState.Idle,
        };
        RunningModel = brain.Phase == CircleAI.Assistant.Lifecycle.BrainPhase.Ready ? brain.ModelId : null;
        _running?.Notify(Status);
    }


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

    // THE SETUP READOUT AND THE RESUME ARE NOT HERE, AND THAT IS THE POINT.
    // CircleAI.Inference.ModelSetup owns both, and ServiceApplication calls them on
    // every start. They lived here first, hung off this service's "brain ready"
    // line, and on the device they never ran once: this brain starts only when
    // somebody asks a question, and nobody had.

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
        // MICROPHONE WHEN WE HAVE IT, AND NOT dataSync AS WELL - THAT COMBINATION IS
        // WHAT KILLED THIS SERVICE ON ANDROID 16.
        //
        // This used to start from TypeDataSync and OR the microphone on top, so every
        // start claimed dataSync. From Android 14 a dataSync foreground service
        // carries a DAILY TIME BUDGET; spend it and startForeground is refused
        // outright until it resets. Measured on a Circle OS device (SDK 36) on
        // 2026-10-04:
        //
        //   ForegroundServiceStartNotAllowedException:
        //     Time limit already exhausted for foreground service type unknown
        //
        // After which the service did not start AT ALL - no pid, no brain, every
        // question answered "brain warming up" for ever. The P30 is Android 10 and
        // cannot reproduce any of it, which is why it was never seen.
        //
        // THE HONEST TYPE IS THE MICROPHONE. This service is foreground because it
        // holds the mic for the wake word; that type is not budgeted, and claiming it
        // alone is both true and survivable. Claiming dataSync alongside it bought
        // nothing and cost the service its life after a few hours of listening.
        //
        // API 30 OR LATER FOR THE MICROPHONE TYPE. TypeMicrophone is 0x80 and arrived
        // in API 30; Android 10 does not know that bit, rejects a StartForeground
        // claiming it, and then kills the process for not having gone foreground -
        // measured on a P30 (API 29), the wake word listening and the app dead ten
        // seconds later. There, dataSync is the whole vocabulary the platform has,
        // and that platform has no budget to exhaust.
        if (Listener is not null &&
            Build.VERSION.SdkInt >= BuildVersionCodes.R &&
            CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio)
                == global::Android.Content.PM.Permission.Granted)
        {
            return global::Android.Content.PM.ForegroundService.TypeMicrophone;
        }

        // NOT LISTENING, OR TOO OLD TO SAY SO. dataSync is what is left: on Android
        // 10 it is the only word available, and with no listener running it is also
        // the truthful one - whatever this service is doing then, it is not holding
        // a microphone.
        return global::Android.Content.PM.ForegroundService.TypeDataSync;
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
    /// <remarks>
    /// PUBLIC BECAUSE THE LISTENER LEARNS ITS PHRASE AFTER IT STARTS. A keyword
    /// spotter does not know what it is listening for until its graphs are loaded,
    /// which is seconds after StartAsync returns — so the one refresh the service
    /// does on its own runs while <see cref="IResidentListener.Describe"/> is still
    /// a placeholder, and the shade then kept that placeholder for the whole time
    /// the microphone was open. The same argument this file already makes about
    /// "Ready": disclosure that does not update is not disclosure.
    /// </remarks>
    public static void RefreshNotification()
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

        // Whether this service is actually in the foreground when this method ends.
        // Read below: a notification is only renewed for a service that got there.
        var foreground = true;

        // Notification FIRST. Android gives a startForegroundService caller a few
        // seconds to call StartForeground or it kills the process with an ANR —
        // and building a Neuron takes far longer than that budget.
        try
        {
            EnsureChannel();
            var notification = BuildNotification("starting…");

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                var claimed = ClaimedTypes();
                try
                {
                    StartForeground(NotificationId, notification, claimed);
                }
                catch (Exception typed)
                {
                    // GOING FOREGROUND MATTERS MORE THAN THE TYPES - AND THE
                    // PARAMETERLESS CALL IS NOT "UNTYPED".
                    //
                    // That is what this did, and the comment it did it under was
                    // wrong: Service.startForeground(int, Notification) passes
                    // FOREGROUND_SERVICE_TYPE_MANIFEST, which Android expands to
                    // every type the MANIFEST declares - here dataSync AND
                    // microphone. So the one case this fallback exists for, a
                    // microphone-typed start refused from the background on Android
                    // 14 or later, was retried with the same microphone bit in it
                    // (checked first, highest bit down) and threw again. The service
                    // then never went foreground at all: no disclosure notification,
                    // no FGS protection, and nothing started.
                    //
                    // dataSync EXPLICITLY. Holding the models resident is genuinely
                    // dataSync work, it needs no permission, and it is a type this
                    // service may claim from the background - so the brain keeps its
                    // notification and its protection while the microphone waits for
                    // a visible screen (ResidentListeningControl.CanBeHeard).
                    global::Android.Util.Log.Warn(LogTag,
                        "StartForeground refused " + claimed + " (" + typed.Message + ")");

                    var narrower = claimed != global::Android.Content.PM.ForegroundService.TypeDataSync;
                    try
                    {
                        if (narrower)
                        {
                            global::Android.Util.Log.Warn(LogTag,
                                "going foreground as dataSync instead - the models stay resident; "
                                + "the microphone needs a start made while a screen of ours is up");
                            StartForeground(NotificationId, notification,
                                global::Android.Content.PM.ForegroundService.TypeDataSync);
                        }
                        else
                        {
                            // dataSync itself was refused. Android 15+ does that for a
                            // dataSync start from BOOT_COMPLETED, and for a daily budget
                            // already spent. The manifest set is the last thing to try.
                            global::Android.Util.Log.Warn(LogTag,
                                "dataSync itself was refused - trying the manifest's types");
                            StartForeground(NotificationId, notification);
                        }
                    }
                    catch (Exception narrowed)
                    {
                        global::Android.Util.Log.Warn(LogTag,
                            "that was refused too (" + narrowed.Message + ") - trying the manifest's types");
                        StartForeground(NotificationId, notification);
                    }
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

            // AND THE BRAIN IS NOT THE THING THAT FAILED.
            //
            // This set State = Failed, which is the word for "this service tried to
            // load a model and gave up" - it is what CircleNeuronConnection and
            // ColdTurn read as "stop waiting, it will never answer". A foreground
            // type the platform refused says nothing whatever about the model, and
            // conflating the two turned one refused microphone claim into a device
            // that answered no questions at all until it was restarted.
            //
            // So the service carries on without foreground protection, which is
            // worse than with it and far better than not loading a brain: Android
            // may freeze or kill this process, and the sticky restart below is what
            // brings it back. The readiness report is the brain's own voice and is
            // left to say what the brain is doing.
            global::Android.Util.Log.Warn(LogTag,
                "carrying on without foreground protection - the models still load, "
                + "and Android may freeze or reclaim this process at any time");

            // NOT A RETURN. Everything below this point is what the brain is built
            // from - the memory probe, connectivity, the pressure callbacks - and
            // returning here skipped all of it, so a refused foreground type left
            // the model selector reading the GC heap limit and sizing the phone like
            // a wearable. Only the notification renewal is skipped, and that is the
            // one thing that would have been a lie.
            foreground = false;
        }

        // ONLY ONCE THE SERVICE IS ACTUALLY IN THE FOREGROUND. Renewing a
        // notification for a service that failed to start would keep the shade
        // claiming a microphone that was refused - the same lie this mechanism
        // exists to end, told from the other direction.
        if (foreground) KeepNotificationAlive();

        // The probe before the brain. Everything the Neuron decides about which
        // models fit is read off DeviceProbe, so measuring the phone has to happen
        // before anything asks — otherwise the first answers are made on the GC
        // heap limit and the device looks like a wearable.
        AndroidDeviceMemory.Install(this);
        AndroidConnectivity.Install(this);

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

        // THE MODEL IS ANOTHER PROCESS'S WHEN THE HOST SAYS SO. With a supervised brain
        // this service only asks it to load; without one (a host that keeps the brain
        // in this process) it builds the node as before.
        _ = System.Threading.Tasks.Task.Run(Brain is not null ? PreloadAsync : BuildNodeAsync);
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
                // owns an CircleAISession - and uses this service for ONE thing: holding
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

            // EVERY STATE ON THIS PATH GOES TO LOGCAT AS WELL AS THE SHADE. It used
            // to go ONLY to the notification: a load that failed wrote its reason into
            // a line of text on the lock screen and nowhere else, so from outside the
            // phone the service was indistinguishable from one still thinking. The
            // only branch that logged was "no brain was asked for".
            Status = "loading the model…";
            State  = ServiceState.Loading;

            // DECLARED OUT HERE so the breadcrumb below can name it. It is resolved
            // inside the lock that builds the node, and that block has closed before
            // the load it has to describe.
            string? wanted = null;

            // DID THE LAST ONE OF THESE KILL THE PHONE? Normally answered already, by
            // the host, before the catalogue bootstrap - see RecordLastLoad. Asked
            // here only for a host that never did.
            if (!_lastLoadRecorded && StateDirectory is { } crumbDir)
                RecordLastLoad(this, Catalogue, crumbDir);

            global::Android.Util.Log.Info(LogTag, Status);
            Notify(Status);

            // THE CATALOGUE FIRST, AND OUTSIDE THE LOCK. The selection below reads
            // it, and an await cannot sit inside a lock - the first version of this
            // wait did, which does not compile (CS1996) on the only target that
            // builds this file, while every test leg, which excludes it, stayed green.
            await WaitForCatalogueAsync().ConfigureAwait(false);

            NeuronNode node;
            lock (Gate)
            {
                if (Node is not null) { Status = "ready"; State = ServiceState.Ready; Notify(Status); return; }

                // The memory manager goes IN, not alongside. Passed here it reaches
                // AIService's brownout path — the one that downshifts and releases —
                // so Android's onTrimMemory and the idle timer both end up pulling
                // the same lever the design already built.
                // A LOADER AND A SELECTOR, OR THERE IS NO MODEL. These were all null,
                // and that was survivable only while nothing ever asked this service
                // for a brain: AIService.ResolveModelPathAsync throws "needs either
                // AIOptions.ModelPath or an IModelLoader" the moment it is started,
                // the catch below wrote the reason into a notification, and Node
                // stayed null for ever. From outside, every question came back
                // "brain warming up, try again shortly" - indefinitely.
                //
                // THE SELECTOR IS THE POINT, not a detail. Pinning AIOptions.ModelPath
                // would also have cleared the exception, and would hardcode a model
                // name into the service - the one thing model acquisition must never
                // do. DeviceAwareModelSelector asks what THIS phone can hold and
                // picks; that is how a P30 and a flagship run the same build.
                //
                // The same three objects CircleAISession builds for an in-process
                // brain, over the same store, so a service and a host head cannot
                // disagree about which model the device is running.
                var options  = factory();
                var registry = new ModelRegistryService();
                var loader   = new BundleModelLoader(options.ModelStorageDirectory!, registry);
                // THE ASSESSED CATALOGUE, WHERE THERE IS ONE. This line was
                // `new DeviceAwareModelSelector(registry)` and it is the reason
                // the catalogue had no callers: SqliteModelCatalog, the assessor
                // and CatalogModelSelector were all built and tested while the
                // live path re-derived fit from the shipped JSON. So a crash
                // verdict written by CrashVerdict.Record could be read by
                // IsRefused and still not reach the selection itself.
                //
                // Preferring falls back to the registry until the first
                // assessment lands, because CatalogueBootstrap runs on a
                // background task and BestFit throws on an unassessed catalogue.
                // (This method waited for that bootstrap before taking the lock -
                // see WaitForCatalogueAsync.)
                var selector = CircleAI.Inference.CatalogModelSelector.Preferring(Catalogue, registry);

                // THE BRAIN LOADS WHAT IS THERE. IT DOES NOT FETCH.
                //
                // AIService.ResolveModelPathAsync downloads a missing model as part of
                // starting, which is right for a host that has no other way to get one
                // and wrong here: this app now has a setup path that a person drives
                // from a client, and two downloads of the same file is not a race that
                // resolves. It produced exactly that -
                //
                //   Download of '.../Qwen3.6-35B-A3B-MNN/.../llm.mnn.weight' failed
                //   after 4 attempt(s) ... 'llm.mnn.weight.tmp' because it is being
                //   used by another process
                //
                // - with the setup run holding the temp file, the brain retrying four
                // times, failing, and the node never coming up while the download it
                // was fighting carried on perfectly well.
                //
                // IT IS ALSO THE WRONG MOMENT TO SPEND SOMEBODY'S DATA. Asking a
                // question should never be what starts a 21 GB download; the setup
                // screen exists so that decision is made deliberately, with the size
                // written next to it.
                //
                // So: if the chosen model is not on disk, say so and stop. The client
                // reads that as "not set up", which is exactly what it is, and offers
                // the download through the path built for it.
                wanted = options.ModelId;

                // NOT EVEN WHEN IT IS NAMED EXPLICITLY. "Nothing less than Qwen3.5
                // is the LLM" holds on every route, and a configured ModelId is the
                // one that walks around the selector.
                if (!string.IsNullOrWhiteSpace(wanted) && registry.GetLatestModel(wanted!) is { Retired: true })
                {
                    global::Android.Util.Log.Warn(LogTag,
                        wanted + " is retired below the brain's floor; choosing instead");
                    wanted = null;
                }

                if (string.IsNullOrWhiteSpace(wanted))
                {
                    var best = selector.BestFit(DeviceProbe.Snapshot(), options.RequiredCapabilities);
                    wanted = best?.ModelId;
                }

                // IF THE ONE IT CHOSE IS NOT HERE, USE THE BEST ONE THAT IS.
                //
                // The selector ranks the CATALOGUE and has no idea what was ever
                // downloaded, so it can name a model this phone does not have while
                // a better one sits on disk. Measured on a Circle OS device on
                // 2026-10-03: it wanted Qwen3.5-0.8B-MNN, which was never downloaded,
                // while a complete 1.17 GB Qwen3.5-2B and a 35B MoE were present -
                // and the person was told their phone had no model at all.
                //
                // A download is still a deliberate act made on the setup screen with
                // the size written next to it. This does not start one; it only
                // refuses to ignore what somebody already chose to fetch.
                if (!string.IsNullOrWhiteSpace(wanted) && !loader.ModelPresent(wanted!))
                {
                    // THE PROBE GOES IN, because standing in for the selector is not
                    // licence to overrule it. Without it this picked the heaviest
                    // model on disk regardless of fit: on 2026-10-04 that was a
                    // 22.8 GB MoE on a 7.6 GB phone, and loading it aborted the
                    // process and set lowmemorykiller on a dozen system apps.
                    var here = loader.BestInstalledChatModel(
                        DeviceProbe.Snapshot(),
                        refused: id => CircleAI.Inference.CrashVerdict.IsRefused(Catalogue, id));
                    if (!string.IsNullOrWhiteSpace(here))
                    {
                        global::Android.Util.Log.Info(LogTag,
                            "the selector wanted " + wanted + ", which is not on this phone; "
                            + "using " + here + ", which is");

                        // AND SAID OUT LOUD, NOT ONLY TO LOGCAT. This is exactly
                        // the decision a person is entitled to hear: the thing
                        // they are talking to is not the one the device would
                        // have chosen. The sentence is AssistantPersona's; the
                        // sequence number is what lets a client speak it once.
                        CircleNeuronLinkService.Decided(
                            CircleAI.Assistant.AssistantPersona.OnTheSmallerBrain);

                        wanted = here;
                    }
                }

                if (string.IsNullOrWhiteSpace(wanted) || !loader.ModelPresent(wanted!))
                {
                    Status = "no model on this device yet — set it up first";
                    State  = ServiceState.Idle;

                    // WHICH ONE IT WANTED, BECAUSE "no model" CAN MEAN TWO THINGS and
                    // they need opposite fixes. On a Circle OS device on 2026-10-03
                    // this printed while a complete 1.17 GB Qwen3.5-2B sat on disk:
                    // the selector had chosen something else and ModelPresent was
                    // false for THAT, so a phone carrying a better model than the P30
                    // was told it had none. Nothing in the log said which id was
                    // being looked for, so the only way to tell the two apart was to
                    // read the selector's source and guess.
                    //
                    // The status line stays plain - it is what a person reads - and
                    // the detail goes to the log, where whoever is debugging is.
                    global::Android.Util.Log.Info(LogTag, Status
                        + " (wanted: " + (string.IsNullOrWhiteSpace(wanted) ? "nothing - the selector chose none" : wanted)
                        + "; on disk: " + loader.InstalledIdsForLog() + ")");
                    Notify(Status);
                    return;
                }

                // AND NOT THE ONE THAT KILLED THE PHONE, whichever route chose it.
                // The filter above only guards the installed-fallback; the selector's
                // own pick and an explicitly configured ModelId both arrive here
                // ungated, and on the device that found this the selector was not the
                // route that chose the fatal model anyway. One check, after every
                // route, is the only version of this that cannot be walked around.
                // A REFUSAL IS A DECISION, AND IT USED TO BE SILENT. The phone
                // worked out, from a crash it survived, that it will not run
                // this again - and the only trace was a log line.
                if (CircleAI.Inference.CrashVerdict.IsRefused(Catalogue, wanted))
                {
                    var instead = loader.BestInstalledChatModel(
                        DeviceProbe.Snapshot(),
                        refused: id => CircleAI.Inference.CrashVerdict.IsRefused(Catalogue, id));

                    global::Android.Util.Log.Warn(LogTag,
                        wanted + " is refused on this phone ("
                        + CircleAI.Inference.CrashVerdict.Reason + "); "
                        + (instead is null ? "nothing else is here" : "using " + instead));

                    if (string.IsNullOrWhiteSpace(instead))
                    {
                        Status = "the brain this phone can run is not here yet";
                        State  = ServiceState.Idle;
                        CircleNeuronLinkService.Decided(
                            CircleAI.Assistant.AssistantPersona.NothingItCanRun);
                        Notify(Status);
                        return;
                    }

                    // IT GAVE UP ON ONE AND PICKED ANOTHER. A person who watched
                    // their phone die loading something deserves to be told it
                    // will not be tried again, rather than quietly getting a
                    // different answer from a different model.
                    CircleNeuronLinkService.Decided(
                        CircleAI.Assistant.AssistantPersona.OnTheSmallerBrain);

                    wanted = instead;
                }

                // AND SAY WHICH ONE, WHICH NOTHING EVER DID. The shade and the log
                // both read "loading the model…" on a phone holding seven of them,
                // chosen by a fit rule nobody can see the output of - so when an
                // answer came back wrong there was no way to tell whether the model
                // was bad, the wrong one, or fine and the prompt was at fault. The
                // desktop head has printed this since the day it was written; this
                // one, which is the one people actually use, did not.
                global::Android.Util.Log.Info(LogTag, "model: " + wanted);

                // AND NOW IT IS ACTUALLY PINNED. This comment used to claim the id was
                // "pinned on the instance the factory made" while ModelId was
                // init-only, so the pin was a no-op: AIService selected again from
                // the catalogue and downloaded whatever IT chose. Measured on a
                // Circle OS device on 2026-10-03 - the service logged "model:
                // Qwen3.5-2B-MNN" and then fetched a 0.8B nobody asked for, next to a
                // complete 2B. Everything above this line decides; this makes the
                // decision stick.
                options.ModelId = wanted;
                RunningModel    = wanted;
                node = new NeuronNode(new AIService(
                    options,
                    modelLoader:          loader,
                    generatorFactory:     null,
                    modelSelector:        selector,
                    modelRegistry:        registry,
                    memoryPressureSource: Memory));
                Node = node;
            }

            // Warm it here, not on the first question. The whole reason this is a
            // service is so nobody waits 13-23 s mid-sentence.
            // THE MARK THAT OUTLIVES AN ABORT. Written and flushed to disk before the
            // call, deleted after it returns - so a file still here on the next start
            // is proof the process died in there, and it names which model was being
            // loaded. The managed catch at the bottom of this method cannot see a
            // SIGABRT; nothing can, from inside the process. This is read at the top
            // of this method.
            var risky = StateDirectory;
            if (risky is not null)
                CircleAI.Assistant.DeviceDiagnostics.BeginRisky(
                    risky, CircleAI.Inference.CrashVerdict.Breadcrumb(wanted ?? "the model"));
            try
            {
                await node.Brain.StartAsync().ConfigureAwait(false);
            }
            finally
            {
                if (risky is not null) CircleAI.Assistant.DeviceDiagnostics.EndRisky(risky);
            }

            Status = node.IsReady ? "ready" : node.StatusMessage;
            State  = node.IsReady ? ServiceState.Ready : ServiceState.Failed;
            if (node.IsReady)
            {
                global::Android.Util.Log.Info(LogTag, "brain ready");

                // A CLEAN LOAD FORGIVES THE STRIKES. They count deaths IN A ROW; a
                // model that has just loaded showed an earlier death was the phone's.
                CircleAI.Inference.CrashVerdict.Loaded(Catalogue, wanted);

                // THE RESUME USED TO HANG OFF THIS LINE, AND THAT WAS THE WRONG HOOK.
                // A Circle OS device held 1.1 GB of a 22.8 GB Qwen3.6-35B-A3B with
                // llm.mnn.weight never arrived, and the fix for it was wired here -
                // where it never ran, because this brain only starts when somebody
                // asks a question and nobody had. Verified on the device: the service
                // process was up, bound, and had logged not one line under this tag.
                //
                // It belongs to the process that OWNS the models, beside the
                // housekeeping sweep in ServiceApplication, which runs on every
                // start whether or not anybody is chatting. One owner, so two
                // gigabyte downloads cannot be started at once - which is the state
                // the resume exists to clear rather than create.
            }
            else global::Android.Util.Log.Warn(LogTag, "brain did not come up: " + Status);

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
            global::Android.Util.Log.Error(LogTag, $"{ex.GetType().Name}: {ex}");
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
            RunningModel = null;
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

    /// <summary>
    /// Whether Android says this service's last death was plainly not the model's.
    /// </summary>
    /// <remarks>
    /// THE PLATFORM'S WORD, WHERE IT GIVES ONE. From Android 11 the system records
    /// why each process ended. A breadcrumb that survived proves only that the
    /// process died inside a load; this says whether it was stopped by the person,
    /// updated underneath itself (which every adb install mid-load does), or lost a
    /// process it was bound to - none of which is the model's doing.
    /// <para>
    /// A LOW-MEMORY KILL IS NOT EXEMPT, on purpose. A model too big for the phone
    /// dies by exactly that kill on every load, and exempting it would let it loop
    /// for ever. Those stay with the two-strike rule in CrashVerdict.
    /// </para>
    /// <para>
    /// Before Android 11 there is no record, and this answers false: the two-strike
    /// rule alone decides, which is still far better than one.
    /// </para>
    /// </remarks>
    private static bool LastDeathWasTheSystems(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return false;

        try
        {
            var am = (ActivityManager?)context.GetSystemService(Context.ActivityService);
            var exits = am?.GetHistoricalProcessExitReasons(context.PackageName, 0, 1);
            if (exits is null || exits.Count == 0) return false;

            // THE DOCUMENTED INTEGERS, NOT THE BINDING'S NAMES. Android's REASON_*
            // values are public, stable API (ApplicationExitInfo); comparing on them
            // compiles whether the binding surfaces Reason as an int or as an enum,
            // and does not depend on how it chose to name the members.
            //
            // EXIT_SELF (1) IS NOT ON THIS LIST. Nothing in this codebase exits its own
            // process, so a self-exit during a load is a native library calling exit()
            // - which is the model's failure, not the platform's.
            const int PermissionChange = 8, UserRequested = 10,
                      UserStopped = 11, DependencyDied = 12,
                      PackageStateChange = 15, PackageUpdated = 16;   // the last two: API 34

            var why = (int)exits[0].Reason;
            var notTheModel =
                   why is PermissionChange or UserRequested or UserStopped or DependencyDied
                || (OperatingSystem.IsAndroidVersionAtLeast(34) && why is PackageStateChange or PackageUpdated);

            global::Android.Util.Log.Info(LogTag,
                $"last exit: reason={why} ({exits[0].Description ?? "no description"}) - "
                + (notTheModel ? "not held against the model" : "counted as a strike if it was mid-load"));
            return notTheModel;
        }
        catch
        {
            // No record is not evidence either way; the strike rule decides.
            return false;
        }
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

    /// <summary>Writes the shade, with one line that no caller may write over.</summary>
    /// <remarks>
    /// THE OPEN MICROPHONE WINS, AND IT HAD BEEN LOSING A RACE. Two things write
    /// this notification: the listener, through RefreshNotification, and the brain,
    /// which reports "loading the model…" and then "ready" as it comes up. The
    /// listener starts first and the model finishes loading several seconds later —
    /// so on a P30 on 2026-10-01 the shade read "ready" while the microphone was
    /// open and the wake lock was held, which is precisely the state this file spends
    /// two paragraphs saying must never be shown.
    /// <para>
    /// Fixed here rather than at either caller because there is no sequence of two
    /// independent writers that is safe: whoever writes last wins, and one of them is
    /// a model load whose timing nobody controls. The microphone is not a status
    /// among others, it is a disclosure, so it is composed in at the one place every
    /// write passes through.
    /// </para>
    /// </remarks>
    private void Notify(string text)
    {
        try
        {
            text = MicrophoneDisclosure() ?? text;

            // Remembered so a renewal can repeat it rather than re-deriving it.
            _lastText = text;

            // POSTED ONLY WHEN IT CHANGED, AND THAT IS NOT AN OPTIMISATION.
            // KeepNotificationAlive ticks every 30 seconds and calls
            // Notify(_lastText) - the same string - so this posted 2 880 identical
            // notifications a day. Measured on the P30 on 2026-10-09:
            //
            //   post_frequency{pkg=...circleai.service, day=1, count=2970, demoted=2970}
            //   post_frequency{pkg=...circleai.service, day=2, count=3101, demoted=3101}
            //
            // EVERY ONE DEMOTED. WhatsApp posts 10-21 a day on the same handset.
            // Android had effectively muted the channel, which means the foreground
            // notice a person relies on to see a 2.8 GB download was being
            // suppressed BECAUSE it repeated itself - the disclosure this file
            // spends two paragraphs insisting must always be visible.
            //
            // The renewal's real job is ArmReaper, the dead-man's switch for a
            // notification that outlives its process on EMUI, and that still runs
            // on every tick. Re-posting unchanged text was never what kept the
            // notice alive; Android does not expire it, and an FGS notification
            // cannot be dismissed while the service runs. A genuine change - the
            // microphone opening, the model finishing, an error - still posts the
            // instant it happens, because this compares the FINAL text, after
            // MicrophoneDisclosure has had its say.
            if (string.Equals(text, _lastPosted, StringComparison.Ordinal)) return;

            if (GetSystemService(NotificationService) is NotificationManager nm)
            {
                nm.Notify(NotificationId, BuildNotification(text));
                _lastPosted = text;
            }
        }
        catch { /* the notification is a courtesy; never take the service down for it */ }
    }

    /// <summary>
    /// The text actually handed to NotificationManager, so an unchanged renewal
    /// can be skipped.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="_lastText"/>, which is what the
    /// renewal REPEATS and must keep being set even on a skipped post - otherwise
    /// a renewal after a skip would repeat a stale string. Null until the first
    /// successful post, so the first notification is never swallowed.
    /// </remarks>
    private string? _lastPosted;
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
