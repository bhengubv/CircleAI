// WorkerConnection.cs
//
// THE MAIN PROCESS'S HANDLE ON THE INFERENCE PROCESS.
//
// Binds InferenceWorkerService, watches the binder for death, and turns the worker's
// polled protocol into awaitable calls. Every transaction runs off the main thread, and
// a dead binder becomes WorkerDiedException carrying what the worker was doing at that
// moment - which is the evidence InferenceSupervisor classifies.

using System.Runtime.CompilerServices;
using Android.Content;
using Android.OS;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;

namespace CircleAI.Service.Droid.Inference;

/// <summary>A binding to the ":inference" process.</summary>
public sealed class WorkerConnection : IAsyncDisposable
{
    private const string Tag = "CircleAI.Supervisor";
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(20);

    private readonly Context _context;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private Connection? _connection;
    private IBinder? _binder;
    private TaskCompletionSource<IBinder>? _connecting;
    private int? _pid;
    private WorkPhase _phase = WorkPhase.None;
    private string? _model;
    private string? _ask;
    private bool _disposed;

    /// <summary>A connection over this app's context.</summary>
    public WorkerConnection(Context context, Action<string>? log = null)
    {
        _context = context.ApplicationContext ?? context;
        _log = log ?? (line => global::Android.Util.Log.Info(Tag, line));
    }

    /// <summary>The worker's process id, while bound.</summary>
    public int? Pid { get { lock (_gate) return _pid; } }

    /// <summary>Whether a live binder is held.</summary>
    public bool IsConnected { get { lock (_gate) return _binder is { } b && b.IsBinderAlive; } }

    /// <summary>The worker process died.</summary>
    public event EventHandler<WorkerDeath>? Died;

