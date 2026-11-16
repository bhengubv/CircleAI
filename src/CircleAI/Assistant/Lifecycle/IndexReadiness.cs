// IndexReadiness.cs
//
// THE INDEX, AS THE SERVICE SAYS IT. The owner, 2026-11-12: "user stays updated - "Until indexing is complete AI is
// functioning at xx%."" and "Later they get told "The more you use it the more it learns what the user needs"".
//
// The brain's index is built by the engine in the phone's idle time (IndexingQueue), in the process that holds the model.
// The engine records how far it has got on flash beside the index; this reads that record and says it as one more
// component of the service's state - the shape every app already reads - so the line a person is shown has one owner and
// crosses the link like any other row. No index being built or served: no component, and nothing said.

using System;
using CircleAI.Inference.Managed.Store;

namespace CircleAI.Assistant.Lifecycle;

/// <summary>The index's state and the line a person is shown for it.</summary>
public static class IndexReadiness
{
    /// <summary>The line for how far the indexing has got: its share while it runs, what it learns from once it is complete.</summary>
    public static string Line(IndexingStatus status) =>
        status.Complete ? ReadinessSentences.Learning : ReadinessSentences.Indexing(status.Percent);

    /// <summary>
    /// The index's component state, from the engine's record in <paramref name="storeDirectory"/>: loading, with its share
    /// and line, while the indexing runs; ready, with the later line, once it is complete. Null when no engine is indexing
    /// a model there.
    /// </summary>
    /// <param name="storeDirectory">Where the engine keeps its stores and records.</param>
    /// <param name="now">When this is said.</param>
    /// <param name="modelFile">
    /// The file of the model asked about - the brain whose index is being built while another answers (BrainInWaiting).
    /// Null asks about the model the engine loaded last.
    /// </param>
    public static ComponentState? Read(string? storeDirectory, DateTimeOffset now, string? modelFile = null)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory)) return null;
        var read = string.IsNullOrWhiteSpace(modelFile) ? IndexingQueue.ReadStatus(storeDirectory) : IndexingQueue.ReadStatus(storeDirectory, modelFile);
        if (read is not { Total: > 0 } status) return null;
        if (status.Complete && !status.Serves) return null;   // an index that failed its check serves nobody: nothing is said of it
        if (status.FromItsOwnFile) return null;               // no index is built or served - every neuron from the model's own file: nothing to learn from use
        return new ComponentState(
            ReadinessComponents.Index,
            status.Complete ? BrainPhase.Ready : BrainPhase.Loading,
            LoadStep.None, null, PauseReason.None,
            Line(status), null, now,
            status.Complete ? null : status.Fraction);
    }

    /// <summary>
    /// Says the index's state to <paramref name="tracker"/>, from the engine's record in <paramref name="storeDirectory"/>.
    /// Where the engine is indexing nothing there, a line said earlier is taken back, and nothing is said where none was.
    /// </summary>
    /// <param name="tracker">Where the service's state is said.</param>
    /// <param name="storeDirectory">Where the engine keeps its stores and records.</param>
    /// <param name="now">When this is said.</param>
    /// <param name="modelFile">The file of the model asked about; null asks about the model the engine loaded last.</param>
    public static void Report(IReadinessTracker tracker, string? storeDirectory, DateTimeOffset now, string? modelFile = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        if (Read(storeDirectory, now, modelFile) is { } index)
        {
            tracker.Report(index);
            return;
        }
        foreach (var component in tracker.Current.Components)
        {
            if (!string.Equals(component.Component, ReadinessComponents.Index, StringComparison.Ordinal)) continue;
            if (component.Detail.Length > 0) tracker.Report(ComponentState.Of(ReadinessComponents.Index, BrainPhase.Unknown, string.Empty, now));
            return;
        }
    }

    /// <summary>
    /// The quieter line under "Tap and talk" for a brain that is ready to answer: the index's line when the service
    /// reports one, else nothing. A brain that is not ready says its own state, not the index's.
    /// </summary>
    public static string Caption(ReadinessSnapshot? snapshot)
    {
        if (snapshot is not { Brain.Phase: BrainPhase.Ready }) return string.Empty;
        foreach (var component in snapshot.Components)
        {
            if (string.Equals(component.Component, ReadinessComponents.Index, StringComparison.Ordinal)) return component.Detail;
        }
        return string.Empty;
    }
}
