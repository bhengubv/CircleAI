// RequestCoordinator.cs
//
// EVERY QUESTION COMES IN HERE, IS WRITTEN DOWN, WAITS ITS TURN, AND GETS AN ANSWER OR
// A REASON. See IAskFront for why there is one door.
//
// WHAT IT REPLACES. The link ran each ask on the binder thread that carried it, waited
// 45 s for a cold brain, generated with no deadline and handed the answer back in the
// reply parcel; the wake turn asked the same in-process node on its own; a second ask
// queued behind the first by blocking a second binder thread on the generator's lock.
// Nothing was saved, nothing had an identity, and a retry asked again.
//
// HOW IT WORKS. Submit stores the ask (durably) and returns. ONE loop answers one ask at
// a time - there is one resident generator and its native handle is not re-entrant -
// choosing Assistant before Foreground before Background and taking turns between apps
// within a lane. Each ask tries the shortcuts (a greeting, the clock), then waits for the
// brain IN THE QUEUE - "still loading" is a state, never a failure - then streams its
// answer into the record. A crash, an Android stop or a memory kill puts a side-effect-
// free ask back in line; anything else that cannot be answered says why.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant.Conversations;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Inference;

namespace CircleAI.Assistant.Requests;

/// <summary>The request coordinator: durable queue, one worker, isolated callers.</summary>
public sealed class RequestCoordinator : IAskFront, IAsyncDisposable
{
    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string Caller { get; init; }
        public required AskLane Lane { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset ResumeAt { get; set; }
        public int LoadFailures { get; set; }
    }

    private readonly IAskStore _store;
    private readonly IBrainWorker _worker;
    private readonly ServiceLimits _limits;
    private readonly IReadinessTracker? _readiness;
    private readonly IConversationStore? _conversations;
    private readonly IReadOnlyList<IAskShortcut> _shortcuts;
    private readonly Func<string, bool> _isFirstParty;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<AskLane, string> _lastCaller = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _stop = new();

    private string? _runningId;
    private CancellationTokenSource? _runningCts;
    private bool _runningCancelledByCaller;
    private Task? _loop;
    private DateTimeOffset _nextPurge;
    private bool _started;

    /// <summary>A coordinator over a store and a brain worker. Call <see cref="Start"/> once.</summary>
    public RequestCoordinator(
        IAskStore store,
        IBrainWorker worker,
        ServiceLimits limits,
        IReadinessTracker? readiness = null,
        IConversationStore? conversations = null,
        IEnumerable<IAskShortcut>? shortcuts = null,
        Func<string, bool>? isFirstParty = null,
        TimeProvider? clock = null,
        Action<string>? log = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _readiness = readiness;
        _conversations = conversations;
        _shortcuts = (shortcuts ?? Array.Empty<IAskShortcut>()).ToArray();
        _isFirstParty = isFirstParty ?? (_ => false);
        _clock = clock ?? TimeProvider.System;
        _log = log ?? (_ => { });
        _worker.StatusChanged += OnWorkerStatus;
    }

    /// <inheritdoc />
    public event EventHandler<AskRecord>? Changed;

    /// <summary>Raised when nothing is waiting and nothing is running.</summary>
    public event EventHandler? Idle;

    /// <summary>Asks waiting their turn (not counting the one being answered).</summary>
    public int QueueDepth { get { lock (_gate) return _open.Count - (_runningId is null ? 0 : 1); } }

    /// <summary>The ask being answered now, if any.</summary>
    public string? CurrentAskId { get { lock (_gate) return _runningId; } }

    /// <summary>Nothing waiting and nothing running.</summary>
    public bool IsIdle { get { lock (_gate) return _open.Count == 0 && _runningId is null; } }

    // ── the door ─────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public AskAdmission Submit(AskSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (string.IsNullOrWhiteSpace(submission.Message))
            return AskAdmission.Refused(FailureKind.Rejected, AskSentences.CouldNotAnswer);
        if (submission.Message.Length > _limits.MaxMessageChars)
            return AskAdmission.Refused(FailureKind.Rejected, AskSentences.TooLong);

