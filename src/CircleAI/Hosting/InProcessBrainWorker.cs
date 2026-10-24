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
    private readonly Func<CancellationToken, Task<InProcessBrain>> _start;
    private readonly TimeProvider _clock;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _one = new(1, 1);

    private InProcessBrain? _brain;
    private Task<InProcessBrain>? _loading;
    private BrainWorkerStatus _status;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;

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
            var brain = await _start(CancellationToken.None).ConfigureAwait(false);
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
            var options = new GenerationOptions { MaxTokens = job.MaxTokens, RetrievalQuery = job.RetrievalQuery, Timing = recorder };
            var clock = Stopwatch.StartNew();
            long ttft = -1;
            var pieces = 0;

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
                            Classify(ex, pieces == 0 ? WorkPhase.Prefill : WorkPhase.Decode, brain.ModelId, _clock.GetUtcNow()), ex);
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

            var total = clock.ElapsedMilliseconds;
            yield return new BrainChunk(string.Empty, Final: true, Timing: new TurnTiming(
                EnrichMs: recorder.EnrichMs,
                PrefillMs: recorder.PrefillMs >= 0 ? recorder.PrefillMs : ttft,
                PromptTokens: recorder.PromptTokens,
                DecodeMs: recorder.DecodeMs >= 0 ? recorder.DecodeMs : (ttft >= 0 ? total - ttft : -1),
                OutputTokens: recorder.OutputTokens >= 0 ? recorder.OutputTokens : pieces,
                TtftMs: ttft,
                ModelId: brain.ModelId,
                KvPath: recorder.KvPath,
                Finish: recorder.Finish ?? "eos"));
        }
        finally
        {
            _one.Release();
        }
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
