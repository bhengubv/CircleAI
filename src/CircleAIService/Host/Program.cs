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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
// NOT OPTIONAL, AND THE TYPE BEING FULLY QUALIFIED DOES NOT COVER IT. LogInformation and
// LogError are extension methods on LoggerExtensions in this namespace, so naming
// ILogger<T> by its full name still leaves them out of scope - the same shape as
// enclosing-namespace-hides-missing-usings, from the other direction.
using Microsoft.Extensions.Logging;

namespace CircleAI.Service.Host;

/// <summary>The desktop head: owns the models, answers over loopback.</summary>
public static class Program
{
    /// <summary>The name the Service Control Manager knows this process by.</summary>
    /// <remarks>
    /// IT WAS CALLED A SERVICE AND WAS NOT ONE. "I looked for Circle AI Service in the
    /// listings with other Windows Services and I didn't see it" - and it was not there,
    /// because nothing in this process ever answered the Service Control Manager. The
    /// released EXE reads PE subsystem 3, WINDOWS_CUI: a console application that printed
    /// "Ctrl-C to stop." and waited on Task.Delay(Infinite). No auto-start at boot, no
    /// restart after a crash, no `sc start`, and it died with the console that launched
    /// it. The repo's only install-windows-service.ps1 belonged to a different project.
    /// </remarks>
    public const string ServiceName = "CircleAIService";

    /// <summary>Starts the service and runs until it is asked to stop.</summary>
    /// <remarks>
    /// ONE BINARY, TWO LIFETIMES, ONE BODY. <see cref="RunAsync"/> is the whole service and
    /// neither host knows anything the other does not; what differs is only who says stop.
    /// A console run keeps Ctrl-C exactly as it was, and a controller-started run goes
    /// through <see cref="ServiceHost"/> so Start, Stop and Shutdown arrive from the SCM.
    /// <para>
    /// WindowsServiceHelpers.IsWindowsService IS THE DISCRIMINATOR, not a flag the
    /// installer has to remember to pass: it reads whether this process was started by
    /// the controller. A flag that the install script and the binary have to agree about
    /// is one fact with two owners, and this file has already paid for that twice today.
    /// </para>
    /// </remarks>
    public static async Task<int> Main(string[] args)
    {
        if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
            return await RunAsServiceAsync().ConfigureAwait(false);

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        return await RunAsync(stopping.Token, Console.WriteLine).ConfigureAwait(false);
    }

    /// <summary>Under the Service Control Manager: the same body, stopped by the SCM.</summary>
    /// <remarks>
    /// THE CONSOLE IS GONE AND WITH IT EVERY LINE THIS SERVICE WRITES, which is why
    /// RunAsync tees its log to a file in the data directory rather than relying on
    /// Console.WriteLine reaching anybody. AddWindowsService also wires the Windows event
    /// log, so a failure to start is visible in Event Viewer where somebody looking at a
    /// stopped service will actually look.
    /// </remarks>
    private static async Task<int> RunAsServiceAsync()
    {
        // FULLY QUALIFIED BECAUSE THIS FILE'S OWN NAMESPACE SHADOWS IT. The namespace is
        // CircleAI.Service.Host, so a bare `Host` binds to that namespace rather than to
        // Microsoft.Extensions.Hosting.Host and the compiler reports the builder missing
        // from "the namespace CircleAI.Service.Host" - which reads like a bad package
        // reference and is a name collision.
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        builder.Services.AddHostedService<ServiceHost>();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>The service proper. Returns 0 on a clean stop, 1 when it could not start.</summary>
    /// <param name="ct">Cancelled when the host, the SCM or Ctrl-C asks it to stop.</param>
    /// <param name="say">Where every line goes; a file in the data directory gets them too.</param>
    public static async Task<int> RunAsync(CancellationToken ct, Action<string> say)
    {
        ArgumentNullException.ThrowIfNull(say);

        // EVERY LINE GOES TO A FILE AS WELL, and under the controller that file is the
        // only copy. A service has no console: Console.WriteLine succeeds and reaches
        // nobody, so a run that fetched 75 models or refused to start would leave no
        // trace at all. Console mode gets the file too - the lines a person wants to
        // read back are the same either way, and the install log that started all of
        // this had to be copied out of a terminal by hand.
        var data = DataDirectory();
        Directory.CreateDirectory(data);
        var tee = Tee(say, Path.Combine(data, "service.log"));

        var store = ModelStore();
        tee($"CircleAI service — models at {store}");
        tee($"CircleAI service — data at {data}");

        try
        {
            say = tee;
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
            }, log: say);

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
                isFirstParty: _ => true, log: say);
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
            await endpoint.StartAsync(service, ct).ConfigureAwait(false);