    /// <summary>Bind (or reuse the binding) and say hello.</summary>
    public async Task<WorkerHello> ConnectAsync(CancellationToken ct)
    {
        IBinder binder;
        Task<IBinder>? waiting = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_binder is { IsBinderAlive: true } live) binder = live;
            else
            {
                binder = null!;
                if (_connecting is null)
                {
                    _connecting = new TaskCompletionSource<IBinder>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _connection = new Connection(this);
                    var intent = new Intent(_context, typeof(InferenceWorkerService));
                    if (!_context.BindService(intent, _connection, Bind.AutoCreate | Bind.Important))
                    {
                        _connecting = null;
                        throw new WorkerDiedException(Death(WorkPhase.Startup));
                    }
                }
                waiting = _connecting.Task;
            }
        }

        if (waiting is not null)
        {
            try { binder = await waiting.WaitAsync(ConnectWait, ct).ConfigureAwait(false); }
            catch (TimeoutException) { throw new WorkerDiedException(Death(WorkPhase.Startup)); }
        }

        var hello = await CallAsync(new() { [WorkerWire.Op] = WorkerWire.OpHello }, WorkPhase.Startup, ct).ConfigureAwait(false);
        var pid = WorkerWire.Int(hello, WorkerWire.Pid);
        lock (_gate) _pid = pid > 0 ? pid : _pid;
        hello.TryGetValue(WorkerWire.Model, out var model);
        return new WorkerHello(pid, WorkerWire.Long(hello, WorkerWire.StartedMs), model);
    }

    /// <summary>Load a model; completes when it is ready.</summary>
    public async Task LoadAsync(WorkerLoadRequest request, IProgress<LoadStep>? step, CancellationToken ct)
    {
        await ConnectAsync(ct).ConfigureAwait(false);
        lock (_gate) { _phase = WorkPhase.Load; _model = request.ModelId; }

        var map = new Dictionary<string, string>
        {
            [WorkerWire.Op] = WorkerWire.OpLoad,
            [WorkerWire.Model] = request.ModelId,
            [WorkerWire.Directory] = request.ModelDirectory,
            [WorkerWire.SkipHash] = request.SkipLoadHash ? "1" : "0",
        };
        Check(await CallAsync(map, WorkPhase.Load, ct).ConfigureAwait(false), WorkPhase.Load);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var status = WorkerWire.GetStatus(
                await CallAsync(new() { [WorkerWire.Op] = WorkerWire.OpStatus }, WorkPhase.Load, ct).ConfigureAwait(false),
                DateTimeOffset.UtcNow);

            if (status.Phase == BrainPhase.Ready && status.ModelId == request.ModelId)
            {
                lock (_gate) _phase = WorkPhase.Idle;
                return;
            }
            if (status.Phase is BrainPhase.Failed or BrainPhase.NoModel && status.LastFailure is { } failure)
            {
                lock (_gate) _phase = WorkPhase.None;
                throw new BrainWorkerException(failure);
            }

            step?.Report(status.Step == LoadStep.None ? LoadStep.LoadingWeights : status.Step);
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Write one answer, piece by piece.</summary>
    public async IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, [EnumeratorCancellation] CancellationToken ct)
    {
        lock (_gate) { _phase = WorkPhase.Prefill; _ask = job.AskId; }
        var start = new Dictionary<string, string>
        {
            [WorkerWire.Op] = WorkerWire.OpGenerate,
            [WorkerWire.Job] = job.AskId,
            [WorkerWire.MaxTokens] = WorkerWire.I(job.MaxTokens),
            [WorkerWire.Spoken] = job.Spoken ? "1" : "0",
            [WorkerWire.DeadlineMs] = WorkerWire.L(job.Deadline.ToUnixTimeMilliseconds()),
        };
        if (job.RetrievalQuery is not null) start[WorkerWire.Retrieval] = job.RetrievalQuery;
        WorkerWire.PutMessages(start, job.Messages);
        Check(await CallAsync(start, WorkPhase.Prefill, ct).ConfigureAwait(false), WorkPhase.Prefill);

        var since = 0;
        try
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    try { await CallAsync(new() { [WorkerWire.Op] = WorkerWire.OpCancel, [WorkerWire.Job] = job.AskId },
                                          WorkPhase.Decode, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* it may already be gone */ }
                    ct.ThrowIfCancellationRequested();
                }

                var poll = await CallAsync(new()
                {
                    [WorkerWire.Op] = WorkerWire.OpPoll,
                    [WorkerWire.Job] = job.AskId,
                    [WorkerWire.Since] = WorkerWire.I(since),
                    [WorkerWire.WaitMs] = "250",
                }, since == 0 ? WorkPhase.Prefill : WorkPhase.Decode, CancellationToken.None).ConfigureAwait(false);
                Check(poll, WorkPhase.Decode);

                poll.TryGetValue(WorkerWire.Text, out var text);
                if (!string.IsNullOrEmpty(text))
                {
                    since += text.Length;
                    lock (_gate) _phase = WorkPhase.Decode;
                    yield return new BrainChunk(text);
                }

                if (WorkerWire.Flag(poll, WorkerWire.Final))
                {
                    if (WorkerWire.GetFailure(poll) is { } failure)
                    {
                        if (failure.Kind == FailureKind.Cancelled) ct.ThrowIfCancellationRequested();
                        throw new BrainWorkerException(failure);
                    }
                    yield return new BrainChunk(string.Empty, Final: true, Timing: WorkerWire.GetTiming(poll));
                    yield break;
                }
            }
        }
        finally
        {
            lock (_gate) { _phase = WorkPhase.Idle; _ask = null; }
        }
    }

    /// <summary>Let the model go.</summary>
    public async Task ReleaseAsync(CancellationToken ct)
    {
        if (!IsConnected) return;
        await CallAsync(new() { [WorkerWire.Op] = WorkerWire.OpRelease }, WorkPhase.Idle, ct).ConfigureAwait(false);
        lock (_gate) _model = null;
    }

    /// <summary>The worker's size and the phone's memory, or null when it is not reachable.</summary>
    public async Task<WorkerHeartbeat?> HeartbeatAsync(CancellationToken ct)
    {
        if (!IsConnected) return null;
        var map = await CallAsync(new() { [WorkerWire.Op] = WorkerWire.OpStatus }, WorkPhase.Idle, ct).ConfigureAwait(false);
        var status = WorkerWire.GetStatus(map, DateTimeOffset.UtcNow);
        return new WorkerHeartbeat(
            WorkerWire.Int(map, WorkerWire.Pid), WorkerWire.Long(map, WorkerWire.RssKb),
            WorkerWire.Long(map, WorkerWire.MemAvailKb), WorkerWire.Long(map, WorkerWire.MemTotalKb),
            WorkerWire.Int(map, WorkerWire.Trim), status.Phase, status.ModelId, DateTimeOffset.UtcNow);
    }

    /// <summary>End the worker process (it is ours; the same uid may kill it).</summary>
    public void Kill()
    {
        int? pid;
        lock (_gate) pid = _pid;
        if (pid is { } p and > 0)
        {
            _log($"ending the inference process {p}");
            global::Android.OS.Process.KillProcess(p);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Connection? connection;
        lock (_gate)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            connection = _connection;
            _connection = null;
            _binder = null;
        }
        if (connection is not null)
        {
            try { _context.UnbindService(connection); } catch { /* already unbound */ }
        }
        return ValueTask.CompletedTask;
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static void Check(IReadOnlyDictionary<string, string> reply, WorkPhase phase)
    {
        if (reply.TryGetValue(WorkerWire.Ok, out var ok) && ok == "1") return;
        var failure = WorkerWire.GetFailure(reply) ?? new Failure(FailureKind.ManagedError, Certainty.Certain, phase, null,
            "worker-error", reply.TryGetValue(WorkerWire.Error, out var e) ? e : "the worker refused", DateTimeOffset.UtcNow);
        throw new BrainWorkerException(failure);
    }

    private Task<IReadOnlyDictionary<string, string>> CallAsync(
        Dictionary<string, string> request, WorkPhase phase, CancellationToken ct) => Task.Run(() =>
    {
        IBinder? binder;
        lock (_gate) binder = _binder;
        if (binder is null || !binder.IsBinderAlive) throw new WorkerDiedException(Death(phase));

        using var data = Parcel.Obtain();
        using var reply = Parcel.Obtain();
        try
        {
            data.WriteInterfaceToken(WorkerWire.Descriptor);
            WorkerParcel.Write(data, request);
            if (!binder.Transact(WorkerWire.TransactCall, data, reply, 0))
                throw new WorkerDiedException(Death(phase));
            reply.ReadException();
            return WorkerParcel.Read(reply);
        }
        catch (DeadObjectException)
        {
            throw new WorkerDiedException(Death(phase));
        }
        finally
        {
            data.Recycle();
            reply.Recycle();
        }
    }, ct);

    private WorkerDeath Death(WorkPhase fallback)
    {
        lock (_gate)
            return new WorkerDeath(_pid, _phase == WorkPhase.None ? fallback : _phase, _model, _ask, DateTimeOffset.UtcNow);
    }

    private void OnConnected(IBinder binder)
    {
        TaskCompletionSource<IBinder>? waiting;
        lock (_gate) { _binder = binder; waiting = _connecting; _connecting = null; }
        try { binder.LinkToDeath(new DeathWatch(this), 0); }
        catch (RemoteException) { /* already dead; the next call says so */ }
        waiting?.TrySetResult(binder);
    }

    private void OnDied()
    {
        WorkerDeath death;
        lock (_gate)
        {
            // ONCE PER DEATH. The binder's death notice and the connection's
            // disconnect both arrive for the same event.
            if (_binder is null) return;
            death = new WorkerDeath(_pid, _phase, _model, _ask, DateTimeOffset.UtcNow);
            _binder = null;
            _phase = WorkPhase.None;
        }
        _log($"the inference process died (pid {death.Pid}, while {death.Phase})");
        try { Died?.Invoke(this, death); } catch { /* the supervisor must not throw here */ }
    }

    private sealed class Connection(WorkerConnection owner) : Java.Lang.Object, IServiceConnection
    {
        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            if (service is not null) owner.OnConnected(service);
        }

        public void OnServiceDisconnected(ComponentName? name) => owner.OnDied();
    }

    private sealed class DeathWatch(WorkerConnection owner) : Java.Lang.Object, IBinderDeathRecipient
    {
        public void BinderDied() => owner.OnDied();
    }
}
