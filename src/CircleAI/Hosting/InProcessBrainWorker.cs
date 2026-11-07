// InProcessBrainWorker.cs
//
// THE BRAIN, IN THIS PROCESS, BEHIND THE SAME INTERFACE AS THE SUPERVISED ONE.
//
// The request coordinator talks to an IBrainWorker and must not care where the model
// lives. On a desktop it lives here; on Android this same class runs INSIDE the
// ":inference" process, and the service talks to it over a binder - so the load, the
// streaming, the timing and the failure classification are written once.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Inference;

namespace CircleAI.Hosting;

/// <summary>A started brain and the model it runs.</summary>
/// <param name="Service">The brain, already started (loaded and warmed).</param>
/// <param name="ModelId">The model it runs.</param>
public sealed record InProcessBrain(IAIService Service, string ModelId);

/// <summary><see cref="IBrainWorker"/> over an <see cref="IAIService"/> in this process.</summary>
public sealed class InProcessBrainWorker : IBrainWorker
{
    // THE SHARE OF AN ANSWER'S TIME ITS PROMPT MAY TAKE TO READ, the rest left to write in.
    private const double ReadingShare = 1.0 / 3;

    // AND THE MOST IT MAY TAKE, HOWEVER LONG THE ANSWER HAS: the first word is what a person waits
    // for. A third of the queue's two minutes was 40 s of reading, and once the C# engine read 8 tokens
    // a second on the P30 the turn filled it with history: "Write one short sentence about the rain."
    // read 230 tokens and spoke after 31 s, where the question alone is 3. What a brain still holds
    // costs nothing, so a live conversation keeps its history; what it would have to read again goes.
    private static readonly TimeSpan ReadingLimit = TimeSpan.FromSeconds(3);

    // LONGER FOR A CONVERSATION, WHICH ONLY COSTS ANYTHING WHEN THE BRAIN HAS LOST IT. One it holds has the
    // new turn to read. One it lost has all of it to read again, and on the P30 on 2026-11-07 a killed
    // brain came back to 103 tokens and three seconds for them: the talk about a dripping tap went,
    // and "Can I fix it myself?" was answered "I need to know what specific issue you are facing".
    private static readonly TimeSpan ThreadLimit = TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task<InProcessBrain>> _start;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _one = new(1, 1);

    private InProcessBrain? _brain;
    private Task<InProcessBrain>? _loading;
    private BrainWorkerStatus _status;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private string _knobs = string.Empty;   // what Wolverine had turned down when this brain was built (S6)
    private bool _inTurn;
    private AnsweringBrain? _answering;     // the brain answering the turn in flight, once it is known (OPEN-GAPS E33)

