// WorkerTypes.cs
//
// The small facts that cross between the service and its inference process, and the
// one exception that says "it died" rather than "it refused".

using CircleAI.Assistant.Lifecycle;

namespace CircleAI.Service.Droid.Inference;

/// <summary>What the worker said when it was reached.</summary>
public sealed record WorkerHello(int Pid, long StartedAtUnixMs, string? LoadedModelId);

/// <summary>Load this model from this store.</summary>
/// <param name="ModelId">The model.</param>
/// <param name="ModelDirectory">The model store's root.</param>
/// <param name="SkipLoadHash">The files were verified already; do not hash the weights again.</param>
/// <param name="Engine">Open it with this engine rather than the one its catalogue row names; null keeps the row's.</param>
/// <param name="KvMode">The working memory's bits the balance asks for (Step 4); null builds it as the engine does.</param>
/// <param name="ContextShare">The share of the context the balance asks for (Step 4); 1 is all of it.</param>
public sealed record WorkerLoadRequest(string ModelId, string ModelDirectory, bool SkipLoadHash, CircleAI.Core.ModelEngine? Engine = null,
                                       CircleAI.Core.KvCompressionMode? KvMode = null, double ContextShare = 1.0);

/// <summary>The worker process died: when, and what it was doing.</summary>
public sealed record WorkerDeath(int? Pid, WorkPhase Phase, string? ModelId, string? AskId, DateTimeOffset At);

/// <summary>The worker's size and the phone's memory, sampled while it lives.</summary>
public sealed record WorkerHeartbeat(int Pid, long VmRssKb, long MemAvailableKb, long MemTotalKb, int LastTrimLevel,
                                     BrainPhase Phase, string? ModelId, DateTimeOffset At);

/// <summary>The worker process died during a call.</summary>
public sealed class WorkerDiedException(WorkerDeath death) : Exception("the inference process died")
{
    /// <summary>When and doing what.</summary>
    public WorkerDeath Death { get; } = death;
}
