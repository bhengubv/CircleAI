// Program.cs
//
// CircleAI as a service on a desktop, which is the same promise as on the phone with
// a different front door.
//
// THE ANDROID HEAD IS A BOUND SERVICE AND THAT SHAPE DOES NOT TRAVEL. A binder is
// Android's; Windows has no equivalent an app can attach to by package name. What is
// the same is the thing being offered - one process on this machine owns the models
// and answers for every app that asks - so the transport changes and nothing else
// does.
//
// HttpLoopbackEndpoint IS THAT TRANSPORT AND IT WAS ALREADY WRITTEN. Its own first
// line says "zero ASP.NET Core dependency and stay portable across Linux / macOS /
// Windows / Android", it binds loopback only, and it already carries a shared-secret
// header. It had no host on this platform; this is the host.
//
// LOOPBACK ONLY, DELIBERATELY. The endpoint binds 127.0.0.1 and nothing else: a
// machine-local engine is not a server, and a service that holds somebody's memory
// has no business being reachable from the network it happens to be on.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Hosting.Endpoints;
using CircleAI.Inference;

namespace CircleAI.Service.Host;

/// <summary>The desktop head: owns the models, answers over loopback.</summary>
public static class Program
{
    /// <summary>Starts the service and runs until it is asked to stop.</summary>
    public static async Task<int> Main(string[] args)
    {
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        var store = ModelStore();
        Console.WriteLine($"CircleAI service — models at {store}");

        try
        {
            using var registry = new ModelRegistryService();
            using var loader   = new BundleModelLoader(store, registry);

            // WHICH MODEL, AND IS IT ACTUALLY HERE. Resolved and printed before
            // anything can block on it.
            //
            // A BRAIN WITH NO MODEL DOES NOT FAIL, IT HANGS. The core's resolve path
            // blocks and ignores its cancellation token, so a question asked against
            // an absent model never returns - measured here: 180 seconds, 55 MB
            // resident, 2.8 seconds of CPU. Nothing was loading; it was waiting. The
            // Android head grew the same guard for the same reason.
            //
            // AND "PRESENT" IS NOT "THE DIRECTORY EXISTS". This store held a
            // Ternary-Bonsai-2-27B folder of 0.0 GB - an abandoned fetch - which is
            // exactly the shape that gets chosen and then never loads.
            //
            // CIRCLEAI_MODEL PINS ONE. On a phone the selector's answer is the only
            // sane one; on a desktop somebody may have four models and a preference,
            // and the alternative is editing a config file to try the other one.
            var wanted = Environment.GetEnvironmentVariable("CIRCLEAI_MODEL");
            if (string.IsNullOrWhiteSpace(wanted))
                // Through Preferring like every other site. This host builds no
                // assessed catalogue, so it resolves to the registry selector -
                // the same behaviour as before, now stated rather than assumed,
                // and one line to change if a catalogue is ever added here.
                wanted = CatalogModelSelector.Preferring(catalogue: null, registry)
                    .BestFit(DeviceProbe.Snapshot(), ChatCapability.Default)?.ModelId;

            // A MISSING MODEL NO LONGER ENDS THE SERVICE. It serves its status - "no
            // brain here yet" - and keeps every question it is asked waiting for one,
            // instead of exiting with a code nobody reads.
            var present = !string.IsNullOrWhiteSpace(wanted) && loader.ModelPresent(wanted!);
            Console.WriteLine(present
                ? $"model: {wanted}"
                : $"no model here yet ({(string.IsNullOrWhiteSpace(wanted) ? "nothing fits this machine" : wanted + " is not under " + store)}); questions will wait for one");

            var options = new AIOptions
            {
                ModelStorageDirectory = store,
                ModelId               = wanted,
                WarmOnStart           = true,
                NeverFetch            = true,
                SystemPrompt          = CircleAI.Assistant.AssistantPersona.Prompt,
                OverviewAnswer        = CircleAI.Assistant.AssistantPersona.CanDoSpoken,
            };

            var service = new AIService(
                options,
                modelLoader:      loader,
                generatorFactory: null,
                modelSelector:    CatalogModelSelector.Preferring(catalogue: null, registry),
                modelRegistry:    registry);

            // THE SAME REQUEST PATH AS THE PHONE: durable questions, one worker, readiness.
            var data = DataDirectory();
            Directory.CreateDirectory(data);
            var readiness = new CircleAI.Assistant.Lifecycle.ReadinessTracker(Path.Combine(data, "readiness.json"));
            await using var brain = new InProcessBrainWorker(async ct =>
            {
                if (!present) throw new ModelNotInstalledException(wanted ?? "(none)", damaged: false);
                await service.StartAsync(ct).ConfigureAwait(false);
                return new InProcessBrain(service, wanted!);
            }, log: line => Console.WriteLine(line));
            brain.StatusChanged += (_, s) => readiness.Report(new CircleAI.Assistant.Lifecycle.ComponentState(
                CircleAI.Assistant.Lifecycle.ReadinessComponents.Brain, s.Phase, s.Step, s.LastFailure?.Kind,
                CircleAI.Assistant.Lifecycle.PauseReason.None,
                s.LastFailure is { } f ? CircleAI.Assistant.Lifecycle.ReadinessSentences.For(f.Kind)
                                       : CircleAI.Assistant.Lifecycle.ReadinessSentences.For(
                                             CircleAI.Assistant.Lifecycle.ComponentState.Of("brain", s.Phase, "", s.Since)),
                s.ModelId, s.Since, null, s.RetryAfter));
            readiness.Report(CircleAI.Assistant.Lifecycle.ComponentState.Of(
                CircleAI.Assistant.Lifecycle.ReadinessComponents.Brain,
                present ? CircleAI.Assistant.Lifecycle.BrainPhase.Installed : CircleAI.Assistant.Lifecycle.BrainPhase.NoModel,
                present ? CircleAI.Assistant.Lifecycle.ReadinessSentences.Installed : CircleAI.Assistant.Lifecycle.ReadinessSentences.NoModel,
                DateTimeOffset.UtcNow, wanted));

            using var store2 = new CircleAI.Assistant.Requests.SqliteAskStore(Path.Combine(data, "asks.db"));

            // ONE MEMORY FOR THIS MACHINE'S SERVICE, as the phone has one for the device.
            using var memory = new CircleAI.Memory.MemoryService(Path.Combine(data, "memory"));
            var shortcuts = new List<CircleAI.Assistant.Requests.IAskShortcut>
            {
                new CircleAI.Assistant.Requests.RememberShortcut(_ => memory),
                new CircleAI.Assistant.Requests.RecallShortcut(_ => memory),
            };
            shortcuts.AddRange(CircleAI.Assistant.Requests.AskShortcuts.Standard());

            await using var coordinator = new CircleAI.Assistant.Requests.RequestCoordinator(
                store2, brain, CircleAI.Assistant.Requests.ServiceLimits.For(DeviceProbe.Snapshot()),
                readiness, conversations: null, shortcuts,
                isFirstParty: _ => true, log: line => Console.WriteLine(line));
            coordinator.Start();

            await using var endpoint = new HttpLoopbackEndpoint(options)
            {
                Asks = coordinator,
                Readiness = readiness,
                HealthFacts = () => new List<KeyValuePair<string, string>>
                {
                    new("pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new("workingSetBytes", Environment.WorkingSet.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new("gcHeapBytes", GC.GetTotalMemory(false).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new("queueDepth", coordinator.QueueDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    new("currentAsk", coordinator.CurrentAskId ?? string.Empty),
                    new("brainPhase", brain.Status.Phase.ToString()),
                    new("brainModel", brain.ModelId ?? string.Empty),
                },
                Quality = (caller, ct) => new CircleAI.Assistant.Quality.QualityRunner(coordinator, caller)
                    .RunAsync(null, TimeSpan.FromMinutes(3), ct),
            };
            await endpoint.StartAsync(service, stopping.Token).ConfigureAwait(false);

            // THE SERVICE EXE OWNS THE MODEL, so it loads it now unless asked not to: a
            // question should not be the thing that pays for the load.
            if (present && Environment.GetEnvironmentVariable("CIRCLEAI_LAZY") != "1")
                _ = brain.EnsureReadyAsync(stopping.Token).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

            // THE PORT AND THE TOKEN ARE THE WHOLE INTERFACE, so they are printed
            // rather than left for somebody to find in a log. The endpoint generates
            // the token when none is configured, which means nothing can reach this
            // process without reading this line or being told it.
            Console.WriteLine($"listening on http://127.0.0.1:{endpoint.BoundPort}");
            Console.WriteLine($"X-Butler-Token: {endpoint.Token}");
            Console.WriteLine("Ctrl-C to stop.");

            try { await Task.Delay(Timeout.Infinite, stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* asked to stop */ }

            await endpoint.StopAsync(CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            // A service that will not start has to say why on the way out: this runs
            // with no screen and its exit code is all a service manager reports.
            Console.Error.WriteLine("CircleAI service could not start: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Where the service keeps its questions and state (CIRCLEAI_DATA overrides).</summary>
    private static string DataDirectory()
    {
        var set = Environment.GetEnvironmentVariable("CIRCLEAI_DATA");
        if (!string.IsNullOrWhiteSpace(set)) return set!;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CircleAI", "service");
    }

    /// <summary>Where this machine keeps the models.</summary>
    /// <remarks>
    /// LocalApplicationData, NOT the install directory. The models are gigabytes, they
    /// are per-user, and a service installed under Program Files cannot write beside
    /// itself. CIRCLEAI_MODELS overrides it for a machine that keeps them on another
    /// disk, which on a desktop is a reasonable thing to want and on a phone is not.
    /// </remarks>
    private static string ModelStore()
    {
        var set = Environment.GetEnvironmentVariable("CIRCLEAI_MODELS");
        if (!string.IsNullOrWhiteSpace(set)) return set!;

        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "CircleAI", "models");
    }
}
