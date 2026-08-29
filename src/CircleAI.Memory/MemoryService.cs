// MemoryService.cs
//
// The memory an application actually holds.
//
// EVERYTHING UNTIL NOW HAS BEEN PIECES. A store, a log, a curve, a command -
// each one correct and none of them a memory an app has. On the phone there was
// a probe that made a folder, proved the loop and deleted it, which is a passing
// test rather than a product. This is the thing that gets held.
//
// IT IS BUILT FOR BEING KILLED, because that is the ordinary case rather than
// the exception. An app on a phone does not get to finish what it was doing:
// the system takes it for memory, the person swipes it away, the battery goes.
// So nothing here is held back for later. Atoms go to the log the moment they
// are recorded, and the wear that decides what has faded is written on the way
// out of every recall - not on a timer, and not on a lifecycle callback, both
// of which a force-stop walks straight past.
//
// ONE STORE, GUARDED. A SQLite connection is not thread-safe and an app will
// reach for its memory from the UI thread and a background one in the same
// second. Memory operations are not a parallel workload, so they are serialised
// rather than made clever.
//
// NO PLATFORM IN HERE. It takes a folder path and nothing else, so the same
// service is what Android holds, what iOS holds, and what a test holds.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory;

/// <summary>The memory an application holds and asks.</summary>
public interface IMemoryService
{
    /// <summary>What bears on what is about to happen.</summary>
    Task<RecallResult> RecallAsync(
        Situation situation, RecallBudget? budget = null, CancellationToken ct = default);

    /// <summary>Remember something, or correct something already remembered.</summary>
    Task RememberAsync(MemoryAtom atom, Guid? supersedes = null, CancellationToken ct = default);

    /// <summary>Read what was said and keep what is worth keeping.</summary>
    Task<LearnReport> LearnAsync(
        string wasSaid, string? subject = null, CancellationToken ct = default);

    /// <summary>Everything currently remembered, newest first.</summary>
    Task<IReadOnlyList<MemoryAtom>> AllAsync(int limit = 200, CancellationToken ct = default);

    /// <summary>How many things are remembered here.</summary>
    Task<int> CountAsync(CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class MemoryService : IMemoryService, IDisposable
{
    private readonly MemoryFolder _folder;
    private readonly MemorySync _sync;
    private readonly MemoryWear _wear;
    private readonly SqliteAtomStore _store;
    private readonly Recall _recall;
    private readonly AtomLearner _learner;

    // ONE AT A TIME. Not for throughput - a memory is asked a few times a minute
    // by a person - but because the alternative is a torn read on a connection
    // two threads are using, which fails rarely and unreproducibly.
    private readonly SemaphoreSlim _one = new(1, 1);
    private bool _disposed;

    /// <param name="folderPath">Where the memory lives. The app's own storage.</param>
    /// <param name="machine">
    /// What this device calls itself, or null to work it out. On a phone that
    /// means minting an id, because every Android device answers "localhost".
    /// </param>
    public MemoryService(string folderPath, string? machine = null)
    {
        _folder = new MemoryFolder(folderPath, machine);
        _folder.EnsureGitIgnore();

        _sync = new MemorySync(_folder);
        _wear = new MemoryWear(_folder);

        // ON DISK, NOT REPLAYED AT EVERY LAUNCH. Cold start is the common case
        // on a phone - the app is killed far more often than it is closed - so
        // the index is kept and only rebuilt when it is not there. Recording
        // goes through the log and the index together, so they cannot drift.
        var fresh = !System.IO.File.Exists(_folder.IndexPath);
        _store = new SqliteAtomStore(_folder.IndexConnectionString);
        if (fresh) _sync.RebuildAsync(_store).GetAwaiter().GetResult();

        _recall = new Recall(_store, _wear);
        _learner = new AtomLearner();
    }

    /// <summary>Where this memory lives.</summary>
    public string Path => _folder.Path;

    /// <summary>What this device calls itself.</summary>
    public string Machine => _folder.Machine;

    /// <summary>Whether keyword search is a real index or the LIKE floor.</summary>
    public bool FullTextAvailable => _store.FullTextAvailable;

    // ------------------------------------------------------------------
    // Asking
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<RecallResult> RecallAsync(
        Situation situation, RecallBudget? budget = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(situation);

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var result = await _recall.ForAsync(situation, budget, ct).ConfigureAwait(false);

            // WRITTEN NOW, NOT LATER. Recall is the only thing that changes
            // wear, and holding it back would mean a force-stop - which never
            // calls a lifecycle callback and is how a phone usually kills an
            // app - taking the session's familiarity with it. The file is a few
            // kilobytes and this costs single-digit milliseconds against a
            // recall that costs eighty.
            _wear.Flush();

            return result;
        }
        finally { _one.Release(); }
    }

    // ------------------------------------------------------------------
    // Remembering
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task RememberAsync(
        MemoryAtom atom, Guid? supersedes = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(atom);

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            // Straight through to the log, which is the durable half. Nothing
            // is queued, so nothing is lost when the app goes away.
            await _sync.RecordAsync(_store, atom, supersedes, ct).ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    /// <inheritdoc />
    public async Task<LearnReport> LearnAsync(
        string wasSaid, string? subject = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(wasSaid))
            return new LearnReport(0, Array.Empty<AtomCandidate>(),
                                   Array.Empty<AtomCandidate>(), Array.Empty<AtomCandidate>());

        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            var episode = new EpisodicMemoryEntry { UserText = wasSaid, AppContext = subject };

            return await _learner.LearnAsync(
                new[] { episode },
                (candidate, token) => _sync.RecordAsync(_store, candidate, ct: token),
                await _store.AllAsync(limit: 5000, ct: ct).ConfigureAwait(false),
                subject,
                ct).ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryAtom>> AllAsync(
        int limit = 200, CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await _store.AllAsync(limit: limit, ct: ct).ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await _store.CountAsync(ct).ConfigureAwait(false);
        }
        finally { _one.Release(); }
    }

    /// <summary>How reachable something is, and whether it has gone quiet.</summary>
    public double Reach(MemoryAtom atom) => _wear.Reach(atom, DateTimeOffset.UtcNow);

    // ------------------------------------------------------------------
    // Going away
    // ------------------------------------------------------------------

    /// <summary>
    /// Write anything outstanding, now.
    /// </summary>
    /// <remarks>
    /// Nothing should be outstanding - that is the point of the design - so
    /// this is a belt on top of braces, for a host that has a lifecycle
    /// callback and would rather use it. It must stay cheap enough to call
    /// synchronously while the system is waiting to kill the process.
    /// </remarks>
    public void Save() => _wear.Flush();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _wear.Flush();
        _store.Dispose();
        _one.Dispose();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);
}
