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

            // THE FRONT DOOR OPENS BEFORE THE MACHINE IS MEASURED, and it used not to.
            //
            // Everything that stood between this process starting and the endpoint
            // binding was work about MODELS: the catalogue's assessment of ninety-odd
            // of them, the store walk that finds dead weight, the selector's query. A
            // caller asking "are you alive" waited for all of it. Measured on a clean
            // Windows 11 install on 2026-10-27: 447 s to the "listening" line with
            // housekeeping on the start path, 14.2 s once housekeeping moved - and this
            // moves what was left of it.
            //
            // SO WHICH MODEL IS NOT KNOWN AT THIS POINT, which is the whole of the
            // change. AIOptions.ModelId used to be filled in here from a choice made
            // just above it; AIService reads that property in StartAsync rather than in
            // its constructor, so the choice can be set the moment it is made and the
            // brain still gets it. `chosen` below is how the brain waits for it.
            //
            // A BRAIN WITH NO MODEL DOES NOT FAIL, IT HANGS. The core's resolve path
            // blocks and ignores its cancellation token, so a question asked against
            // an absent model never returns - measured here: 180 seconds, 55 MB
            // resident, 2.8 seconds of CPU. Nothing was loading; it was waiting. The
            // Android head grew the same guard for the same reason, and the guard is
            // still here - it has moved into the load delegate, beside the choice.
            //
            // AND "PRESENT" IS NOT "THE DIRECTORY EXISTS". This store held a
            // Ternary-Bonsai-2-27B folder of 0.0 GB - an abandoned fetch - which is
            // exactly the shape that gets chosen and then never loads.
            var data = DataDirectory();
            Directory.CreateDirectory(data);

            // THE MODELS LOOK AFTER THEMSELVES HERE AS THEY DO ON THE PHONE: an assessed
            // catalogue, the choice made from it, the leftovers swept, what is owed
            // fetched, and what a better model has superseded reclaimed.
            //
            // THIS HOST USED TO CHOOSE FROM THE BARE REGISTRY, and on this Windows box
            // on 2026-10-19 that chose Ternary-Bonsai-2-27B - a GGUF, in a build that
            // ships no llama.cpp for Windows - with 3.6 GB of it half down. It could
            // never have loaded. The assessed catalogue knows which engines this build
            // has, which is the whole difference.
            //
            // CIRCLEAI_UPKEEP=report says what it WOULD do and does none of it: no
            // fetch, no sweep, no reclaim. For a machine on a metered link, and for
            // seeing the plan before it spends anything.
            var reportOnly = string.Equals(Environment.GetEnvironmentVariable("CIRCLEAI_UPKEEP"), "report", StringComparison.OrdinalIgnoreCase);
            using var catalogue = new SqliteModelCatalog("Data Source=" + Path.Combine(data, "catalogue.db"));

            // RECLAIMED ONLY IN FAVOUR OF A MODEL THAT HAS LOADED HERE (CatalogueHousekeeping's
            // rule 5), so on a first run this is always empty, and it is said either way.
            // HOUSEKEEPING IS UPKEEP, NOT STARTUP, AND IT USED TO RUN HERE.
            // Reclaim walks the store and deletes; SweepAbandoned scans it. Both sat
            // between the process starting and the endpoint binding, so on a machine
            // with a few GB of models they were the first thing a person waited for and
            // the last thing they asked for. Measured on this dev box: 447 s to the
            // "listening" line with upkeep on, 14.2 s with it off, same binary.
            //
            // AND IT DELETES THINGS THE NEXT STEP WANTS. On 2026-10-25 the reclaim
            // took Qwen3-0.6B-MNN out from under a 32-bit service that then had no
            // model it could run and sat answering "the brain could not start" for
            // six asks. Doing it after a brain has loaded means the thing being
            // replaced has actually been replaced.
            //
            // It runs in the background block below, after the brain - and so does its
            // report, which no longer sits on the start path either: the report reads
            // the catalogue, and the catalogue is not assessed until after the bind.

            // THE CHOICE, AS A PROMISE RATHER THAN A VALUE. The machine is measured and
            // the model picked in that same background block; this is what the brain
            // waits on until then. It is ALWAYS completed - with null when nothing fits,
            // and in a finally when the assessment throws - so nothing waits for ever.
            var chosen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var options = new AIOptions
            {
                ModelStorageDirectory = store,
                WarmOnStart           = true,
                NeverFetch            = true,
                SystemPrompt          = CircleAI.Assistant.AssistantPersona.Prompt,
                OverviewAnswer        = CircleAI.Assistant.AssistantPersona.CanDoSpoken,
                // The same notes as the phone's - see BrainSkills.
                SkillStore            = CircleAI.Service.BrainSkills.Embedded(),
            };

            var service = new AIService(
                options,
                modelLoader:      loader,
                generatorFactory: null,
                modelSelector:    CatalogModelSelector.Preferring(catalogue, registry),
                modelRegistry:    registry);

            // THE SAME REQUEST PATH AS THE PHONE: durable questions, one worker, readiness.
            var readiness = new CircleAI.Assistant.Lifecycle.ReadinessTracker(Path.Combine(data, "readiness.json"));
            await using var brain = new InProcessBrainWorker(async ct =>
            {
                // IT WAITS FOR THE CHOICE RATHER THAN DENYING ONE. A question can now
                // arrive before the machine has been measured, and "no brain on this
                // device" is a wrong answer from a service that is still choosing one.
                var pick = await chosen.Task.WaitAsync(ct).ConfigureAwait(false);

                // ASKED AT LOAD TIME, NOT AT START: the fetch below may land it since.
                if (string.IsNullOrWhiteSpace(pick) || !loader.ModelPresent(pick!))
                    throw new ModelNotInstalledException(pick ?? "(none)", damaged: false);

                // SET ONE LINE BEFORE IT IS READ. AIService.StartAsync resolves
                // AIOptions.ModelId at that moment, so the order is guaranteed rather
                // than assumed, and the property is the only thing the choice has to
                // reach.
                options.ModelId = pick;
                await service.StartAsync(ct).ConfigureAwait(false);
                return new InProcessBrain(service, pick!);
            }, log: line => Console.WriteLine(line));

            // A MODEL THAT LOADED HERE IS PROVEN HERE - the only kind housekeeping lets
            // replace another (rule 5), so without this line nothing is ever reclaimed.
            brain.StatusChanged += (_, s) =>
            {
                if (s.Phase == CircleAI.Assistant.Lifecycle.BrainPhase.Ready && s.ModelId is { Length: > 0 } loaded)
                    try { catalogue.RecordLoad(loaded); } catch { /* a verdict lost costs a reclaim, not the brain */ }
            };
            brain.StatusChanged += (_, s) => readiness.Report(new CircleAI.Assistant.Lifecycle.ComponentState(
                CircleAI.Assistant.Lifecycle.ReadinessComponents.Brain, s.Phase, s.Step, s.LastFailure?.Kind,
                CircleAI.Assistant.Lifecycle.PauseReason.None,
                s.LastFailure is { } f ? CircleAI.Assistant.Lifecycle.ReadinessSentences.For(f.Kind)
                                       : CircleAI.Assistant.Lifecycle.ReadinessSentences.For(
                                             CircleAI.Assistant.Lifecycle.ComponentState.Of("brain", s.Phase, "", s.Since)),
                s.ModelId, s.Since, null, s.RetryAfter));
            // AND THE FIRST STATUS SAYS "CHOOSING", WHICH IS WHAT IS HAPPENING. This
            // reported Installed or NoModel from a choice already made; the choice is
            // now made after the bind, and reporting NoModel in that window would tell
            // a caller this machine has no brain before anything had looked.
            readiness.Report(CircleAI.Assistant.Lifecycle.ComponentState.Of(
                CircleAI.Assistant.Lifecycle.ReadinessComponents.Brain,
                CircleAI.Assistant.Lifecycle.BrainPhase.Unknown,
                "measuring this machine and choosing a model",
                DateTimeOffset.UtcNow));

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

            // THE PORT AND THE TOKEN ARE THE WHOLE INTERFACE, so they are printed
            // rather than left for somebody to find in a log. The endpoint generates
            // the token when none is configured, which means nothing can reach this
            // process without reading this line or being told it.
            //
            // PRINTED BEFORE THE BLOCK BELOW STARTS, so the log reads in the order the
            // service actually works: the door opens, and then the machine is measured.
            // Kicking the block off first would let its lines land above "listening" and
            // read as though the bind had waited for them again.
            Console.WriteLine($"listening on http://127.0.0.1:{endpoint.BoundPort}");
            Console.WriteLine($"X-Butler-Token: {endpoint.Token}");
            Console.WriteLine("Ctrl-C to stop.");

            // AND NOW THE WORK ABOUT MODELS, ALL OF IT OFF THE START PATH: what machine
            // this is, what the catalogue makes of it, which model it chooses - and then
            // the phone's order, what is half down first and then what the first-run plan
            // wants and has no byte of. The service answers what it can while gigabytes
            // arrive; a brain that lands is loaded straight away. Desktop builds have no
            // speech stack, so no voices.
            _ = Task.Run(async () =>
            {
                static void Say(string line) => Console.WriteLine(line);
                string? wanted = null;
                try
                {
                    // WHAT MACHINE THIS IS, SAID OUT LOUD - see MachineLineAsync.
                    Say(await MachineLineAsync(stopping.Token).ConfigureAwait(false));

                    // ONE READING OF THE DEVICE, shared by the assessment, the choice
                    // and the plan. Three snapshots could disagree, and the last would
                    // have decided - the repo's own "one fact with two owners" rule.
                    var probe = DeviceProbe.Snapshot();

                    var assessed = CatalogueBootstrap.Run(catalogue, DeviceModelAssessor.ForThisBuild(catalogue),
                        probe, modelsDirectory: store);
                    Say($"catalogue: {assessed.Assessed} assessed, {assessed.Compatible} run on this machine");

                    // SAID, NOT DELETED: bytes this build can never open, left for a person to clear.
                    foreach (var id in loader.InstalledIds().Where(loader.ModelPartial).Where(id => !loader.CanRunHere(id)))
                        Say($"dead weight: {id} is half downloaded and this build cannot run it; it is left for you to remove");

                    // THE DEVICE DECIDES, AND NOTHING ELSE DOES. The service runs on this
                    // machine, measures it, and only then picks its model from the assessed
                    // catalogue - the owner's rule (2026-10-21): anything other than that is
                    // wrong. CIRCLEAI_MODEL used to pin one here and skip the choice; it is
                    // gone.
                    wanted = CatalogModelSelector.Preferring(catalogue, registry)
                        .BestFit(probe, ChatCapability.Default)?.ModelId;

                    // A MISSING MODEL NO LONGER ENDS THE SERVICE. It serves its status - "no
                    // brain here yet" - and keeps every question it is asked waiting for one,
                    // instead of exiting with a code nobody reads.
                    var present = !string.IsNullOrWhiteSpace(wanted) && loader.ModelPresent(wanted!);
                    Say(present
                        ? $"model: {wanted}"
                        : $"no model here yet ({(string.IsNullOrWhiteSpace(wanted) ? "nothing fits this machine" : wanted + " is not under " + store)}); questions will wait for one");

                    readiness.Report(CircleAI.Assistant.Lifecycle.ComponentState.Of(
                        CircleAI.Assistant.Lifecycle.ReadinessComponents.Brain,
                        present ? CircleAI.Assistant.Lifecycle.BrainPhase.Installed : CircleAI.Assistant.Lifecycle.BrainPhase.NoModel,
                        present ? CircleAI.Assistant.Lifecycle.ReadinessSentences.Installed : CircleAI.Assistant.Lifecycle.ReadinessSentences.NoModel,
                        DateTimeOffset.UtcNow, wanted));

                    // AND THE CHOICE IS RELEASED, which unblocks any question that was
                    // asked while the machine was still being measured.
                    chosen.TrySetResult(wanted);

                    // THE SERVICE EXE OWNS THE MODEL, so it loads it now unless asked not
                    // to: a question should not be the thing that pays for the load.
                    if (present && Environment.GetEnvironmentVariable("CIRCLEAI_LAZY") != "1")
                        _ = brain.EnsureReadyAsync(stopping.Token)
                                 .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);

                    var plan = CircleAI.Assistant.FirstRun.Plan(registry, loader, probe, speech: false)
                        .Select(s => (Name: s.Model.Name, Bytes: s.Model.TotalBytes))
                        .ToList();
                    if (reportOnly)
                    {
                        var would = CatalogueHousekeeping.Plan(catalogue);
                        Say(would.Count == 0 ? "housekeeping: nothing to reclaim" : "housekeeping would reclaim:");
                        foreach (var item in would) Say($"  {item.Id}: {item.Reason}");

                        Say("setup: " + ModelSetup.Describe(loader));
                        foreach (var (name, have, need) in ModelSetup.Unfinished(loader))
                            Say($"would carry on with {name}: {have} of {need} bytes");
                        foreach (var (name, need) in ModelSetup.NotStarted(loader, plan, brainFirst: wanted))
                            Say($"would fetch {name} ({need} bytes)");
                        Say("upkeep: report only - nothing fetched, swept or reclaimed");
                        return;
                    }

                    await ModelSetup.ResumeUnfinishedAsync(loader, Say, stopping.Token).ConfigureAwait(false);

                    // THE BRAIN IS NOT ONE OF SEVENTY-FIVE. Naming it here lifts it to
                    // the head of the queue; everything else keeps smallest-first, which
                    // is still right for the wake word and a voice.
                    await ModelSetup.FetchNotStartedAsync(
                        loader, plan, Say, stopping.Token, landed: null, brainFirst: wanted)
                        .ConfigureAwait(false);

                    if (!present && !string.IsNullOrWhiteSpace(wanted) && loader.ModelPresent(wanted!))
                        await brain.EnsureReadyAsync(stopping.Token).ConfigureAwait(false);

                    // UPKEEP LAST, AND ONLY ONCE THERE IS A BRAIN. Housekeeping's rule 5
                    // only reclaims in favour of a model that has LOADED here, so running
                    // it before the brain is both useless and destructive - it has nothing
                    // to reclaim in favour of, and it can take the model about to be used.
                    CatalogueHousekeeping.Reclaim(catalogue, store, say: line => Say("housekeeping: " + line));
                    var freed = CatalogueHousekeeping.SweepAbandoned(store, TimeSpan.FromHours(1));
                    Say(freed > 0
                        ? $"housekeeping: swept {freed} bytes of abandoned downloads"
                        : "housekeeping: no abandoned downloads to sweep");
                }
                catch (OperationCanceledException) { /* asked to stop */ }
                catch (Exception ex) { Say("setup failed: " + ex.Message); }
                finally
                {
                    // NOTHING MAY WAIT FOR EVER ON A CHOICE THAT WAS NEVER MADE. If the
                    // assessment threw, a question still has to be answered - with "no
                    // brain here", which is true - rather than parked against a promise
                    // nobody will keep. TrySetResult, so the success path above keeps
                    // whatever it already set.
                    chosen.TrySetResult(wanted);
                }
            });

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

    /// <summary>One line naming the machine this service is running on.</summary>
    /// <remarks>
    /// THE LOG DESCRIBED THE MODELS AND NEVER THE MACHINE. A Windows 11 install log
    /// read back on 2026-10-27 - Core i7-1165G7, 32 GB, Iris Xe - said where the
    /// models live and that the catalogue had assessed ninety-six of them, and not
    /// one word about the hardware those ninety-six verdicts were computed from. So
    /// there was no way to tell from the log why the rest had been ruled out.
    /// <para>
    /// CapabilityProbe already reads all of it - CPU name, physical against logical
    /// cores, installed RAM, GPU with its VRAM, NPU - and no head on this platform
    /// had ever called it. <see cref="DeviceProbe.Snapshot"/>, which is what the
    /// assessment runs on, cannot: it hard-codes <see cref="GpuKind.None"/> unless a
    /// caller supplies one, so an Iris Xe reads as no GPU at all.
    /// </para>
    /// <para>
    /// AND THE LINE SAYS WHAT RUNS THE MODEL, NOT ONLY WHAT IS FITTED. The GPU and
    /// the NPU are named because they are there, and then it says plainly that
    /// inference is on the CPU - which is a fact about what ships, not a preference.
    /// Both Windows RIDs stage exactly seven natives: MNN.dll, mnnbridge.dll,
    /// llama.dll, llamabridge.dll and ggml{,-base,-cpu}.dll. There is no MNN_CL.dll,
    /// no MNN_Vulkan.dll and no ggml-cuda or ggml-vulkan, so there is no GPU backend
    /// for either engine to load; the Win32 MNN is built -DMNN_OPENCL=OFF
    /// -DMNN_VULKAN=OFF outright. The one GPU path this product has ever measured
    /// came in 22x SLOWER than the same phone's own CPU. Reporting the Iris Xe as a
    /// capability would be the same lie the missing line told, pointing the other way.
    /// </para>
    /// <para>
    /// OFF THE START PATH BECAUSE IT SHELLS OUT. WindowsCapabilityProbe runs
    /// powershell.exe four times - CPU name, physical cores, GPU, NPU - with 3 to 4 s
    /// timeouts each, so up to thirteen seconds of process spawning. That belongs
    /// after the endpoint binds, which is where it is called from. NEVER THROWS
    /// except on cancellation: a service that cannot describe its host still runs
    /// on it.
    /// </para>
    /// </remarks>
    private static async Task<string> MachineLineAsync(CancellationToken ct)
    {
        try
        {
            var host = await new CircleAI.Runtime.Capabilities.CapabilityProbe()
                .ProbeAsync(ct).ConfigureAwait(false);

            // INVARIANT ON EVERY NUMBER, FORMATTED BEFORE IT IS INTERPOLATED. This box
            // writes a decimal comma, and one interpolated string cannot be handed to
            // FormattableString.Invariant once it has been concatenated with another -
            // the trap ServiceApplication's device line records. Formatting each number
            // on its own sidesteps both.
            var inv   = System.Globalization.CultureInfo.InvariantCulture;
            var ramGb = (host.TotalPhysicalMemoryBytes / 1e9).ToString("F1", inv);
            var gpu   = host.Gpu is { } g
                ? g.VramBytes > 0 ? g.Model + " (" + (g.VramBytes / 1e9).ToString("F1", inv) + " GB)" : g.Model
                : "no GPU";
            var npu   = host.Npu is { } n ? n.Model : "no NPU";
            var bits  = (Environment.Is64BitProcess ? 64 : 32).ToString(inv);

            // WHICH ENGINES THIS BUILD ACTUALLY HAS, probed rather than declared -
            // ShippedEngines asks whether the llama.cpp native really loaded. It is the
            // reason a GGUF row is or is not compatible on this machine, so it belongs
            // on the same line as the hardware.
            var engines = string.Join(" + ", DeviceModelAssessor.ShippedEngines()
                .OrderBy(e => e)
                .Select(e => e switch
                {
                    ModelEngine.Mnn      => "MNN",
                    ModelEngine.LlamaCpp => "llama.cpp",
                    _                    => e.ToString(),
                }));

            return $"machine: {host.CpuModel}, {host.PhysicalCoreCount} cores / {host.LogicalCoreCount} threads, "
                 + $"{ramGb} GB RAM, {gpu}, {npu}, {host.Os} {host.OsVersion} {host.Arch} in a {bits}-bit process; "
                 + $"inference on the CPU, engines {engines}";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return "machine: could not be read (" + ex.Message + ")"; }
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
