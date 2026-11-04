// IBrainWorker.cs
//
// THE BOUNDARY BETWEEN DECIDING AND GENERATING.
//
// The request coordinator decides what to answer next and keeps the record; a brain
// worker loads a model and writes an answer. On Android the worker is a supervised
// second process (":inference"), so a native abort or the low-memory killer takes the
// model down and leaves the queue, the records and the link standing - and the death
// is seen, classified and reported instead of being the end of everything. On a
// desktop, and in tests, the worker runs in-process. The coordinator cannot tell
// which, and must not need to.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Inference;

namespace CircleAI.Assistant.Requests;

/// <summary>What the worker is doing.</summary>
/// <param name="Phase">Where the brain has got to.</param>
/// <param name="Step">Which part of a load.</param>
/// <param name="ModelId">The model it holds or is loading.</param>
/// <param name="LastFailure">The last classified failure, if the brain is Failed or Paused.</param>
/// <param name="RetryAfter">When a failed brain will be tried again by itself; null means not without being asked.</param>
/// <param name="ProcessId">The worker's process id, when it runs in its own process.</param>
/// <param name="Since">When it entered this phase.</param>
public sealed record BrainWorkerStatus(
    BrainPhase Phase,
    LoadStep Step,
    string? ModelId,
    Failure? LastFailure,
    TimeSpan? RetryAfter,
    int? ProcessId,
    DateTimeOffset Since)
{
    /// <summary>Not started.</summary>
    public static BrainWorkerStatus NotStarted(DateTimeOffset now) =>
        new(BrainPhase.Unknown, LoadStep.None, null, null, null, null, now);
}

/// <summary>One answer to write.</summary>
/// <param name="AskId">The ask it belongs to (for breadcrumbs and logs; never the text).</param>
/// <param name="Messages">The conversation to answer: prior turns, then the question. No system turn - the worker owns the persona.</param>
/// <param name="RetrievalQuery">What to search skills and memory for: the question alone, never the facts the caller attached.</param>
/// <param name="MaxTokens">The longest answer allowed.</param>
/// <param name="Spoken">The answer will be read aloud.</param>
/// <param name="Deadline">The latest moment the answer may still be written; generation stops there.</param>
/// <param name="Effort">How hard the model thinks first (<see cref="EffortRouter"/>); null leaves the brain's own default.</param>
/// <param name="ThinkingBudget">The most tokens the thought may take before it is closed; 0 sets none.</param>
/// <param name="Plain">The messages reach the model as given - no persona, no notes, no tools (<see cref="GenerationOptions.Plain"/>).</param>
/// <param name="Tools">
/// The caller allowed tools for this turn (<see cref="AskSubmission.Agentic"/>): the answer is the tool
/// turn's (<see cref="CircleAI.Hosting.ToolTurn"/>), written whole once the tools have run, not streamed.
/// </param>
public sealed record BrainJob(
    string AskId,
    IReadOnlyList<ChatMessage> Messages,
    string? RetrievalQuery,
    int MaxTokens,
    bool Spoken,
    DateTimeOffset Deadline,
    ReasoningEffort? Effort = null,
    int ThinkingBudget = 0,
    bool Plain = false,
    bool Tools = false);

/// <summary>A piece of an answer, or the end of one.</summary>
/// <param name="Text">The new text (may be empty on the final piece).</param>
/// <param name="Final">This is the last piece.</param>
/// <param name="Timing">Measured timing, on the final piece.</param>
public sealed record BrainChunk(string Text, bool Final = false, TurnTiming? Timing = null);

/// <summary>
/// The brain answering a turn: the main brain, or a specialist a router brought in beside it.
/// </summary>
/// <remarks>
/// THE MAIN SLOT IS NOT ALWAYS THE ONE WORKING (OPEN-GAPS E33). On the P30 on 2026-11-02 a picture
/// went to the C# 2B and its tower, and when the watchdog ended them the health event, the death line
/// and the strike rules all named Qwen3.5-0.8B-MNN - the brain in the main slot, idle. A host that
/// watches a turn from another process sets a death, a failure and a speed against this brain.
/// </remarks>
/// <param name="ModelId">The model.</param>
/// <param name="Slot">Where it answers from: <see cref="Main"/>, or the specialist's ("Vision specialist").</param>
public sealed record AnsweringBrain(string ModelId, string Slot)
{
    /// <summary>The main brain's slot.</summary>
    public const string Main = "main";

    /// <summary>The model and its slot, as a log line names them: "Ternary-Bonsai-2-2B (Vision specialist)".</summary>
    public override string ToString() => $"{ModelId} ({Slot})";
}

/// <summary>A worker that could not do what it was asked, with the classified reason.</summary>
public sealed class BrainWorkerException : Exception
{
    /// <summary>Why.</summary>
    public Failure Failure { get; }

    /// <summary>A failure, said plainly.</summary>
    public BrainWorkerException(Failure failure, Exception? inner = null)
        : base(failure.Detail, inner) => Failure = failure;
}

/// <summary>Loads a model and writes answers. One answer at a time.</summary>
public interface IBrainWorker : IAsyncDisposable
{
    /// <summary>What the worker is doing now. Never null.</summary>
    BrainWorkerStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes. Handlers must not throw or block.</summary>
    event EventHandler<BrainWorkerStatus>? StatusChanged;

    /// <summary>
    /// Raised when the worker learns which brain answers the job in flight - the main brain, or a
    /// specialist a router brought in beside it - and again whenever that changes. Handlers must not
    /// throw or block.
    /// </summary>
    /// <remarks>
    /// <see cref="BrainWorkerStatus.ModelId"/> IS THE MAIN SLOT'S, NOT THE ASK'S (OPEN-GAPS E33). On the P30
    /// on 2026-11-03 a picture went to SmolVLM, and the ask - and the link's row for it, "the model working
    /// on it" - named Qwen3.5-0.8B-MNN, idle in the main slot, from start to finish.
    /// </remarks>
    event EventHandler<AnsweringBrain>? AnsweringChanged;

    /// <summary>
    /// Make sure a model is loaded and warm, starting the worker if needed. Completes
    /// when the brain is Ready; throws <see cref="BrainWorkerException"/> when it cannot
    /// be (no model, a classified failure). Safe to call concurrently: every caller
    /// waits on the same load.
    /// </summary>
    Task EnsureReadyAsync(CancellationToken ct);

    /// <summary>
    /// Write one answer. Pieces arrive as they are written; the last has
    /// <see cref="BrainChunk.Final"/> and the timing. Throws
    /// <see cref="BrainWorkerException"/> with a classified failure when the worker
    /// dies or refuses. Cancelling stops the answer as soon as the runtime allows.
    /// </summary>
    IAsyncEnumerable<BrainChunk> GenerateAsync(BrainJob job, CancellationToken ct);

    /// <summary>Let the model go (memory pressure, idle). The next EnsureReadyAsync loads it again.</summary>
    Task ReleaseAsync(CancellationToken ct);
}
