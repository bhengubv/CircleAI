// ServiceApplication.cs
//
// The standalone CircleAI service, bootstrapped.
//
// EVERYTHING HERE WAS IN THE HYBRID SAMPLE'S MauiProgram, which is the thing this
// app exists to undo. LinkIpc.HostPackage named the sample, so "the shared brain on
// this device" was something a person got by installing a demo — and any second app
// that wanted it had to hope the demo was still there. The host is now an app of its
// own, and the sample is one of its clients.
//
// WHAT AN APPLICATION CLASS IS FOR HERE: the link service can be bound by another app
// before anybody opens this one, and OnBind runs with whatever statics were set at
// process start. Wiring in an Activity would mean the first bind after a cold start
// found no grant store and no memory — so it happens here, before any component runs.

using Android.App;
using Android.Runtime;
using CircleAI.Core;
using CircleAI.Device;
using CircleAI.Linking;
using CircleAI.Assistant;
using CircleAI.Skills;
using CircleAI.Assistant.Device;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Memory;

// Aliased because this file shares one assembly with the MAUI-targeting android
// leg from 3.8.1, and Microsoft.Maui.Controls is in scope here.
using Application = Android.App.Application;

namespace CircleAI.Service.Android;

// WHAT THIS APP IS CALLED AND WHAT IT LOOKS LIKE, in one place.
//
// IT HAD NEITHER. The label read "CircleAI" - the same string as the app, so the two
// entries in Settings > Apps were indistinguishable - and no icon was set at all, so
// Android drew its generic placeholder. Confirmed on a P30 on 2026-10-01: dumpsys
// reported no icon, and the App info page showed the default glyph. A store listing
// cannot ship that, and neither can somebody trying to work out which of the two is
// holding their microphone.
//
// HERE RATHER THAN IN AndroidManifest.xml, and the difference is not cosmetic: this
// attribute WINS over the template's <application> element, so a label set in the
// manifest is silently discarded. Measured - the manifest said "Circle AI Service"
// and `aapt2 dump badging` read back "CircleAI". One owner, and it is this line.
//
// THE SAME MARK AS THE APP, deliberately. It is one product; a second identity for
// the engine behind it would be a thing to distrust rather than to recognise. The
// NAME is what separates them in the list.
//
// AND THE MARK IS GENERATED, NOT COPIED. Resources/drawable/ic_launcher*.xml come
// out of tools/brand-mark/gen_mark.py, from the same strokes() that writes the app's
// appicon.svg - so the logo cannot drift between the app and the service that answers
// for it. They were 22 PNGs lifted out of the MAUI head's build output for a while,
// which is a second copy of the mark with no owner.
//
// VECTORS BECAUSE THIS IS NOT A MAUI PROJECT. There is no resizetizer in a plain
// Android SDK build to turn an svg into five densities, and the geometry is in the
// generator anyway. ic_launcher.xml is the whole icon in one vector for the API 24-25
// floor; drawable-anydpi-v26 carries the adaptive version for everything newer.
//
// No RoundIcon: an adaptive icon is masked by the launcher, so a separate round
// drawable would be a second thing to keep in step for no visible gain.
[Application(
    Label = "Circle AI Service",
    Icon  = "@drawable/ic_launcher")]
public sealed class ServiceApplication : Application
{
    public ServiceApplication(IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer) { }

    /// <summary>What the brain is told it can do: the manifest, then the packs.</summary>
    /// <remarks>
    /// THE LIBRARY IS NOT OPEN YET WHEN THIS RUNS. Opening it unpacks 20 MB out of
    /// the APK so it happens on a background task, while the brain is built eagerly
    /// so a question never waits on a cold model. LiveSkillStore is asked on every
    /// call rather than captured once, so the library joins the moment it lands
    /// instead of missing the composite by a second and never reaching the model.
    /// </remarks>
    private static ISkillStore ServiceSkills()
        => new CompositeSkillStore(
        [
            CapabilityManifestSkillStore.Default,
            ConsumerSkillPack.Shared,
            new LiveSkillStore(() => CircleNeuronLinkService.Skills),
        ]);

    /// <summary>The supervised brain, for memory signals. Main process only.</summary>
    private CircleAI.Service.Droid.Inference.InferenceSupervisor? _supervisor;

    /// <summary>Android asked this process to give memory back.</summary>
    public override void OnTrimMemory([GeneratedEnum] global::Android.Content.TrimMemory level)
    {
        base.OnTrimMemory(level);
        _supervisor?.OnTrimMemory((int)level);
    }

    /// <summary>
    /// THE INFERENCE PROCESS GETS THE BRAIN'S OPTIONS AND NOTHING ELSE. OnCreate runs in
    /// every process of this package; everything below it - grants, memory, voice, wake,
    /// catalogue, downloads - belongs to the main process and must not run twice.
    /// </summary>
    private void OnCreateInferenceProcess()
    {
        AndroidDeviceMemory.Install(this);
        base.OnCreate();
        global::Android.Util.Log.Info("CircleAI.Worker", "inference process started");

        // A TEST SWITCH, NOT A LOG: with soak/prompt-dump.on in this app's own test folder,
        // the exact text handed to the model each turn is written beside it, so what it
        // receives is read rather than assumed - on 2026-10-20 the persona had never
        // reached it. Off unless a developer has put that file there AND armed the switches
        // from adb (see TestSwitchesArmedFile): the folder is not this app's alone.
        try
        {
            var testDir = System.IO.Path.Combine(GetExternalFilesDir(null)!.AbsolutePath, "soak");
            if (!System.IO.File.Exists(TestSwitchesArmedFile(this))) testDir = string.Empty;
            if (testDir.Length > 0 && System.IO.File.Exists(System.IO.Path.Combine(testDir, "prompt-dump.on")))
            {
                var dumpFile = System.IO.Path.Combine(testDir, "prompt-dump.txt");
                CircleAI.Inference.QwenTextGenerator.PromptDump = text => System.IO.File.AppendAllText(dumpFile, text);
                global::Android.Util.Log.Info("CircleAI.Worker", "prompt dump on");
            }

            // THE SAME KIND OF SWITCH for how the notes are worded: soak/notes-template.txt,
            // one line, "\n" for a line break, "{notes}" and "{question}" where they go. A
            // wording is measured by writing the file and restarting this process - not by a
            // build of the whole service for each one.
            var templateFile = System.IO.Path.Combine(testDir, "notes-template.txt");
            if (testDir.Length > 0 && System.IO.File.Exists(templateFile))
            {
                CircleAI.Hosting.AIService.NotesTemplateForTest =
                    System.IO.File.ReadAllText(templateFile).Trim().Replace("\\n", "\n", StringComparison.Ordinal);
                global::Android.Util.Log.Info("CircleAI.Worker", "notes template from the test folder");
            }

            // AND FOR THE SAMPLER: soak/sampler.json, read when the model loads. Greedy makes
            // an answer depend on its input alone - so two answers to one input that differ
            // prove something else reached the model.
            var samplerFile = System.IO.Path.Combine(testDir, "sampler.json");
            if (testDir.Length > 0 && System.IO.File.Exists(samplerFile))
            {
                CircleAI.Inference.QwenTextGenerator.SamplerJsonForTest = System.IO.File.ReadAllText(samplerFile).Trim();
                global::Android.Util.Log.Info("CircleAI.Worker", "sampler from the test folder");
            }
        }
        catch { /* no test folder, no switches */ }

        var nativeDir = ApplicationInfo?.NativeLibraryDir;
        CircleAI.Service.Droid.Inference.InferenceWorkerService.OptionsFactory = request => new CircleAI.Hosting.AIOptions
        {
            NativeLibDir          = nativeDir,
            ModelStorageDirectory = request.ModelDirectory,
            ModelId               = request.ModelId,
            WarmOnStart           = true,
            NeverFetch            = true,
            SkipLoadHash          = request.SkipLoadHash,
            SystemPrompt          = AssistantPersona.Prompt,
            OverviewAnswer        = AssistantPersona.CanDoSpoken,

            // THE EMBEDDED STORES ONLY. The 20 MB library is unpacked by the main
            // process, and opening it here could race that unpack; the manifest - the
            // assistant's honesty about itself - is embedded and is what matters most.
            SkillStore = new CompositeSkillStore(
            [
                CapabilityManifestSkillStore.Default,
                ConsumerSkillPack.Shared,
            ]),
        };
    }

