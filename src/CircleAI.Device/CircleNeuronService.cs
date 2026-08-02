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
[Service(
    Name                  = "ai.circle.CircleNeuronService",
    Exported              = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
public sealed class CircleNeuronService : Service
{
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

    /// <summary>Stops the resident service and releases the models it holds.</summary>
    public static void Stop(Context context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var app = context.ApplicationContext ?? context;
        app.StopService(new Intent(app, typeof(CircleNeuronService)));
    }

    public override IBinder OnBind(Intent? intent) => new CircleNeuronBinder(this);

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        // Notification FIRST. Android gives a startForegroundService caller a few
        // seconds to call StartForeground or it kills the process with an ANR —
        // and building a Neuron takes far longer than that budget.
        try
        {
            EnsureChannel();
            var notification = BuildNotification("starting…");
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
                StartForeground(NotificationId, notification,
                    global::Android.Content.PM.ForegroundService.TypeDataSync);
            else
                StartForeground(NotificationId, notification);
        }
        catch (Exception ex)
        {
            Status = $"could not go foreground: {ex.Message}";
            State  = ServiceState.Failed;
            return StartCommandResult.Sticky;
        }

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
                Status = "no brain configured — set CircleNeuronService.OptionsFactory before Start";
                State  = ServiceState.Failed;
                Notify(Status);
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

    private Notification BuildNotification(string text) =>
        new Notification.Builder(this, ChannelId)
            .SetContentTitle("Circle AI")
            .SetContentText(text)
            .SetSmallIcon(global::Android.Resource.Drawable.IcMenuManage)
            .SetOngoing(true)
            .Build();

    private void Notify(string text)
    {
        try
        {
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
