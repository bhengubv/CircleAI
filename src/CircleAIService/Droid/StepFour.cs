// StepFour.cs
//
// STEP 4 OF THE OWNER'S FRAMEWORK, COMPOSED: EVERY BRAIN ON, PRIMED, BALANCED, AND VISIBLE TO APPS.
//
// A block of its own beside ServiceApplication.OnCreate, which calls it once in the main process,
// after the request path, the catalogue and the device facts exist and before the start-up fetch.
// Everything here wires parts that live in the library and are tested there:
//
//   ServicePreferences      the person's choices, read and changed over the link (SettingsGet/Set)
//   PreferenceDownloadGate  "not over mobile data" and the storage limit, on every download
//   SetupBoard              every model on its way in, for SetupProgress; titled in FirstRun's words,
//                           primed state followed from the readiness tracker (SetupBoardFollow)
//   priming on arrival      a brain that lands is loaded and warmed at once; the ears and the voice
//                           are warmed before anybody speaks
//   LinkImageStore          pictures across the link, on disk before their asks exist
//   BalanceLoop             shrink before dropping under heat, a flat battery or no memory, and grow
//                           back when calm - between turns, with bands and a ten-minute gap

using CircleAI.Assistant.Device;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Core;
using CircleAI.Core.Logging;
using CircleAI.Device;
using CircleAI.Inference;
using CircleAI.Linking;

namespace CircleAI.Service.Android;

public sealed partial class ServiceApplication
{
    private IDisposable? _boardFollow;
    private BalanceLoop? _balanceLoop;

