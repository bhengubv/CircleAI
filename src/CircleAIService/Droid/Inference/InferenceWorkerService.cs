// InferenceWorkerService.cs
//
// THE MODEL LIVES HERE, IN ITS OWN PROCESS (":inference").
//
// One process used to hold the link, the downloads, the wake word and the model, so one
// native abort in MNN - or the low-memory killer choosing the biggest process on a
// 3.87 GB phone, which is always the one holding the model - ended every caller's
// question, the download in flight and the microphone at once, and left nothing alive to
// say why. Now only this process dies. The service sees the binder die, classifies it,
// keeps the question in its queue, and starts this again.
//
// IT DOES ONE THING: load the model it is told to and write the answer it is asked for,
// through the same InProcessBrainWorker the desktop exe uses. Choosing the model,
// verdicts, readiness and the queue all stay in the main process, which outlives this.

using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;
using CircleAI.Core.Models;
using CircleAI.Hosting;
using CircleAI.Inference;

namespace CircleAI.Service.Droid.Inference;

/// <summary>Hosts the model in the ":inference" process; driven by <see cref="WorkerConnection"/>.</summary>
[Service(Name = "ai.circle.InferenceWorkerService", Process = ":inference", Exported = false)]
public sealed class InferenceWorkerService : global::Android.App.Service
{
    private const string Tag = "CircleAI.Worker";

    /// <summary>Builds the brain's options for a load. Set by ServiceApplication in this process.</summary>
    public static Func<WorkerLoadRequest, AIOptions>? OptionsFactory { get; set; }

    private static readonly object Gate = new();
    private static readonly long StartedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static InProcessBrainWorker? _brain;
    private static WorkerLoadRequest? _loaded;
    private static Job? _job;
    private static int _trim;

    private sealed class Job
    {
        public required string Id { get; init; }
        public readonly System.Text.StringBuilder Text = new();
        public readonly CancellationTokenSource Cts = new();
        public readonly SemaphoreSlim News = new(0, int.MaxValue);
        public volatile bool Final;
        public TurnTiming? Timing;
        public Failure? Failure;
    }

    /// <inheritdoc />
    public override IBinder OnBind(Intent? intent) => new WorkerBinder();

    /// <inheritdoc />
    public override void OnTrimMemory([GeneratedEnum] TrimMemory level)
    {
        Volatile.Write(ref _trim, (int)level);
        base.OnTrimMemory(level);
    }

    /// <inheritdoc />
    public override void OnDestroy()
    {
        InProcessBrainWorker? brain;
        lock (Gate) { brain = _brain; _brain = null; _loaded = null; }
        if (brain is not null) _ = brain.DisposeAsync().AsTask();
        base.OnDestroy();
    }

    private sealed class WorkerBinder : Binder
    {
        protected override bool OnTransact(int code, Parcel? data, Parcel? reply, int flags)
        {
            if (data is null || code != WorkerWire.TransactCall) return base.OnTransact(code, data, reply, flags);
            data.EnforceInterface(WorkerWire.Descriptor);

            // OUR OWN APP ONLY. The service is not exported, so nobody else can bind it;
            // this says so again where the work is done.
            if (Binder.CallingUid != global::Android.OS.Process.MyUid()) return false;

            IReadOnlyDictionary<string, string> result;
            try { result = Handle(WorkerParcel.Read(data)); }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn(Tag, "call failed: " + ex.GetType().Name);
                result = new Dictionary<string, string> { [WorkerWire.Ok] = "0", [WorkerWire.Error] = ex.GetType().Name };
            }