    /// <summary>Who the recorded-question run asks as.</summary>
    private const string QualityCaller = "internal:quality";

    /// <summary>The flag that lets the files in soak/ act as test switches: in this app's internal storage.</summary>
    /// <remarks>
    /// THE SWITCHES' FOLDER IS NOT THIS APP'S ALONE. /sdcard/Android/data/.../soak is group
    /// sdcard_rw on the P30 (Android 10, listed 2026-10-20), so an app holding storage
    /// permission can write there - and could have turned the prompt dump on and read every
    /// question, or planted a wording for the notes into every turn. A file there now counts
    /// only while this flag exists, and only a DUMP-protected adb broadcast (SoakReceiver,
    /// "arm" and "disarm") sets or clears it; no other app can reach internal storage.
    /// </remarks>
    private static string TestSwitchesArmedFile(global::Android.Content.Context context) =>
        System.IO.Path.Combine(context.FilesDir!.AbsolutePath, "CircleAI", "state", "test-switches.armed");

    /// <summary>The questions in soak/quality-typed.txt, one a line; null when there is no such file.</summary>
    private static IReadOnlyList<string>? TypedQuestionsForTest(string soakDir)
    {
        try
        {
            var file = System.IO.Path.Combine(soakDir, "quality-typed.txt");
            if (!System.IO.File.Exists(file)) return null;
            var lines = System.IO.File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            return lines.Count > 0 ? lines : null;
        }
        catch { return null; }
    }

