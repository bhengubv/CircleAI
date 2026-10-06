// MemorySync.cs
//
// Turning three machines' logs into one local index.
//
// THE INDEX IS DISPOSABLE AND THE LOGS ARE NOT. Replay rebuilds the database
// from the text, which means a corrupt index, a schema change or a machine
// that has never seen the folder before all cost the same thing: a rebuild.
// Nothing about a memory depends on a file git cannot merge.
//
// SUPERSEDING IS RESOLVED HERE, not in the log. A log line can only point
// backwards at what it replaces; the forward pointer the index wants is worked
// out by walking the records in time order. Doing it during replay is also
// what makes a correction made on the Mac apply to a decision made on Windows -
// they are just two lines in one ordered stream.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory;

/// <summary>What a rebuild did.</summary>
/// <param name="Records">Lines read across every machine's log.</param>
/// <param name="Atoms">Atoms in the index afterwards, superseded ones included.</param>
/// <param name="Current">Atoms that are still the current answer.</param>
/// <param name="Machines">How many machines have contributed.</param>
public sealed record SyncReport(int Records, int Atoms, int Current, int Machines);

/// <summary>Keeps the local index in step with the logs.</summary>
public sealed class MemorySync
{
    private readonly MemoryFolder _folder;
    private readonly AtomLog _log;

    public MemorySync(MemoryFolder folder)
    {
        _folder = folder ?? throw new ArgumentNullException(nameof(folder));
        _log = new AtomLog(folder);
    }

    /// <summary>The log this machine writes to.</summary>
    public AtomLog Log => _log;

    /// <summary>
    /// Remember something: append it to the log, then put it in the index.
    /// </summary>
    /// <remarks>
    /// LOG FIRST. If the process dies between the two, the atom is still
    /// remembered and the next rebuild picks it up; the other order loses it
    /// while leaving the index looking healthy.
    /// </remarks>
    public async Task RecordAsync(
        IAtomStore store, MemoryAtom atom, Guid? supersedes = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(atom);

        // Index what the LOG says, not what the caller passed. The line is
        // stamped with this machine and normalised on the way out, and reading
        // it back is what makes "the index now" and "the index after a rebuild"
        // the same thing without two pieces of code having to agree.
        var stored = AtomLog.Rehydrate(_log.Append(atom, supersedes));

        if (supersedes is { } old)
            await store.SupersedeAsync(old, stored, ct).ConfigureAwait(false);
        else
            await store.AddAsync(stored, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Rebuild the index from every machine's log.
    /// </summary>
    /// <remarks>
    /// Safe to run at startup, after a pull, or whenever the index is
    /// suspected: it is idempotent, because an atom's identity comes from the
    /// log rather than from insertion order.
    /// </remarks>
    public async Task<SyncReport> RebuildAsync(IAtomStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        var replay = Replay();
        if (replay.Records == 0) return new SyncReport(0, 0, 0, 0);

        ct.ThrowIfCancellationRequested();

        var stored = 0;
        foreach (var atom in replay.Atoms)
        {
            await store.AddAsync(atom, ct).ConfigureAwait(false);
            stored++;
        }

        return new SyncReport(
            Records: replay.Records,
            Atoms: stored,
            Current: replay.Atoms.Count(a => a.IsCurrent),
            Machines: replay.Machines);
    }

    /// <summary>
    /// Every atom that is still an answer, from the logs alone.
    /// </summary>
    /// <remarks>
    /// NO INDEX INVOLVED. Writing to the memory never needed one - only reading
    /// it back does - and a capture that builds a whole database to find out
    /// what it already knows pays for a query it is not going to make. That
    /// cost grows with the log, on the path that runs most often.
    /// </remarks>
    public IReadOnlyList<MemoryAtom> Current() =>
        Replay().Atoms.Where(a => a.IsCurrent).ToList();

    // ------------------------------------------------------------------
    // Replay
    // ------------------------------------------------------------------

    /// <summary>
    /// Every machine's log, walked in time order into finished atoms.
    /// </summary>
    /// <remarks>
    /// TIME ORDER IS THE WHOLE TRICK. A correction always arrives after the
    /// thing it corrects however the files happened to be concatenated, which
    /// is what makes a correction made on the Mac apply to a decision made on
    /// Windows: two lines in one ordered stream, not two databases arguing.
    /// </remarks>
    private (int Records, int Machines, IReadOnlyList<MemoryAtom> Atoms) Replay()
    {
        var records = _log.ReadAll();
        if (records.Count == 0)
            return (0, 0, Array.Empty<MemoryAtom>());

        var atoms = new Dictionary<string, MemoryAtom>(StringComparer.OrdinalIgnoreCase);
        var supersededBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var corrections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var correctedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            if (record.Supersedes is { Length: > 0 } old)
            {
                supersededBy[old] = record.Id;

                // The count carries down the chain, so an atom corrected on
                // three different machines reads as corrected three times
                // rather than once each.
                corrections[record.Id] = corrections.GetValueOrDefault(old) + 1;
                correctedAt[record.Id] = AtomLog.Time(record.Recorded);
            }

            atoms[record.Id] = AtomLog.Rehydrate(record);
        }

        var finished = atoms.Select(pair => new MemoryAtom
        {
            Id               = pair.Value.Id,
            Kind             = pair.Value.Kind,
            Text             = pair.Value.Text,
            Subject          = pair.Value.Subject,
            Challenge        = pair.Value.Challenge,
            Outcome          = pair.Value.Outcome,
            SourceEpisode    = pair.Value.SourceEpisode,
            RecordedAtUtc    = pair.Value.RecordedAtUtc,
            Machine          = pair.Value.Machine,
            Verify           = pair.Value.Verify,
            Corrections      = corrections.GetValueOrDefault(pair.Key),
            LastCorrectedUtc = correctedAt.TryGetValue(pair.Key, out var c) ? c : null,
            SupersededBy     = supersededBy.TryGetValue(pair.Key, out var next) &&
                               Guid.TryParseExact(next, "N", out var g) ? g : null,
        }).ToList();

        return (records.Count,
                records.Select(r => r.Machine).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                finished);
    }
}