    /// <summary>A worker that builds its brain with <paramref name="start"/> the first time it is needed.</summary>
    public InProcessBrainWorker(Func<CancellationToken, Task<InProcessBrain>> start, TimeProvider? clock = null, Action<string>? log = null)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _clock = clock ?? TimeProvider.System;
        _log = log ?? (_ => { });
        _status = BrainWorkerStatus.NotStarted(_clock.GetUtcNow()) with { ProcessId = Environment.ProcessId };
    }

    /// <summary>The model loaded now, or null.</summary>
    public string? ModelId { get { lock (_gate) return _brain?.ModelId; } }

    /// <inheritdoc />
    public BrainWorkerStatus Status { get { lock (_gate) return _status; } }

    /// <inheritdoc />
    public event EventHandler<BrainWorkerStatus>? StatusChanged;

    /// <inheritdoc />
    public event EventHandler<AnsweringBrain>? AnsweringChanged;

    /// <inheritdoc />
    public async Task EnsureReadyAsync(CancellationToken ct)
    {
        Task<InProcessBrain> loading;
        lock (_gate)
        {
            if (_brain is not null) return;
            if (_loading is null)
            {
                if (_status.Phase == BrainPhase.Failed && _clock.GetUtcNow() < _retryAt && _status.LastFailure is { } last)
                    throw new BrainWorkerException(last);

                // A LOAD THAT FAILED BEFORE ITS FIRST AWAIT has already run its catch, which
                // cleared _loading - so storing it here kept a failed load for good, and no
                // load was tried again until the process restarted: the desktop service
                // answered "not installed" after the model it was waiting for had landed.
                var started = LoadAsync();
                _loading = started.IsCompleted ? null : started;
                loading = started;
            }
            else loading = _loading;
        }

        // EVERY CALLER WAITS ON THE SAME LOAD, and a caller that stops waiting does not
        // stop the load: the next question will want it.
        await loading.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<InProcessBrain> LoadAsync()
    {
        Set(BrainPhase.Loading, LoadStep.LoadingWeights, null, null, null);
        var clock = Stopwatch.StartNew();
        try
        {
            // A MARKER BEFORE THE STEP THAT CAN DIE WITHOUT AN EXCEPTION (S6), naming the knobs
            // the brain is built with - the main process reads it if this process dies in here.
            var knobs = CircleAI.Inference.BrainTuning.Current().Describe();
            lock (_gate) _knobs = knobs;
            InProcessBrain brain;
            using (CircleAI.Assistant.RiskySteps.Begin("load | " + knobs))
                brain = await _start(CancellationToken.None).ConfigureAwait(false);
            lock (_gate) { _brain = brain; _loading = null; }
            Set(BrainPhase.Ready, LoadStep.None, brain.ModelId, null, null);
            _log($"brain ready: {brain.ModelId} in {clock.ElapsedMilliseconds} ms");
            return brain;
        }
        catch (Exception ex)
        {
            var failure = Classify(ex, WorkPhase.Load, null, _clock.GetUtcNow());
            var retry = failure.Kind == FailureKind.NotInstalled ? (TimeSpan?)TimeSpan.FromSeconds(10)
                      : failure.Retryable ? TimeSpan.FromSeconds(30) : null;
            lock (_gate)
            {
                _loading = null;
                _retryAt = _clock.GetUtcNow() + (retry ?? TimeSpan.FromMinutes(5));
            }
            Set(failure.Kind == FailureKind.NotInstalled ? BrainPhase.NoModel : BrainPhase.Failed,
                LoadStep.None, null, failure, retry);
            _log($"brain did not start: {failure.Kind} ({ex.GetType().Name})");
            throw new BrainWorkerException(failure, ex);
        }
    }

    /// <summary>
    /// Which part of the load is running - <see cref="LoadStep.LoadingWeights"/> while the model is
    /// built, <see cref="LoadStep.Warming"/> while its first prefill runs - so the status says so
    /// (OPEN-GAPS A14). Ignored outside a load. Wire it to <c>AIOptions.LoadStepChanged</c>.
    /// </summary>
    public void ReportStep(LoadStep step)
    {
        BrainWorkerStatus status;
        lock (_gate) status = _status;
        if (status.Phase != BrainPhase.Loading || status.Step == step) return;
        Set(BrainPhase.Loading, step, status.ModelId, null, null);
    }

    /// <summary>
    /// Which brain answers the turn in flight - the main brain, or a specialist a router brought in
    /// beside it - so its timing, a failure and a death name the brain that was working, not the one
    /// in the main slot (OPEN-GAPS E33). Ignored outside a turn. Wire it to <c>AIOptions.Answering</c>.
    /// </summary>
    public void ReportAnswering(AnsweringBrain brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        string knobs;
        bool changed;
        lock (_gate)
        {
            if (!_inTurn) return;
            changed = brain != _answering;
            _answering = brain;
            knobs = _knobs;
        }

        // THE MARKER SAYS WHO IS WORKING: a process that dies in this turn is read by its marker.
        CircleAI.Assistant.RiskySteps.Restate($"generate {brain.ModelId} | {brain.Slot} | {knobs}");

        // AND THE ASK SAYS IT TOO (OPEN-GAPS E33): the coordinator names the brain on the ask's record.
        if (!changed) return;
        try { AnsweringChanged?.Invoke(this, brain); } catch { /* readers must not break the worker */ }
    }

    /// <summary>The brain answering the turn in flight, once its slot is chosen; null between turns.</summary>
    public AnsweringBrain? Answering { get { lock (_gate) return _inTurn ? _answering : null; } }

    // The model a turn's timing and failures name: the brain answering it, else the one loaded.
    private string Working(InProcessBrain brain) => Answering?.ModelId ?? brain.ModelId;

    /// <inheritdoc />
    public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        await EnsureReadyAsync(ct).ConfigureAwait(false);

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            InProcessBrain brain;
            lock (_gate) brain = _brain ?? throw new BrainWorkerException(
                new Failure(FailureKind.Unknown, Certainty.Certain, WorkPhase.Prefill, null, "released", "the brain was released", _clock.GetUtcNow()));

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var left = job.Deadline - _clock.GetUtcNow();
            if (left > TimeSpan.Zero) deadline.CancelAfter(left);

            var recorder = new TurnTimingRecorder();
            // THE QUESTION'S OWN EFFORT, chosen where the question came in (EffortRouter), and a
            // plain job's messages as given - both decided in the service, both carried here.
            var options = new GenerationOptions
            {
                MaxTokens = job.MaxTokens,
                RetrievalQuery = job.RetrievalQuery,
                Timing = recorder,
                Effort = job.Effort,
                ThinkingBudget = job.ThinkingBudget,
                Plain = job.Plain,
                JustRead = job.JustRead,
                Conversation = job.Conversation,
                Spoken = job.Spoken,
                ReadWithin = left > TimeSpan.Zero
                    ? TimeSpan.FromTicks(Math.Min((left * ReadingShare).Ticks, (job.Conversation is null ? ReadingLimit : ThreadLimit).Ticks))
                    : null,
            };
            var clock = Stopwatch.StartNew();
            long ttft = -1;
            var pieces = 0;

            // AND BEFORE EVERY ANSWER (S6): an illegal instruction or a SIGSEGV in the kernels
            // ends this process mid-answer with no catch block run.
            string knobs;
            lock (_gate) { knobs = _knobs; _inTurn = true; _answering = null; }
            using var risky = CircleAI.Assistant.RiskySteps.Begin($"generate {brain.ModelId} | {knobs}");

            // A TURN WITH TOOLS IS ANSWERED WHOLE (OPEN-GAPS A16): the engine's own tool for a
            // plain intent, else the agentic loop (ToolTurn) - and the caller, which asked with
            // tools because its streamed answer began a call, replaces what it showed with this.
            if (job.Tools)
            {
                string answer;
                try
                {
                    answer = await ToolTurn.RunAsync(brain.Service, LastUserTurn(job.Messages), job.RetrievalQuery, options, deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (BrainWorkerException) { throw; }
                catch (Exception ex)
                {
                    throw new BrainWorkerException(Classify(ex, WorkPhase.Prefill, Working(brain), _clock.GetUtcNow()), ex);
                }

                ttft = clock.ElapsedMilliseconds;
                _log($"tool turn answered in {ttft} ms");
                if (answer.Length > 0) yield return new BrainChunk(answer);
                yield return Ending(Working(brain), recorder, clock.ElapsedMilliseconds, ttft, answer.Length > 0 ? 1 : 0);
                yield break;
            }

            var stream = brain.Service.StreamAsync(job.Messages, options, deadline.Token).GetAsyncEnumerator(deadline.Token);
            try
            {
                while (true)
                {
                    string piece;
                    try
                    {
                        if (!await stream.MoveNextAsync().ConfigureAwait(false)) break;
                        piece = stream.Current;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (BrainWorkerException) { throw; }
                    catch (Exception ex)
                    {
                        throw new BrainWorkerException(
                            Classify(ex, pieces == 0 ? WorkPhase.Prefill : WorkPhase.Decode, Working(brain), _clock.GetUtcNow()), ex);
                    }

                    if (string.IsNullOrEmpty(piece)) continue;
                    if (ttft < 0) ttft = clock.ElapsedMilliseconds;
                    pieces++;
                    yield return new BrainChunk(piece);
                }
            }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            yield return Ending(Working(brain), recorder, clock.ElapsedMilliseconds, ttft, pieces);
        }
        finally
        {
            lock (_gate) _inTurn = false;
            _one.Release();
        }
    }

    // The last piece of an answer: what the recorder measured, else what the stream showed - and the
    // brain that answered it, which a speed is learned for.
    private static BrainChunk Ending(string modelId, TurnTimingRecorder recorder, long total, long ttft, int pieces) =>
        new(string.Empty, Final: true, Timing: new TurnTiming(
            EnrichMs: recorder.EnrichMs,
            PrefillMs: recorder.PrefillMs >= 0 ? recorder.PrefillMs : ttft,
            PromptTokens: recorder.PromptTokens,
            DecodeMs: recorder.DecodeMs >= 0 ? recorder.DecodeMs : (ttft >= 0 ? total - ttft : -1),
            OutputTokens: recorder.OutputTokens >= 0 ? recorder.OutputTokens : pieces,
            TtftMs: ttft,
            ModelId: modelId,
            KvPath: recorder.KvPath,
            Finish: recorder.Finish ?? "eos"));

    // The turn a tool turn answers: the last the person said (the coordinator's conversation ends on it).
    private static string LastUserTurn(IReadOnlyList<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (string.Equals(messages[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                return messages[i].Content ?? string.Empty;
        return string.Empty;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(CancellationToken ct)
    {
        InProcessBrain? brain;
        await _one.WaitAsync(ct).ConfigureAwait(false);   // never under a running answer
        try
        {
            lock (_gate) { brain = _brain; _brain = null; }
            if (brain is null) return;
            await brain.Service.DisposeAsync().ConfigureAwait(false);
            Set(BrainPhase.Installed, LoadStep.None, brain.ModelId, null, null);
            _log($"brain released: {brain.ModelId}");
        }
        finally
        {
            _one.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        InProcessBrain? brain;
        lock (_gate) { brain = _brain; _brain = null; }
        if (brain is not null)
        {
            try { await brain.Service.DisposeAsync().ConfigureAwait(false); } catch { /* going away */ }
        }
    }

    private void Set(BrainPhase phase, LoadStep step, string? modelId, Failure? failure, TimeSpan? retryAfter)
    {
        BrainWorkerStatus status;
        lock (_gate)
            status = _status = new BrainWorkerStatus(phase, step, modelId, failure, retryAfter, Environment.ProcessId, _clock.GetUtcNow());
        try { StatusChanged?.Invoke(this, status); } catch { /* readers must not break the worker */ }
    }

    /// <summary>What kind of failure an exception from loading or generating is.</summary>
    /// <remarks>
    /// Typed exceptions are certain; the native load's one error string is only a
    /// probable memory failure - the bridge reports memory and file damage alike.
    /// </remarks>
    public static Failure Classify(Exception ex, WorkPhase phase, string? modelId, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var inner = ex is AggregateException { InnerException: { } first } ? first : ex;
        var type = inner.GetType().Name;
        Failure F(FailureKind kind, Certainty certainty, string detail) =>
            new(kind, certainty, phase, inner is ModelNotInstalledException m ? m.ModelId : modelId, "exception=" + type, detail, at);

        return inner switch
        {
            ModelNotInstalledException { Damaged: true } => F(FailureKind.ModelFilesDamaged, Certainty.Certain, "the model's files are damaged"),
            ModelNotInstalledException => F(FailureKind.NotInstalled, Certainty.Certain, "the model is not on this device"),
            OperationCanceledException => F(FailureKind.Cancelled, Certainty.Certain, "the work was cancelled"),
            TimeoutException => F(FailureKind.TimedOut, Certainty.Certain, "the work ran out of time"),
            OutOfMemoryException => F(FailureKind.OutOfMemory, Certainty.Certain, "the phone ran out of memory"),
            FileNotFoundException or DirectoryNotFoundException or InvalidDataException
                => F(FailureKind.ModelFilesDamaged, Certainty.Inferred, "a model file is missing or unreadable"),
            InvalidOperationException when inner.Message.Contains("available RAM", StringComparison.OrdinalIgnoreCase)
                || inner.Message.Contains("mnn_llm_load", StringComparison.OrdinalIgnoreCase)
                => F(FailureKind.OutOfMemory, Certainty.Inferred, "the model could not be loaded, probably for lack of memory"),
            _ => F(FailureKind.ManagedError, Certainty.Certain, "an error in Circle AI's own code"),
        };
    }
}