        AskRecord stored;
        lock (_gate)
        {
            // THE SAME REQUEST ID IS THE SAME ASK. A client that lost the reply to its
            // submit - the service died, the binder broke - sends it again, and must get
            // the ask it already has rather than a second generation of the same answer.
            if (!string.IsNullOrWhiteSpace(submission.ClientRequestId)
                && _store.FindByClientRequest(submission.CallerKey, submission.ClientRequestId!) is { } existing)
                return AskAdmission.Of(existing, duplicate: true, PositionLocked(existing.Id));

            var internalCaller = submission.CallerKey.StartsWith("internal:", StringComparison.Ordinal);
            if (!internalCaller)
            {
                var cap = _isFirstParty(submission.CallerKey) ? _limits.PerFirstPartyQueued : _limits.PerCallerQueued;
                if (_open.Values.Count(e => e.Caller == submission.CallerKey) >= cap)
                    return AskAdmission.Refused(FailureKind.Rejected, AskSentences.Busy);
            }

            // A FULL QUEUE NEVER STOPS SOMEBODY WHO SPOKE. The last few places are kept
            // for the Assistant lane, so a burst from apps cannot crowd out a voice.
            var waiting = _open.Count - (_runningId is null ? 0 : 1);
            var room = _limits.QueueCapacity - (submission.Lane == AskLane.Assistant ? 0 : _limits.AssistantReserved);
            if (waiting >= room)
                return AskAdmission.Refused(FailureKind.Rejected, AskSentences.Busy);

            var record = AskRecord.Accept(Guid.NewGuid().ToString("N"), submission, _clock.GetUtcNow());
            stored = _store.Insert(record);   // durable before anybody hears "accepted"
            if (stored.Id != record.Id)
                return AskAdmission.Of(stored, duplicate: true, PositionLocked(stored.Id));

            _open[stored.Id] = new Entry { Id = stored.Id, Caller = stored.CallerKey, Lane = stored.Lane, CreatedAt = stored.CreatedAt };
        }