            // THE PORT AND THE TOKEN ARE THE WHOLE INTERFACE, so they are printed
            // rather than left for somebody to find in a log. The endpoint generates
            // the token when none is configured, which means nothing can reach this
            // process without reading this line or being told it.
            //
            // PRINTED BEFORE THE BLOCK BELOW STARTS, so the log reads in the order the
            // service actually works: the door opens, and then the machine is measured.
            // Kicking the block off first would let its lines land above "listening" and
            // read as though the bind had waited for them again.
            say($"listening on http://127.0.0.1:{endpoint.BoundPort}");

            // AND WRITTEN DOWN, BECAUSE A SERVICE HAS NOBODY TO PRINT TO. The port and
            // the token ARE the whole interface, and printing them was fine for a
            // console app a person had just launched: under the Service Control Manager
            // that line goes nowhere and nothing on the machine can reach this process.
            // So they go in a file beside the readiness the endpoint already writes, and
            // a client reads it instead of being told.
            //
            // WHAT THIS COSTS, SAID PLAINLY. The token is the only thing standing between
            // a local process and somebody's memory, and a file is readable by whoever
            // the ACL allows. Under a per-user data directory that is the user who owns
            // it, which is the same reach the printed line had. Installed as a service
            // the data directory is machine-wide, so EndpointFile tightens it to SYSTEM,
            // Administrators and Users-read: every app the logged-in person runs can
            // find the service, which is the product's own promise - "one process on
            // this machine owns the models and answers for every app that asks" - and on
            // a machine with several accounts that is wider than the console was. Per-user
            // tokens are the honest fix and are not this change.
            WriteEndpointFile(Path.Combine(data, "endpoint.json"), endpoint.BoundPort, endpoint.Token, say);

            if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
            {
                say($"running as the Windows service '{ServiceName}'; stop it with: sc stop {ServiceName}");
                say($"the port and token are in {Path.Combine(data, "endpoint.json")}");
            }
            else
            {
                // STRAIGHT TO THE CONSOLE AND NOT THROUGH THE TEE, deliberately. The tee
                // appends to service.log, and a token in a log file outlives the process
                // that issued it - the one secret here is the one line that must not be
                // written down beside everything else.
                Console.WriteLine($"X-Butler-Token: {endpoint.Token}");
                say("Ctrl-C to stop.");
            }