    /// <summary>Asks <paramref name="questions"/> TYPED, up to the typed answer length, and reports what came back.</summary>
    /// <remarks>
    /// A TEST SWITCH (soak/quality-typed.txt, armed only). Every recorded question is asked
    /// spoken, capped at 80 tokens, so nothing had measured how a typed answer runs on - and
    /// greedy decoding, the sampler since 2026-10-20, has no repetition guard. Answered is
    /// all this reports; whether an answer repeats itself is read from what it says.
    /// </remarks>
    private static async Task<CircleAI.Assistant.Quality.QualityReport> AskTypedAsync(
        CircleAI.Assistant.Requests.IAskFront front, IReadOnlyList<string> questions)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        var items = new List<CircleAI.Assistant.Quality.QualityItem>();
        string? model = null;
        for (var i = 0; i < questions.Count; i++)
        {
            var id = $"typed-{i + 1}";
            var admission = front.Submit(new CircleAI.Assistant.Requests.AskSubmission(
                QualityCaller, $"quality-{run}-{id}", string.Empty, CircleAI.Assistant.Requests.AskLane.Foreground,
                questions[i], questions[i], Spoken: false));
            if (!admission.Accepted || admission.Record is null)
            {
                items.Add(new(id, false, "refused: " + admission.Refusal, string.Empty, null));
                continue;
            }

            var done = await front.WaitAsync(QualityCaller, admission.Record.Id, TimeSpan.FromMinutes(4), CancellationToken.None).ConfigureAwait(false);
            var answered = done?.State == CircleAI.Assistant.Requests.AskState.Succeeded;
            items.Add(new(id, answered, answered ? "answered" : "not answered: " + (done?.State.ToString() ?? "missing"),
                          answered ? done!.Text : done?.Sentence ?? string.Empty, done?.Timing));
            model ??= done?.Timing?.ModelId ?? done?.ModelId;
        }
        return new(items, items.Count(x => x.Passed), items.Count, model);
    }

    /// <summary>The recorded questions a quality run asks, in order, when soak/quality-ids.txt names them; all of them otherwise.</summary>
    /// <remarks>
    /// A TEST SWITCH. "maize,colours,maize" asks maize after maize's own answer and after
    /// another question's, which is how a phone shows whether one turn reaches the next. A
    /// repeat gets its own id ("maize#2") - the queue would otherwise hand back the first
    /// answer to the same request id instead of asking again.
    /// </remarks>
    private static IReadOnlyList<CircleAI.Assistant.Quality.RecordedQuestion>? QuestionsForTest(string soakDir)
    {
        try
        {
            var file = System.IO.Path.Combine(soakDir, "quality-ids.txt");
            if (!System.IO.File.Exists(file)) return null;

            var known = CircleAI.Assistant.Quality.RecordedQuestions.Load().ToDictionary(q => q.Id, StringComparer.Ordinal);
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var picked = new List<CircleAI.Assistant.Quality.RecordedQuestion>();
            foreach (var id in System.IO.File.ReadAllText(file).Split([',', '\n', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!known.TryGetValue(id, out var q)) continue;
                var n = seen[id] = seen.GetValueOrDefault(id) + 1;
                picked.Add(n == 1 ? q : q with { Id = $"{id}#{n}" });
            }
            return picked.Count > 0 ? picked : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Every question - from an app, the wake word or the OS assistant - goes through
    /// one durable queue to a brain supervised in its own process.
    /// </summary>
    private void ComposeRequestPath()
    {
        try
        {
            var circleDir = System.IO.Path.Combine(FilesDir!.AbsolutePath, "CircleAI");
            var stateDir = System.IO.Path.Combine(circleDir, "state");
            System.IO.Directory.CreateDirectory(stateDir);

            var readiness = new CircleAI.Assistant.Lifecycle.ReadinessTracker(
                System.IO.Path.Combine(stateDir, "readiness.json"));
            var connection = new CircleAI.Service.Droid.Inference.WorkerConnection(this);
            var supervisor = new CircleAI.Service.Droid.Inference.InferenceSupervisor(
                this, connection, readiness,
                catalogue: () => CircleAI.Device.CircleNeuronService.Catalogue,
                stateDirectory: stateDir,
                modelStoreDirectory: ModelStore.Path,
                optionsFactory: () => CircleNeuronService.OptionsFactory?.Invoke() ?? new CircleAI.Hosting.AIOptions(),
                announce: (sentence, awaiting) => CircleNeuronLinkService.Decided(sentence, awaiting));
            _supervisor = supervisor;

            // THE FIRST CHOICE WAITS FOR THIS LAUNCH'S CATALOGUE, as the brain host's did:
            // choosing from yesterday's ranks picked the wrong model on 2026-10-16.
            var brain = new CatalogueGatedBrain(supervisor, () => CircleNeuronService.CatalogueReady);

            var store = new CircleAI.Assistant.Requests.SqliteAskStore(System.IO.Path.Combine(circleDir, "asks.db"));

            // THE DEVICE'S ONE MEMORY, read at ask time: it is opened elsewhere in start-up,
            // and a question must find it whichever ran first. The quality run gets a fresh
            // one of its own each time - its "clinic appointment" is a test, not something
            // the person said.
            CircleAI.Memory.IMemoryService? qualityMemory = null;
            CircleAI.Memory.IMemoryService? MemoryFor(string caller) =>
                caller == QualityCaller ? qualityMemory : CircleNeuronLinkService.Memory;
            Func<string?> wakePhrase = () => CircleNeuronService.Listener?.Describe;

            var shortcuts = new List<CircleAI.Assistant.Requests.IAskShortcut>
            {
                new CircleNeuronLinkService.SelfRequests(),
                new CircleAI.Assistant.Requests.RememberShortcut(MemoryFor, wakePhrase),
                new CircleAI.Assistant.Requests.RecallShortcut(MemoryFor, wakePhrase),
            };
            shortcuts.AddRange(CircleAI.Assistant.Requests.AskShortcuts.Standard(wakePhrase));

            var coordinator = new CircleAI.Assistant.Requests.RequestCoordinator(
                store, brain, CircleAI.Assistant.Requests.ServiceLimits.For(CircleAI.Core.DeviceProbe.Snapshot()),
                readiness, conversations: null, shortcuts,
                isFirstParty: key => CircleNeuronLinkService.FirstPartySignatures is { } sigs
                                     && sigs.Any(s => key.EndsWith(":" + s[..Math.Min(16, s.Length)], StringComparison.Ordinal)),
                log: line => global::Android.Util.Log.Info("CircleAI.Asks", line));
            supervisor.SchedulerIdle = () => coordinator.IsIdle;

            CircleNeuronService.Brain = brain;
            CircleNeuronService.Asks = coordinator;
            CircleNeuronService.Readiness = readiness;
            CircleNeuronLinkService.VersionCode = VersionCodeOf(this);
            CircleNeuronLinkService.HealthFacts = () => Health(coordinator, supervisor, readiness);
            CircleAI.Service.Droid.Assistant.CircleAssistSession.RunSpokenTurn = phrase => WakeTurn.RunAsync(phrase);
            CircleAI.Service.Droid.Assistant.CircleRecognitionService.Speech = () => CircleNeuronLinkService.Speech;
            CircleAI.Service.Droid.Assistant.AssistantRole.Report(readiness, this);

            readiness.Setting(CircleAI.Assistant.Lifecycle.ReadinessSettings.LinkDescriptor, LinkIpc.Descriptor);
            readiness.Setting(CircleAI.Assistant.Lifecycle.ReadinessSettings.LinkProtocol, LinkIpc.Protocol.ToString(System.Globalization.CultureInfo.InvariantCulture));
            readiness.Setting(CircleAI.Assistant.Lifecycle.ReadinessSettings.AudioMaxBytes, LinkAudio.MaxAudioBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            readiness.Setting(CircleAI.Assistant.Lifecycle.ReadinessSettings.AudioFormat, "pcm16le-16000-mono");
            readiness.Setting(CircleAI.Assistant.Lifecycle.ReadinessSettings.TurnImages, "0");
            readiness.Setting(CircleAI.Assistant.Lifecycle.ReadinessSettings.TurnAgentic, "0");

            coordinator.Start();

            // THE SOAK RUNS IN HERE, so it needs nothing from the phone but the service
            // staying alive - and a restart in the middle of it is recorded, not fatal.
            var soakDir = System.IO.Path.Combine(GetExternalFilesDir(null)?.AbsolutePath ?? stateDir, "soak");
            var soak = new CircleAI.Assistant.Quality.SoakRunner(
                coordinator, () => Health(coordinator, supervisor, readiness), soakDir,
                log: line => global::Android.Util.Log.Info("CircleAI.Soak", line),
                fault: supervisor.KillForTest);
            CircleAI.Service.Droid.SoakReceiver.Runner = soak;
            CircleAI.Service.Droid.SoakReceiver.BootId = readiness.BootId;
            CircleAI.Service.Droid.SoakReceiver.Quality = () => _ = Task.Run(async () =>
            {
                // A FRESH MEMORY PER RUN, so "When is my clinic appointment?" is answered
                // from this run's "Remember that...", never a pile of earlier ones.
                var qualityDir = System.IO.Path.Combine(circleDir, "memory-quality");
                (qualityMemory as IDisposable)?.Dispose();
                qualityMemory = null;
                try { if (System.IO.Directory.Exists(qualityDir)) System.IO.Directory.Delete(qualityDir, recursive: true); }
                catch { /* a leftover only adds old answers to a test memory */ }
                qualityMemory = new CircleAI.Memory.MemoryService(qualityDir);

                var armed = System.IO.File.Exists(TestSwitchesArmedFile(this));
                var typed = armed ? TypedQuestionsForTest(soakDir) : null;
                var report = typed is { Count: > 0 }
                    ? await AskTypedAsync(coordinator, typed).ConfigureAwait(false)
                    : await new CircleAI.Assistant.Quality.QualityRunner(coordinator, QualityCaller)
                        .RunAsync(armed ? QuestionsForTest(soakDir) : null, TimeSpan.FromMinutes(3), CancellationToken.None).ConfigureAwait(false);
                foreach (var item in report.Items)
                    global::Android.Util.Log.Info("CircleAI.Quality",
                        $"QUALITY {item.Id} {(item.Passed ? "PASS" : "FAIL")} {item.Reason} "
                        + (item.Timing is { } t ? t.ToLogLine(item.Id) : string.Empty));
                global::Android.Util.Log.Info("CircleAI.Quality", $"QUALITY summary {report.Passed}/{report.Total} model={report.ModelId}");
                try
                {
                    System.IO.Directory.CreateDirectory(soakDir);
                    System.IO.File.WriteAllText(System.IO.Path.Combine(soakDir, "quality.json"),
                        System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                catch { /* the log has it */ }
            });
            if (soak.Running) soak.Start(null, readiness.BootId, resume: true);
            CircleAI.Service.Droid.SoakReceiver.KillWorker = supervisor.KillForTest;
            CircleAI.Service.Droid.SoakReceiver.ArmTestSwitches = on =>
            {
                var flag = TestSwitchesArmedFile(this);
                try
                {
                    if (on)
                    {
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(flag)!);
                        System.IO.File.WriteAllText(flag, DateTimeOffset.UtcNow.ToString("O"));
                    }
                    else if (System.IO.File.Exists(flag)) System.IO.File.Delete(flag);
                    global::Android.Util.Log.Info("CircleAI.Soak", on ? "test switches armed" : "test switches disarmed");
                }
                catch (Exception ex) { global::Android.Util.Log.Warn("CircleAI.Soak", "test switches: " + ex.GetType().Name); }
            };
            CircleAI.Service.Droid.SoakReceiver.Ask = text =>
            {
                var admission = coordinator.Submit(new CircleAI.Assistant.Requests.AskSubmission(
                    "internal:adb", Guid.NewGuid().ToString("N"), "adb", CircleAI.Assistant.Requests.AskLane.Foreground, text, text));
                global::Android.Util.Log.Info("CircleAI.Soak", admission.Accepted ? "ask " + admission.Record!.Id + " accepted" : "ask refused");
            };

            // A BETTER MODEL IS TRIED ONLY WHEN NOBODY IS WAITING AND THE PHONE IS ON
            // CHARGE - and the working one keeps answering until the new one has passed.
            _validation = new System.Threading.Timer(_ =>
                _ = supervisor.ValidateCandidateIfDueAsync(CancellationToken.None)
                              .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted),
                null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15));

            global::Android.Util.Log.Info("CircleAI.Asks", "request path ready: queue, readiness, supervised brain");
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Error("CircleAI.Asks", "request path not composed: " + ex);
        }
    }

    private System.Threading.Timer? _validation;

    private static long VersionCodeOf(global::Android.Content.Context context)
    {
        try
        {
            var info = context.PackageManager?.GetPackageInfo(context.PackageName!, 0);
            return info is null ? 0 : OperatingSystem.IsAndroidVersionAtLeast(28) ? info.LongVersionCode : info.VersionCode;
        }
        catch { return 0; }
    }

    // WHAT THE SERVICE KNOWS ABOUT ITS OWN HEALTH - text-free, for the Health verb and
    // the soak run.
    private static IReadOnlyList<KeyValuePair<string, string>> Health(
        CircleAI.Assistant.Requests.RequestCoordinator coordinator,
        CircleAI.Service.Droid.Inference.InferenceSupervisor supervisor,
        CircleAI.Assistant.Lifecycle.IReadinessTracker readiness)
    {
        string N(long v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var status = supervisor.Status;
        var uptimeMs = global::Android.OS.SystemClock.ElapsedRealtime() - global::Android.OS.Process.StartElapsedRealtime;
        return new List<KeyValuePair<string, string>>
        {
            new("pid", N(global::Android.OS.Process.MyPid())),
            new("uptimeSec", N(uptimeMs / 1000)),
            new("rssKb", N(CircleAI.Service.Droid.Inference.InferenceWorkerService.ProcKb("/proc/self/status", "VmRSS:"))),
            new("threads", N(CircleAI.Service.Droid.Inference.InferenceWorkerService.ProcKb("/proc/self/status", "Threads:"))),
            new("gcHeapBytes", N(GC.GetTotalMemory(false))),
            new("memAvailableKb", N(CircleAI.Service.Droid.Inference.InferenceWorkerService.ProcKb("/proc/meminfo", "MemAvailable:"))),
            new("queueDepth", N(coordinator.QueueDepth)),
            new("currentAsk", coordinator.CurrentAskId ?? string.Empty),
            new("brainPhase", status.Phase.ToString()),
            new("brainModel", status.ModelId ?? string.Empty),
            new("workerPid", status.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
            new("pendingCandidate", supervisor.PendingCandidate ?? string.Empty),
            new("readinessSeq", N(readiness.Seq)),
            new("bootId", readiness.BootId),
            new("heard", N(CircleNeuronLinkService.Heard)),
        };
    }

    /// <summary>
    /// The supervised brain, holding its first choice until this launch's catalogue is ready.
    /// </summary>
    private sealed class CatalogueGatedBrain(
        CircleAI.Service.Droid.Inference.InferenceSupervisor inner, Func<Task?> catalogueReady)
        : CircleAI.Assistant.Requests.IBrainWorker
    {
        private int _waited;

        public CircleAI.Assistant.Requests.BrainWorkerStatus Status => inner.Status;

        public event EventHandler<CircleAI.Assistant.Requests.BrainWorkerStatus>? StatusChanged
        {
            add => inner.StatusChanged += value;
            remove => inner.StatusChanged -= value;
        }

        public async Task EnsureReadyAsync(CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _waited, 1) == 0 && catalogueReady() is { } ready)
            {
                try { await ready.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (TimeoutException) { /* select from what the catalogue holds */ }
                catch (OperationCanceledException) { throw; }
                catch { /* a broken bootstrap costs the freshest ranks, never the brain */ }
            }
            await inner.EnsureReadyAsync(ct).ConfigureAwait(false);
        }

        public IAsyncEnumerable<CircleAI.Assistant.Requests.BrainChunk> GenerateAsync(
            CircleAI.Assistant.Requests.BrainJob job, CancellationToken ct) => inner.GenerateAsync(job, ct);

        public Task ReleaseAsync(CancellationToken ct) => inner.ReleaseAsync(ct);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    public override void OnCreate()
    {
        if (CircleAI.Service.Droid.ProcessRole.IsInferenceProcess())
        {
            OnCreateInferenceProcess();
            return;
        }

        // MEASURE THE DEVICE BEFORE ANYTHING ASKS WHAT FITS. DeviceProbe reads the
        // GC heap limit on Android — about 100 MB inside the sandbox — unless a head
        // installs the real hook, so every model-fit decision would be made against a
        // fiction. It has to run before base.OnCreate, because base can start
        // components that probe.
        AndroidDeviceMemory.Install(this);
        AndroidConnectivity.Install(this);

        base.OnCreate();

        // WHERE STANDING GRANTS LIVE. A killed service process must not forget who
        // the person already approved: an in-memory store is safe but asks again on
        // every restart, which trains people to tap through consent without reading.
        var grants = System.IO.Path.Combine(FilesDir!.AbsolutePath, "link-grants.json");
        CircleNeuronLinkService.Grants = new FileLinkGrantStore(grants);

        // OUR OWN APPS ARE FIRST PARTY, AND THE SET USED TO BE EMPTY.
        //
        // What was here said "NOBODY IS TRUSTED BY DEFAULT ... populating this is a
        // deliberate decision about which packages ship as 'ours', not a
        // convenience" - and then populated nothing. LinkGate's doc is explicit
        // about what that means: "Empty means every caller is treated as
        // third-party." So AutoApprove, the whole reason the gate has four outcomes
        // instead of three, could never fire.
        //
        // WHAT IT COST, on the P30 on 2026-10-09 after a pm clear:
        //
        //     CircleAI is not ready
        //     not linked for Chat - approve in Circle AI first
        //
        // The person's own assistant, on their own phone, asking them to authorise
        // it to talk to itself - with a biometric sheet. That is the same defect as
        // the "Turn it on" button: a default that is off, and the work handed back.
        //
        // FIRST PARTY MEANS SAME SIGNING KEY, which is the boundary Android itself
        // uses for signature-level permissions. An app signed with this key is one
        // we shipped; anybody who holds the key can already publish updates to these
        // packages, so trusting it grants nothing that was being withheld.
        //
        // STATED PLAINLY, BECAUSE IT IS A REAL WIDENING: every app signed with this
        // key now reaches the brain AND the person's memory with no prompt. Third
        // parties are unaffected - they still get NeedAuth and a device-auth sheet.
        //
        // Read from the OS, never pasted in: a hard-coded digest is wrong the day
        // the key rotates, and the failure is a person being asked to approve their
        // own assistant. If it cannot be read the set stays empty and everything
        // prompts, which is the old behaviour and the safe way to fail.
        var ownSignature = CircleNeuronLinkService.OwnSignatureDigest(this);
        CircleNeuronLinkService.FirstPartySignatures = ownSignature is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>([ownSignature], StringComparer.Ordinal);

        global::Android.Util.Log.Info("CircleAI.Setup", ownSignature is null
            ? "link: could not read our own signing key; every caller will be prompted"
            : "link: our own signing key is trusted; apps we signed need no approval");

        // THE PERSON'S MEMORY, AND THE RECALL / REMEMBER VERBS WERE DEAD WITHOUT IT.
        // CircleNeuronLinkService.Memory defaults to null, and null is answered with
        // "memory not available" - so the two verbs that have been on the wire the
        // whole time returned a failure on every call, and the only reason nobody saw
        // it is that the one client kept its own store and never asked.
        //
        // ONE STORE FOR THE DEVICE, which is the whole point of putting it here: the
        // service's folder, every linked app reading and writing the same atoms, and
        // uninstalling a client does not take somebody's name with it.
        //
        // Built eagerly at process start rather than on first bind: a bind can arrive
        // from another app before anything in this one has run, and opening SQLite
        // inside a transaction the caller is blocked on is the wrong place for it.
        CircleNeuronLinkService.Memory = new MemoryService(
            System.IO.Path.Combine(FilesDir!.AbsolutePath, "CircleAI", "memory"));

        // THE PHONEMISER, OR THIS PROCESS CAN HEAR AND THINK BUT NEVER SPEAK. Every
        // voice trained on espeak IPA needs it - which is every language here except
        // Japanese - and without this call CircleAISpeaker.MobilePhonemizerFactory
        // stays null and synthesis refuses with "on-device phonemizer not wired".
        //
        // The client used to make this call and no longer can: it ships neither the
        // library nor the 11.9 MB of data. Both moved here with the synthesis, and
        // espeak-ng stays OUT OF PROCESS - it is GPL-3.0, and invoking it rather
        // than linking it is what keeps it off this app's licence.
        // global:: ON EVERY Android.* CALL IN THIS FILE, AND IT IS NOT DECORATION.
        // This assembly's namespace is CircleAI.Service.Android, which SHADOWS the
        // platform's own Android namespace from inside its own files: a bare
        // Android.Util.Log resolves to CircleAI.Service.Android.Util.Log, which does
        // not exist, and the compiler reports it as "the type or namespace name
        // 'Util' does not exist" - which reads as a missing assembly reference and is
        // nothing of the sort. CircleAI.Assistant.Device is named .Device rather than
        // .Android for exactly this reason; this app cannot be, so it pays with the
        // qualifier.
        try
        {
            VoiceWiring.Install(this);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("CircleAI.Voice", "phonemiser not wired: " + ex.Message);
        }

        // SAY WHICH ENGINES ACTUALLY LOADED, BECAUSE SILENCE READ AS SUCCESS.
        // LlamaGenerator.NativeVersion documents itself as "a fact to report, not a
        // crash" and nothing reported it. It mattered on 2026-10-09: libllamabridge
        // .so had been built on 28 September, left in a gitignored directory, and
        // never staged into runtimes/ - so the GGUF engine was absent from every
        // APK and the only trace was an Android runtime warning that a p/invoke
        // "may fail". Once TryGetVersion was changed to ask NativeLibrary.TryLoad
        // instead of throwing, even that warning stopped, and the absence of a
        // complaint became indistinguishable from a working engine. A 46 MB native
        // library is either doing something or it is dead weight, and the build
        // cannot tell you which.
        //
        // OFF THE MAIN THREAD, for the same reason the voice wiring is: this is the
        // first dlopen of libllama.so and its three ggml dependencies.
        _ = System.Threading.Tasks.Task.Factory.StartNew(() =>
        {
            try
            {
                var gguf = CircleAI.Inference.LlamaGenerator.NativeVersion;
                global::Android.Util.Log.Info("CircleAI.Engines", gguf is null
                    ? "gguf: NO BACKEND - libllamabridge.so did not load. A .gguf model cannot be opened on this device."
                    : $"gguf: {gguf}");
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("CircleAI.Engines", $"gguf: probe failed - {ex.GetType().Name}: {ex.Message}");
            }
        },
        System.Threading.CancellationToken.None,
        System.Threading.Tasks.TaskCreationOptions.LongRunning,
        System.Threading.Tasks.TaskScheduler.Default);

        // WHERE A SIDELOADED VOICE AND THE JAPANESE DICTIONARY ARE FOUND. Three
        // statics that the client used to set against ITS external files directory,
        // which was the wrong process: the code that reads them is here. The
        // external directory, because that is where adb or a file manager can put
        // something without root - and the importer still checks every byte against
        // the catalogue's hash, so this is a delivery route and not a trust hole.
        var sideload = GetExternalFilesDir(null)?.AbsolutePath;
        CircleAI.Assistant.Voice.CircleAISpeaker.SideloadFolder = sideload;
        CircleAI.Voice.OpenJTalkPhonemizer.DictionaryFolder = sideload;
        CircleAI.Voice.OpenJTalkPhonemizer.ModelStoreFolder = ModelStore.Path;

        // Managed voice logging reaches nothing through ILogger on Android, so it
        // goes to logcat directly - the one place it is actually readable.
        CircleAI.Voice.VoiceTrace.Sink = line => global::Android.Util.Log.Info("CircleAI.Voice", line);

        // SPEECH FOR THE LINK, which has been refused on every call until now.
        // CircleNeuronLinkService.Speech defaults to null and null answers "speech
        // not available", so the audio transaction - the codec, the 800 KB budget,
        // the client end - was complete and dead. This is the recogniser and the
        // voice it needed.
        CircleNeuronLinkService.Speech = new ServiceSpeech();

        // SETTING THIS APP UP, FROM AN APP THAT HAS A SCREEN. CircleNeuronLinkService
        // .Setup defaults to null and null answers "setup not available", so the five
        // setup verbs were refused - which left the models unreachable by anybody:
        // this app cannot ask for them (no UI) and the client could not either.
        // DeviceSetup is the real thing; it plans, fetches and reports.
        // The microphone state is read from the platform (this app cannot prompt for
        // it - no screens), and the footprint gate is the same MemoryManager the
        // storage census uses, so "can I fetch this" is answered against the real disk.
        CircleNeuronLinkService.Setup = new DeviceSetup(
            new DeviceMicrophoneAccess(this),
            new MemoryManager(new DeviceResourcesReader()),
            // A LAMBDA, BECAUSE THE CATALOGUE IS BUILT FURTHER DOWN THIS METHOD.
            // Reading it here would capture null forever and make the setup plan
            // offer models this phone cannot run - which is the whole thing being
            // fixed. Same shape as the LiveSkillStore accessor above.
            catalogue: () => CircleAI.Device.CircleNeuronService.Catalogue);

        // THE BRAIN ITSELF, AND NOTHING HAS EVER ASKED FOR ONE. CircleNeuronService
        // hosts a NeuronNode only when a host sets OptionsFactory; left null it runs,
        // holds the microphone, and reports "listening only — no brain was asked for".
        // That property had ZERO setters in the repo, and its own comment explains why
        // it was reasonable: the hybrid app used to host its brain in its OWN process
        // and wanted this service purely to keep the mic open with the screen off.
        //
        // That world is gone. The app holds no model now, so if this app does not ask
        // for a brain, nothing on the device has one - which is exactly what happened:
        // every question came back "brain warming up, try again shortly" for ever,
        // because Node was null and always would be.
        //
        // WarmOnStart pays the cold start once, up front, rather than on the first
        // question somebody asks. On a P30 that is thirteen to twenty-three seconds,
        // and it is far better spent while the notification says "loading" than while
        // a person waits on a blank bubble.
        // THE GPU IS 22x SLOWER ON THIS PHONE AND THE EXPERIMENT IS OVER.
        //
        // libMNN_CL.so and libMNN_Vulkan.so ship in this APK and nothing had ever
        // asked for them, so it looked like free speed waiting to be claimed:
        // prefill is one large compute-bound matrix multiply, which is what a GPU is
        // for. Measured on the P30, same 193-token warm-up both times:
        //
        //   cpu      prefill   5 559 ms      28.8 ms/token
        //   opencl   prefill 124 349 ms     644.3 ms/token
        //
        // MNN accepted it and the Mali driver loaded - this is not a failure to
        // engage, it is the GPU genuinely being that much worse. A Kirin 710 reports
        // i8sdot:0, fp16:0, i8mm:0, so there is no hardware help on either side, and
        // whatever MNN's OpenCL path does with quantised weights costs far more than
        // it saves. The kernel cache cannot help either: it has no writer at all, so
        // the kernels are rebuilt on every load - see mnn-opencl-cache-has-no-writer.
        //
        // QwenTextGenerator.Backend stays null, which is the model's own choice. The
        // switch is kept because the answer is per-device and a phone with real GPU
        // compute may well go the other way; this one does not.

        // AND kvcache_mmap STAYS OFF. Tested 2026-10-03 now that the working
        // directory is writable, and it still SIGSEGVs - twice in ninety seconds,
        // pid 7198 then 7304, both "prcp FGS", fault addr 0x0 in MNN::ThreadPool
        // ::enqueue on a .NET TP Worker. The identical signature as September.
        //
        // WHY THE DIRECTORY DID NOT SAVE IT. MNN prepends a relative "prefixcache/"
        // to the ABSOLUTE session path, so what it tries to create is
        //
        //   prefixcache//data/user/0/com.bhengubv.circleai.service/files/...session_0.k
        //
        // - the whole absolute tree recreated underneath a relative folder. A
        // writable working directory lets it make "prefixcache" and nothing below.
        // AND kvcache_mmap GOES BACK OFF, because it buys nothing.
        //
        // The path fix works: PathFor now returns a bare filename, MNN builds
        // "prefixcache/<key>.session", and the SIGSEGV that has haunted this since
        // September is gone. The cache fills - 96 files, 25 MB, right key, right
        // directory. And it does not make anything faster:
        //
        //   cold, no cache at all        prefill 5 559 ms
        //   warm, 96 files on disk       prefill 4 951 ms
        //   warm again, same files       prefill 5 014 ms
        //
        // Three loads of the same 193-token prompt. The two warm ones are within
        // 1.3% of each other and the gap to cold is thermal noise. MNN is writing
        // the KV and not reusing it - the attention implementation that logs here is
        // CPULinearAttention, whose KV is not the shape a prefix cache replays, and
        // our own prefix-hit/miss line cannot tell us either: it reads
        // setPrefixCacheFile's return value, and the run that wrote all 25 MB logged
        // "cache=on", which in our code means "not writing".
        //
        // So: 25 MB of disk and a code path that SIGSEGV'd twice, for no measured
        // gain. Off. The path fix stays - it is correct, it is tested, and it is
        // what makes this worth trying again on a model whose attention can use it.
        // AllowKvCacheMmap is the one line that turns it back on.


        CircleNeuronService.OptionsFactory = () => new CircleAI.Hosting.AIOptions
        {
            NativeLibDir          = ApplicationInfo?.NativeLibraryDir,
            ModelStorageDirectory = ModelStore.Path,
            WarmOnStart           = true,

            // WHO IT IS, WHICH THIS PATH HAD NEVER BEEN TOLD. AIOptions.SystemPrompt
            // was left at the SDK default here - "You are B!, a helpful on-device
            // assistant" - because the persona was a private constant inside the app's
            // own CircleAISession, and the app stopped holding a model. Every question
            // arrives through the link and is answered HERE, so the tuned text was
            // being written for a path nothing reaches.
            //
            // On a P30 on 2026-10-02 that cost the product its own name: asked what it
            // could do, it replied "I'm Qwen3.5, the latest version of Alibaba's
            // multimodal large language model" and listed Qwen's abilities, part of it
            // in Chinese. It is not that the model was wrong about itself; nobody had
            // told it otherwise.
            SystemPrompt          = AssistantPersona.Prompt,

            // AND IT SAYS IT, rather than being told it and hoping. With the sentence
            // in the prompt alone, a P30 answered "What can you do?" with "I am here
            // to help you! What can I do for you today?" - right identity, no false
            // claims, and not one of the things it can actually do. The same persona
            // asks for one or two short sentences, so it gave a short one.
            //
            // What this phone can do is a fact THIS app owns. The model was guessing
            // at it, and the precedent for taking a known answer off the dice is
            // arithmetic, which the core already answers itself.
            OverviewAnswer        = AssistantPersona.CanDoSpoken,

            // AND WHAT IT CAN ACTUALLY DO, WHICH THE BRAIN IN THIS PROCESS HAD NEVER
            // BEEN TOLD. AIOptions.SkillStore is what SkillContextBuilder reads to put
            // the assistant's own capabilities in front of it, and this app never set
            // it. The library WAS opened and handed to CircleNeuronLinkService.Skills,
            // so a linked app could search it, and to CircleAISession.Library, which is
            // the app's session path. The model that actually answers got neither, and
            // had been introducing itself from one line of persona.
            //
            // MANIFEST FIRST, and that order is the care point: it is not a set of
            // skills, it is the assistant's honesty about itself, every entry carrying
            // a [status]. A community skill called "voice" must never become what the
            // phone says about its own voice support.
            SkillStore = ServiceSkills(),
        };

        // WHAT THE PHONE ANSWERS TO. DeviceWakePhrases keeps the chosen phrase in this
        // app's own store and judges a new one against the KWS bundle's tokeniser -
        // both of which are here, which is exactly why a client could only ever be
        // told to go elsewhere. Wired, it can be set from any approved app.
        CircleNeuronLinkService.WakePhrases = new DeviceWakePhrases(
            new SqliteAppStore(System.IO.Path.Combine(FilesDir!.AbsolutePath, "CircleAI", "app.db")));

        // LISTENING, AND WHAT THIS DEVICE HOLDS. The last two rows in the client's
        // settings that could only report: "Answer to its name" pointed at an app with
        // no launcher icon, and the abilities read "Nothing for this yet" because the
        // client has no models to count. Both facts are here, so both are served here.
        var footprint = new MemoryManager(new DeviceResourcesReader());

        // AND WHAT HAPPENS WHEN IT HEARS ITS NAME, WHICH WAS NOTHING AT ALL.
        //
        // The detector fired, CircleNeuronLinkService.OnWoke incremented an integer,
        // and that was the entire consequence: no tone, no microphone, no question,
        // no answer. Every part existed and no line joined them - the repo's named
        // first defect, sitting on the headline feature. A person said "Hey B" on a
        // P30 on 2026-10-01, the log read HEARD "HEY B" p=0.3029, and the phone did
        // not react.
        //
        // WIRED HERE because this is the process that has all four pieces: the
        // microphone, the recogniser, the brain and the voice. The turn itself is
        // WakeTurn, in the product.
        WakeTurn.Speech = CircleNeuronLinkService.Speech;

        // AND WARM IT NOW, NOT AT THE FIRST WAKE. Measured on the one wake that
        // fired in four days on the P30: seven seconds between the owner saying
        // the name and hearing "Hi.", four of them the voice model opening for
        // the first time. Not awaited - the service has a microphone to open,
        // and a voice that cannot warm only leaves the greeting synthesised
        // live, exactly as it was.
        _ = Task.Run(() => WakeTurn.WarmAsync());

        var resident = new ResidentListeningControl(this);
        resident.Woke += (_, phrase) =>
        {
            // NOT AWAITED, AND IT MUST NOT BE. This fires on the wake loop's own
            // thread, and the first thing the turn does is stop that loop - awaiting
            // here would have the loop waiting on itself.
            _ = WakeTurn.RunAsync(phrase);
        };

        CircleNeuronLinkService.Resident = resident;

        // AND COME BACK AFTER BEING KILLED, WHICH IS NOT THE SAME AS AFTER A REBOOT.
        //
        // Android SIGKILLed this process twice on a P30 on 2026-10-02 - MemFree at
        // 80 MB of 3.7 GB, load average 44, every other app's services going down in
        // the same window. ActivityManager restarted the service, the brain came
        // back, and LISTENING DID NOT: there was no code anywhere that resumed it.
        // The phone stopped answering to its name and nothing said so, while the
        // app's checkbox went on showing it ticked.
        //
        // BootReceiver covers the reboot case and deliberately does NOT resume -
        // from Android 14 a microphone-typed service may not be started from
        // BOOT_COMPLETED, and holding a microphone should follow a deliberate act
        // rather than a power cycle. A process restart is a different thing: the
        // person already said yes, nothing about that changed, and the only reason
        // the microphone closed is that the phone was short of memory.
        //
        // ResidentPrefs is the record of what they chose. It defaults to yes, so a
        // fresh install listens; an explicit no is written now and is obeyed here.
        //
        // THE CATALOGUE, BEFORE ANYTHING CAN START THE BRAIN - and resuming listening,
        // just below, can. It used to be opened inside the background task after the
        // download sweep, so whether the brain found a catalogue, a crash verdict or
        // anything to wait for was a race: on the P30 on 2026-10-16 the catalogue won
        // by about 130 ms. In order, and each step for a reason:
        //
        //   1. Open it. A file handle, a CREATE TABLE IF NOT EXISTS and a write-ahead
        //      log - milliseconds - and enough to answer "is this model refused".
        //   2. Record how the last load ended, BEFORE the bootstrap: its housekeeping
        //      removes a model in favour of one that has loaded here and is not
        //      refused, so a death recorded afterwards could let a model on its second
        //      strike serve as the replacement. See CircleNeuronService.RecordLastLoad.
        //   3. Bootstrap in the background - refresh from the registry, reconcile what
        //      is on disk, assess, then housekeeping - and hand the brain the task, so
        //      it chooses from this launch's ranks and not the last one's.
        //
        // AddModelCatalogue exists, wires SqliteModelCatalog + DeviceModelAssessor +
        // CatalogueFeedWriter, is tested by ModelCatalogueWiringTests - and had ZERO
        // CALLERS. There is no DI container in this process, so the catalogue is built
        // directly and handed over as a static, the same way OptionsFactory, Facts,
        // Listener and StorageDirectory already are.
        // THE ONE DOOR FOR QUESTIONS, AND THE BRAIN IN ITS OWN PROCESS. Built before the
        // catalogue so the supervisor - not the old rule that made every surviving
        // breadcrumb a strike - judges how the last run ended.
        ComposeRequestPath();

        try
        {
            var circleDir = System.IO.Path.Combine(FilesDir!.AbsolutePath, "CircleAI");
            System.IO.Directory.CreateDirectory(circleDir);

            var catalogue = new CircleAI.Inference.SqliteModelCatalog(
                "Data Source=" + System.IO.Path.Combine(circleDir, "catalogue.db"));

            var stateDir = System.IO.Path.Combine(circleDir, "state");
            System.IO.Directory.CreateDirectory(stateDir);

            CircleAI.Device.CircleNeuronService.Catalogue = catalogue;
            CircleAI.Device.CircleNeuronService.StateDirectory = stateDir;

            // CLASSIFIED, NOT COUNTED. On API 29 every death that left a breadcrumb used
            // to be a strike - a reboot, an update, the low-memory killer - and two of
            // them refused the only brain the phone could run.
            _supervisor?.RecoverFromPreviousRun();

            CircleAI.Device.CircleNeuronService.CatalogueReady = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // HOUSEKEEPING'S MODEL HALF, SWITCHED ON. The sweep's comment below
                    // promised it and nothing did it: the bootstrap ran with
                    // reclaimInferior off, so a superseded model sat on the phone for
                    // good. It runs inside the bootstrap, after the assessment, because
                    // its rules read the verdicts - the rules in CatalogueHousekeeping:
                    // chat models only, never in favour of a model that has not loaded
                    // on this phone, and never the model the brain is running.
                    var result = CircleAI.Inference.CatalogueBootstrap.Run(
                        catalogue,
                        CircleAI.Inference.DeviceModelAssessor.ForThisBuild(catalogue),
                        CircleAI.Core.DeviceProbe.Snapshot(),
                        modelsDirectory: ModelStore.Path,
                        reclaimInferior: true,
                        keep: id => string.Equals(
                            id, CircleAI.Device.CircleNeuronService.RunningModel, StringComparison.Ordinal));

                    global::Android.Util.Log.Info("CircleAI.Setup",
                        $"catalogue: {result.Assessed} assessed, {result.Compatible} run on this device");

                    // AND IF ANY OF IT IS DEAD WEIGHT, SAY SO. This is the first moment
                    // the device knows: the assessment has just run, so it can tell "a
                    // model I cannot use" from "a model I have not judged yet". It says
                    // it once and offers; a model REFUSED from evidence is never deleted
                    // here - that takes a person's yes.
                    CircleAI.Device.CircleNeuronLinkService.AnnounceDeadWeightIfAny();
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Warn("CircleAI.Setup",
                        "catalogue could not be assessed: " + ex.Message);
                }
            });
        }
        catch (Exception ex)
        {
            // A device with no catalogue refuses nothing and selects as it always did.
            // Worse than having one; far better than no brain at all.
            global::Android.Util.Log.Warn("CircleAI.Setup", "catalogue unavailable: " + ex.Message);
        }

        try
        {
            if (ResidentPrefs.WasRunning(this))
            {
                global::Android.Util.Log.Info("CircleAI.Wake", "resuming: the owner had it on");
                _ = resident.ResumeAsync();
            }
            else
            {
                global::Android.Util.Log.Info("CircleAI.Wake", "not resuming: the owner turned it off");
            }
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("CircleAI.Wake", "could not resume listening: " + ex.Message);
        }
        // THE CATALOGUE GOES IN HERE TOO. Without it every ability row re-derived
        // fit from the shipped JSON while the runtime selected from the assessed
        // catalogue - so a model this phone had been killed by could be named on
        // a screen as the one it uses. Same lambda shape as DeviceSetup above,
        // and for the same reason: the catalogue is built later in this method.
        CircleNeuronLinkService.Facts    = new DeviceFacts(
            footprint, catalogue: () => CircleAI.Device.CircleNeuronService.Catalogue);

        // HOUSEKEEPING, ON INIT, BECAUSE NOTHING WAS DOING IT HERE. The catalogue
        // bootstrap used to run in the hybrid app with reclaimInferior: true, and it
        // left with the models - so the process that now OWNS them inherited none of
        // the tidying. Two kinds of waste accumulate and neither is visible to the
        // storage census, which counts downloaded models as precious:
        //
        //   - a model a better one of similar size has superseded;
        //   - a model that no longer fits this device, when something else here can
        //     do the job - the 21.2 GB MoE fetched on an unmeasured mmap claim and
        //     refused by the corrected rule was exactly that;
        //   - and the leftovers of a download that stopped: 2.9 GB of .tmp nothing
        //     would ever look at again.
        //
        // OFF THE MAIN THREAD AND AFTER AN HOUR'S IDLE. This walks the model store,
        // which is gigabytes, and OnCreate runs before any component does. The idle
        // window is what makes it safe to run at start-up at all: a download in
        // flight is a .tmp being written to right now, and killing it would end a
        // fetch somebody is watching a progress bar for.
        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                var freed = CircleAI.Inference.CatalogueHousekeeping
                    .SweepAbandoned(ModelStore.Path, TimeSpan.FromHours(1));

                global::Android.Util.Log.Info("CircleAI.Housekeeping",
                    freed > 0
                        ? $"swept {CircleAI.Assistant.MemoryBudget.Human(freed)} of abandoned downloads"
                        : "no abandoned downloads to sweep");
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("CircleAI.Housekeeping", "sweep failed: " + ex.Message);
            }

            // AND THEN FINISH WHAT THE OWNER ALREADY STARTED - HERE, NOT ON THE BRAIN.
            //
            // A Circle OS device held 1.1 GB of a 22.8 GB Qwen3.6-35B-A3B, the
            // smartest model on it, with llm.mnn.weight never arrived. The first fix
            // for that hung off CircleNeuronService's "brain ready" line, and on the
            // device it never ran once: that brain only starts when somebody asks a
            // question, and nobody had. The service process was up, bound, and had
            // logged not one line under its own tag.
            //
            // This is the process that OWNS the models, and it runs on every start
            // whether or not anybody is chatting - so it is the one place a resume
            // belongs. It is also the only place, which is what stops two gigabyte
            // downloads being started at once.
            //
            // AFTER the sweep, deliberately: the sweep is what clears a resume marker
            // whose bytes are gone, and resuming from one of those writes into a hole.
            //
            // AND THROUGH A FOREGROUND SERVICE, BECAUSE THIS PROCESS GETS FROZEN.
            // The resume ran here directly first, and on the device it stopped dead
            // three times at the same kind of point - 13.67 GB of 21.3 GB, process
            // alive, sockets silent, nothing in the log. ActivityManager: this
            // process was CACHED (curProcState=15) with the screen on and the phone
            // charging, so Android's freezer had suspended its threads. A frozen
            // thread cannot return from a read, and cannot run the stall-guard timer
            // meant to notice either. ModelFetchService exists to hold the process
            // up for the duration; it shows nothing when nothing is owed.
            //
            // (The catalogue used to be opened here, after the sweep - which the brain
            // could beat. It is opened in OnCreate now, before anything can start the
            // brain; see "THE CATALOGUE, BEFORE ANYTHING CAN START THE BRAIN".)
            try
            {
                CircleAI.Device.ModelFetchService.StorageDirectory = ModelStore.Path;

                using var loader = new CircleAI.Inference.BundleModelLoader(
                    ModelStore.Path, new CircleAI.Core.Models.ModelRegistryService());

                // The one unconditional line, said whether there is news or not.
                global::Android.Util.Log.Info("CircleAI.Setup",
                    "setup: " + CircleAI.Inference.ModelSetup.Describe(loader));

                CircleAI.Device.ModelFetchService.StartIfAnythingIsOwed(this, loader);
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("CircleAI.Setup", "resume failed: " + ex.Message);
            }

            await System.Threading.Tasks.Task.CompletedTask.ConfigureAwait(false);
        });

        // AND THE SKILL LIBRARY, off the UI thread: the first call unpacks 20 MB out
        // of the APK, and OnCreate runs before any component does. Nothing needs it
        // until a turn, and a bind that arrives first will simply find it a moment
        // later.
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var skills = SkillLibrary.Open(this);
                if (skills is not null) CircleNeuronLinkService.Skills = skills;
                CircleAI.Assistant.CircleAISession.Library = skills;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("CircleAI.Skills", "skill library: " + ex.Message);
            }
        });

        // No auth gate on the SERVICE: a background service cannot raise a biometric
        // sheet. Approval happens in LinkConsentActivity, launched FOR RESULT by the
        // foreground client, which is also how the OS tells the consent screen which
        // package is really asking.
    }
}
