// DeviceSetup.cs
//
// First run on this phone, over the same FirstRun planner the native head uses.
//
// THE PLAN IS NOT REIMPLEMENTED. FirstRun.Plan decides what to fetch and in what
// order, and every one of those decisions was argued: two voices by NAME rather
// than by fit, because asking for "a TTS model that fits" once put a Nepali voice
// in an English assistant's mouth; anything already on disk skipped, so an
// interrupted setup resumes rather than restarting; a phone too small for a chat
// model still gets a voice and ears rather than being refused outright.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceSetup : ISetup
{
    private static string StorageDir => ModelStore.Path;

    /// <summary>The run in flight, if there is one.</summary>
    /// <remarks>
    /// This service is a singleton, which is what makes one shared run possible:
    /// the page comes and goes, the download does not.
    /// </remarks>
    private Task? _run;

    /// <summary>Who wants to hear about it. The page re-subscribes when it returns.</summary>
    private readonly List<IProgress<SetupProgressReport>> _listeners = [];

    private readonly object _gate = new();

    /// <inheritdoc />
    public bool IsRunning
    {
        get { lock (_gate) return _run is { IsCompleted: false }; }
    }

    /// <inheritdoc />
    public Task<Readiness> ReadinessAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);

            bool Has(ModelModality m) => registry.AllModels
                .Any(e => e.Modality == m && loader.ModelExists(e.Name));

            var voice = Has(ModelModality.Tts);
            var ears = Has(ModelModality.Asr);
            var brain = Has(ModelModality.Chat);
            var anything = voice || ears || brain;

            // The same wording the native screen uses, kept here rather than in
            // the page: it is a description of a device, not of a layout.
            const string lead = "Tap and talk";

            if (voice && ears && brain)
                return new Readiness(ReadyStage.Ready, lead, "", CanTalk: true);

            // "GETTING READY" IS ONLY TRUE WHILE SOMETHING IS ACTUALLY COMING.
            //
            // This used to be the resting state for half the permutations - ears
            // but no voice, a brain but neither, anything that was not one of the
            // three cases spelled out below - and it told the owner of the phone
            // "you can talk to it in a moment" when nothing was downloading and
            // nothing ever would. There is no background fetcher in this app;
            // the only thing that fetches is the wizard, and Home only offers the
            // wizard when the stage is NeedsSetup. So every one of those states
            // was a dead end that claimed to be progress: wait for a moment that
            // never comes, with no way from that screen to make it come.
            //
            // IsRunning is what makes the sentence checkable rather than hopeful.
            if (IsRunning)
                return new Readiness(ReadyStage.Waking, "Getting ready",
                    "You can talk to it in a moment.", CanTalk: voice && ears);

            // CAN TALK BEFORE IT CAN THINK. As soon as it can hear and speak,
            // pressing the circle does something useful even though the brain is
            // not here - Home's tap greets you in a catalogued language.
            if (voice && ears)
                return new Readiness(ReadyStage.CanListen, lead,
                    "Answering still needs a download.", CanTalk: true);

            // ANYTHING ELSE IS MISSING SOMETHING AND NOTHING IS COMING FOR IT, so
            // it says so and the tap goes where that can be fixed. Two wordings,
            // because arriving at a fresh phone and coming back to a half-finished
            // one are different moments: one is an invitation, the other is a job
            // left undone, and the second must not read as though nothing has been
            // done at all.
            return anything
                ? new Readiness(ReadyStage.NeedsSetup, "Finish setting it up",
                    "Some of it is still missing. Tap to get the rest.", CanTalk: false)
                : new Readiness(ReadyStage.NeedsSetup, "Let's set it up",
                    "It needs a few things first. Tap to start.", CanTalk: false);
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<SetupItem>>(() =>
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);

            // speech: true, because this head compiles the voice stack in. The
            // chat-only APK passes false so setup does not spend somebody's data
            // on 140 MB no line of that binary can open.
            return FirstRun.Plan(registry, loader, DeviceProbe.Snapshot(), speech: true)
                .Select(s => new SetupItem(s.Title, s.Model.TotalBytes))
                .ToList();
        }, ct);

    /// <inheritdoc />
    public Task<Census> CensusAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);

            // speech: true for the same reason PlanAsync passes it - this head
            // compiles the voice stack in, and a census that counted a voice a
            // chat-only build cannot open would be describing a different app.
            var census = FirstRun.Census(registry, loader, DeviceProbe.Snapshot(), speech: true);

            return new Census(
                census.Rows
                    .Select(r => new CensusRow(r.Title, r.Present, r.Bytes, r.Detail))
                    .ToList(),
                census.Present,
                census.Total,
                $"{census.Present} of {census.Total} on this phone");
        }, ct);

    /// <inheritdoc />
    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // ATTACH, DO NOT DUPLICATE. Whoever asks second is a page that came
            // back to a download already in progress, not a second download.
            _listeners.Add(progress);
            if (_run is { IsCompleted: false }) return _run;

            _run = Task.Run(async () =>
            {
                using var registry = new ModelRegistryService();
                using var loader = new BundleModelLoader(StorageDir, registry);
                var steps = FirstRun.Plan(registry, loader, DeviceProbe.Snapshot(), speech: true);

                var inner = new Progress<SetupProgress>(p =>
                {
                    var report = new SetupProgressReport(
                        p.Index, p.Count, p.Title, p.Fraction, p.Remaining);

                    // Copied under the lock: a page attaching mid-report would
                    // otherwise mutate the list being walked.
                    IProgress<SetupProgressReport>[] listeners;
                    lock (_gate) listeners = _listeners.ToArray();
                    foreach (var l in listeners) l.Report(report);
                });

                try
                {
                    await FirstRun.RunAsync(loader, steps, inner, ct).ConfigureAwait(false);
                }
                finally
                {
                    // A finished run holds nothing: the next Start is a real
                    // start, and a page that never came back is not kept alive
                    // by a list this class owns.
                    lock (_gate) _listeners.Clear();
                }
            }, ct);

            return _run;
        }
    }

    /// <inheritdoc />
    public async Task<bool> AllowMicrophoneAsync(CancellationToken ct = default)
    {
        var status = await Permissions.CheckStatusAsync<Permissions.Microphone>()
            .ConfigureAwait(false);
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.Microphone>()
                .ConfigureAwait(false);
        return status == PermissionStatus.Granted;
    }

    /// <inheritdoc />
    /// <remarks>
    /// PORTED FROM HomeActivity.AskToKeepRunning, vendor list and all. Huawei,
    /// Xiaomi, Oppo and Vivo each kill foreground services on their own schedule
    /// whatever Android says, and only the owner can exempt an app - so the
    /// standard intent is tried first, and the vendor's own screen after it.
    /// Every one of them is wrapped: these components move between firmwares,
    /// and an assistant that crashes asking permission to keep running is worse
    /// than one that quietly cannot.
    /// </remarks>
    public Task<bool> AllowBackgroundAsync(CancellationToken ct = default)
        => MainThread.InvokeOnMainThreadAsync(() =>
        {
            var context = Android.App.Application.Context;
            var package = context.PackageName!;

            // The standard one first. On phones that honour it, this is the whole fix.
            try
            {
                var pm = (Android.OS.PowerManager?)context.GetSystemService(
                    Android.Content.Context.PowerService);
                if (pm is not null && !pm.IsIgnoringBatteryOptimizations(package))
                {
                    var intent = new Android.Content.Intent(
                        Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations,
                        Android.Net.Uri.Parse("package:" + package));
                    intent.SetFlags(Android.Content.ActivityFlags.NewTask);
                    context.StartActivity(intent);
                    return true;
                }

                // Already exempt. Nothing to open, and nothing wrong.
                if (pm is not null) return true;
            }
            catch (Exception ex)
            {
                CircleAI.Voice.VoiceTrace.Write("battery exemption unavailable: " + ex.Message);
            }

            // Then the vendor's own list. Huawei first - it is the phone this was
            // built and measured on, and the one most likely to kill the service.
            foreach (var (pkg, cls) in new[]
            {
                ("com.huawei.systemmanager", "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity"),
                ("com.huawei.systemmanager", "com.huawei.systemmanager.optimize.process.ProtectActivity"),
                ("com.miui.securitycenter",  "com.miui.permcenter.autostart.AutoStartManagementActivity"),
                ("com.coloros.safecenter",   "com.coloros.safecenter.permission.startup.StartupAppListActivity"),
                ("com.vivo.permissionmanager","com.vivo.permissionmanager.activity.BgStartUpManagerActivity"),
            })
            {
                try
                {
                    var intent = new Android.Content.Intent();
                    intent.SetComponent(new Android.Content.ComponentName(pkg, cls));
                    intent.SetFlags(Android.Content.ActivityFlags.NewTask);
                    context.StartActivity(intent);
                    return true;
                }
                catch { /* not this vendor, or not this firmware */ }
            }

            return false;
        });

    /// <inheritdoc />
    public Task<IReadOnlyList<TourStep>> TourAsync(
        TimeSpan remaining, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<TourStep>>(() =>
        {
            // UNDER TWO MINUTES IS NOT WORTH INTERRUPTING. On a fast link setup
            // barely appears, and a tour that flashes up and vanishes is noise.
            if (remaining < TimeSpan.FromMinutes(2)) return [];

            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);

            bool Has(ModelModality m) => registry.AllModels
                .Any(e => e.Modality == m && loader.ModelExists(e.Name));

            var steps = new List<TourStep>();
            var budget = remaining.TotalSeconds;

            void Offer(string title, string body, string? action, string? route, int seconds)
            {
                if (seconds > budget) return;
                steps.Add(new TourStep(title, body, action, route));
                budget -= seconds;
            }

            if (Has(ModelModality.Tts))
                Offer("Your language",
                      "Hear it speak, and pick the one you want to be answered in.",
                      "Choose a language", "languages", 90);

            // "#microphone", not "wake". A label that says "allow" has to allow,
            // not travel somewhere that happens to ask on arrival.
            Offer("Let it hear you",
                  "The microphone is only used when you talk to it. Nothing is recorded.",
                  "Allow the microphone", "#microphone", 20);

            if (Has(ModelModality.WakeWord))
                Offer("Say “Hey B”",
                      "Wake it without touching the phone. Try it now and see it light up.",
                      "Try the wake word", "wake", 60);

            // THE STEP THAT DECIDES WHETHER ANY OF THE REST SURVIVES. Huawei,
            // Xiaomi, Oppo and Vivo all kill foreground services on their own
            // schedule whatever Android says, and only the owner can exempt an
            // app. Skip it and the assistant goes deaf an hour later for reasons
            // nobody can see - the phone will not say it did it.
            Offer("Keep it awake",
                  "This phone stops apps in the background. Allowing it to run means "
                + "it can still hear you later.",
                  "Allow it to run", "#background", 40);

            Offer("Build your CV while you wait",
                  "Answer a few questions and it writes one for you.",
                  "Start my CV", "career", 120);

            return steps;
        }, ct);
}