            // AND NOW THE WORK ABOUT MODELS, ALL OF IT OFF THE START PATH: what machine
            // this is, what the catalogue makes of it, which model it chooses - and then
            // the phone's order, what is half down first and then what the first-run plan
            // wants and has no byte of. The service answers what it can while gigabytes
            // arrive; a brain that lands is loaded straight away. Desktop builds have no
            // speech stack, so no voices.
            _ = Task.Run(async () =>
            {
                void Say(string line) => say(line);
                string? wanted = null;
                try
                {
                    // WHAT MACHINE THIS IS, SAID OUT LOUD - see MachineLineAsync.
                    Say(await MachineLineAsync(ct).ConfigureAwait(false));

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
                        _ = brain.EnsureReadyAsync(ct)
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

                    await ModelSetup.ResumeUnfinishedAsync(loader, Say, ct).ConfigureAwait(false);

                    // THE BRAIN IS NOT ONE OF SEVENTY-FIVE. Naming it here lifts it to
                    // the head of the queue; everything else keeps smallest-first, which
                    // is still right for the wake word and a voice.
                    await ModelSetup.FetchNotStartedAsync(
                        loader, plan, Say, ct, landed: null, brainFirst: wanted)
                        .ConfigureAwait(false);

                    if (!present && !string.IsNullOrWhiteSpace(wanted) && loader.ModelPresent(wanted!))
                        await brain.EnsureReadyAsync(ct).ConfigureAwait(false);

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

            try { await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* asked to stop */ }

            await endpoint.StopAsync(CancellationToken.None).ConfigureAwait(false);

            // THE FILE GOES WHEN THE PORT DOES. A stale endpoint.json names a port
            // nothing is listening on and a token that opens nothing, and a client that
            // trusts it waits on a socket that will never answer - the same shape as the
            // abandoned-download directory this service already learned not to leave.
            try { File.Delete(Path.Combine(data, "endpoint.json")); } catch { /* best effort */ }

            say("stopped.");
            return 0;
        }
        catch (Exception ex)
        {
            // A service that will not start has to say why on the way out: this runs
            // with no screen and its exit code is all a service manager reports.
            say("CircleAI service could not start: " + ex.Message);
            Console.Error.WriteLine("CircleAI service could not start: " + ex.Message);
            return 1;
        }
    }

    /// <summary>Sends every line to <paramref name="inner"/> and appends it to a file.</summary>
    /// <remarks>
    /// NEVER THROWS AND NEVER BLOCKS THE SERVICE. A log that cannot be written is a lost
    /// line, not a failed start: a full disk, a locked file or a directory somebody
    /// removed under us must not be the reason the machine has no assistant. Opened per
    /// line rather than held, because this writes a few hundred lines over a start and
    /// then goes quiet for days, and a held handle is one more thing to own and release
    /// correctly across two lifetimes.
    /// <para>
    /// CAPPED, because the download lines are per-model and a machine that keeps failing
    /// a fetch would otherwise grow this without limit. At the cap the file is rolled
    /// once to .1 and started again, so the last two windows survive and nothing older
    /// does.
    /// </para>
    /// </remarks>
    private static Action<string> Tee(Action<string> inner, string path)
    {
        const long CapBytes = 4L * 1024 * 1024;
        var gate = new object();

        return line =>
        {
            inner(line);
            try
            {
                lock (gate)
                {
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > CapBytes)
                    {
                        var rolled = path + ".1";
                        try { File.Delete(rolled); } catch { /* best effort */ }
                        try { File.Move(path, rolled); } catch { /* best effort */ }
                    }

                    File.AppendAllText(
                        path,
                        DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", System.Globalization.CultureInfo.InvariantCulture)
                            + "  " + line + Environment.NewLine);
                }
            }
            catch { /* a lost line is not a failed start */ }
        };
    }

    /// <summary>Writes the port and token where a client can find them, and locks it down.</summary>
    /// <remarks>
    /// WHAT THE PRINTED LINE USED TO BE. HttpLoopbackEndpoint generates a token when none
    /// is configured, so nothing reaches this process without being told it - which was
    /// fine when a person had just started it in a terminal and is useless under the
    /// controller. The readiness file already lives in this directory; this is the same
    /// idea for the address.
    /// <para>
    /// THE ACL IS THE WHOLE PROTECTION, so it is set explicitly rather than inherited. A
    /// machine-wide ProgramData directory grants Users modify by default, which would let
    /// any local account REPLACE this file and point every client at a process of their
    /// own. SYSTEM and Administrators keep full control; Users get read, because every
    /// app the signed-in person runs has to find the service. Inheritance is switched off
    /// so a change to the parent cannot widen it later.
    /// </para>
    /// </remarks>
    private static void WriteEndpointFile(string path, int port, string? token, Action<string> say)
    {
        try
        {
            var json = "{\n"
                     + "  \"port\": " + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                     + "  \"token\": " + System.Text.Json.JsonSerializer.Serialize(token ?? string.Empty) + ",\n"
                     + "  \"pid\": " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                     + "  \"startedUtc\": " + System.Text.Json.JsonSerializer.Serialize(DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)) + "\n"
                     + "}\n";

            File.WriteAllText(path, json);
            if (OperatingSystem.IsWindows()) RestrictToAdminsAndUsers(path);
        }
        catch (Exception ex)
        {
            // A client can still be told the port by hand, so this is reported and not
            // fatal - but it is reported, because a service nothing can address is the
            // failure this file exists to prevent and it must not be silent.
            say("could not write the endpoint file: " + ex.Message);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictToAdminsAndUsers(string path)
    {
        var info = new FileInfo(path);
        var acl  = info.GetAccessControl();

        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (System.Security.AccessControl.FileSystemAccessRule existing in
                 acl.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
            acl.RemoveAccessRule(existing);

        void Allow(System.Security.Principal.WellKnownSidType who, System.Security.AccessControl.FileSystemRights rights)
            => acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                new System.Security.Principal.SecurityIdentifier(who, null),
                rights,
                System.Security.AccessControl.AccessControlType.Allow));

        Allow(System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.AccessControl.FileSystemRights.FullControl);
        Allow(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, System.Security.AccessControl.FileSystemRights.FullControl);
        Allow(System.Security.Principal.WellKnownSidType.BuiltinUsersSid, System.Security.AccessControl.FileSystemRights.Read);

        info.SetAccessControl(acl);
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
        return Path.Combine(MachineRootOr(Environment.SpecialFolder.LocalApplicationData), "CircleAI", "service");
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

        return Path.Combine(MachineRootOr(Environment.SpecialFolder.LocalApplicationData), "CircleAI", "models");
    }

