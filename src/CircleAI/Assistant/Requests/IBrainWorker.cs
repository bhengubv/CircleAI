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
public sealed record BrainJob(
    string AskId,
    IReadOnlyList<ChatMessage> Messages,
    string? RetrievalQuery,
    int MaxTokens,
    bool Spoken,
    DateTimeOffset Deadline);

/// <summary>A piece of an answer, or the end of one.</summary>
/// <param name="Text">The new text (may be empty on the final piece).</param>
/// <param name="Final">This is the last piece.</param>
/// <param name="Timing">Measured timing, on the final piece.</param>
public sealed record BrainChunk(string Text, bool Final = false, TurnTiming? Timing = null);

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
