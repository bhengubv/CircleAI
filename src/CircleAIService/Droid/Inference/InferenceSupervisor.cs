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
    private string? _inWaiting;       // the brain this phone chose that cannot answer yet, while another answers for it
    private string? _inWaitingFile;   // its file, which is what the engine's record of its indexing is named for
    private bool _loadedForIndex;     // the brain loaded now was loaded for its index: built small, not warmed, and it answers nobody
    private bool _nothingRunnable;
    private DateTimeOffset _lastUse = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastTurn = DateTimeOffset.MinValue;      // the last time it was asked anything
    private DateTimeOffset _loadedAgainFor = DateTimeOffset.MinValue; // the turn the last LoadAgainIfInUse was for
    private BalanceShape _shape = BalanceShape.Full;

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
        _connection.WorkingChanged += OnWorking;
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

    /// <inheritdoc />
    public event EventHandler<AnsweringBrain>? AnsweringChanged;

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

        // A QUESTION WHILE THE BRAIN IN WAITING IS LOADED FOR ITS INDEX (BrainInWaiting): the brain that answers is
        // brought back first. On the P30 the 27B with no index wrote a token every 26 to 74 seconds; the 2B beside it
        // is loaded in seconds and answers at seven or eight a second.
        if (LoadedForItsIndex() is { } sitting)
        {
            Say($"ask {job.AskId} came while {sitting} was loaded for its index: the answering brain is brought back for it");
            await ReleaseAsync(ct).ConfigureAwait(false);
            await EnsureReadyAsync(ct).ConfigureAwait(false);
        }
        var model = Status.ModelId;

        // THE WATCHDOG: no progress for 90 s means a hung native call, not a slow
        // phone. It ends the worker, and the death that follows is classified as ours,
        // not the model's or Android's.
        //
        // PROGRESS IS A PIECE OF THE ANSWER, OR THE WORKER SPENDING THE PROCESSOR ON IT
        // (OPEN-GAPS E33). "No output for 90 s" ended the C# 2B and its tower twice on the
        // P30 on 2026-11-02 while they read a picture turn at about two tokens a second, and
        // Wolverine, told the turn had timed out, shortened every brain's context for seven
        // hours. A hung call spends no CPU; the worker's heartbeat carries its own CPU time.
        // A call that spins and never writes is ended by its ceiling instead: a minute past
        // its ask's own deadline, by the heartbeat (StartHeartbeat).
        using var watch = new CancellationTokenSource();
        var progress = new ProgressWatch(DateTimeOffset.UtcNow, NoProgress);
        _ = Task.Run(async () =>
        {
            var waiting = false;
            try
            {
                while (!watch.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), watch.Token).ConfigureAwait(false);
                    WorkerHeartbeat? beat;
                    lock (_gate) beat = _heartbeat;
                    if (beat is { CpuMs: >= 0 }) progress.Worked(beat.Pid, beat.CpuMs, beat.At);

                    var now = DateTimeOffset.UtcNow;
                    if (progress.Stalled(now))
                    {
                        _log($"no progress on ask {job.AskId} for {NoProgress.TotalSeconds:0} s: nothing written, "
                             + $"and the inference process spent no time on it{On(_connection.Working)}; ending the worker");
                        lock (_gate) _killedByUs = true;
                        _connection.Kill();
                        return;
                    }

                    // SAID ONCE, where the old rule ended it: the phone is slow, not stuck.
                    if (!waiting && progress.Silent(now))
                    {
                        waiting = true;
                        _log($"ask {job.AskId}: nothing written for {NoProgress.TotalSeconds:0} s, and the inference process "
                             + $"is working on it{(progress.Cores is { } cores ? $" ({cores:0.0} cores)" : string.Empty)}"
                             + $"{On(_connection.Working)}; waiting");
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
                    Fail(failure, model, died.Death.Working);
                    throw new BrainWorkerException(failure, died);
                }
                catch (BrainWorkerException refused)
                {
                    // THE WORKER LIVED AND SAID WHY IT COULD NOT ANSWER (S6) - an error in its own
                    // code, a prompt too long for its context. Its process's HealthEvents never reach
                    // this one, so the failure is raised here, where Wolverine runs - AGAINST THE BRAIN
                    // THE WORKER NAMES (OPEN-GAPS E33): SmolVLM's -6 for a picture was raised as the
                    // main brain's failure on the P30.
                    var working = _connection.Working;
                    var subject = refused.Failure.ModelId ?? working?.ModelId ?? model;
                    RaiseHealth(refused.Failure, subject, null, working?.ModelId == subject ? working?.Slot : null);
                    throw;
                }

                progress.Wrote(DateTimeOffset.UtcNow);
                if (chunk.Final)
                {
                    // THE BRAIN THAT ANSWERED, as the worker names it in the timing: a specialist's
                    // speed is not the main brain's (OPEN-GAPS E33).
                    var answered = chunk.Timing?.ModelId ?? model;
                    LearnSpeed(answered, chunk.Timing);

                    // AN ANSWER IS WOLVERINE'S PROOF (S6): a knob it turned down worked, and one
                    // back at full after its window has worked at full.
                    if (!job.JustRead) CircleAI.Hosting.SelfHealing.DeviceWolverine.Current?.BrainAnswered(answered);
                }
                yield return chunk;
            }
        }
        finally
        {
            lock (_gate) _lastUse = _lastTurn = DateTimeOffset.UtcNow;
            watch.Cancel();
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The fewest tokens an answer needs for its speed to count.</summary>
    /// <remarks>A two-word answer is mostly start-up; its rate says little about the brain.</remarks>
    private const int TokensForASpeed = 8;

    /// <summary>
    /// Records how fast a brain answered, so BrainSpeed can judge it - and estimate its siblings
    /// on the same engine - before anything chooses or fetches.
    /// </summary>
    /// <remarks>
    /// SelfModel.LearnSpeed WAS WRITTEN AND NEVER CALLED, so no speed was ever known and the
    /// selector could rank only by the shipped quality figure. Every answer is a measurement.
    /// </remarks>
    private void LearnSpeed(string? model, TurnTiming? timing)
    {
        if (string.IsNullOrWhiteSpace(model) || timing is not { OutputTokens: >= TokensForASpeed, DecodeMs: > 0 }) return;
        var speed = timing.OutputTokens * 1000.0 / timing.DecodeMs;
        try { CircleAI.SelfKnowledge.SelfModel.Current?.LearnSpeed(model, speed); }
        catch (Exception ex) { _log($"could not record {model}'s speed: {ex.GetType().Name}"); }
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(CancellationToken ct)
    {
        string? model;
        lock (_gate) { model = _status.ModelId; _loadedForIndex = false; }
        try { await _connection.ReleaseAsync(ct).ConfigureAwait(false); }
        catch (WorkerDiedException) { /* already gone, which is what was wanted */ }
        Set(new BrainWorkerStatus(BrainPhase.Installed, LoadStep.None, model, null, null, null, DateTimeOffset.UtcNow));
        Say("the brain was released to free memory");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _connection.Died -= OnWorkerDied;
        _connection.WorkingChanged -= OnWorking;
        _beat?.Dispose();
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    // THE WORKER NAMED THE BRAIN ANSWERING (OPEN-GAPS E33), said on as it came, so the ask names it too.
    private void OnWorking(object? sender, AnsweringBrain brain)
    {
        try { AnsweringChanged?.Invoke(this, brain); } catch { /* readers must not break the answer */ }
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

    /// <summary>
    /// Load <paramref name="model"/> as the brain for <paramref name="span"/>, whatever the plan says - a
    /// developer check (S9) that a brain too big for this phone is stepped down from, by the rules
    /// that would step down from it anyway. The brain held now is let go so the next question loads
    /// the pinned one; a brain Wolverine has stepped down from is still stepped down from, which is
    /// the point. Null or empty unpins, and the plan chooses again.
    /// </summary>
    public async Task PinForTestAsync(string? model, TimeSpan span, CancellationToken ct)
    {
        var pinned = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        lock (_gate) { _pinned = pinned; _pinnedUntil = DateTimeOffset.UtcNow + span; }
        _log(pinned is null
            ? "the test pin is let go; the plan chooses the brain again"
            : $"pinned for a test: {pinned} for {span.TotalMinutes:0} min, whatever the plan says");
        await ReleaseAsync(ct).ConfigureAwait(false);
    }

    private string? _pinned;
    private DateTimeOffset _pinnedUntil;

    // The brain pinned for a test, while its time lasts.
    private string? Pinned()
    {
        lock (_gate) return _pinned is not null && DateTimeOffset.UtcNow < _pinnedUntil ? _pinned : null;
    }

    /// <summary>
    /// A developer check: <paramref name="model"/> loaded in the inference process with
    /// <paramref name="engine"/> (null: its catalogue row's), asked one question by the path every
    /// question takes there - AIService, the persona, the generator its build site makes - and
    /// then let go, so the next question loads the chosen brain again.
    /// </summary>
    /// <remarks>
    /// NOT WATCHED FOR PROGRESS AND NOT UNDER THE LOAD LIMIT: a first prefill on the C# engine
    /// outlasts both on the P30, and this exists to measure it. Nothing here counts for or
    /// against the model; a check is not evidence about the person's brain. Refused while the
    /// queue is answering, because it takes the brain away.
    /// </remarks>
    public async Task<(string Text, TurnTiming? Timing, long LoadMs)> AskOnceForTestAsync(
        string model, string modelDirectory, ModelEngine? engine, string question, int maxTokens, CancellationToken ct,
        byte[]? image = null, int drafts = 0, int? seed = null)
    {
        if (SchedulerIdle?.Invoke() == false) throw new InvalidOperationException("the queue is answering; try again when it is idle");
        await ReleaseAsync(ct).ConfigureAwait(false);
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await _connection.LoadAsync(new WorkerLoadRequest(model, modelDirectory, SkipLoadHash: true, engine, Drafts: drafts, Seed: seed),
                                        null, ct).ConfigureAwait(false);
            var loadMs = clock.ElapsedMilliseconds;

            var text = new System.Text.StringBuilder();
            TurnTiming? timing = null;
            // WITH A PICTURE when one is given (S9): the brain sees it with its own tower, found
            // beside it by the tower's row name, as a question about a photo is answered there.
            var job = new BrainJob("check-" + Guid.NewGuid().ToString("N")[..8],
                [new ChatMessage("user", question) { ImageBytes = image }],
                question, maxTokens, Spoken: false, DateTimeOffset.UtcNow.AddHours(1));
            await foreach (var chunk in _connection.GenerateAsync(job, ct).ConfigureAwait(false))
            {
                text.Append(chunk.Text);
                if (chunk.Final) timing = chunk.Timing;
            }

            // A CHECK IS A MEASUREMENT TOO - of the model on the engine its row names. A check
            // through another engine measures that engine, not the brain this phone would run.
            using (var registry = new ModelRegistryService())
                if (engine is null || registry.GetLatestModel(model)?.Engine == engine) LearnSpeed(model, timing);

            return (text.ToString(), timing, loadMs);
        }
        finally
        {
            try { await ReleaseAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* the next question loads whatever it needs */ }
        }
    }

    // ── balance (Step 4) ─────────────────────────────────────────────────────

    /// <summary>The shape the balance gave the brain: its working memory, context, and how much of it runs.</summary>
    public BalanceShape Shape { get { lock (_gate) return _shape; } }

    /// <summary>When Android last said memory was critically short (TRIM_MEMORY_COMPLETE), for the balance.</summary>
    /// <remarks>
    /// A TIME, NOT A LEVEL: the last level is kept for ever, so one bad minute last week would read
    /// as pressure today. The balance counts it only while it is recent.
    /// </remarks>
    public DateTimeOffset LastCriticalTrim { get { lock (_gate) return _lastCriticalTrim; } }

    private DateTimeOffset _lastCriticalTrim = DateTimeOffset.MinValue;

    /// <summary>
    /// Gives the brain a new shape, between turns: the next load takes it; a loaded brain whose
    /// working memory, context or size must change is let go now, and the balance loop primes it
    /// again; on the released rung it is let go and stays so until a question loads it.
    /// </summary>
    /// <remarks>
    /// THE CALLER CHECKS THE QUEUE IS IDLE (BalanceLoop does), and this checks again: a brain
    /// in the middle of an answer is never taken away for a shape.
    /// </remarks>
    public async Task ReshapeAsync(BalanceShape shape, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(shape);
        BalanceShape before;
        lock (_gate) { before = _shape; _shape = shape; }
        if (before == shape) return;

        _log($"balance: the brain's shape is now {shape.Describe()} (was {before.Rung})");
        if (Status.Phase != BrainPhase.Ready || SchedulerIdle?.Invoke() == false) return; // the next load takes it

        // NOR IS A BRAIN TAKEN AWAY WHILE ITS INDEX IS BEING BUILT. It answers nobody, so the shape is nothing to it (it
        // is built as the engine builds it: LoadAsync), and letting it go ends the unit in hand: on the P30 on 2026-11-13
        // "calm again for 10 minutes: the brain grows back" threw away seven and a half minutes of a ten-minute unit.
        // The next load that answers takes the shape.
        if (LoadedForItsIndex() is not null) return;

        // ONLY THE C# ENGINE READS THE WORKING MEMORY'S BITS: a change of bits alone does not reload
        // an MNN brain, which would come back exactly as it went.
        bool managed;
        using (var registry = new ModelRegistryService())
            managed = Status.ModelId is { } running && registry.GetLatestModel(running)?.Engine == ModelEngine.Managed;
        if (shape.Released || shape.Rebuilds(before, workingMemoryCounts: managed))
            await ReleaseAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// On the released rung: lets go of a brain a question loaded since, once nothing has been
    /// asked for two minutes.
    /// </summary>
    public async Task RestIfIdleAsync(CancellationToken ct)
    {
        DateTimeOffset lastUse;
        lock (_gate) lastUse = _lastUse;
        if (!Shape.Released || Status.Phase != BrainPhase.Ready || SchedulerIdle?.Invoke() != true) return;
        if (DateTimeOffset.UtcNow - lastUse < TimeSpan.FromMinutes(2)) return;
        _log("balance: resting - the brain is let go until the next question");
        await ReleaseAsync(ct).ConfigureAwait(false);
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
        lock (_gate)
        {
            _lastTrim = level;
            lastUse = _lastUse;
            if (level >= 80) _lastCriticalTrim = DateTimeOffset.UtcNow; // the balance reads memory from this (Step 4)
        }

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

    private async Task LoadAsync(string? forced, bool forItsIndex = false)
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
            Say($"loading {model} in the inference process (hash {(skipHash ? "trusted" : "checked")})");

            // THE MARK THAT OUTLIVES BOTH PROCESSES. If the phone reboots or the app is
            // updated mid-load, nothing survives to see the death - except this.
            CircleAI.Assistant.DeviceDiagnostics.BeginRisky(_stateDir, CrashVerdict.Breadcrumb(model));
            var freeAtStart = DeviceProbe.Snapshot().UsableRamGb;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var workerDied = false;
            try
            {
                // THE LIMIT IS EACH STEP'S, NOT THE WHOLE LOAD'S. A load that goes on from checking its files to building
                // to warming is making progress; one stuck in a step is not. On the P30 on 2026-11-09 the 27B checked its
                // file (44 s), built with no copies (4 s) and was warming - its first prefill, every layer streamed from
                // flash - when three minutes from the start ended it.
                using var limit = new CancellationTokenSource(LoadLimit);
                var shape = Shape; // the balance's working memory and context (Step 4)
                var lastStep = -1;
                // A BRAIN LOADED FOR ITS INDEX IS BUILT SMALL, WITH THE ENGINE'S OWN MEMORY (BrainInWaiting.LoadShape),
                // whatever the balance asks of a brain that answers.
                var (kvMode, contextShare) = BrainInWaiting.LoadShape(forItsIndex, shape.KvMode, shape.ContextShare);
                await _connection.LoadAsync(new WorkerLoadRequest(model, _store, skipHash, null, kvMode, contextShare, forItsIndex),
                    new Progress<LoadStep>(step =>
                    {
                        if (Interlocked.Exchange(ref lastStep, (int)step) != (int)step)
                        {
                            try { limit.CancelAfter(LoadLimit); }
                            catch (ObjectDisposedException) { /* the load is over */ }
                        }
                        Report(BrainPhase.Loading, step, model, null, ReadinessSentences.For(step));
                    }),
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
            lock (_gate) _loadedForIndex = forItsIndex;
            ClearSetback(model);
            MarkVerified(model);
            lock (_gate) { _failures = 0; _retryAt = DateTimeOffset.MinValue; _lastUse = DateTimeOffset.UtcNow; }
            Set(new BrainWorkerStatus(BrainPhase.Ready, LoadStep.None, model, null, null, _connection.Pid, DateTimeOffset.UtcNow));
            Report(BrainPhase.Ready, LoadStep.None, model, null, ReadinessSentences.Ready);
            Say($"brain ready: {model} in {clock.ElapsedMilliseconds} ms (free RAM at start {freeAtStart:0.00} GB)");
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

    /// <summary>Where the inference process writes the marker before each risky step (RiskySteps).</summary>
    private string WorkerRiskDirectory => CircleAI.Assistant.RiskySteps.For(_stateDir, "inference");

    /// <summary>
    /// The brain's failure as a HealthEvent (S6) - the inference process dying throws nothing in
    /// this process, so until now healing never heard of one. The marker it wrote before its
    /// risky step, when one survived, says what it was doing and with which knobs in force.
    /// </summary>
    /// <remarks>
    /// NOT for what is nobody's fault to heal: a cancellation, a test's own kill, a busy worker,
    /// no model yet (setup's business, raised by every question that waits on it).
    /// <para>
    /// <paramref name="model"/> IS THE BRAIN THAT WAS WORKING, and <paramref name="slot"/> where it
    /// answered from when that was not the main slot (OPEN-GAPS E33): Wolverine steps down from the
    /// event's subject.
    /// </para>
    /// </remarks>
    private void RaiseHealth(Failure failure, string? model, string? marker, string? slot = null)
    {
        try
        {
            if (failure.Kind is FailureKind.Cancelled or FailureKind.FaultInjected or FailureKind.Rejected or FailureKind.NotInstalled)
                return;
            var errorType = CircleAI.Hosting.SelfHealing.EngineFaults.ErrorType(failure.Kind, failure.Evidence);
            var context = DeviceContext.Read().Describe() + (marker is null ? string.Empty : " | died during: " + marker);
            var where = slot is null or AnsweringBrain.Main ? string.Empty : $" ({slot})";
            CircleAI.Core.Logging.HealthEvents.Raise(CircleAI.Core.Logging.HealthEvents.Of(
                CircleAI.Hosting.SelfHealing.HealingRules.EngineArea, $"{failure.Phase} {model ?? failure.ModelId}{where}".Trim(),
                errorType, CircleAI.Hosting.SelfHealing.EngineFaults.Site(failure.Phase),
                failure.Evidence + "; " + failure.Detail, context, subject: model ?? failure.ModelId));
        }
        catch (Exception ex)
        {
            _log("could not raise the brain's failure as a health event: " + ex.GetType().Name);
        }
    }

    /// <summary>The model a worker marker names ("generate &lt;model&gt; | knobs"); null for a load marker, which names none.</summary>
    private static string? ModelOfMarker(string marker)
    {
        const string generate = "generate ";
        if (!marker.StartsWith(generate, StringComparison.Ordinal)) return null;
        var end = marker.IndexOf(" |", StringComparison.Ordinal);
        var model = (end < 0 ? marker[generate.Length..] : marker[generate.Length..end]).Trim();
        return model.Length == 0 ? null : model;
    }

    /// <summary>What a brain was doing when its process ended, for the death line: the step, and the slot when not the main one.</summary>
    private static string Doing(WorkPhase phase, AnsweringBrain? working)
    {
        var step = phase switch
        {
            WorkPhase.Startup or WorkPhase.Verify or WorkPhase.Load => "loading",
            WorkPhase.Warm => "warming up",
            WorkPhase.Prefill => "reading its turn",
            WorkPhase.Decode => "answering",
            _ => "working",
        };
        return working is { Slot: not AnsweringBrain.Main } w ? $"{step} as the {w.Slot}" : step;
    }

    // " on Ternary-Bonsai-2-2B (Vision specialist)" for a log line, once the worker has said; else nothing.
    private static string On(AnsweringBrain? working) => working is null ? string.Empty : $" on {working}";

    /// <summary>The inference process's surviving marker, read and cleared; null when it ended every step it began.</summary>
    private string? TakeWorkerMarker()
    {
        try
        {
            var dir = WorkerRiskDirectory;
            var marker = CircleAI.Assistant.RiskySteps.Leftover(dir);
            if (marker is not null) CircleAI.Assistant.RiskySteps.Clear(dir);
            return marker;
        }
        catch { return null; }
    }

    private void Fail(Failure failure, string? model, AnsweringBrain? working = null)
    {
        // AGAINST THE BRAIN THAT WAS WORKING (OPEN-GAPS E33). On the P30 on 2026-11-02 the C# 2B
        // and its tower, reading a picture turn as the Vision specialist, were ended by the
        // watchdog, and the health event and this line both named Qwen3.5-0.8B-MNN - the main
        // brain, idle in its slot - "while ... loading". The worker says which brain answers; its
        // marker says it too, for a death before the next poll heard. The hold-off, the retry and
        // the status stay the main brain's: it is what the next load loads.
        var marker = TakeWorkerMarker();
        if (working is null && failure.Phase is WorkPhase.Prefill or WorkPhase.Decode && marker is not null
            && ModelOfMarker(marker) is { } named && named != model)
            working = new AnsweringBrain(named, "specialist");
        var subject = working?.ModelId ?? model;
        RaiseHealth(failure, subject, marker, working?.Slot);

        // THE VERDICT FOLLOWS THE KIND. Only a native crash while the model was
        // working is a strike; everything else is the moment or the phone, and
        // refusing the model for it would refuse the only brain this phone can run. A
        // load that stopped unexplained, or for memory, holds the next one off instead.
        if (subject is not null)
            CrashVerdict.Record(_catalogue(), CrashVerdict.Breadcrumb(subject), line => _log(line),
                                killedByTheSystem: !failure.CountsAgainstModel, doing: Doing(failure.Phase, working),
                                why: $"{failure.Kind}, {failure.Evidence}");

        // A CRASH IS A REASON TO LOOK, NOT A VERDICT (the owner, 2026-11-15): the model's files are checked before its
        // next load - a damaged file is repaired, not loaded again - and the next try waits (FailurePolicy.HoldsOff).
        if (subject is not null && failure.CountsAgainstModel) ForgetVerified(subject);
        if (model is not null && FailurePolicy.HoldsOff(failure.Kind, failure.Phase)) RecordSetback(model);

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
                var marker = TakeWorkerMarker();   // inside an ask, it names the brain answering (OPEN-GAPS E33)
                RaiseHealth(failure, (marker is null ? null : ModelOfMarker(marker)) ?? death.ModelId, marker);
                _readiness.Failed(failure);
                Set(new BrainWorkerStatus(BrainPhase.Installed, LoadStep.None, death.ModelId, failure, null, null, DateTimeOffset.UtcNow));
                Report(BrainPhase.Installed, LoadStep.None, death.ModelId, failure.Kind, ReadinessSentences.For(failure.Kind));
                _log($"the idle brain's process ended ({failure.Kind}); it loads again on the next question");
                LoadAgainIfInUse();
            });
        }
    }

    /// <summary>Starts the load the next question would wait for, when one is likely to come.</summary>
    /// <remarks>
    /// SOMEBODY IN THE MIDDLE OF A CONVERSATION IS ABOUT TO ASK. On the P30 on 2026-11-07 a brain killed
    /// between two turns came back only once the next question had reached it, 13,6 s after that. ONCE
    /// PER TURN, and a load is not a turn: a phone that keeps taking the brain back for memory is
    /// not fed another, and the next question decides as it always did.
    /// </remarks>
    private void LoadAgainIfInUse()
    {
        lock (_gate)
        {
            if (_lastTurn == _loadedAgainFor || DateTimeOffset.UtcNow - _lastTurn > TimeSpan.FromMinutes(2)) return;
            _loadedAgainFor = _lastTurn;
        }

        _log("it was asked something a moment ago: loading it again now");
        _ = EnsureReadyAsync(CancellationToken.None).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
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
        // THE SPEEDS THIS PHONE MEASURED, READ BACK FIRST: they decide which brain is quick enough to
        // talk with (BrainSpeed), and with none read back rank alone decides - the P30 would want the
        // Bonsai 4B. The device's memory is loaded fire-and-forget at start-up, so the first plan
        // waits for it, briefly; a memory that will not open plans on the catalogue, as before.
        try { CircleAI.SelfKnowledge.SelfModel.Current?.Loaded.Wait(TimeSpan.FromSeconds(10)); }
        catch { /* never a reason not to choose */ }

        var options = _options();
        var catalogue = _catalogue();
        var probe = DeviceProbe.Snapshot();
        lock (_gate) _nothingRunnable = false;
        using var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(_store, registry);
        var selector = DeviceAwareModelSelector.Preferring(catalogue, registry);

        // ONE CHECK OF WHAT THIS PHONE MAY USE, ASKED BY EVERY PICK BELOW (OPEN-GAPS E33). Each pick wrote
        // its own filter and they drifted apart: the balance stepped down to brains Wolverine was avoiding;
        // the stand-in for an avoided brain and the brain kept while a candidate waits never asked whether a
        // brain had been killed here at more free memory than there is now; and the replacement after a kill
        // never asked whether its files were all on disk - InstalledIds lists a half-fetched folder too.
        // Two tiers, as the picks meant them: what is never used here - retired, not a chat brain, a
        // companion, nothing in this build opens it, not all on disk, refused - and what is not used NOW:
        // a brain Wolverine is stepping down from, or one killed here at more free memory than there is now.
        // A pick that must name a brain takes one of the second kind only when there is nothing else; a
        // pick that would replace the chosen brain keeps the chosen one rather than take one of them.
        var tuning = BrainTuning.Current();
        bool Usable(string id) => registry.GetLatestModel(id) is { Retired: false, Modality: ModelModality.Chat } e
                                  && !BrainCompanions.IsCompanion(e) && EngineChoice.CanRun(e) && loader.ModelPresent(id)
                                  && !CrashVerdict.IsRefused(catalogue, id);
        bool NotNow(string id) => tuning.Avoids(id) || (KilledAt(id) is { } killed && probe.UsableRamGb < killed);
        bool UsableNow(string id) => Usable(id) && !NotNow(id);

        // The best brain on the phone for a pick that must name one: one it may use now, else one it may use.
        string? StandIn() => loader.BestInstalledChatModel(probe, refused: id => !UsableNow(id))
                             ?? loader.BestInstalledChatModel(probe, refused: id => !Usable(id));

        // A BRAIN PINNED FOR A TEST (S9) before the plan, and still under every rule below it.
        string? wanted = Pinned() ?? options.ModelId;
        if (!string.IsNullOrWhiteSpace(wanted) && registry.GetLatestModel(wanted!) is { Retired: true }) wanted = null;
        if (string.IsNullOrWhiteSpace(wanted))
        {
            wanted = selector.BestFit(probe, options.RequiredCapabilities)?.ModelId;

            // STEP 1, SAID OUT LOUD: the device as read, the brain chosen for it, and - when a
            // higher-ranked one was too slow to talk with here - which and why.
            var context = DeviceContext.Read(probe);
            Say("plan: " + BrainPlan.Explain(context, wanted is null ? null : registry.GetLatestModel(wanted), registry.AllModels)
                 + " | " + context.Describe());
        }

        // NOT DOWNLOADED IS NOT "THE BIG ONE STOPPED THIS PHONE". The old host said
        // OnTheSmallerBrain here; the preferred model was simply not on the phone.
        if (!string.IsNullOrWhiteSpace(wanted) && !loader.ModelPresent(wanted!))
        {
            var here = StandIn();
            Say($"the selector wanted {wanted}, which is not on this phone; {(here is null ? "nothing else is" : "using " + here)}");
            wanted = here;
        }

        if (string.IsNullOrWhiteSpace(wanted) || !loader.ModelPresent(wanted!)) return null;

        if (CrashVerdict.IsRefused(catalogue, wanted))
        {
            var instead = StandIn();
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
                .Where(id => id != wanted && UsableNow(id))
                .OrderBy(id => registry.GetLatestModel(id)?.MinRamGb ?? double.MaxValue)
                .FirstOrDefault();
            if (other is not null)
            {
                Say($"{wanted} was killed for memory at {killedAt:0.00} GB free; {probe.UsableRamGb:0.00} GB now - using {other}");
                wanted = other;
            }
        }

        // WOLVERINE STEPPED DOWN FROM IT (S6): this brain ran out of memory or crashed here, and
        // for SelfHeal's window the next brain this phone holds answers instead - "not now", not
        // "defective", and the floor still holds: nothing retired is ever the replacement.
        if (tuning.Avoids(wanted))
        {
            var instead = loader.BestInstalledChatModel(probe, refused: id => !UsableNow(id));
            if (!string.IsNullOrWhiteSpace(instead))
            {
                Say($"{wanted} is stepped down from for now (Wolverine: {tuning.Describe()}); using {instead}");
                wanted = instead;
            }
            else _log($"{wanted} is stepped down from, and nothing else this phone holds can answer; keeping it");
        }

        // A BRAIN WHOSE INDEX FAILED ITS CHECK HERE IS NOT THIS PHONE'S BRAIN (2026-11-14; BrainInWaiting.PastAFailedIndex).
        // The selector knows nothing of an index: it went on naming the 27B on the P30, and the brain the climb fetches
        // next (ModelChoice.NextStep) would never have been loaded. Of the brains here this phone may use now, loaded or
        // not, the best that has not failed takes its place - and is validated just below like any that has not loaded.
        if (registry.GetLatestModel(wanted!) is { } picked)
        {
            var past = BrainInWaiting.PastAFailedIndex(picked,
                loader.InstalledIds().Where(UsableNow).Select(id => registry.GetLatestModel(id)!), probe,
                entry => BrainInWaiting.IndexOf(entry));
            if (past.Said is { } said) Say(said);
            wanted = past.Brain.Name;
        }

        // KEEP THE WORKING MODEL UNTIL ITS REPLACEMENT IS VALIDATED. A better model that
        // has never loaded on this phone is a candidate; the proven one keeps answering.
        //
        // NOT A BRAIN THAT WAITS ON ITS INDEX (2026-11-14; BrainInWaiting.WaitsOnItsIndex): asked three questions with no index,
        // it would read every layer from flash for every word. It goes on to the brain in waiting below, where the brain
        // beside it answers until its index serves - the 9B the climb fetches after the 4B on the P30.
        bool WaitsOnItsIndexHere(string id) => registry.GetLatestModel(id) is { } e && BrainInWaiting.WaitsOnItsIndex(e, probe, BrainInWaiting.IndexOf(e));
        bool IndexFailedHere(string id) => registry.GetLatestModel(id) is { } e && BrainInWaiting.FailedHere(e, probe, BrainInWaiting.IndexOf(e));
        if (catalogue is not null && catalogue.Assessment(wanted!) is { Proven: false } && !WaitsOnItsIndexHere(wanted!))
        {
            // OF THE PROVEN BRAINS THIS PHONE MAY USE NOW (UsableNow), THE BONSAI 2 FLEET FIRST, THEN BY RANK
            // (ModelChoice.Best) - the rule every pick uses, with no speed gate (the owner, 2026-11-04). With
            // no proven brain it may use now, the candidate answers. NOR ONE WHOSE INDEX FAILED HERE: it answers nobody, and
            // the P30's 27B, proven by the loads that built its index, would have been the brain "kept" while the 4B waited.
            var proven = CircleAI.Inference.ModelChoice.Best(loader.InstalledIds()
                .Where(UsableNow)
                .Where(id => !IndexFailedHere(id))
                .Select(id => catalogue.Assessment(id))
                .Where(a => a is { Proven: true, Refused: false })
                .Select(a => a!.Entry));
            if (proven is not null && proven.Name != wanted && !Rejected(wanted!))
            {
                lock (_gate) _pendingCandidate = wanted;
                Say($"{wanted} has not loaded on this phone yet; keeping {proven.Name} until it is validated");
                wanted = proven.Name;
            }
        }

        // A SMALLER BRAIN WHILE THE PHONE IS UNDER PRESSURE (Step 4's balance), when one is here: of the
        // brains on the device smaller than this one that it may use now (UsableNow, above), the one
        // ModelChoice.StepDown takes - the Bonsai 2 fleet first, then rank, and never a brain outside the
        // fleet in place of one in it. None is not a reason to have no brain; the chosen one stays, lean.
        if (Shape.SmallerBrain && registry.GetLatestModel(wanted!) is { } current)
        {
            var smaller = loader.InstalledIds()
                .Where(UsableNow)
                .Select(id => registry.GetLatestModel(id)!)
                .Where(e => e.TotalBytes > 0 && e.TotalBytes < current.TotalBytes)
                .ToList();
            var instead = CircleAI.Inference.ModelChoice.StepDown(current, smaller);
            _log(instead is null
                ? $"balance: no brain smaller than {wanted} that may stand in for it is on this phone; it stays, lean"
                : $"balance: {instead.Name} answers instead of {wanted} while the phone is under pressure");
            if (instead is not null) wanted = instead.Name;
        }

        // THE BRAIN THIS PHONE CHOSE CANNOT ANSWER YET (2026-11-13; BrainInWaiting). The engine reads it from flash, the
        // phone cannot keep it in RAM, and its index is not complete - "a background process for hours of course just
        // like updating an OS" (the owner). Until it is, the best brain here that the phone does keep in RAM goes on
        // answering, and the chosen one is loaded while the phone is idle, where its index gets built
        // (SitWithTheBrainInWaitingAsync). With nothing here to stand in, the chosen one answers as well as it can.
        string? waiting = null, waitingFile = null;
        if (registry.GetLatestModel(wanted!) is { } best)
        {
            var others = loader.InstalledIds()
                .Where(id => id != wanted && UsableNow(id) && catalogue?.Assessment(id) is { Proven: true, Refused: false })
                .Select(id => registry.GetLatestModel(id)!)
                .ToList();
            // WHO ANSWERS, WHO WAITS AND WHAT IS SAID OF IT ARE DECIDED IN THE LIBRARY (BrainInWaiting.Plan), where each case
            // is a test: an index that is complete and failed its check leaves nothing to build, so its brain is not
            // loaded for it again; a record with every unit done and no measure kept has its check still to run.
            var plan = BrainInWaiting.Plan(best, others, probe, entry => BrainInWaiting.IndexOf(entry));
            if (plan.Said is { } said) Say(said);
            waiting = plan.Waiting?.Name;
            waitingFile = plan.WaitingFile;
            wanted = plan.Answers.Name;
        }
        lock (_gate) { _inWaiting = waiting; _inWaitingFile = waitingFile; }

        return wanted;
    }

    // ── the brain in waiting ─────────────────────────────────────────────────

    // A LOAD FOR THE INDEX THAT FAILED IS NOT TRIED AGAIN THE NEXT MINUTE (BrainInWaiting.SitAgainAfter): the failures in a
    // row, and no other load for the index before this.
    private int _sitFailures;
    private DateTimeOffset _sitNotBefore = DateTimeOffset.MinValue;

    /// <summary>
    /// While the phone is idle - nothing saying it is in use, neither hot nor low, nothing asked for a few minutes - loads the brain
    /// in waiting, so the engine that holds it builds its index (IndexingQueue, a unit at a time). When the phone is
    /// picked up again the brain that answers is brought back. Called on a clock by the service.
    /// </summary>
    /// <remarks>
    /// THE ENGINE BUILDS AN INDEX FOR THE MODEL IT HOLDS, in its own idle clock, so a brain that is never loaded is never
    /// indexed: without this the phone would answer with the 2B for good and the 27B would wait for an index nothing
    /// was building. Its first load is also where the engine begins the queue and records it, which is how this
    /// process comes to know how far it has got.
    /// <para>
    /// WHAT TO DO IS DECIDED IN THE LIBRARY (BrainInWaiting.Sitting), where every branch is a test on any machine. Three
    /// of them - the wait after a load that failed, the phone picked up while the brain sits, a check that fails while
    /// it is loaded - had run nowhere while they were written here, in a head no test project can reference.
    /// </para>
    /// </remarks>
    public async Task SitWithTheBrainInWaitingAsync(CancellationToken ct)
    {
        string? inWaiting;
        string? running;
        DateTimeOffset lastTurn, notBefore;
        bool loading;
        lock (_gate)
        {
            inWaiting = _inWaiting;
            running = _status.Phase == BrainPhase.Ready ? _status.ModelId : null;
            lastTurn = _lastTurn;
            loading = _loading is not null;
            notBefore = _sitNotBefore;
        }
        if (inWaiting is not { } waiting) return;

        var now = DateTimeOffset.UtcNow;
        var move = BrainInWaiting.Sitting(
            new SittingNow(waiting, running, loading, SchedulerIdle?.Invoke() == true, PhoneIdle(), now - lastTurn, now, notBefore),
            failedItsCheck: () => FailedItsCheck(waiting),
            readyToAnswer: () => ReadyToAnswer(waiting),
            heldOff: () => HeldOff(waiting) is not null);
        switch (move)
        {
            case SittingMove.LetGoFailed:
                Say($"{waiting}'s index failed its check: it is let go, and the answering brain is loaded");
                await BringTheAnsweringBrainBackAsync(waiting, ct).ConfigureAwait(false);
                return;
            case SittingMove.LetGoInUse:
                Say($"the phone is in use again: {waiting} is let go with its index as far as it got, and the answering brain is loaded");
                await BringTheAnsweringBrainBackAsync(waiting, ct).ConfigureAwait(false);
                return;
            case SittingMove.LoadForItsIndex:
                break;
            default:
                return;
        }

        Say($"the phone is idle: {waiting} is loaded so that its index gets built; a question brings {running ?? "the answering brain"} back");
        await ReleaseAsync(ct).ConfigureAwait(false);
        Task sitting;
        lock (_gate)
        {
            if (_loading is not null) return;   // a question got in first: it is answered, and the sitting waits
            sitting = _loading = Task.Run(() => LoadAsync(waiting, forItsIndex: true));
        }
        try
        {
            await sitting.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate) { _sitFailures = 0; _sitNotBefore = DateTimeOffset.MinValue; }
        }
        catch (Exception ex)
        {
            TimeSpan wait;
            lock (_gate)
            {
                wait = BrainInWaiting.SitAgainAfter(++_sitFailures);
                _sitNotBefore = DateTimeOffset.UtcNow + wait;
            }
            Say($"{waiting} did not load for its index ({ex.GetType().Name}); it is tried again in {wait.TotalMinutes:0} min, and the next question loads the answering brain");
        }
    }

    // The brain in waiting is let go and the brain that answers is loaded - before anything is asked.
    private async Task BringTheAnsweringBrainBackAsync(string waiting, CancellationToken ct)
    {
        await ReleaseAsync(ct).ConfigureAwait(false);
        try { await EnsureReadyAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Say($"the answering brain did not load after {waiting} was let go ({ex.GetType().Name}); the next question loads it"); }
    }

    // The brain in waiting, when it is the one loaded and still cannot answer: it is here for its index.
    private string? LoadedForItsIndex()
    {
        lock (_gate) return _loadedForIndex && _status.Phase == BrainPhase.Ready ? _status.ModelId : null;
    }

    // Whether a brain's index failed its check with nothing left to try (IndexingStatus.NothingLeftToTry), by the engine's
    // record of it. One that failed at a smaller share is not let go: its engine goes on to an index listing more neurons
    // in the same sitting (IndexingRungs; the owner, 2026-11-15: "clean up and try again").
    private static bool FailedItsCheck(string model)
    {
        try
        {
            using var registry = new ModelRegistryService();
            return registry.GetLatestModel(model) is { } entry && BrainInWaiting.IndexOf(entry) is { NothingLeftToTry: true };
        }
        catch { return false; }
    }

    // Whether a brain answers as it should here now (BrainInWaiting.Ready), by the engine's record of its index.
    private static bool ReadyToAnswer(string model)
    {
        try
        {
            using var registry = new ModelRegistryService();
            return registry.GetLatestModel(model) is not { } entry
                   || BrainInWaiting.Ready(entry, DeviceProbe.Snapshot(), BrainInWaiting.IndexOf(entry));
        }
        catch { return true; }
    }

    // Whether the phone is in nobody's hand and well (BrainInWaiting.PhoneIdle), by what the device says of itself.
    private static bool PhoneIdle()
    {
        try
        {
            return BrainInWaiting.PhoneIdle(DeviceContext.InUseProbe?.Invoke(), DeviceContext.PowerProbe?.Invoke() ?? PowerState.Unknown);
        }
        catch { return false; }
    }

    // Said where the service's log file keeps it as well as in logcat: which brain answers and which waits is read
    // back hours later, and logcat on a phone holds minutes.
    private void Say(string line)
    {
        // CircleLog writes logcat and the file; the plain log is only for where that fails.
        try { CircleAI.Core.Logging.CircleLog.Info("Supervisor", line); }
        catch { _log(line); }
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

        // ONE AT A TIME (2026-11-14): the clock asks, and so does a brain that lands (TakeUpAsync) - a second validation
        // would let go the brain the first is asking its questions.
        if (Interlocked.Exchange(ref _validating, 1) != 0) return false;
        try
        {
            return await ValidateAsync(candidate, active, ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _validating, 0);
        }
    }

    private int _validating;

    // The candidate loaded and asked three questions with known answers: kept only if it answers them all.
    private async Task<bool> ValidateAsync(string candidate, string? active, CancellationToken ct)
    {
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
            var tokens = 0;
            var decodeMs = 0L;
            foreach (var (q, a) in golden)
            {
                var text = new System.Text.StringBuilder();

                // THE QUESTION AS WRITTEN, AND NOTHING WITH IT (OPEN-GAPS E33). As a turn "What is 17 times 4?"
                // is a tool cue: on the P30 on 2026-11-03 it reached the C# 2B with two tool definitions, outran
                // its two minutes before a word, and the 2B was rejected for a week by its deadline, not by its
                // speed. A known answer is the brain's own - no persona, no notes, no tools, no shortcut - and a
                // check the service runs latches no tools into the person's turns after it.
                var job = new BrainJob("validate-" + Guid.NewGuid().ToString("N"),
                    new[] { new ChatMessage("user", q) }, q, 64, false, DateTimeOffset.UtcNow.AddMinutes(2), Plain: true);
                await foreach (var chunk in GenerateAsync(job, ct).ConfigureAwait(false))
                {
                    text.Append(chunk.Text);
                    if (chunk is { Final: true, Timing: { OutputTokens: > 0, DecodeMs: > 0 } t }) { tokens += t.OutputTokens; decodeMs += t.DecodeMs; }
                }
                if (text.ToString().Contains(a, StringComparison.OrdinalIgnoreCase)) right++;
            }
            passed = right == golden.Length;
            _log($"validation of {candidate}: {right}/{golden.Length}");

            // THE THREE ANSWERS TOGETHER ARE A MEASUREMENT - one short answer is mostly start-up - and it
            // is recorded, so the plan can say how fast this brain answers here. Right is what passes it:
            // speed is no gate (the owner, 2026-11-04), and a brain this phone runs slowly is the engine's
            // to make faster, not a reason to keep another.
            LearnSpeed(candidate, new TurnTiming(DecodeMs: decodeMs, OutputTokens: tokens));
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

    // ── a brain that lands ───────────────────────────────────────────────────

    /// <summary>
    /// A brain has landed on the phone (BundleModelLoader.ModelLanded): the choice is made again now, with it among the
    /// rest, not at the next load - so it is validated, waits on its index or answers, as the choice says. False when no
    /// brain answers yet: then the first load chooses, through the catalogue's gate.
    /// </summary>
    /// <remarks>
    /// WHAT TO DO IS DECIDED IN THE LIBRARY (BrainInWaiting.TakingUp), where each case is a test. The supervisor chose only
    /// when it loaded, and a brain that answers is not loaded again: on the P30 on 2026-11-14 the 4B the climb fetched
    /// landed at 15:42:52 behind the 2B, and an hour and a half later nothing had validated it.
    /// </remarks>
    public async Task<bool> TakeUpAsync(string landed, CancellationToken ct)
    {
        // NOT ON THE DOWNLOAD'S THREAD: a landing is said from inside the download, and choosing reads the catalogue, the
        // registry and the index records.
        await Task.Yield();

        // A LOAD IN FLIGHT CHOSE BEFORE THIS BRAIN WAS HERE, or was told what to load: the choice is made once it is done.
        Task? inFlight;
        lock (_gate) inFlight = _loading;
        if (inFlight is not null)
        {
            try { await inFlight.WaitAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* a load that failed says why itself */ }
        }

        string? running;
        bool forItsIndex;
        lock (_gate)
        {
            running = _status.Phase == BrainPhase.Ready ? _status.ModelId : null;
            forItsIndex = _loadedForIndex;
        }

        var (move, choice) = BrainInWaiting.TakingUp(running, forItsIndex, () =>
        {
            Say($"{landed} is here: the brain is chosen again now, not at the next load");
            return Choose();
        });
        switch (move)
        {
            case TakeUpMove.FirstLoadChooses:
                return false;
            case TakeUpMove.SittingDecides:
                Say($"{running} is loaded for its index: it goes when the clock that sits with it says, and the next load takes {choice ?? "the brain chosen"}");
                return true;
            case TakeUpMove.Stays:
                await ValidateNowIfDueAsync(ct).ConfigureAwait(false);
                return true;
        }

        // THE CHOICE IS ANOTHER BRAIN, and it answers from now - once nobody is asking: an answer being given is not cut off
        // for it. Ten minutes at most; after that the next load takes it, as before.
        for (var waited = 0; waited < 40 && SchedulerIdle?.Invoke() != true; waited++)
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        if (SchedulerIdle?.Invoke() != true)
        {
            Say($"{choice} is this phone's brain now, and it was asked something for ten minutes: the next load takes it");
            return true;
        }

        Say($"{choice} is this phone's brain now: {running} is let go and {choice} is loaded");
        await ReleaseAsync(ct).ConfigureAwait(false);
        Task loading;
        lock (_gate)
        {
            if (_loading is not null) return true;   // a question got in first, and its load chose
            loading = _loading = Task.Run(() => LoadAsync(choice!));
        }
        try { await loading.WaitAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { Say($"{choice} did not load ({ex.GetType().Name}); the next question loads the brain this phone chooses"); }
        return true;
    }

    // The candidate the choice left to be validated is validated now when its time has come - nobody asking, the phone on
    // charge - and else by the clock, every quarter of an hour, which is said.
    private async Task ValidateNowIfDueAsync(CancellationToken ct)
    {
        string? candidate;
        lock (_gate) candidate = _pendingCandidate;
        if (candidate is null) return;
        if (await ValidateCandidateIfDueAsync(ct).ConfigureAwait(false)) return;

        // One that was tried, or is being tried, says what came of it itself.
        bool notTried;
        lock (_gate) notTried = _pendingCandidate == candidate && Volatile.Read(ref _validating) == 0;
        if (notTried) Say($"{candidate} is validated once nobody is asking and the phone is on charge: the clock asks every 15 minutes");
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

    // After a crash counted against the model: its next load checks its files' hash again rather than trusting the last
    // check, so a damaged file is found and repaired before it is loaded into the same crash.
    private void ForgetVerified(string model)
    {
        try { File.Delete(StatePath("verified", model)); }
        catch { /* the stamp stays; the next load trusts it, as it would have */ }
    }

    // "freeGb|build": the free RAM a load of the model was killed at, and the build of this app that was killed (its
    // package's last update time).
    private void RecordKilledAt(string model, double freeGb)
    {
        try
        {
            var path = StatePath("killed", model);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            File.WriteAllText(path, freeGb.ToString("R", inv) + "|" + (ExitEvidence.Now(_context).Updated ?? -1).ToString(inv));
        }
        catch { }
    }

    // A KILL ON ANOTHER BUILD SAYS NOTHING ABOUT THIS ONE. What a model needs in RAM is the engine's to decide, and an
    // update can change it by gigabytes: the 27B was killed at 1.73 GB free while the engine copied its layers into RAM
    // (2026-11-09 on the P30), and needs a fraction of that once they stream from flash. A record from another build - or
    // one written before records kept their build - is set aside: the model gets one try on this build, and a kill on
    // it holds as it always did.
    private double? KilledAt(string model)
    {
        try
        {
            var path = StatePath("killed", model);
            if (!File.Exists(path)) return null;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var parts = File.ReadAllText(path).Split('|');
            if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out var gb)) return null;
            if (parts.Length < 2 || !long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, inv, out var build)
                || ExitEvidence.Now(_context).Updated is not { } current || build != current)
                return null;
            return gb;
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

            // EVERY REFUSAL AN EARLIER RULE WROTE IS LIFTED FIRST, then the last run's death is counted under today's
            // rule: no model is refused any more (the owner, 2026-11-15: "no models should be blocked"), and nothing
            // writes a refusal now, so after the first start this finds none.
            CircleAI.Inference.CrashVerdict.LiftRefusals(_catalogue(), line => _log(line));

            // AND THE INFERENCE PROCESS'S OWN MARKER (S6): a step it began - a load, an answer -
            // and never finished, because the process ended with no handler run. A marker still
            // open at the next start is the fault, and it names the knobs that were in force.
            var workerMarker = TakeWorkerMarker();

            if (crumb is null && workerMarker is not null)
            {
                var markerModel = ModelOfMarker(workerMarker);
                var phase = workerMarker.StartsWith("load", StringComparison.Ordinal) ? WorkPhase.Load : WorkPhase.Decode;
                var died = FailureClassifier.Classify(ExitEvidence.ForPreviousWorker(_context, phase, markerModel) with
                {
                    BootChanged = bootChanged,
                    PackageUpdated = updated,
                });
                RaiseHealth(died, markerModel, workerMarker);
                _log($"the last run's inference process ended inside a step it began ({workerMarker}): {died.Kind} ({died.Evidence})");
            }

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
                if (model is not null && failure.CountsAgainstModel) ForgetVerified(model);   // its files are checked before it is tried again

                // THE MAIN PROCESS DIED INSIDE A LOAD - on the P30, iAware taking both
                // processes for memory. Not the model's crash: that kills the worker alone,
                // and is judged live. So the next load of it waits (FailurePolicy.HoldOff).
                if (model is not null && FailurePolicy.HoldsOff(failure.Kind, failure.Phase)) RecordSetback(model);

                _readiness.Failed(failure);
                _log($"the last run ended inside a load of {model}: {failure.Kind} ({failure.Evidence})");
                RaiseHealth(failure, model, workerMarker);
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

                // THE INDEX, AS THE ENGINE RECORDS IT ON FLASH: how far its indexing has got in the phone's idle time, or
                // that it is complete - the line a person is shown under "Tap and talk". The engine is in the worker's
                // process; its record is in the cache folder both processes share.
                // Where another brain answers while this phone's own is indexed, the record read is the one being built.
                try
                {
                    string? waitingFile;
                    lock (_gate) waitingFile = _inWaitingFile;

                    // OFF ITS CHARGER THE INDEX BUILDS NOTHING (IndexingWhen), so an unfinished index
                    // off the cable says it waits for charge - the same power reading the idle check uses.
                    var power = DeviceContext.PowerProbe?.Invoke() ?? PowerState.Unknown;
                    var offCharger = CircleAI.Inference.Managed.Store.IndexingWhen.OffItsCharger(power);

                    IndexReadiness.Report(_readiness, CircleAI.Inference.Managed.ManagedChatGenerator.DefaultStoreDirectory, DateTimeOffset.UtcNow, waitingFile, offCharger);
                }
                catch (Exception ex)
                {
                    _log("the index's state could not be read: " + ex.GetType().Name);
                }

                // THE CEILING (OPEN-GAPS E33). The watchdog counts the worker's CPU time as progress,
                // so a native call that spins and never writes is not ended there - it is ended here,
                // a minute past its ask's own deadline, whoever is still waiting for it. The queue gave
                // up at the deadline and a cancelled C# prefill stops at its next token, so an ask
                // still running then is a call that will not stop.
                if (beat.Job is not null && ProgressWatch.Overran(TimeSpan.FromMilliseconds(beat.OverdueMs)))
                {
                    _log($"the inference process is still on ask {beat.Job}{On(beat.Working)}, {beat.OverdueMs / 1000} s past "
                         + "its deadline: a call that does not stop; ending the worker");
                    lock (_gate) _killedByUs = true;
                    _connection.Kill();
                    return;
                }

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
        var signal = ReadCrashBuffer && death.Pid is { } crashed ? CrashLogSignal(crashed) : null;
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
            CrashBufferShowsSignal: ReadCrashBuffer && death.Pid is { } pid ? CrashLogNamesSignal(pid) : null,
            CrashSignal: signal);
    }

    public static ExitFacts ForPreviousProcess(Context context, string? model)
    {
        var (reason, status, description) = ExitRecord(context, null, null);
        return new ExitFacts((int)Build.VERSION.SdkInt, WorkPhase.Load, model, DateTimeOffset.UtcNow,
                             reason, status, description);
    }

    /// <summary>How the last run's inference process ended, for a marker it left (S6).</summary>
    public static ExitFacts ForPreviousWorker(Context context, WorkPhase phase, string? model)
    {
        var (reason, status, description) = ExitRecord(context, null, ":inference");
        return new ExitFacts((int)Build.VERSION.SdkInt, phase, model, DateTimeOffset.UtcNow,
                             reason, status, description);
    }

    /// <summary>
    /// The signal our own crash log names for <paramref name="pid"/> - "Fatal signal 4 (SIGILL)
    /// ... pid 1234" - or null (S6). API 29 keeps no exit record, and this is what tells an
    /// illegal instruction, which scalar kernels cure, from a segfault.
    /// </summary>
    private static int? CrashLogSignal(int pid)
    {
        try
        {
            using var proc = Java.Lang.Runtime.GetRuntime()!.Exec(new[] { "logcat", "-d", "-b", "crash", "-t", "200" });
            using var reader = new StreamReader(proc!.InputStream!);
            int? found = null;
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                var at = line.IndexOf("Fatal signal ", StringComparison.Ordinal);
                if (at < 0 || (!line.Contains($"pid {pid} ", StringComparison.Ordinal) && !line.Contains($"pid {pid}(", StringComparison.Ordinal)))
                    continue;
                var digits = new string(line.AsSpan(at + "Fatal signal ".Length).ToArray().TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(digits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n))
                    found = n;
            }
            return found;
        }
        catch { return null; }
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

    internal static (long? Boot, long? Updated) Now(Context context)
    {
        long? boot = null, updated = null;
        try { boot = global::Android.Provider.Settings.Global.GetInt(context.ContentResolver, "boot_count", -1); } catch { }
        try { updated = context.PackageManager?.GetPackageInfo(context.PackageName!, 0)?.LastUpdateTime; } catch { }
        return (boot is -1 ? null : boot, updated);
    }
}
