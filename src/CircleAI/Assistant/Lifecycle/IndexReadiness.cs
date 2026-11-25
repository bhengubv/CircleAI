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
    /// <param name="offCharger">
    /// Whether the phone is off its charger. While the index is unfinished this says the work is
    /// paused for charge (<see cref="PauseReason.WaitingForCharge"/>), so a screen can say it
    /// carries on while charging rather than show a bar that is not moving.
    /// </param>
    public static ComponentState? Read(string? storeDirectory, DateTimeOffset now, string? modelFile = null, bool offCharger = false)
    {
        if (string.IsNullOrWhiteSpace(storeDirectory)) return null;
        var read = string.IsNullOrWhiteSpace(modelFile) ? IndexingQueue.ReadStatus(storeDirectory) : IndexingQueue.ReadStatus(storeDirectory, modelFile);
        if (read is not { Total: > 0 } status) return null;
        if (status.Complete && !status.Serves) return null;   // an index that failed its check serves nobody: nothing is said of it
        if (status.FromItsOwnFile) return null;               // no index is built or served - every neuron from the model's own file: nothing to learn from use

        // OFF ITS CHARGER IT BUILDS NOTHING (IndexingWhen): while the work is unfinished and the
        // phone is off the cable the share is held where it is, so the component says it waits for
        // charge - and a screen says "it carries on while charging" instead of showing a bar that
        // is not moving and reads as stuck. A complete index waits for nothing.
        var pause = !status.Complete && offCharger ? PauseReason.WaitingForCharge : PauseReason.None;
        return new ComponentState(
            ReadinessComponents.Index,
            status.Complete ? BrainPhase.Ready : BrainPhase.Loading,
            LoadStep.None, null, pause,
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
    /// <param name="offCharger">Whether the phone is off its charger, so an unfinished index says it waits for charge.</param>
    public static void Report(IReadinessTracker tracker, string? storeDirectory, DateTimeOffset now, string? modelFile = null, bool offCharger = false)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        if (Read(storeDirectory, now, modelFile, offCharger) is { } index)
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

    /// <summary>The sentence shown while the optimise is paused for charge - one owner for the words.</summary>
    public const string HoldLine = "It carries on while the phone is charging.";

    /// <summary>
    /// The index's progress for a screen, from the "index" component of <paramref name="snapshot"/>: null when
    /// nothing is being built - no index component, or one that is complete (that is "learning", a quieter line
    /// elsewhere) or has been taken back. Only a build in progress has a bar to draw.
    /// </summary>
    /// <remarks>
    /// THE CLIENT HALF OF <see cref="Read"/>. Read puts the engine's record onto the snapshot as a component;
    /// this reads that one component back as the typed view a screen draws a bar from - so the share and the
    /// "waits for charge" state have one owner across the link rather than a parser on each side.
    /// </remarks>
    public static IndexProgress? Progress(ReadinessSnapshot? snapshot)
    {
        if (snapshot is null) return null;
        foreach (var component in snapshot.Components)
        {
            if (!string.Equals(component.Component, ReadinessComponents.Index, StringComparison.Ordinal)) continue;
            if (component.Phase != BrainPhase.Loading) return null;   // Ready is "learning"; Unknown is taken back
            var percent = component.Progress is { } fraction ? (int)Math.Clamp(Math.Round(fraction * 100), 0, 100) : 0;
            var onHold = component.Pause == PauseReason.WaitingForCharge;
            return new IndexProgress(percent, component.Detail, onHold, onHold ? HoldLine : null);
        }
        return null;
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
