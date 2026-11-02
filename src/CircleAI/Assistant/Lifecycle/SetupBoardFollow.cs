// SetupBoardFollow.cs
//
// PRIMED BEFORE READY, SAID ON THE SAME BOARD AS THE DOWNLOAD (Step 4).
//
// A brain is not ready when its last byte lands: it is verified, mapped, warmed with one tiny
// answer and its system prompt kept as a primed start, and only then does it answer. The readiness
// tracker is where the supervisor (and the desktop host) already say each of those steps - one
// owner of "is the brain ready" - so the setup board follows it rather than being told twice: a
// model loading reads "priming", a model answering reads "ready", a model that failed and will try
// again reads "healing". The ears and the voice say theirs the same way when they carry a model id.

using System;
using CircleAI.Inference;

namespace CircleAI.Assistant.Lifecycle;

/// <summary>Mirrors the readiness of every component that names a model onto the setup board.</summary>
public static class SetupBoardFollow
{
    /// <summary>Follow <paramref name="tracker"/> from now on; dispose the result to stop.</summary>
    public static IDisposable Follow(IReadinessTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        EventHandler<ReadinessSnapshot> handler = (_, snapshot) => Apply(snapshot);
        tracker.Changed += handler;
        Apply(tracker.Current);
        return new Unfollow(() => tracker.Changed -= handler);
    }

    /// <summary>One snapshot onto the board. Exposed for tests.</summary>
    public static void Apply(ReadinessSnapshot snapshot)
    {
        if (snapshot is null) return;
        try
        {
            Mirror(snapshot.Brain);
            foreach (var component in snapshot.Components) Mirror(component);
        }
        catch
        {
            // The board is a report; a readiness change must never fail because of it.
        }
    }

    private static void Mirror(ComponentState state)
    {
        if (string.IsNullOrWhiteSpace(state.ModelId)) return;
        var id = state.ModelId!;
        switch (state.Phase)
        {
            case BrainPhase.Loading:
                SetupBoard.Priming(id, ReadinessSentences.For(state));
                break;
            case BrainPhase.Ready:
                SetupBoard.Ready(id, "loaded, warmed and answering");
                break;
            case BrainPhase.Failed or BrainPhase.Paused when state.RetryAfter is not null:
                SetupBoard.Healing(id, ReadinessSentences.For(state));
                break;
            case BrainPhase.Failed:
                SetupBoard.Failed(id, ReadinessSentences.For(state));
                break;
            // INSTALLED IS NOT NEWS FOR THE BOARD: a brain released to spare memory was primed
            // already, and one still waiting to be loaded keeps saying so.
        }
    }

    private sealed class Unfollow(Action stop) : IDisposable
    {
        private Action? _stop = stop;

        public void Dispose()
        {
            var stop = System.Threading.Interlocked.Exchange(ref _stop, null);
            stop?.Invoke();
        }
    }
}
