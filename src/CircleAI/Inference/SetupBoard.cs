// SetupBoard.cs
//
// EVERY MODEL ON ITS WAY IN, IN ONE PLACE, WHOEVER FETCHES IT (Step 4: "apps see download progress").
//
// Four paths pull models into the service - the background fetch, setup started from an app, the
// abilities row's Turn on, the voice and ears fetched on first use - and only the second could
// report, one line at a time, to the app that started it. Every one of them goes through
// BundleModelLoader and InFlightDownloads, so that is where the board is fed: once per real
// download, whichever path asked. The planners add what is still to come (Queue), the brain's
// supervisor what is being primed (through the readiness tracker), and an app polls the result
// over the existing SetupProgress verb - a Binder link cannot push.
//
// PER PROCESS, LIKE InFlightDownloads, because the downloads are. On Android every pull path runs
// in the service's main process, which is also the one the link answers from.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>What the board says about the whole of setup, for the one-line summary row.</summary>
/// <param name="Settled">How many items are ready or failed.</param>
/// <param name="Count">How many items there are.</param>
/// <param name="Title">The item being worked on now, or the last one.</param>
/// <param name="Fraction">0..1 across everything, weighted by bytes.</param>
/// <param name="Remaining">Time left for everything still to download, at the live rate; zero when unknown.</param>
/// <param name="Checking">The current item moves no bytes: it is being verified or primed.</param>
/// <param name="Done">Nothing is left to happen by itself.</param>
public sealed record SetupBoardSummary(
    int Settled, int Count, string Title, double Fraction, TimeSpan Remaining, bool Checking, bool Done);