    /// <summary>ProgramData under the controller, the user's own folder otherwise.</summary>
    /// <remarks>
    /// PER-USER IS THE WRONG ANSWER FOR A SERVICE AND THE RIGHT ONE FOR A CONSOLE RUN.
    /// LocalApplicationData under LocalSystem resolves to
    /// C:\Windows\System32\config\systemprofile\AppData\Local - a real, writable, and
    /// completely different store from the one a console run filled. Installed as a
    /// service with the old rule, this process would have reported "no model here yet"
    /// on a machine holding every model it needs and started re-downloading gigabytes,
    /// which looks exactly like a broken install and is not one.
    /// <para>
    /// CommonApplicationData (ProgramData) is the machine-wide answer, and it is the one
    /// the repo's other service installer already uses for its runtime and model roots.
    /// The install script sets CIRCLEAI_MODELS and CIRCLEAI_DATA explicitly anyway, so
    /// this is the default rather than the mechanism - but a default that silently points
    /// at a different disk location than the one a person has been filling is the kind of
    /// fact that costs a day, so it is decided here and said in the log.
    /// </para>
    /// </remarks>
    private static string MachineRootOr(Environment.SpecialFolder perUser)
        => OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService()
            ? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            : Environment.GetFolderPath(perUser);
}

/// <summary>The service under the Service Control Manager: Start, Stop, Shutdown.</summary>
/// <remarks>
/// ALL IT DOES IS HOLD THE TOKEN. BackgroundService gives the SCM contract its shape -
/// ExecuteAsync runs until the host's stopping token trips, and the host trips it on
/// `sc stop`, on a machine shutdown and on Ctrl-C alike - so the service body itself
/// never learns which of the three happened and does not need to.
/// <para>
/// AND IT STOPS THE HOST WHEN THE BODY GIVES UP. A service whose work has ended but whose
/// process stays up reads as Running in the listings while answering nothing, which is
/// worse than a stopped one: the thing a person checks says it is fine. RunAsync returning
/// 1 - it could not start - therefore ends the host rather than idling, and the non-zero
/// exit is what tells the SCM to apply its restart policy.
/// </para>
/// </remarks>
internal sealed class ServiceHost : BackgroundService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ServiceHost> _logger;

    public ServiceHost(IHostApplicationLifetime lifetime, ILogger<ServiceHost> logger)
    {
        _lifetime = lifetime;
        _logger   = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // THE EVENT LOG GETS THE LIFECYCLE AND THE FILE GETS THE DETAIL. AddWindowsService
        // wires the event log, and that is where somebody looking at a stopped service
        // looks first - so start, stop and failure go there, while the seventy-five
        // download lines go to service.log where they can be read without a viewer.
        var exit = 1;
        try
        {
            _logger.LogInformation("CircleAI service starting.");
            exit = await Program.RunAsync(stoppingToken, line => _logger.LogInformation("{Line}", line))
                                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            exit = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CircleAI service stopped by an unhandled error.");
        }
        finally
        {
            if (exit == 0) _logger.LogInformation("CircleAI service stopped.");
            else           _logger.LogError("CircleAI service exited with {Exit}.", exit);

            Environment.ExitCode = exit;
            _lifetime.StopApplication();
        }
    }
}
