// InferenceSupervisor.cs
//
// THE BRAIN, AS THE QUEUE SEES IT ON ANDROID: CHOSEN, LOADED AND WATCHED FROM THE MAIN
// PROCESS, RUN IN THE INFERENCE PROCESS.
//
// It owns everything about the model that must outlive the model's process: which model
// (the same rules the brain host used, with the Qwen3.5 floor as data), whether its files
// were already verified (so the 9.4 s weight hash is paid once, not per start), the load's
// breadcrumb, the verdict when a load or an answer dies, readiness, and when to try again.
//
// A DEATH IS CLASSIFIED HERE, WHILE THE EVIDENCE IS FRESH. The worker dying alone is
// itself evidence this process could not have had before the split: the phone did not
// reboot and the app was not updated, or this process would be gone too. With the last
// heartbeat's free memory and the last trim level, a low-memory kill and a native crash
// can be told apart even on API 29 - and only a crash is held against the model.

using Android.App;
using Android.Content;
using Android.OS;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Inference;

namespace CircleAI.Service.Droid.Inference;

/// <summary>The supervised brain: an <see cref="IBrainWorker"/> over the ":inference" process.</summary>
public sealed class InferenceSupervisor : IBrainWorker
{
    private const string Tag = "CircleAI.Supervisor";
    private static readonly TimeSpan[] Backoff =
        { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5) };
    private static readonly TimeSpan NoProgress = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan LoadLimit = TimeSpan.FromMinutes(3);
    private const string HeldOffEvidence = "held-off";

    private readonly Context _context;
    private readonly WorkerConnection _connection;
    private readonly IReadinessTracker _readiness;
    private readonly Func<IModelCatalog?> _catalogue;
    private readonly string _stateDir;
    private readonly string _store;
    private readonly Func<AIOptions> _options;
    private readonly Action<string, bool> _announce;
    private readonly Action<string> _log;
    private readonly object _gate = new();

    private BrainWorkerStatus _status;
    private Task? _loading;
    private int _failures;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private WorkerHeartbeat? _heartbeat;
    private int _lastTrim;
    private bool _killedByUs;
    private bool _killedForTest;
    private Timer? _beat;
    private bool _recovered;
    private string? _pendingCandidate;
    private bool _nothingRunnable;
    private DateTimeOffset _lastUse = DateTimeOffset.UtcNow;

    /// <summary>A supervisor over a worker connection.</summary>
    public InferenceSupervisor(
        Context context,
        WorkerConnection connection,
        IReadinessTracker readiness,
        Func<IModelCatalog?> catalogue,
        string stateDirectory,
        string modelStoreDirectory,
        Func<AIOptions> optionsFactory,
        Action<string, bool>? announce = null,
        Action<string>? log = null)
    {
        _context = context.ApplicationContext ?? context;
        _connection = connection;
        _readiness = readiness;
        _catalogue = catalogue;
        _stateDir = stateDirectory;
        _store = modelStoreDirectory;
        _options = optionsFactory;
        _announce = announce ?? ((_, _) => { });
        _log = log ?? (line => global::Android.Util.Log.Info(Tag, line));
        _status = BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow);
        _connection.Died += OnWorkerDied;
    }

    /// <summary>Whether the queue has nothing to do (set by the composition root).</summary>
    public Func<bool>? SchedulerIdle { get; set; }

    /// <summary>The model the brain runs, while it runs one.</summary>
    public string? RunningModel { get { lock (_gate) return _status.Phase == BrainPhase.Ready ? _status.ModelId : null; } }

    /// <summary>A better model that is on the phone but not yet proven here.</summary>
    public string? PendingCandidate { get { lock (_gate) return _pendingCandidate; } }

    /// <inheritdoc />
    public BrainWorkerStatus Status { get { lock (_gate) return _status; } }

    /// <inheritdoc />
    public event EventHandler<BrainWorkerStatus>? StatusChanged;

    // ── IBrainWorker ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task EnsureReadyAsync(CancellationToken ct)
    {
        Task loading;
        lock (_gate)
        {
            if (_status.Phase == BrainPhase.Ready && _connection.IsConnected) return Task.CompletedTask;
            if (_loading is null)
            {
                var now = DateTimeOffset.UtcNow;
                if (now < _retryAt && _status.LastFailure is { } last)
                    return Task.FromException(new BrainWorkerException(last));
                _loading = Task.Run(() => LoadAsync(null));
            }
            loading = _loading;
        }
        return loading.WaitAsync(ct);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await EnsureReadyAsync(ct).ConfigureAwait(false);
        var model = Status.ModelId;

        // THE WATCHDOG: no progress for 90 s means a hung native call, not a slow
        // phone (a P30 prefill of a long prompt is ~30 s). It ends the worker, and the
        // death that follows is classified as ours, not the model's or Android's.
        using var watch = new CancellationTokenSource();
        var lastProgress = DateTimeOffset.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!watch.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), watch.Token).ConfigureAwait(false);
                    if (DateTimeOffset.UtcNow - lastProgress > NoProgress)
                    {
                        _log($"no progress on ask {job.AskId} for {NoProgress.TotalSeconds:0} s; ending the worker");
                        lock (_gate) _killedByUs = true;
                        _connection.Kill();
                        return;
                    }
                }
            }
            catch (System.OperationCanceledException) { }
        });

        IAsyncEnumerator<BrainChunk> stream = _connection.GenerateAsync(job, ct).GetAsyncEnumerator(ct);
        try
        {
            while (true)
            {
                BrainChunk chunk;
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false)) break;
                    chunk = stream.Current;
                }
                catch (WorkerDiedException died)
                {
                    var failure = await ClassifyDeathAsync(died.Death).ConfigureAwait(false);
                    Fail(failure, model);
                    throw new BrainWorkerException(failure, died);
                }

                lastProgress = DateTimeOffset.UtcNow;
                yield return chunk;
            }
        }
        finally
        {
            lock (_gate) _lastUse = DateTimeOffset.UtcNow;
            watch.Cancel();
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(CancellationToken ct)
    {
        string? model;
        lock (_gate) model = _status.ModelId;
        try { await _connection.ReleaseAsync(ct).ConfigureAwait(false); }
        catch (WorkerDiedException) { /* already gone, which is what was wanted */ }
        Set(new BrainWorkerStatus(BrainPhase.Installed, LoadStep.None, model, null, null, null, DateTimeOffset.UtcNow));
        _log("the brain was released to free memory");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _connection.Died -= OnWorkerDied;
        _beat?.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    // ── memory ───────────────────────────────────────────────────────────────

    /// <summary>End the inference process on purpose (fault injection), recorded as ours.</summary>
    /// <remarks>
    /// A death we caused must not be read as the phone's or the model's: the next
    /// classification says FaultInjected, so no strike and no memory verdict follows from
    /// it - and NOT the watchdog's TimedOut, which is never retried and so failed the very
    /// question this exists to prove survives.
    /// </remarks>
    public void KillForTest()
    {
        lock (_gate) _killedForTest = true;
        _connection.Kill();
    }

    /// <summary>Android asked the app to trim memory.</summary>
    /// <remarks>
    /// THE OWNER'S RULE: Qwen3.5 stays loaded unless the phone is critically short of
    /// memory. Only RUNNING_CRITICAL (15) or COMPLETE (80), and only when nothing is
    /// being answered, lets the model go; the next question loads it again.
    /// </remarks>
    public void OnTrimMemory(int level)
    {
        DateTimeOffset lastUse;
        lock (_gate) { _lastTrim = level; lastUse = _lastUse; }

        // NOT BETWEEN TWO QUESTIONS. Measured on the P30 on 2026-10-17: RUNNING_CRITICAL
        // (15) arrives while the model is resident - a 3.87 GB phone is always near the
        // line with it loaded - and one landed in the milliseconds between two queued
        // questions, so the brain was released and reloaded 15 s later for the next one.
        // A brain in use stays; one idle for two minutes goes when the phone asks.
        // AND NOT ON LEVEL 15 AT ALL ANY MORE, ON THE STRENGTH OF THE NOTE ABOVE.
        //
        // If RUNNING_CRITICAL "arrives while the model is resident - a 3.87 GB phone is
        // always near the line with it loaded" as a matter of course, then 15 is not a
        // shortage signal on this hardware. It is the NORMAL STATE with a brain loaded,
        // and releasing on it meant every question asked more than two minutes after the
        // last one paid a cold load - which, for an assistant, is nearly every question.
        //
        // MEASURED ON THE P30 ON 2026-10-28, end to end:
        //   brain ready: Qwen3.5-0.8B-MNN in 14926 ms      (at service start, unprompted)
        //   Loading.razor:593 warms it again on the way out of the load screen
        //   ...and the next trim released both, so a spoken question waited ~14 s for a
        //   reload before prefill could begin. The warm-up was never the missing piece;
        //   this release was throwing it away.
        //
        // 80 IS TRIM_MEMORY_COMPLETE - "at the end of the LRU list, killed next" - and
        // that one IS a shortage worth a release.
        //
        // WHAT THIS COSTS, SAID PLAINLY: the model now stays resident through level 15,
        // so Android's low-memory killer is likelier to take the WHOLE service instead of
        // just the model. That is a real trade and it is the owner's to make; it was made
        // deliberately in favour of a phone that answers when spoken to. If the service
        // starts dying under memory pressure instead, this condition is the thing to put
        // back - and OnWorkerDied's classification will say so rather than blaming the model.
        var quiet = DateTimeOffset.UtcNow - lastUse > TimeSpan.FromMinutes(2);
        if (level == 15 && Status.Phase == BrainPhase.Ready)
            _log($"trim level 15 (routine on this phone with a model resident): kept the brain, "
               + $"it answered {(DateTimeOffset.UtcNow - lastUse).TotalSeconds:0} s ago");
        if (level >= 80 && !quiet && Status.Phase == BrainPhase.Ready)
            _log($"trim level {level}: kept the brain, it answered {(DateTimeOffset.UtcNow - lastUse).TotalSeconds:0} s ago");
        if (level >= 80 && quiet && SchedulerIdle?.Invoke() == true && Status.Phase == BrainPhase.Ready)
        {
            // WHICH SIGNAL, AND HOW ANDROID SEES THIS PROCESS - so a release can be told
            // apart from a phone that simply ranks a foreground service as background.
            var importance = "?";
            try
            {
                var info = new ActivityManager.RunningAppProcessInfo();
                ActivityManager.GetMyMemoryState(info);
                importance = ((int)info.Importance).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            catch { }
            _log($"trim level {level} (importance {importance}) with nothing to answer: releasing the brain");
            _ = Task.Run(() => ReleaseAsync(CancellationToken.None));
        }
    }

    // ── loading ──────────────────────────────────────────────────────────────

    private async Task LoadAsync(string? forced)
    {
        string? model = null;
        try
        {
            if (!_recovered) RecoverFromPreviousRun();

            var choice = forced ?? Choose();
            if (choice is null)
            {
                // NOTHING WILL ARRIVE BY ITSELF when every model here is refused, and the
                // queue is told so - RetryAfter null, "not without being asked" - so a
                // question hears why at once instead of waiting for a setup that has
                // nothing left to bring. AND IT IS NOT REMEMBERED: the next question chooses
                // again, so a pardon ("try the big one again") said a moment ago counts.
                bool nothingRunnable;
                lock (_gate) nothingRunnable = _nothingRunnable;
                var none = new Failure(FailureKind.NotInstalled, Certainty.Certain, WorkPhase.Load, null,
                    nothingRunnable ? "refused" : "no-model",
                    nothingRunnable ? "every model on this phone is refused" : "no usable model is on this phone",
                    DateTimeOffset.UtcNow);
                Set(new BrainWorkerStatus(BrainPhase.NoModel, LoadStep.None, null, none,
                    nothingRunnable ? null : TimeSpan.FromSeconds(30), null, DateTimeOffset.UtcNow));
                Report(BrainPhase.NoModel, LoadStep.None, null, null, ReadinessSentences.NoModel);
                lock (_gate) _retryAt = nothingRunnable ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow.AddSeconds(30);
                throw new BrainWorkerException(none);
            }

            model = choice;

            // HELD OFF, NOT REFUSED. A load of this model stopped a moment ago with nothing
            // to explain it, or for memory, and loading straight back into the same
            // pressure is how the P30 lost its service twice in four minutes on
            // 2026-10-23. Before the breadcrumb, so a held-off load leaves none.
            if (HeldOff(model) is { } held) throw HoldOffNow(model, held.Count, held.Remaining);

            var skipHash = Verified(model);
            Set(new BrainWorkerStatus(BrainPhase.Loading, skipHash ? LoadStep.LoadingWeights : LoadStep.Verifying, model, null, null, _connection.Pid, DateTimeOffset.UtcNow));
            Report(BrainPhase.Loading, skipHash ? LoadStep.LoadingWeights : LoadStep.Verifying, model, null, ReadinessSentences.Loading);
            _log($"loading {model} in the inference process (hash {(skipHash ? "trusted" : "checked")})");

            // THE MARK THAT OUTLIVES BOTH PROCESSES. If the phone reboots or the app is
            // updated mid-load, nothing survives to see the death - except this.
            CircleAI.Assistant.DeviceDiagnostics.BeginRisky(_stateDir, CrashVerdict.Breadcrumb(model));
            var freeAtStart = DeviceProbe.Snapshot().UsableRamGb;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var workerDied = false;
            try
            {
                using var limit = new CancellationTokenSource(LoadLimit);
                await _connection.LoadAsync(new WorkerLoadRequest(model, _store, skipHash),
                    new Progress<LoadStep>(step => Report(BrainPhase.Loading, step, model, null, ReadinessSentences.Loading)),
                    limit.Token).ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                lock (_gate) _killedByUs = true;
                _connection.Kill();
                workerDied = true;
                throw new WorkerDiedException(new WorkerDeath(_connection.Pid, WorkPhase.Load, model, null, DateTimeOffset.UtcNow));
            }
            catch (WorkerDiedException)
            {
                workerDied = true;
                throw;
            }
            finally
            {
                // A DEATH KEEPS ITS BREADCRUMB UNTIL ITS VERDICT IS WRITTEN (below, in the
                // catch). Classifying takes most of a second, and a phone killing the worker
                // for memory may take this process next - with the crumb gone and nothing
                // recorded, the next start would load straight back into the same pressure.
                if (!workerDied) CircleAI.Assistant.DeviceDiagnostics.EndRisky(_stateDir);
            }

            CrashVerdict.Loaded(_catalogue(), model);
            ClearSetback(model);
            MarkVerified(model);
            lock (_gate) { _failures = 0; _retryAt = DateTimeOffset.MinValue; _lastUse = DateTimeOffset.UtcNow; }
            Set(new BrainWorkerStatus(BrainPhase.Ready, LoadStep.None, model, null, null, _connection.Pid, DateTimeOffset.UtcNow));
            Report(BrainPhase.Ready, LoadStep.None, model, null, ReadinessSentences.Ready);
            _log($"brain ready: {model} in {clock.ElapsedMilliseconds} ms (free RAM at start {freeAtStart:0.00} GB)");
            StartHeartbeat();
        }
        catch (WorkerDiedException died)
        {
            var failure = await ClassifyDeathAsync(died.Death).ConfigureAwait(false);
            Fail(failure, model);
            CircleAI.Assistant.DeviceDiagnostics.EndRisky(_stateDir);
            throw new BrainWorkerException(failure, died);
        }
        catch (BrainWorkerException ex) when ((ex.Failure.Kind != FailureKind.NotInstalled || model is not null)
                                              && !ex.Failure.Evidence.StartsWith(HeldOffEvidence, StringComparison.Ordinal))
        {
            Fail(ex.Failure with { ModelId = ex.Failure.ModelId ?? model }, model);
            throw;
        }
        finally
        {
            lock (_gate) _loading = null;
        }
    }

    private void Fail(Failure failure, string? model)
    {
        // THE VERDICT FOLLOWS THE KIND. Only a native crash while the model was
        // working is a strike; everything else is the moment or the phone, and
        // refusing the model for it would refuse the only brain this phone can run. A
        // load that stopped unexplained, or for memory, holds the next one off instead.
        if (model is not null)
        {
            CrashVerdict.Record(_catalogue(), CrashVerdict.Breadcrumb(model), line => _log(line),
                                killedByTheSystem: !failure.CountsAgainstModel);
            if (FailurePolicy.HoldsOff(failure.Kind, failure.Phase)) RecordSetback(model);
        }

        _readiness.Failed(failure);
        var holdOff = model is not null && HeldOff(model) is { } held ? held.Remaining : TimeSpan.Zero;
        TimeSpan retry;
        lock (_gate)
        {
            retry = Backoff[Math.Min(_failures, Backoff.Length - 1)];
            if (holdOff > retry) retry = holdOff;
            _failures++;
            _retryAt = DateTimeOffset.UtcNow + retry;
        }

        var paused = failure.Kind is FailureKind.OutOfMemory or FailureKind.AndroidStopped or FailureKind.AndroidTimeout;
        var phase = failure.Kind == FailureKind.NotInstalled ? BrainPhase.NoModel : paused ? BrainPhase.Paused : BrainPhase.Failed;
        Set(new BrainWorkerStatus(phase, LoadStep.None, model, failure, retry, null, DateTimeOffset.UtcNow));
        _readiness.Report(new ComponentState(ReadinessComponents.Brain, phase, LoadStep.None, failure.Kind,
            failure.Kind == FailureKind.OutOfMemory ? PauseReason.WaitingForMemory
            : paused ? PauseReason.StartNotAllowed : PauseReason.None,
            ReadinessSentences.For(failure.Kind), model, DateTimeOffset.UtcNow, null, retry));
        _log($"brain {phase}: {failure.Kind} ({failure.Certainty}, {failure.Evidence}); next try in {retry.TotalSeconds:0} s");
    }

    private void OnWorkerDied(object? sender, WorkerDeath death)
    {
        // A DEATH WHILE IDLE STILL TAKES THE MODEL WITH IT. The next question loads
        // it again; readiness says what happened in the meantime.
        if (death.Phase is WorkPhase.Idle or WorkPhase.None && Status.Phase == BrainPhase.Ready)
        {
            _ = Task.Run(async () =>
            {
                var failure = await ClassifyDeathAsync(death).ConfigureAwait(false);
                _readiness.Failed(failure);
                Set(new BrainWorkerStatus(BrainPhase.Installed, LoadStep.None, death.ModelId, failure, null, null, DateTimeOffset.UtcNow));
                Report(BrainPhase.Installed, LoadStep.None, death.ModelId, failure.Kind, ReadinessSentences.For(failure.Kind));
                _log($"the idle brain's process ended ({failure.Kind}); it loads again on the next question");
            });
        }
    }

    private async Task<Failure> ClassifyDeathAsync(WorkerDeath death)
    {
        // ANDROID WRITES ITS EXIT RECORD A MOMENT AFTER THE BINDER DIES.
        await Task.Delay(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);
        bool byUs, forTest;
        WorkerHeartbeat? beat;
        int trim;
        lock (_gate)
        {
            byUs = _killedByUs; _killedByUs = false;
            forTest = _killedForTest; _killedForTest = false;
            beat = _heartbeat; trim = _lastTrim;
        }

        var facts = ExitEvidence.ForWorker(_context, death, beat, trim) with
        {
            KilledByUs = byUs ? true : null,
            KilledForTest = forTest ? true : null,
        };
        var failure = FailureClassifier.Classify(facts);
        if (failure.Kind == FailureKind.OutOfMemory && death.ModelId is { } m)
            RecordKilledAt(m, DeviceProbe.Snapshot().UsableRamGb);
        return failure;
    }

    // ── choosing the model ───────────────────────────────────────────────────

    /// <summary>
    /// Which model to load: the same rules the brain host used, with the floor as data -
    /// and a better model that has never loaded here waits until it is validated.
    /// </summary>
    private string? Choose()
    {
        var options = _options();
        var catalogue = _catalogue();
        var probe = DeviceProbe.Snapshot();
        lock (_gate) _nothingRunnable = false;
        using var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(_store, registry);
        var selector = DeviceAwareModelSelector.Preferring(catalogue, registry);

        string? wanted = options.ModelId;
        if (!string.IsNullOrWhiteSpace(wanted) && registry.GetLatestModel(wanted!) is { Retired: true }) wanted = null;
        if (string.IsNullOrWhiteSpace(wanted)) wanted = selector.BestFit(probe, options.RequiredCapabilities)?.ModelId;

        // NOT DOWNLOADED IS NOT "THE BIG ONE STOPPED THIS PHONE". The old host said
        // OnTheSmallerBrain here; the preferred model was simply not on the phone.
        if (!string.IsNullOrWhiteSpace(wanted) && !loader.ModelPresent(wanted!))
        {
            var here = loader.BestInstalledChatModel(probe, refused: id => CrashVerdict.IsRefused(catalogue, id));
            _log($"the selector wanted {wanted}, which is not on this phone; {(here is null ? "nothing else is" : "using " + here)}");
            wanted = here;
        }

        if (string.IsNullOrWhiteSpace(wanted) || !loader.ModelPresent(wanted!)) return null;

        if (CrashVerdict.IsRefused(catalogue, wanted))
        {
            var instead = loader.BestInstalledChatModel(probe, refused: id => CrashVerdict.IsRefused(catalogue, id));
            if (string.IsNullOrWhiteSpace(instead))
            {
                _announce(CircleAI.Assistant.AssistantPersona.NothingItCanRun, false);
                lock (_gate) _nothingRunnable = true;
                return null;
            }
            _announce(CircleAI.Assistant.AssistantPersona.OnTheSmallerBrain, false);
            wanted = instead;
        }

        // KILLED HERE AT MORE FREE MEMORY THAN THERE IS NOW: it will be killed again.
        // 'Not now', not 'defective' - and the floor still wins if nothing else is here.
        if (KilledAt(wanted!) is { } killedAt && probe.UsableRamGb < killedAt)
        {
            var other = loader.InstalledIds()
                .Where(id => id != wanted && registry.GetLatestModel(id) is { Retired: false, Modality: ModelModality.Chat }
                             && !CrashVerdict.IsRefused(catalogue, id) && !(KilledAt(id) is { } k && probe.UsableRamGb < k))
                .OrderBy(id => registry.GetLatestModel(id)?.MinRamGb ?? double.MaxValue)
                .FirstOrDefault();
            if (other is not null)
            {
                _log($"{wanted} was killed for memory at {killedAt:0.00} GB free; {probe.UsableRamGb:0.00} GB now - using {other}");
                wanted = other;
            }
        }

        // KEEP THE WORKING MODEL UNTIL ITS REPLACEMENT IS VALIDATED. A better model that
        // has never loaded on this phone is a candidate; the proven one keeps answering.
        if (catalogue is not null && catalogue.Assessment(wanted!) is { Proven: false })
        {
            var proven = loader.InstalledIds()
                .Select(id => catalogue.Assessment(id))
                .Where(a => a is { Proven: true, Refused: false } && !a.Entry.Retired && a.Entry.Modality == ModelModality.Chat)
                .OrderByDescending(a => a!.Entry.QualityRank)
                .FirstOrDefault();
            if (proven is not null && proven.Entry.Name != wanted && !Rejected(wanted!))
            {
                lock (_gate) _pendingCandidate = wanted;
                _log($"{wanted} has not loaded on this phone yet; keeping {proven.Entry.Name} until it is validated");
                wanted = proven.Entry.Name;
            }
        }

        return wanted;
    }

    // ── validation of a better model ─────────────────────────────────────────

    /// <summary>
    /// Try the pending candidate, when the phone is idle and charging: load it, ask it
    /// three questions with known answers, and keep it only if it answers them all.
    /// </summary>
    public async Task<bool> ValidateCandidateIfDueAsync(CancellationToken ct)
    {
        string? candidate;
        string? active;
        lock (_gate) { candidate = _pendingCandidate; active = _status.ModelId; }
        if (candidate is null || SchedulerIdle?.Invoke() != true || !Charging() || Rejected(candidate)) return false;

        // HELD OFF IS NOT FAILED. Validating now would release the working brain, throw the
        // hold-off at once, and reject for a week a model that was only told to wait.
        if (HeldOff(candidate) is not null) return false;

        _log($"validating {candidate} while the phone is idle and charging");
        await ReleaseAsync(ct).ConfigureAwait(false);
        var golden = new (string Q, string A)[]
        {
            ("What is 17 times 4?", "68"),
            ("What is the capital of Kenya?", "Nairobi"),
            ("How many days are in a leap year?", "366"),
        };

        var passed = false;
        try
        {
            lock (_gate) _loading = Task.Run(() => LoadAsync(candidate));
            await EnsureReadyAsync(ct).ConfigureAwait(false);
            var right = 0;
            foreach (var (q, a) in golden)
            {
                var text = new System.Text.StringBuilder();
                var job = new BrainJob("validate-" + Guid.NewGuid().ToString("N"),
                    new[] { new ChatMessage("user", q) }, q, 64, false, DateTimeOffset.UtcNow.AddMinutes(2));
                await foreach (var chunk in GenerateAsync(job, ct).ConfigureAwait(false)) text.Append(chunk.Text);
                if (text.ToString().Contains(a, StringComparison.OrdinalIgnoreCase)) right++;
            }
            passed = right == golden.Length;
            _log($"validation of {candidate}: {right}/{golden.Length}");
        }
        catch (Exception ex)
        {
            _log($"validation of {candidate} failed: {ex.GetType().Name}");
        }

        lock (_gate) _pendingCandidate = null;
        if (passed) return true;   // it is loaded, proven by its load, and the best: it stays

        MarkRejected(candidate);
        if (active is not null)
        {
            await ReleaseAsync(ct).ConfigureAwait(false);
            lock (_gate) _loading = Task.Run(() => LoadAsync(active));
            try { await EnsureReadyAsync(ct).ConfigureAwait(false); } catch { /* readiness says why */ }
        }
        return false;
    }

    private bool Charging()
    {
        try
        {
            var battery = _context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
            var plugged = battery?.GetIntExtra(BatteryManager.ExtraPlugged, 0) ?? 0;
            return plugged != 0;
        }
        catch { return false; }
    }

    // ── what survives a process: verified files, kills, rejections ───────────

    private string StatePath(string kind, string model) =>
        Path.Combine(_stateDir, kind, string.Concat(model.Split(Path.GetInvalidFileNameChars())) + ".txt");

    private (long Size, long Modified)? Anchor(string model)
    {
        try
        {
            var dir = Path.Combine(_store, model);
            var biggest = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                .Where(f => !f.Name.EndsWith(".tmp", StringComparison.Ordinal))
                .OrderByDescending(f => f.Length).FirstOrDefault();
            return biggest is null ? null : (biggest.Length, biggest.LastWriteTimeUtc.Ticks);
        }
        catch { return null; }
    }

    // THE HASH IS PAID ONCE. After a load that hashed the weights and succeeded, the
    // weight file's size and time are written down; a later load with the same file
    // trusts them instead of reading 470 MB again (about 9.4 s on the P30).
    private bool Verified(string model)
    {
        try
        {
            var path = StatePath("verified", model);
            return File.Exists(path) && Anchor(model) is { } a && File.ReadAllText(path) == $"{a.Size}:{a.Modified}";
        }
        catch { return false; }
    }

    private void MarkVerified(string model)
    {
        try
        {
            if (Anchor(model) is not { } a) return;
            var path = StatePath("verified", model);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{a.Size}:{a.Modified}");
        }
        catch { /* the next load hashes again; slower, never wrong */ }
    }

    private void RecordKilledAt(string model, double freeGb)
    {
        try
        {
            var path = StatePath("killed", model);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, freeGb.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { }
    }

    private double? KilledAt(string model)
    {
        try
        {
            var path = StatePath("killed", model);
            return File.Exists(path) && double.TryParse(File.ReadAllText(path), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : null;
        }
        catch { return null; }
    }

    private bool Rejected(string model)
    {
        try
        {
            var path = StatePath("rejected", model);
            return File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(7);
        }
        catch { return false; }
    }

    private void MarkRejected(string model)
    {
        try
        {
            var path = StatePath("rejected", model);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { }
    }

    // LOADS THAT STOPPED UNEXPLAINED, OR FOR MEMORY: "count|ticks" - how many in a row, and
    // when the last one did. On disk, because the death that matters takes this process
    // with it: the back-off above lives in memory, and on the P30 it never outlived the
    // kill it was meant to slow down. A clean load clears it (FailurePolicy.HoldOff).
    private void RecordSetback(string model)
    {
        try
        {
            var count = (Setback(model)?.Count ?? 0) + 1;
            var path = StatePath("setback", model);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, count.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                + DateTimeOffset.UtcNow.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch { }
    }

    private (int Count, DateTimeOffset At)? Setback(string model)
    {
        try
        {
            var path = StatePath("setback", model);
            if (!File.Exists(path)) return null;
            var parts = File.ReadAllText(path).Split('|');
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return parts.Length == 2
                   && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, inv, out var count)
                   && long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, inv, out var ticks)
                ? (count, new DateTimeOffset(ticks, TimeSpan.Zero))
                : null;
        }
        catch { return null; }
    }

    private void ClearSetback(string model)
    {
        try { File.Delete(StatePath("setback", model)); }
        catch { }
    }

    /// <summary>How long the next load of <paramref name="model"/> must still wait, if at all.</summary>
    private (int Count, TimeSpan Remaining)? HeldOff(string model)
    {
        if (Setback(model) is not { } s) return null;
        var now = DateTimeOffset.UtcNow;
        var wait = FailurePolicy.HoldOff(s.Count);

        // A CLOCK SET BACK must not hold the model off until it catches up - a day, if the
        // date was a day ahead. The stamp moves to now, so it waits one hold-off at most.
        if (s.At > now)
        {
            try
            {
                File.WriteAllText(StatePath("setback", model), s.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + "|" + now.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            catch { }
            return (s.Count, wait);
        }

        var remaining = s.At + wait - now;
        return remaining > TimeSpan.Zero ? (s.Count, remaining) : null;
    }

    /// <summary>Report the brain paused for memory until the hold-off ends, and the failure to throw.</summary>
    /// <remarks>
    /// NOT THROUGH Fail(), which would record a verdict and replace the wait with the
    /// in-memory back-off. Every caller until then is answered by EnsureReadyAsync's
    /// fast path, and a waiting question is told why once its share of the wait is used.
    /// </remarks>
    private BrainWorkerException HoldOffNow(string model, int count, TimeSpan remaining)
    {
        var now = DateTimeOffset.UtcNow;
        var held = new Failure(FailureKind.OutOfMemory, Certainty.Inferred, WorkPhase.Load, model,
            HeldOffEvidence + " n=" + count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{model} stopped while loading {count} time(s) in a row; waiting before the next try", now);
        lock (_gate) _retryAt = now + remaining;
        Set(new BrainWorkerStatus(BrainPhase.Paused, LoadStep.None, model, held, remaining, null, now));
        _readiness.Report(new ComponentState(ReadinessComponents.Brain, BrainPhase.Paused, LoadStep.None,
            FailureKind.OutOfMemory, PauseReason.WaitingForMemory, ReadinessSentences.For(FailureKind.OutOfMemory),
            model, now, null, remaining));
        _log($"holding off {model}: it stopped while loading {count} time(s) in a row; next try in {remaining.TotalSeconds:0} s");
        return new BrainWorkerException(held);
    }

    // ONCE, WHEN THE RULE FOR UNEXPLAINED DEATHS CHANGED (2026-10-23): the refusals it made
    // come back on one strike. See CrashVerdict.ReconsiderAmbiguousRefusals for why only once.
    private void ReconsiderAmbiguousRefusalsOnce()
    {
        try
        {
            var marker = Path.Combine(_stateDir, "reconsidered-unexplained-deaths.txt");
            if (File.Exists(marker) || _catalogue() is not { } catalogue) return;
            if (CrashVerdict.ReconsiderAmbiguousRefusals(catalogue, line => _log(line)) < 0) return;   // not done: next start
            Directory.CreateDirectory(_stateDir);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { /* tried again at the next start */ }
    }

    // ── the previous process ─────────────────────────────────────────────────

    /// <summary>
    /// Classify how the LAST process ended, if it died inside a load, and record it -
    /// replacing the old rule that every surviving breadcrumb was a strike.
    /// </summary>
    public void RecoverFromPreviousRun()
    {
        lock (_gate) { if (_recovered) return; _recovered = true; }
        try
        {
            var crumb = CircleAI.Assistant.DeviceDiagnostics.PreviousCrash(_stateDir);
            var (bootChanged, updated) = ExitEvidence.CompareRunStamp(_context, _stateDir);

            // THE OLD RULES' REFUSALS FIRST, then the last run's death under today's rule -
            // so a refusal being healed is not struck once more before it is looked at.
            CircleAI.Inference.CrashVerdict.Reconsider(_catalogue(), line => _log(line));
            ReconsiderAmbiguousRefusalsOnce();

            if (crumb is not null)
            {
                var model = CrashVerdict.ModelFrom(crumb);
                var facts = ExitEvidence.ForPreviousProcess(_context, model) with
                {
                    BootChanged = bootChanged,
                    PackageUpdated = updated,
                };
                var failure = FailureClassifier.Classify(facts);
                CrashVerdict.Record(_catalogue(), crumb, line => _log(line), killedByTheSystem: !failure.CountsAgainstModel);

                // THE MAIN PROCESS DIED INSIDE A LOAD - on the P30, iAware taking both
                // processes for memory. Not the model's crash: that kills the worker alone,
                // and is judged live. So the next load of it waits (FailurePolicy.HoldOff).
                if (model is not null && FailurePolicy.HoldsOff(failure.Kind, failure.Phase)) RecordSetback(model);

                _readiness.Failed(failure);
                _log($"the last run ended inside a load of {model}: {failure.Kind} ({failure.Evidence})");
                CircleAI.Assistant.DeviceDiagnostics.EndRisky(_stateDir);
            }
            ExitEvidence.WriteRunStamp(_context, _stateDir);
        }
        catch (Exception ex)
        {
            _log("could not read how the last run ended: " + ex.GetType().Name);
        }
    }

    // ── status ───────────────────────────────────────────────────────────────

    private void StartHeartbeat()
    {
        _beat ??= new Timer(async _ =>
        {
            try
            {
                var beat = await _connection.HeartbeatAsync(CancellationToken.None).ConfigureAwait(false);
                if (beat is null) return;
                lock (_gate) _heartbeat = beat;

                // THE WORKER IS BOUNDED. The same ceiling the fit rules use - total
                // memory less 1.5 GB, or 40% of it, whichever is larger (2.37 GB on the
                // P30) - is a limit on what the model's process may hold. Past it, and
                // with nothing being answered, the model is let go rather than left to
                // push the rest of the phone into the low-memory killer.
                var ceilingKb = Math.Max(beat.MemTotalKb - 1_572_864, (long)(beat.MemTotalKb * 0.4));
                if (beat.MemTotalKb > 0 && beat.VmRssKb > ceilingKb && SchedulerIdle?.Invoke() == true
                    && Status.Phase == BrainPhase.Ready)
                {
                    _log($"the worker holds {beat.VmRssKb / 1024} MB, over its {ceilingKb / 1024} MB ceiling; releasing while idle");
                    await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch { /* a death is reported by the binder, not here */ }
        }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    private void Set(BrainWorkerStatus status)
    {
        lock (_gate) _status = status;
        try { StatusChanged?.Invoke(this, status); } catch { }
    }

    private void Report(BrainPhase phase, LoadStep step, string? model, FailureKind? kind, string detail) =>
        _readiness.Report(new ComponentState(ReadinessComponents.Brain, phase, step, kind, PauseReason.None,
                                             detail, model, DateTimeOffset.UtcNow));
}

/// <summary>Gathers what Android and this app know about how a process ended.</summary>
internal static class ExitEvidence
{
    /// <summary>Read our own crash log for the worker's signal. Unproven on EMUI 10.</summary>
    public static bool ReadCrashBuffer = true;

    public static ExitFacts ForWorker(Context context, WorkerDeath death, WorkerHeartbeat? beat, int lastTrim)
    {
        var (reason, status, description) = ExitRecord(context, death.Pid, ":inference");
        double? free = beat is { MemTotalKb: > 0 } ? beat.MemAvailableKb / (double)beat.MemTotalKb : null;
        return new ExitFacts(
            ApiLevel: (int)Build.VERSION.SdkInt,
            Phase: death.Phase,
            ModelId: death.ModelId,
            At: death.At,
            ExitReason: reason,
            ExitStatus: status,
            ExitDescription: description,
            WorkerDiedAlone: true,
            MemAvailableFractionAtDeath: free,
            LastTrimLevel: Math.Max(lastTrim, beat?.LastTrimLevel ?? 0),
            CrashBufferShowsSignal: ReadCrashBuffer && death.Pid is { } pid ? CrashLogNamesSignal(pid) : null);
    }

    public static ExitFacts ForPreviousProcess(Context context, string? model)
    {
        var (reason, status, description) = ExitRecord(context, null, null);
        return new ExitFacts((int)Build.VERSION.SdkInt, WorkPhase.Load, model, DateTimeOffset.UtcNow,
                             reason, status, description);
    }

    private static (int? Reason, int? Status, string? Description) ExitRecord(Context context, int? pid, string? processSuffix)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30)) return (null, null, null);
        try
        {
            var am = (ActivityManager?)context.GetSystemService(Context.ActivityService);
            var exits = am?.GetHistoricalProcessExitReasons(context.PackageName, pid ?? 0, 5);
            if (exits is null || exits.Count == 0) return (null, null, null);
            var pick = exits.FirstOrDefault(e => (pid is null || e.Pid == pid)
                                                 && (processSuffix is null || (e.ProcessName ?? "").EndsWith(processSuffix, StringComparison.Ordinal)))
                       ?? exits[0];
            return ((int)pick.Reason, pick.Status, pick.Description);
        }
        catch { return (null, null, null); }
    }

    private static bool? CrashLogNamesSignal(int pid)
    {
        try
        {
            using var proc = Java.Lang.Runtime.GetRuntime()!.Exec(new[] { "logcat", "-d", "-b", "crash", "-t", "200" });
            using var reader = new StreamReader(proc!.InputStream!);
            var log = reader.ReadToEnd();
            return log.Contains("pid " + pid, StringComparison.Ordinal) && log.Contains("Fatal signal", StringComparison.Ordinal)
                || log.Contains("pid: " + pid, StringComparison.Ordinal) && log.Contains("signal", StringComparison.Ordinal);
        }
        catch { return null; }
    }

    // API 29 HAS NO EXIT RECORDS, BUT IT HAS A BOOT COUNT AND AN UPDATE TIME. Written at
    // every start; a change since the last start explains a breadcrumb without blaming
    // the model.
    public static (bool? BootChanged, bool? Updated) CompareRunStamp(Context context, string stateDir)
    {
        try
        {
            var path = Path.Combine(stateDir, "run-stamp.txt");
            if (!File.Exists(path)) return (null, null);
            var parts = File.ReadAllText(path).Split(':');
            if (parts.Length < 2) return (null, null);
            var (boot, updated) = Now(context);
            return (boot is { } b && long.TryParse(parts[0], out var pb) ? b != pb : null,
                    updated is { } u && long.TryParse(parts[1], out var pu) ? u != pu : null);
        }
        catch { return (null, null); }
    }

    public static void WriteRunStamp(Context context, string stateDir)
    {
        try
        {
            var (boot, updated) = Now(context);
            Directory.CreateDirectory(stateDir);
            File.WriteAllText(Path.Combine(stateDir, "run-stamp.txt"), $"{boot ?? -1}:{updated ?? -1}");
        }
        catch { }
    }

    private static (long? Boot, long? Updated) Now(Context context)
    {
        long? boot = null, updated = null;
        try { boot = global::Android.Provider.Settings.Global.GetInt(context.ContentResolver, "boot_count", -1); } catch { }
        try { updated = context.PackageManager?.GetPackageInfo(context.PackageName!, 0)?.LastUpdateTime; } catch { }
        return (boot is -1 ? null : boot, updated);
    }
}