        _log($"ask {stored.Id} accepted lane={stored.Lane} spoken={(stored.Spoken ? 1 : 0)} chars={stored.Message.Length}");
        Raise(stored);
        _signal.Release();
        lock (_gate) return AskAdmission.Of(stored, duplicate: false, PositionLocked(stored.Id));
    }

    /// <inheritdoc />
    public AskRecord? Status(string callerKey, string askId)
    {
        var ask = _store.Get(askId);
        return ask is not null && ask.CallerKey == callerKey ? ask : null;
    }

    /// <inheritdoc />
    public bool Cancel(string callerKey, string askId)
    {
        var ask = Status(callerKey, askId);
        if (ask is null || ask.IsFinal) return false;

        lock (_gate)
        {
            if (_runningId == askId)
            {
                _runningCancelledByCaller = true;
                try { _runningCts?.Cancel(); } catch (ObjectDisposedException) { }
            }
        }

        var done = Finish(askId, AskState.Cancelled, FailureKind.Cancelled, AskSentences.Cancelled, keepText: true);
        _log($"ask {askId} cancelled by its caller");
        return done is { State: AskState.Cancelled };
    }

    /// <inheritdoc />
    public IReadOnlyList<AskRecord> List(string callerKey, int limit, bool openOnly = false) =>
        _store.List(callerKey, Math.Clamp(limit, 1, 200), openOnly);

    /// <inheritdoc />
    public int PositionOf(string askId) { lock (_gate) return PositionLocked(askId); }

    /// <inheritdoc />
    public Task<AskRecord?> WaitAsync(string callerKey, string askId, TimeSpan budget, CancellationToken ct) =>
        WaitUntilAsync(callerKey, askId, budget, r => r.IsFinal, ct);

    /// <inheritdoc />
    public Task<AskRecord?> WaitForChangeAsync(string callerKey, string askId, long afterSeq, TimeSpan budget, CancellationToken ct) =>
        WaitUntilAsync(callerKey, askId, budget, r => r.IsFinal || r.Seq > afterSeq, ct);

    private async Task<AskRecord?> WaitUntilAsync(
        string callerKey, string askId, TimeSpan budget, Func<AskRecord, bool> done, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<AskRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, AskRecord r)
        {
            if (r.Id == askId && done(r)) tcs.TrySetResult(r);
        }

        Changed += Handler;
        try
        {
            var now = Status(callerKey, askId);
            if (now is null || done(now) || budget <= TimeSpan.Zero) return now;

            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(budget);
            var finished = await Task.WhenAny(tcs.Task, Task.Delay(Timeout.Infinite, timer.Token)).ConfigureAwait(false);
            return finished == tcs.Task ? await tcs.Task.ConfigureAwait(false) : Status(callerKey, askId);
        }
        finally
        {
            Changed -= Handler;
        }
    }

    // ── start, recovery, stop ────────────────────────────────────────────────

    /// <summary>Recover what the last process left open, tidy, and start answering.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        var now = _clock.GetUtcNow();
        var resumed = 0;
        var failed = 0;
        foreach (var ask in _store.Open())
        {
            lock (_gate) if (_open.ContainsKey(ask.Id)) continue;

            if (ask.State == AskState.Running)
            {
                // IT WAS BEING ANSWERED WHEN THE PROCESS DIED. A question is safe to ask
                // again; something that changes the world is not, and is not re-run.
                if (!ask.SideEffectFree || ask.Attempts >= _limits.MaxAttempts)
                {
                    Finish(ask.Id, AskState.Failed, ask.Kind ?? FailureKind.Unknown,
                           ask.Kind is { } k ? AskSentences.For(k) : AskSentences.CouldNotAnswer, keepText: true);
                    failed++;
                    continue;
                }
                Save(ask.Id, r => r with { State = AskState.Queued, Text = string.Empty, UpdatedAt = now });
            }

            lock (_gate)
                _open[ask.Id] = new Entry
                {
                    Id = ask.Id, Caller = ask.CallerKey, Lane = ask.Lane, CreatedAt = ask.CreatedAt,
                    ResumeAt = ask.State == AskState.Paused ? now.AddSeconds(5) : DateTimeOffset.MinValue,
                };
            resumed++;
        }

        if (resumed + failed > 0) _log($"recovered {resumed} open ask(s), {failed} could not be resumed");
        Purge(now);

        if (_readiness is { } r)
        {
            r.Setting(ReadinessSettings.MaxTokensSpoken, _limits.MaxTokensSpoken.ToString(System.Globalization.CultureInfo.InvariantCulture));
            r.Setting(ReadinessSettings.MaxTokensTyped, _limits.MaxTokensTyped.ToString(System.Globalization.CultureInfo.InvariantCulture));
            r.Setting(ReadinessSettings.MaxPromptChars, _limits.MaxMessageChars.ToString(System.Globalization.CultureInfo.InvariantCulture));
            r.Setting(ReadinessSettings.PerCallerQueued, _limits.PerCallerQueued.ToString(System.Globalization.CultureInfo.InvariantCulture));
            r.Setting(ReadinessSettings.RetentionHours, ((int)_limits.Retention.TotalHours).ToString(System.Globalization.CultureInfo.InvariantCulture));
            r.Setting(ReadinessSettings.TurnStreaming, "1");
        }

        _loop = Task.Factory.StartNew(() => LoopAsync(_stop.Token), CancellationToken.None,
                                      TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        _signal.Release();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _worker.StatusChanged -= OnWorkerStatus;
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
        _signal.Release();
        if (_loop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch { /* the loop leaves a running ask Running; the next Start re-queues it */ }
        }
        _stop.Dispose();
    }

    private void OnWorkerStatus(object? sender, BrainWorkerStatus status)
    {
        // A BRAIN THAT CAME UP, OR A MODEL THAT ARRIVED, IS NEWS FOR EVERY WAITING ASK.
        if (status.Phase is BrainPhase.Ready or BrainPhase.Installed)
        {
            var now = _clock.GetUtcNow();
            lock (_gate)
                foreach (var e in _open.Values)
                    if (e.ResumeAt > now) e.ResumeAt = now;
            _signal.Release();
        }
    }

    // ── the loop ─────────────────────────────────────────────────────────────

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Entry? next;
            TimeSpan wait;
            bool idle;
            try
            {
                var now = _clock.GetUtcNow();
                ExpireOverdue(now);
                if (now >= _nextPurge) Purge(now);
                (next, wait, idle) = PickNext(now);
            }
            catch (Exception ex)
            {
                _log("ask loop: " + ex.GetType().Name);
                next = null; wait = TimeSpan.FromSeconds(5); idle = false;
            }

            if (next is null)
            {
                if (idle) { try { Idle?.Invoke(this, EventArgs.Empty); } catch { } }
                try { await _signal.WaitAsync(wait, stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try { await RunOneAsync(next, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log($"ask {next.Id} failed in the loop: {ex.GetType().Name}");
                Finish(next.Id, AskState.Failed, FailureKind.ManagedError, AskSentences.CouldNotAnswer, keepText: true);
            }
        }
    }

    private (Entry? Next, TimeSpan Wait, bool Idle) PickNext(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_open.Count == 0) return (null, TimeSpan.FromMinutes(5), true);

            var soonest = TimeSpan.FromSeconds(30);
            foreach (var lane in new[] { AskLane.Assistant, AskLane.Foreground, AskLane.Background })
            {
                var ready = _open.Values.Where(e => e.Lane == lane && e.ResumeAt <= now)
                                        .OrderBy(e => e.CreatedAt).ToList();
                if (ready.Count == 0) continue;

                // TAKE TURNS BETWEEN APPS. The oldest ask from somebody other than the
                // last caller served in this lane goes first, so one app's burst cannot
                // hold another app's single question behind it.
                _lastCaller.TryGetValue(lane, out var last);
                var pick = ready.FirstOrDefault(e => e.Caller != last) ?? ready[0];
                _lastCaller[lane] = pick.Caller;
                return (pick, TimeSpan.Zero, false);
            }

            foreach (var e in _open.Values)
            {
                var until = e.ResumeAt - now;
                if (until > TimeSpan.Zero && until < soonest) soonest = until;
            }
            return (null, soonest, false);
        }
    }

    private void ExpireOverdue(DateTimeOffset now)
    {
        List<Entry> overdue;
        lock (_gate)
            overdue = _open.Values.Where(e => e.Id != _runningId && Overdue(e, now)).ToList();
        foreach (var e in overdue)
        {
            _log($"ask {e.Id} expired after {(now - e.CreatedAt).TotalSeconds:0} s waiting");
            Expire(_store.Get(e.Id), e.Id);
        }
    }

    private bool Overdue(Entry e, DateTimeOffset now) =>
        now - e.CreatedAt > _limits.QueueDeadlineFor(e.Lane)
        || (e.Lane == AskLane.Assistant && now - e.CreatedAt > _limits.AssistantStaleAfter);

    private async Task RunOneAsync(Entry entry, CancellationToken stop)
    {
        var ask = _store.Get(entry.Id);
        if (ask is null || ask.IsFinal) { Forget(entry.Id); return; }

        var turnCame = _clock.GetUtcNow();
        if (Overdue(entry, turnCame))
        {
            Expire(ask, ask.Id);
            return;
        }

        // 1. ANSWERS THAT NEED NO MODEL. A greeting, the date, a sound that was not
        //    speech: answered at once, from the queue, whatever the brain is doing.
        foreach (var shortcut in _shortcuts)
        {
            string? said;
            try { said = await shortcut.TryAnswerAsync(ask, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log($"shortcut {shortcut.Name} failed: {ex.GetType().Name}"); continue; }
            if (string.IsNullOrWhiteSpace(said)) continue;

            var done = _clock.GetUtcNow();
            var timing = new TurnTiming(QueueMs: Ms(turnCame - ask.CreatedAt), LoadWaitMs: 0,
                                        TotalMs: Ms(done - ask.CreatedAt), Finish: "shortcut");
            Complete(ask, said!, timing, done);
            _log($"ask {ask.Id} answered by {shortcut.Name}");
            return;
        }

        // 2. THE BRAIN, WAITED FOR IN THE QUEUE. A cold load is 13-23 s on the P30; a
        //    question asked during it is not failed, it waits - and if this ask's share
        //    of the wait runs out it goes back in line still saying "loading".
        long loadWaitMs = 0;
        if (_worker.Status.Phase != BrainPhase.Ready)
        {
            Save(ask.Id, r => r with { State = AskState.WaitingForModel, Kind = null, Sentence = AskSentences.StillLoading, UpdatedAt = _clock.GetUtcNow() });
            var waitStarted = _clock.GetUtcNow();
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stop);
                budget.CancelAfter(_limits.LoadWaitBudget);
                await _worker.EnsureReadyAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                Requeue(entry, TimeSpan.FromSeconds(5));
                return;
            }
            catch (BrainWorkerException ex)
            {
                OnLoadFailed(entry, ex.Failure);
                return;
            }
            loadWaitMs = Ms(_clock.GetUtcNow() - waitStarted);
        }

        // 3. ANSWER IT.
        var started = _clock.GetUtcNow();
        ask = Save(ask.Id, r => r with
        {
            State = AskState.Running,
            Attempts = r.Attempts + 1,
            StartedAt = r.StartedAt ?? started,
            ModelId = _worker.Status.ModelId,
            Kind = null,
            Sentence = null,
            Text = string.Empty,
            UpdatedAt = started,
        }) ?? ask;
        if (ask.IsFinal) { Forget(ask.Id); return; }   // cancelled while it waited

        var job = new BrainJob(
            AskId: ask.Id,
            Messages: Conversation(ask),
            RetrievalQuery: ask.Asked,
            MaxTokens: _limits.MaxTokensFor(ask.Spoken),
            Spoken: ask.Spoken,
            Deadline: started + _limits.GenerationDeadline);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(_limits.GenerationDeadline);
        lock (_gate) { _runningId = ask.Id; _runningCts = cts; _runningCancelledByCaller = false; }

        var text = new StringBuilder();
        TurnTiming? workerTiming = null;
        long ttftMs = -1;
        var lastWrite = started;
        try
        {
            await foreach (var chunk in _worker.GenerateAsync(job, cts.Token).ConfigureAwait(false))
            {
                if (chunk.Text.Length > 0)
                {
                    if (ttftMs < 0) ttftMs = Ms(_clock.GetUtcNow() - started);
                    text.Append(chunk.Text);
                }
                if (chunk.Final) workerTiming = chunk.Timing;

                var now = _clock.GetUtcNow();
                if (now - lastWrite >= _limits.TextWriteInterval && text.Length > 0)
                {
                    var partial = text.ToString();
                    Save(ask.Id, r => r.State == AskState.Running ? r with { Text = partial, UpdatedAt = now } : r);
                    lastWrite = now;
                }
            }

            var finished = _clock.GetUtcNow();
            var answer = Guarded(ask, PlainReply.Clean(text.ToString()), cutShort: workerTiming?.Finish == "length");
            if (string.IsNullOrWhiteSpace(answer))
            {
                Finish(ask.Id, AskState.Failed, FailureKind.ManagedError, AskSentences.CouldNotAnswer, keepText: false);
                _log($"ask {ask.Id} produced no answer");
                return;
            }
            Complete(ask, answer, Merge(workerTiming, ask, started, loadWaitMs, ttftMs, finished), finished);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            throw;   // the service is going away: leave it Running, the next Start re-queues it
        }
        catch (OperationCanceledException)
        {
            bool byCaller;
            lock (_gate) byCaller = _runningCancelledByCaller;
            if (byCaller) return;   // Cancel already made it final

            // THE DEADLINE. What was said so far is still an answer; nothing at all is not.
            var finished = _clock.GetUtcNow();
            var partial = Guarded(ask, PlainReply.Clean(text.ToString()), cutShort: true);
            if (!string.IsNullOrWhiteSpace(partial))
                Complete(ask, partial, Merge(workerTiming, ask, started, loadWaitMs, ttftMs, finished) with { Finish = "deadline" }, finished);
            else
                Finish(ask.Id, AskState.Failed, FailureKind.TimedOut, AskSentences.TimedOut, keepText: false);
        }
        catch (BrainWorkerException ex)
        {
            OnGenerationFailed(entry, ask, ex.Failure);
        }
        finally
        {
            lock (_gate) { _runningId = null; _runningCts = null; _runningCancelledByCaller = false; }
        }
    }

    /// <summary>The model's answer after <see cref="AnswerGuard"/>, logged (never the text) when it changed it.</summary>
    /// <remarks>
    /// THE FINAL TEXT IS THE GUARDED ONE, and a client that streamed "No." still ends on it:
    /// CircleAiLinkClient reads a finished answer whole, once.
    /// </remarks>
    private string Guarded(AskRecord ask, string answer, bool cutShort = false)
    {
        var checkedAnswer = AnswerGuard.Check(ask.Asked, answer, cutShort);
        if (!string.Equals(checkedAnswer, answer, StringComparison.Ordinal))
            _log($"ask {ask.Id}: the answer guard rewrote a {answer.Length}-char answer");
        return checkedAnswer;
    }

    private void OnLoadFailed(Entry entry, Failure failure)
    {
        entry.LoadFailures++;
        var now = _clock.GetUtcNow();
        var status = _worker.Status;

        // WHAT IS LEFT OF THE WAIT, NOT ITS FULL LENGTH. The worker writes its status once,
        // when the failure happens, and answers every question after that from it - so a
        // two-minute hold-off read 100 s in is twenty seconds away, not two minutes.
        var left = status.RetryAfter is { } wait ? status.Since + wait - now : TimeSpan.FromSeconds(15);
        var retryAfter = left > TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1);

        // TOLD, NOT WAITED ON FOR EVER. A question the brain could not start for used to
        // wait here with no end but the queue's deadline, and a waiting question's
        // sentence never reaches the app: on the P30 on 2026-10-23 the typing dots ran for
        // three minutes while every model on the phone was refused, and the person was
        // told nothing. Now it ends, with the reason, when nothing will change by itself,
        // or when the next try would land past its share of the wait. A brain still
        // loading or still downloading inside that share is still waited for.
        if (EndsWhenTheBrainCannotStart(failure.Kind))
        {
            var nothingComing = failure.Kind == FailureKind.NotInstalled && status.RetryAfter is null;
            var next = failure.Kind switch
            {
                FailureKind.NotInstalled      => TimeSpan.FromSeconds(30),   // the waits below, as they are
                FailureKind.ModelFilesDamaged => TimeSpan.FromSeconds(60),
                _                             => retryAfter,
            };
            if (nothingComing || now - entry.CreatedAt + next > PatienceFor(entry.Lane))
            {
                Finish(entry.Id, AskState.Failed, failure.Kind,
                       AskSentences.BrainCouldNotStart(failure.Kind, nothingComing), keepText: false);
                _log($"ask {entry.Id} told: the brain could not start ({failure.Kind}{(nothingComing ? ", nothing to wait for" : "")})");
                return;
            }
        }

        _log($"ask {entry.Id} waits: the brain could not start ({failure.Kind})");

        switch (failure.Kind)
        {
            case FailureKind.NotInstalled:
                Wait(entry, AskState.WaitingForSetup, failure.Kind, AskSentences.NotSetUpYet, null, TimeSpan.FromSeconds(30));
                return;
            case FailureKind.PermissionMissing:
                Wait(entry, AskState.WaitingForPermission, failure.Kind, AskSentences.NeedsPermission, failure.Detail, TimeSpan.FromSeconds(60));
                return;
            case FailureKind.ModelFilesDamaged:
                Wait(entry, AskState.Paused, failure.Kind, AskSentences.RepairingFiles, null, TimeSpan.FromSeconds(60));
                return;
            case FailureKind.OutOfMemory:
            case FailureKind.AndroidStopped:
            case FailureKind.AndroidTimeout:
                Wait(entry, AskState.Paused, failure.Kind, AskSentences.For(failure.Kind), null, retryAfter);
                return;
        }

        if (entry.LoadFailures < _limits.MaxAttempts && failure.Retryable)
            Wait(entry, AskState.Paused, failure.Kind, AskSentences.For(failure.Kind), null, retryAfter);
        else
            Finish(entry.Id, AskState.Failed, failure.Kind, AskSentences.For(failure.Kind), keepText: false);
    }

    /// <summary>The kinds a question waits on for a while, then is told the brain could not start.</summary>
    /// <remarks>
    /// UNKNOWN IS ONE OF THEM: the inference process dying alone mid-load with nothing to
    /// say why - the P30's commonest load death - is classified Unknown, and it reaches
    /// here only from a load. NOT A MISSING PERMISSION: that waits on a person, who may be
    /// on the consent screen that very moment, and it keeps its own wait.
    /// </remarks>
    private static bool EndsWhenTheBrainCannotStart(FailureKind kind) =>
        kind is FailureKind.NotInstalled or FailureKind.OutOfMemory or FailureKind.AndroidStopped
             or FailureKind.AndroidTimeout or FailureKind.ModelFilesDamaged or FailureKind.Unknown;

    /// <summary>How long a question waits for a brain that cannot start, before it is told why.</summary>
    /// <remarks>
    /// THE LOAD WAIT, COUNTED FROM WHEN IT WAS ASKED - the saved time, so a service killed
    /// and restarted in the middle does not start the count again. A background question
    /// keeps its lane's own, longer deadline.
    /// </remarks>
    private TimeSpan PatienceFor(AskLane lane) =>
        lane == AskLane.Background ? _limits.BackgroundQueueDeadline : _limits.LoadWaitBudget;

    /// <summary>End a question that waited too long - with the reason it waited, when there was one.</summary>
    /// <remarks>
    /// "I couldn't get to that in time" threw away the one thing a person could act on,
    /// for a question that waited all along on a brain that could not start. ONLY A
    /// QUESTION NEVER STARTED: one cut off mid-answer carries a kind too, and "while I was
    /// getting my brain ready" would not be true of it.
    /// </remarks>
    private void Expire(AskRecord? record, string id)
    {
        var waitedOn = record is { StartedAt: null } ? record.Kind : null;
        if (waitedOn is { } kind && EndsWhenTheBrainCannotStart(kind))
            Finish(id, AskState.Expired, kind, AskSentences.BrainCouldNotStart(kind), keepText: false);
        else
            Finish(id, AskState.Expired, FailureKind.TimedOut, AskSentences.Expired, keepText: false);
    }

    private void OnGenerationFailed(Entry entry, AskRecord ask, Failure failure)
    {
        var attempts = _store.Get(ask.Id)?.Attempts ?? ask.Attempts;
        _log($"ask {ask.Id} interrupted ({failure.Kind}, attempt {attempts})");

        if (failure.Retryable && ask.SideEffectFree && attempts < _limits.MaxAttempts)
        {
            var paused = failure.Kind is FailureKind.OutOfMemory or FailureKind.AndroidStopped or FailureKind.AndroidTimeout;
            var delay = paused ? _worker.Status.RetryAfter ?? TimeSpan.FromSeconds(15) : TimeSpan.Zero;
            Save(ask.Id, r => r.IsFinal ? r : r with
            {
                State = paused ? AskState.Paused : AskState.Queued,
                Kind = failure.Kind,
                Sentence = AskSentences.For(failure.Kind),
                Text = string.Empty,
                UpdatedAt = _clock.GetUtcNow(),
            });
            Requeue(entry, delay);
            return;
        }

        Finish(ask.Id, AskState.Failed, failure.Kind, AskSentences.For(failure.Kind), keepText: true);
    }

    // ── records ──────────────────────────────────────────────────────────────

    private IReadOnlyList<ChatMessage> Conversation(AskRecord ask)
    {
        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(ask.SessionId))
        {
            if (_conversations is not null)
            {
                foreach (var turn in _conversations.Window(ask.CallerKey, ask.SessionId, _limits.HistoryChars))
                    messages.Add(new ChatMessage(turn.Role, turn.Text));
            }
            else
            {
                // NO SEPARATE STORE NEEDED: the caller's own answered asks in this session
                // ARE the conversation. Newest first while they fit, then put in order.
                var budget = _limits.HistoryChars;
                var pairs = new List<(string Q, string A)>();
                foreach (var prior in _store.List(ask.CallerKey, 40))
                {
                    if (prior.Id == ask.Id || prior.SessionId != ask.SessionId || prior.State != AskState.Succeeded
                        || prior.CreatedAt > ask.CreatedAt) continue;
                    var cost = prior.Asked.Length + prior.Text.Length;
                    if (cost > budget) break;
                    budget -= cost;
                    pairs.Add((prior.Asked, prior.Text));
                }
                for (var i = pairs.Count - 1; i >= 0; i--)
                {
                    messages.Add(new ChatMessage("user", pairs[i].Q));
                    messages.Add(new ChatMessage("assistant", pairs[i].A));
                }
            }
        }

        messages.Add(new ChatMessage("user", ask.Message));
        return messages;
    }

    private static TurnTiming Merge(TurnTiming? worker, AskRecord ask, DateTimeOffset started, long loadWaitMs, long ttftMs, DateTimeOffset finished)
    {
        var w = worker ?? TurnTiming.None;
        var firstStart = ask.StartedAt ?? started;
        return w with
        {
            QueueMs = Ms(firstStart - ask.CreatedAt),
            LoadWaitMs = loadWaitMs,
            TtftMs = ttftMs,
            TotalMs = Ms(finished - ask.CreatedAt),
            ModelId = w.ModelId ?? ask.ModelId,
            Finish = w.Finish ?? "eos",
        };
    }

    private void Complete(AskRecord ask, string answer, TurnTiming timing, DateTimeOffset at)
    {
        var done = Save(ask.Id, r => r.IsFinal ? r : r with
        {
            State = AskState.Succeeded,
            Text = answer,
            Kind = null,
            Sentence = null,
            Timing = timing,
            UpdatedAt = at,
            FinishedAt = at,
        });
        Forget(ask.Id);
        if (done is not { State: AskState.Succeeded }) return;

        _log(timing.ToLogLine(ask.Id));
        if (_conversations is not null && !string.IsNullOrEmpty(ask.SessionId))
        {
            try
            {
                _conversations.Append(ask.CallerKey, ask.SessionId, "user", ask.Asked, ask.Id, ask.CreatedAt);
                _conversations.Append(ask.CallerKey, ask.SessionId, "assistant", answer, ask.Id, at);
            }
            catch (Exception ex) { _log("conversation not saved: " + ex.GetType().Name); }
        }
    }

    private AskRecord? Finish(string id, AskState state, FailureKind? kind, string sentence, bool keepText)
    {
        var at = _clock.GetUtcNow();
        var done = Save(id, r => r.IsFinal ? r : r with
        {
            State = state,
            Kind = kind,
            Sentence = sentence,
            Text = keepText ? r.Text : string.Empty,
            UpdatedAt = at,
            FinishedAt = at,
        });
        Forget(id);
        return done;
    }

    private void Wait(Entry entry, AskState state, FailureKind kind, string sentence, string? needs, TimeSpan retryAfter)
    {
        Save(entry.Id, r => r.IsFinal ? r : r with
        {
            State = state, Kind = kind, Sentence = sentence, NeedsPermission = needs, UpdatedAt = _clock.GetUtcNow(),
        });
        Requeue(entry, retryAfter);
    }

    private void Requeue(Entry entry, TimeSpan after)
    {
        lock (_gate)
            if (_open.TryGetValue(entry.Id, out var e)) e.ResumeAt = _clock.GetUtcNow() + after;
        _signal.Release();
    }

    private void Forget(string id)
    {
        bool idle;
        lock (_gate)
        {
            _open.Remove(id);
            idle = _open.Count == 0 && (_runningId is null || _runningId == id);
        }
        if (idle) _signal.Release();
    }

    private AskRecord? Save(string id, Func<AskRecord, AskRecord> change)
    {
        var saved = _store.Update(id, change);
        if (saved is not null) Raise(saved);
        return saved;
    }

    private void Raise(AskRecord record)
    {
        try { Changed?.Invoke(this, record); }
        catch { /* a reader that throws must not stop the answer */ }
    }

    private int PositionLocked(string id)
    {
        if (!_open.ContainsKey(id) || _runningId == id) return -1;
        var order = _open.Values
            .Where(e => e.Id != _runningId)
            .OrderBy(e => e.Lane)
            .ThenBy(e => e.CreatedAt)
            .Select(e => e.Id)
            .ToList();
        return order.IndexOf(id);
    }

    private void Purge(DateTimeOffset now)
    {
        _nextPurge = now.AddHours(24);
        try
        {
            var gone = _store.Purge(now - _limits.Retention);
            var idle = _conversations?.Purge(now.AddHours(-24)) ?? 0;
            if (gone + idle > 0) _log($"forgot {gone} finished ask(s) and {idle} idle conversation(s)");
        }
        catch (Exception ex) { _log("purge failed: " + ex.GetType().Name); }
    }

    private static long Ms(TimeSpan span) => (long)Math.Max(0, span.TotalMilliseconds);
}