/// <summary>The process-wide record of every model being set up: queued, arriving, primed, ready.</summary>
public static class SetupBoard
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, SetupItemStatus> Items = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> Order = [];
    private static long _version;

    /// <summary>
    /// The words for a model that was not planned with a title - "the ears", not the file name.
    /// Set by the composition root (FirstRun's titles); unset, the id is shown.
    /// </summary>
    public static Func<string, string?>? TitleFor { get; set; }

    /// <summary>The clock; a test sets its own.</summary>
    public static TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>Goes up with every change, so a caller can tell nothing moved.</summary>
    public static long Version
    {
        get { lock (Gate) return _version; }
    }

    /// <summary>Every item, in the order it first appeared.</summary>
    public static IReadOnlyList<SetupItemStatus> Snapshot()
    {
        lock (Gate) return Order.Select(id => Items[id]).ToList();
    }

    /// <summary>One item, or null when the board has never heard of it.</summary>
    public static SetupItemStatus? Of(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;
        lock (Gate) return Items.GetValueOrDefault(modelId);
    }

    /// <summary>
    /// A model the plan will fetch, waiting its turn. One already moving is left as it is; one
    /// that failed or waited for a network goes back in line with its attempts kept.
    /// </summary>
    public static void Queue(string modelId, string? title, long bytesTotal, long bytesDone = 0)
    {
        Update(modelId, title, current =>
        {
            if (current is { State: SetupItemState.Downloading or SetupItemState.Verifying
                                   or SetupItemState.Priming or SetupItemState.Healing })
                return current;
            return Make(current, modelId, title, SetupItemState.Queued, bytesDone, bytesTotal,
                        bytesDone > 0 ? "part of it is here already; it carries on" : string.Empty);
        });
    }

    /// <summary>A download's progress: bytes, rate and time left, or the check of what arrived.</summary>
    public static void Progress(string modelId, DownloadProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Phase == DownloadPhase.Complete) return; // Arrived says that

        Update(modelId, null, current =>
        {
            var total = progress.TotalBytes > 0 ? progress.TotalBytes : current?.BytesTotal ?? 0;
            var attempt = Math.Max(current?.Attempt ?? 1, Math.Max(1, progress.Attempt));
            var recovering = current is { State: SetupItemState.Failed or SetupItemState.Healing };
            var (state, detail) = progress.Phase switch
            {
                DownloadPhase.Retrying  => (SetupItemState.Healing, $"trying again (attempt {progress.Attempt})"),
                DownloadPhase.Verifying => (SetupItemState.Verifying, "checking every byte against its hash"),
                DownloadPhase.Resuming  => (recovering ? SetupItemState.Healing : SetupItemState.Downloading,
                                            "carrying on from where it stopped"),
                _                       => (recovering ? SetupItemState.Healing : SetupItemState.Downloading,
                                            recovering ? "fetching it again after a failure" : string.Empty),
            };
            var moving = state is SetupItemState.Downloading or SetupItemState.Healing;
            return new SetupItemStatus(
                modelId, current?.Title ?? Title(modelId, null), state,
                Math.Max(0, progress.BytesReceived), total,
                moving ? Math.Max(0, progress.BytesPerSecond) : 0,
                moving ? progress.EstimatedTimeRemaining : TimeSpan.Zero,
                detail, attempt,
                current is not null && current.State == state ? current.Since : Clock.GetUtcNow());
        });
    }

    /// <summary>
    /// Every byte is here and checked. A brain still has to be loaded and warmed before it is
    /// ready (<paramref name="primes"/>); anything else is ready now.
    /// </summary>
    public static void Arrived(string modelId, bool primes)
        => Update(modelId, null, current => Make(current, modelId, null,
            primes ? SetupItemState.Priming : SetupItemState.Ready,
            current?.BytesTotal ?? 0, current?.BytesTotal ?? 0,
            primes ? "on the phone; it is loaded and warmed when it becomes the brain" : string.Empty));

    /// <summary>It cannot download now: no network, or only one the person said not to use.</summary>
    public static void WaitingForNetwork(string modelId, string why)
        => Update(modelId, null, current => Make(current, modelId, null, SetupItemState.WaitingForNetwork,
            current?.BytesDone ?? 0, current?.BytesTotal ?? 0, why));

    /// <summary>It was stopped - cancelled, or the phone's time limit - and carries on later.</summary>
    public static void Paused(string modelId, string why)
        => Update(modelId, null, current => current is { Settled: true }
            ? current
            : Make(current, modelId, null, SetupItemState.Queued, current?.BytesDone ?? 0, current?.BytesTotal ?? 0, why));

    /// <summary>It could not be fetched or primed, and why.</summary>
    public static void Failed(string modelId, string why)
        => Update(modelId, null, current => Make(current, modelId, null, SetupItemState.Failed,
            current?.BytesDone ?? 0, current?.BytesTotal ?? 0, why));

    /// <summary>Carrying on after a failure.</summary>
    public static void Healing(string modelId, string why)
        => Update(modelId, null, current => Make(current, modelId, null, SetupItemState.Healing,
            current?.BytesDone ?? 0, current?.BytesTotal ?? 0, why) with
            {
                Attempt = current is { State: SetupItemState.Failed } ? current.Attempt + 1 : current?.Attempt ?? 1,
            });

    /// <summary>Being loaded and warmed; <paramref name="step"/> says which part.</summary>
    public static void Priming(string modelId, string step)
        => Update(modelId, null, current => Make(current, modelId, null, SetupItemState.Priming,
            current?.BytesTotal ?? 0, current?.BytesTotal ?? 0, step));

    /// <summary>Here, checked and primed.</summary>
    public static void Ready(string modelId, string detail = "")
        => Update(modelId, null, current => Make(current, modelId, null, SetupItemState.Ready,
            current?.BytesTotal ?? 0, current?.BytesTotal ?? 0, detail));

    /// <summary>The whole of setup in one line, or null when the board is empty.</summary>
    public static SetupBoardSummary? Summary()
    {
        var items = Snapshot();
        if (items.Count == 0) return null;

        var settled = items.Count(i => i.Settled);
        var current = items.FirstOrDefault(i => i.State is SetupItemState.Downloading or SetupItemState.Healing
                                                  or SetupItemState.Verifying or SetupItemState.Priming)
                      ?? items.FirstOrDefault(i => !i.Settled)
                      ?? items[^1];

        // BY BYTES, NOT BY COUNT: a 7 MB wake word and a 2 GB brain are not two halves of setup.
        var total = items.Sum(i => Math.Max(1, i.BytesTotal));
        var done = items.Sum(i => i.State == SetupItemState.Ready ? Math.Max(1, i.BytesTotal)
                                  : Math.Min(Math.Max(0, i.BytesDone), Math.Max(1, i.BytesTotal)));

        // EVERYTHING STILL TO COME AT TODAY'S RATE, because a person wants to know when the LAST
        // byte lands, not this one.
        var rate = items.Where(i => i.BytesPerSecond > 0).Select(i => i.BytesPerSecond).DefaultIfEmpty(0).Max();
        var owed = items.Where(i => !i.Settled && i.State != SetupItemState.Priming)
                        .Sum(i => Math.Max(0, i.BytesTotal - i.BytesDone));
        var remaining = rate > 0 && owed > 0 ? TimeSpan.FromSeconds(owed / rate) : TimeSpan.Zero;

        return new SetupBoardSummary(
            settled, items.Count, current.Title, Math.Clamp((double)done / total, 0, 1), remaining,
            Checking: current.State is SetupItemState.Verifying or SetupItemState.Priming,
            Done: settled == items.Count);
    }

    /// <summary>An <see cref="IProgress{T}"/> that tells the board, then <paramref name="inner"/>.</summary>
    public static IProgress<DownloadProgress> Relay(string modelId, IProgress<DownloadProgress>? inner)
        => new Tee(modelId, inner);

    /// <summary>Forgets everything. For tests.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Items.Clear();
            Order.Clear();
            _version++;
        }
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private static void Update(string modelId, string? title, Func<SetupItemStatus?, SetupItemStatus> change)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return;
        try
        {
            lock (Gate)
            {
                Items.TryGetValue(modelId, out var current);
                var next = change(current);
                if (!string.IsNullOrWhiteSpace(title) && next.Title != title) next = next with { Title = title! };
                if (current is not null && current == next) return;
                if (current is null) Order.Add(modelId);
                Items[modelId] = next;
                _version++;
            }
        }
        catch
        {
            // THE BOARD IS A REPORT, NEVER A REASON TO STOP A DOWNLOAD. A title hook that throws
            // must not take the bytes with it.
        }
    }

    private static SetupItemStatus Make(SetupItemStatus? current, string modelId, string? title, SetupItemState state,
                                        long done, long total, string detail)
        => new(modelId, current?.Title ?? Title(modelId, title), state, Math.Max(0, done), Math.Max(0, total),
               0, TimeSpan.Zero, detail ?? string.Empty, current?.Attempt ?? 1,
               current is not null && current.State == state ? current.Since : Clock.GetUtcNow());

    private static string Title(string modelId, string? title)
    {
        if (!string.IsNullOrWhiteSpace(title)) return title!;
        try { return TitleFor?.Invoke(modelId) is { Length: > 0 } named ? named : modelId; }
        catch { return modelId; }
    }

    private sealed class Tee(string modelId, IProgress<DownloadProgress>? inner) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            Progress(modelId, value);
            inner?.Report(value);
        }
    }
}