            reply?.WriteNoException();
            if (reply is not null) WorkerParcel.Write(reply, result);
            return true;
        }
    }

    private static IReadOnlyDictionary<string, string> Handle(IReadOnlyDictionary<string, string> req)
    {
        req.TryGetValue(WorkerWire.Op, out var op);
        var res = new Dictionary<string, string>(StringComparer.Ordinal) { [WorkerWire.Ok] = "1" };
        switch (op)
        {
            case WorkerWire.OpHello:
                res[WorkerWire.Pid] = WorkerWire.I(global::Android.OS.Process.MyPid());
                res[WorkerWire.StartedMs] = WorkerWire.L(StartedMs);
                lock (Gate) if (_loaded is not null && _brain?.ModelId is not null) res[WorkerWire.Model] = _loaded.ModelId;
                return res;

            case WorkerWire.OpLoad:
                Load(req);
                return res;

            case WorkerWire.OpStatus:
                InProcessBrainWorker? b;
                lock (Gate) b = _brain;
                WorkerWire.PutStatus(res, b?.Status ?? BrainWorkerStatus.NotStarted(DateTimeOffset.UtcNow)
                                              with { ProcessId = global::Android.OS.Process.MyPid() });
                Heartbeat(res);
                return res;

            case WorkerWire.OpGenerate:
                return Generate(req, res);

            case WorkerWire.OpPoll:
                return Poll(req, res);

            case WorkerWire.OpCancel:
                lock (Gate)
                    if (_job is { } j && req.TryGetValue(WorkerWire.Job, out var id) && id == j.Id)
                        try { j.Cts.Cancel(); } catch (ObjectDisposedException) { }
                return res;

            case WorkerWire.OpRelease:
                InProcessBrainWorker? rb;
                lock (Gate) rb = _brain;
                if (rb is not null) rb.ReleaseAsync(CancellationToken.None).GetAwaiter().GetResult();
                return res;

            default:
                res[WorkerWire.Ok] = "0";
                res[WorkerWire.Error] = "unknown op";
                return res;
        }
    }

    private static void Load(IReadOnlyDictionary<string, string> req)
    {
        req.TryGetValue(WorkerWire.Model, out var model);
        req.TryGetValue(WorkerWire.Directory, out var dir);
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(dir))
            throw new ArgumentException("a load needs a model and a directory");

        var request = new WorkerLoadRequest(model!, dir!, WorkerWire.Flag(req, WorkerWire.SkipHash));
        InProcessBrainWorker brain;
        InProcessBrainWorker? previous = null;
        lock (Gate)
        {
            if (_brain is not null && _loaded?.ModelId == request.ModelId)
            {
                brain = _brain;
            }
            else
            {
                previous = _brain;
                brain = _brain = new InProcessBrainWorker(ct => StartAsync(request, ct),
                                                          log: line => global::Android.Util.Log.Info(Tag, line));
                _loaded = request;
            }
        }

        // NEVER ON THE BINDER THREAD. A load is 13-23 s on the P30; the caller polls.
        _ = Task.Run(async () =>
        {
            if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
            try { await brain.EnsureReadyAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { global::Android.Util.Log.Warn(Tag, "load failed: " + ex.GetType().Name); }
        });
    }

    private static async Task<InProcessBrain> StartAsync(WorkerLoadRequest request, CancellationToken ct)
    {
        var factory = OptionsFactory ?? throw new InvalidOperationException("the inference process has no options");
        var options = factory(request);
        options.ModelId = request.ModelId;

        var registry = new ModelRegistryService();
        var loader = new BundleModelLoader(request.ModelDirectory, registry);
        var service = new AIService(
            options,
            modelLoader:      loader,
            generatorFactory: null,
            modelSelector:    DeviceAwareModelSelector.Preferring(null, registry),
            modelRegistry:    registry);
        await service.StartAsync(ct).ConfigureAwait(false);
        return new InProcessBrain(service, request.ModelId);
    }

    private static IReadOnlyDictionary<string, string> Generate(IReadOnlyDictionary<string, string> req, Dictionary<string, string> res)
    {
        InProcessBrainWorker? brain;
        Job job;
        lock (Gate)
        {
            brain = _brain;
            if (brain is null || brain.Status.Phase != BrainPhase.Ready)
            {
                res[WorkerWire.Ok] = "0";
                WorkerWire.PutFailure(res, new Failure(FailureKind.Unknown, Certainty.Certain, WorkPhase.Prefill,
                    _loaded?.ModelId, "not-ready", "the model is not loaded in the worker", DateTimeOffset.UtcNow));
                return res;
            }
            if (_job is { Final: false })
            {
                res[WorkerWire.Ok] = "0";
                WorkerWire.PutFailure(res, new Failure(FailureKind.Rejected, Certainty.Certain, WorkPhase.Prefill,
                    _loaded?.ModelId, "busy", "an answer is already being written", DateTimeOffset.UtcNow));
                return res;
            }
            job = _job = new Job { Id = req.TryGetValue(WorkerWire.Job, out var id) && id.Length > 0 ? id : Guid.NewGuid().ToString("N") };
        }

        req.TryGetValue(WorkerWire.Retrieval, out var retrieval);
        var deadlineMs = WorkerWire.Long(req, WorkerWire.DeadlineMs);
        var brainJob = new BrainJob(
            AskId: job.Id,
            Messages: WorkerWire.GetMessages(req),
            RetrievalQuery: retrieval,
            MaxTokens: Math.Max(1, WorkerWire.Int(req, WorkerWire.MaxTokens, 512)),
            Spoken: WorkerWire.Flag(req, WorkerWire.Spoken),
            Deadline: deadlineMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(deadlineMs) : DateTimeOffset.UtcNow.AddMinutes(2));

        _ = Task.Run(async () =>
        {
            try
            {
                await foreach (var chunk in brain.GenerateAsync(brainJob, job.Cts.Token).ConfigureAwait(false))
                {
                    lock (job.Text) job.Text.Append(chunk.Text);
                    if (chunk.Final) job.Timing = chunk.Timing;
                    job.News.Release();
                }
            }
            catch (BrainWorkerException ex) { job.Failure = ex.Failure; }
            catch (System.OperationCanceledException)
            {
                job.Failure = new Failure(FailureKind.Cancelled, Certainty.Certain, WorkPhase.Decode, brain.ModelId,
                    "cancelled", "the answer was cancelled", DateTimeOffset.UtcNow);
            }
            catch (Exception ex) { job.Failure = InProcessBrainWorker.Classify(ex, WorkPhase.Decode, brain.ModelId, DateTimeOffset.UtcNow); }
            finally
            {
                job.Final = true;
                job.News.Release();
            }
        });

        res[WorkerWire.Job] = job.Id;
        return res;
    }

    private static IReadOnlyDictionary<string, string> Poll(IReadOnlyDictionary<string, string> req, Dictionary<string, string> res)
    {
        Job? job;
        lock (Gate) job = _job;
        if (job is null || !req.TryGetValue(WorkerWire.Job, out var id) || id != job.Id)
        {
            res[WorkerWire.Ok] = "0";
            res[WorkerWire.Error] = "unknown job";
            return res;
        }

        var since = Math.Max(0, WorkerWire.Int(req, WorkerWire.Since));
        var wait = Math.Clamp(WorkerWire.Int(req, WorkerWire.WaitMs), 0, 250);

        // WAIT A MOMENT FOR NEWS, server side, so a poll is an answer rather than a spin.
        int length;
        lock (job.Text) length = job.Text.Length;
        if (!job.Final && length <= since && wait > 0) job.News.Wait(wait);

        string text;
        lock (job.Text)
        {
            var all = job.Text.ToString();
            text = since < all.Length ? all[since..] : string.Empty;
            length = all.Length;
        }

        res[WorkerWire.Text] = text;
        res[WorkerWire.Length] = WorkerWire.I(length);
        res[WorkerWire.Final] = job.Final ? "1" : "0";
        if (job.Final && job.Timing is { } t) WorkerWire.PutTiming(res, t);
        if (job.Final && job.Failure is { } f) WorkerWire.PutFailure(res, f);
        return res;
    }

    // WHAT THE SUPERVISOR NEEDS TO JUDGE A DEATH LATER: this process's size, the phone's
    // free memory, and the last trim level Android sent here.
    private static void Heartbeat(Dictionary<string, string> res)
    {
        res[WorkerWire.RssKb] = WorkerWire.L(ProcKb("/proc/self/status", "VmRSS:"));
        res[WorkerWire.MemAvailKb] = WorkerWire.L(ProcKb("/proc/meminfo", "MemAvailable:"));
        res[WorkerWire.MemTotalKb] = WorkerWire.L(ProcKb("/proc/meminfo", "MemTotal:"));
        res[WorkerWire.Trim] = WorkerWire.I(Volatile.Read(ref _trim));
    }

    internal static long ProcKb(string file, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!line.StartsWith(key, StringComparison.Ordinal)) continue;
                var digits = new string(line.AsSpan(key.Length).ToArray().Where(char.IsDigit).ToArray());
                return long.TryParse(digits, out var kb) ? kb : 0;
            }
        }
        catch { /* not readable here */ }
        return 0;
    }
}

/// <summary>A flat string map in a Parcel - the one format both ends of the worker binder use.</summary>
internal static class WorkerParcel
{
    public static IReadOnlyDictionary<string, string> Read(Parcel parcel)
    {
        var count = parcel.ReadInt();
        var map = new Dictionary<string, string>(Math.Max(0, count), StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = parcel.ReadString();
            var value = parcel.ReadString();
            if (key is not null) map[key] = value ?? string.Empty;
        }
        return map;
    }

    public static void Write(Parcel parcel, IReadOnlyDictionary<string, string> map)
    {
        parcel.WriteInt(map.Count);
        foreach (var (key, value) in map)
        {
            parcel.WriteString(key);
            parcel.WriteString(value);
        }
    }
}