    /// <summary>Wires Step 4 in the main process. Never throws: each part that fails says so and the rest go on.</summary>
    private void InstallStepFour()
    {
        var stateDir = System.IO.Path.Combine(FilesDir!.AbsolutePath, "CircleAI", "state");
        try { System.IO.Directory.CreateDirectory(stateDir); }
        catch (Exception ex) { CircleLog.Warn("Setup", "step 4: no state folder: " + ex.Message); }

        ServicePreferences? preferences = null;
        Step("preferences", () =>
        {
            // THE PERSON'S CHOICES, kept in the service's own storage and read on every use.
            preferences = ServicePreferences.Current = new ServicePreferences(System.IO.Path.Combine(stateDir, "preferences.json"));
            ApplyLogLevels(preferences.Get(ServicePreferences.LogLevels));
            preferences.Changed += (key, value) =>
            {
                if (key == ServicePreferences.LogLevels) ApplyLogLevels(value);

                // MOBILE DATA ALLOWED AGAIN, OR MORE ROOM: what waited for it can come now.
                if (key is ServicePreferences.MobileData or ServicePreferences.StorageCapGb) KickFetch();
            };
        });

        Step("download gate", () =>
        {
            var prefs = preferences;
            BundleModelLoader.DefaultGate = () => new PreferenceDownloadGate(
                mobileDataAllowed: () => prefs?.Flag(ServicePreferences.MobileData) ?? true,
                metered: Metered,
                storageCapGb: () => prefs?.Number(ServicePreferences.StorageCapGb) ?? 0,
                storeBytes: () => PreferenceDownloadGate.StoreBytes(ModelStore.Path));
        });

        Step("setup board", () =>
        {
            // EVERY ROW IN THE WORDS A PERSON READS: "the ears", not a file name.
            SetupBoard.TitleFor = id =>
            {
                using var registry = new CircleAI.Core.Models.ModelRegistryService();
                return registry.GetLatestModel(id) is { } entry ? CircleAI.Assistant.FirstRun.TitleOf(entry) : null;
            };

            // PRIMED BEFORE READY: the brain's verify, load, warm-up and Ready, as the supervisor
            // reports them, mirrored onto the brain's row.
            if (CircleNeuronService.Readiness is { } readiness) _boardFollow = SetupBoardFollow.Follow(readiness);
        });

        Step("priming on arrival", () =>
        {
            // A MODEL THAT LANDS IS PRIMED NOW, not when somebody first needs it - and not after the
            // whole fetch, which on a fresh phone is every voice behind the brain.
            BundleModelLoader.ModelLanded += name => _ = PrimeLandedAsync(name);

            // AND WHAT IS ALREADY HERE IS PRIMED ONCE HOUSEKEEPING HAS FINISHED: the ears (the brain
            // primes itself at start-up through the supervisor, the voice through WakeTurn.WarmAsync).
            _ = Task.Run(async () =>
            {
                await AfterHousekeepingAsync().ConfigureAwait(false);
                await PrimeEarsAsync().ConfigureAwait(false);
            });
        });

        Step("pictures", () =>
        {
            var images = new LinkImageStore(System.IO.Path.Combine(FilesDir!.AbsolutePath, "CircleAI", "pictures"));
            var swept = images.Sweep(TimeSpan.FromDays(1));
            if (swept > 0) CircleLog.Info("Setup", $"pictures: {swept} older than a day removed");
            CircleNeuronLinkService.Images = images;
            CircleNeuronLinkService.Shrink = bytes => AndroidImageShrink.Shrink(bytes, ImageBudget.VisionMaxEdge);
            if (CircleNeuronService.Asks is CircleAI.Assistant.Requests.RequestCoordinator coordinator)
                coordinator.Images = ask => images.For(ask.CallerKey, ask.ClientRequestId);
            CircleNeuronService.Readiness?.Setting(ReadinessSettings.TurnImages, "1");
        });

        Step("balance", () =>
        {
            if (_supervisor is not { } supervisor) return;
            var balance = new BrainBalance(file: System.IO.Path.Combine(stateDir, "balance.json"));

            // A HOT PHONE IS NOT FORGOTTEN BY A RESTART: the remembered rung is the first load's shape.
            _ = supervisor.ReshapeAsync(balance.Shape, CancellationToken.None);

            var coordinator = CircleNeuronService.Asks as CircleAI.Assistant.Requests.RequestCoordinator;
            var prefs = preferences;
            _balanceLoop = new BalanceLoop(
                balance,
                read: () => BalanceNow(supervisor),
                idle: () => coordinator?.IsIdle ?? true,
                apply: supervisor.ReshapeAsync,
                prime: ct => CircleNeuronService.Brain?.EnsureReadyAsync(ct) ?? Task.CompletedTask,
                rest: supervisor.RestIfIdleAsync,
                enabled: () => prefs?.Flag(ServicePreferences.Balance) ?? true,
                log: line => CircleLog.Info("Balance", line));
            _balanceLoop.Start(TimeSpan.FromMinutes(1));

            // SEEING RESTS WHEN THE SHAPE SAYS SO, and "what can you do now" says why.
            CircleNeuronLinkService.Resting = () => balance.Shape.Vision
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["Seeing"] = "resting while the phone is under pressure" };
            CircleLog.Info("Balance", $"balance on: {balance.Shape.Describe()}; remembered {string.Join(", ", balance.Worked.Select(w => $"{w.Key}={w.Value}"))}");

            // AND THE HEALTH VERB SAYS WHERE IT STANDS, beside the brain it shapes.
            var health = CircleNeuronLinkService.HealthFacts;
            CircleNeuronLinkService.HealthFacts = () =>
            {
                var facts = health?.Invoke()?.ToList() ?? new List<KeyValuePair<string, string>>();
                facts.Add(new("balance", balance.Shape.Describe()));
                facts.Add(new("setup", SetupBoard.Summary() is { } board ? $"{board.Settled} of {board.Count} settled" : "nothing on the way"));
                return facts;
            };
        });
    }

    // One part of Step 4, said when it fails and never allowed to stop the rest.
    private static void Step(string what, Action install)
    {
        try { install(); }
        catch (Exception ex) { CircleLog.Warn("Setup", $"step 4: {what} not installed: {ex.GetType().Name}: {ex.Message}"); }
    }

    // The person's log levels (log.levels), applied as they are set; empty keeps the defaults.
    private static void ApplyLogLevels(string levels)
    {
        if (string.IsNullOrWhiteSpace(levels)) return;
        try { CircleLog.Info("Setup", $"log levels from the settings: {CircleLog.ApplyLevels(levels)} applied"); }
        catch (Exception ex) { CircleLog.Warn("Setup", "log levels could not be applied: " + ex.Message); }
    }

    // Whether the active network is metered; null when the phone does not say.
    private bool? Metered()
    {
        try
        {
            return GetSystemService(ConnectivityService) is global::Android.Net.ConnectivityManager cm && cm.ActiveNetwork is not null
                ? cm.IsActiveNetworkMetered
                : null;
        }
        catch { return null; }
    }

    // What the balance reads: the battery and charger from the device context, the heat from the
    // thermal service, and a critical memory trim in the last ten minutes.
    private BalanceReading BalanceNow(CircleAI.Service.Droid.Inference.InferenceSupervisor supervisor)
    {
        PowerState power;
        try { power = DeviceContext.PowerProbe?.Invoke() ?? PowerState.Unknown; }
        catch { power = PowerState.Unknown; }

        var heat = _thermal?.CurrentState switch
        {
            CircleAI.Hosting.ThermalState.Critical => 2,
            CircleAI.Hosting.ThermalState.Serious => 1,
            _ => 0,
        };
        var memory = DateTimeOffset.UtcNow - supervisor.LastCriticalTrim < TimeSpan.FromMinutes(10);
        return new BalanceReading(power.BatteryPercent, power.Charging, heat, memory);
    }

    // Starts the background fetch when a preference may have let something through.
    private void KickFetch()
    {
        try
        {
            using var loader = new BundleModelLoader(ModelStore.Path, new CircleAI.Core.Models.ModelRegistryService());
            ModelFetchService.StartIfAnythingIsOwed(this, loader);
        }
        catch (Exception ex) { CircleLog.Warn("Setup", "the fetch could not start: " + ex.Message); }
    }

    // Waits for start-up housekeeping, which OnCreate sets just after this block runs.
    private static async Task AfterHousekeepingAsync()
    {
        for (var i = 0; i < 60 && CircleNeuronService.HousekeepingDone is null; i++)
            await Task.Delay(500).ConfigureAwait(false);
        await ModelSetup.WaitForHousekeepingAsync(CircleNeuronService.HousekeepingDone, _ => { }).ConfigureAwait(false);
    }

    // A model that just arrived: a brain is loaded and warmed (the supervisor decides which brain
    // runs - a better one waits to be validated); the ears are built and warmed; a voice warms the
    // greeting. Never throws.
    private async Task PrimeLandedAsync(string name)
    {
        try
        {
            using var registry = new CircleAI.Core.Models.ModelRegistryService();
            if (registry.GetLatestModel(name) is not { } entry) return;

            switch (entry.Modality)
            {
                case ModelModality.Chat when !BrainCompanions.IsCompanion(entry):
                    CircleLog.Info("Setup", $"{name} is here; priming the brain");
                    if (CircleNeuronService.Brain is { } brain) await brain.EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false);
                    break;
                case ModelModality.Asr:
                    await PrimeEarsAsync().ConfigureAwait(false);
                    break;
                case ModelModality.Tts when !CircleAI.Assistant.Device.WakeTurn.GreetingWarmed:
                    await PrimeVoiceAsync().ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex)
        {
            CircleLog.Info("Setup", $"{name} is here and could not be primed yet ({ex.GetType().Name}); it is primed when first used");
        }
    }

    // The ears built and warmed before anybody speaks, and said on the readiness tracker - which the
    // setup board follows.
    private static async Task PrimeEarsAsync()
    {
        if (CircleNeuronLinkService.Speech is not CircleAI.Assistant.Device.ServiceSpeech speech) return;
        var readiness = CircleNeuronService.Readiness;
        readiness?.Report(new ComponentState(ReadinessComponents.Ears, BrainPhase.Loading, LoadStep.Warming, null,
            PauseReason.None, "Getting its ears ready.", null, DateTimeOffset.UtcNow));

        var (id, why) = await speech.PrimeAsync().ConfigureAwait(false);
        readiness?.Report(id is not null
            ? ComponentState.Of(ReadinessComponents.Ears, BrainPhase.Ready, "Its ears are ready.", DateTimeOffset.UtcNow, id)
            : ComponentState.Of(ReadinessComponents.Ears,
                why.Contains("not on the phone", StringComparison.Ordinal) ? BrainPhase.NoModel : BrainPhase.Installed,
                "Its ears are not ready: " + why + ".", DateTimeOffset.UtcNow));
    }

    // The voice opened and the greeting rendered, so a wake never waits for it.
    private static async Task PrimeVoiceAsync()
    {
        var readiness = CircleNeuronService.Readiness;
        readiness?.Report(new ComponentState(ReadinessComponents.Voice, BrainPhase.Loading, LoadStep.Warming, null,
            PauseReason.None, "Getting its voice ready.", null, DateTimeOffset.UtcNow));
        await CircleAI.Assistant.Device.WakeTurn.WarmAsync().ConfigureAwait(false);
        readiness?.Report(CircleAI.Assistant.Device.WakeTurn.GreetingWarmed
            ? ComponentState.Of(ReadinessComponents.Voice, BrainPhase.Ready, "Its voice is ready.", DateTimeOffset.UtcNow)
            : ComponentState.Of(ReadinessComponents.Voice, BrainPhase.Installed, "Its voice warms when it first speaks.", DateTimeOffset.UtcNow));
    }
}
