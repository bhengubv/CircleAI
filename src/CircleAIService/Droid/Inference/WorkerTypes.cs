// WorkerTypes.cs
//
// The small facts that cross between the service and its inference process, and the
// one exception that says "it died" rather than "it refused".

using CircleAI.Assistant.Lifecycle;
using CircleAI.Assistant.Requests;

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
/// <param name="ModelId">The brain it was told to load - the main slot's.</param>
/// <param name="Working">The brain answering the ask in flight, as the worker last said; null when it had not said (OPEN-GAPS E33).</param>
public sealed record WorkerDeath(int? Pid, WorkPhase Phase, string? ModelId, string? AskId, DateTimeOffset At,
                                 AnsweringBrain? Working = null);

/// <summary>The worker's size and the phone's memory, sampled while it lives - and what it is spending on the ask in flight.</summary>
/// <param name="CpuMs">The worker process's CPU time so far; -1 where it could not read it (OPEN-GAPS E33).</param>
/// <param name="Job">The ask the worker is still answering; null when none.</param>
/// <param name="OverdueMs">How far past its deadline that ask is; 0 when it is not.</param>
/// <param name="Working">The brain answering it, once its slot is chosen.</param>
public sealed record WorkerHeartbeat(int Pid, long VmRssKb, long MemAvailableKb, long MemTotalKb, int LastTrimLevel,
                                     BrainPhase Phase, string? ModelId, DateTimeOffset At,
                                     long CpuMs = -1, string? Job = null, long OverdueMs = 0, AnsweringBrain? Working = null);

/// <summary>The worker process died during a call.</summary>
public sealed class WorkerDiedException(WorkerDeath death) : Exception("the inference process died")
{
    /// <summary>When and doing what.</summary>
    public WorkerDeath Death { get; } = death;
}
