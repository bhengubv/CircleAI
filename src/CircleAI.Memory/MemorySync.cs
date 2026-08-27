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

        _log.Append(atom, supersedes);

        if (supersedes is { } old)
            await store.SupersedeAsync(old, atom, ct).ConfigureAwait(false);
        else
            await store.AddAsync(atom, ct).ConfigureAwait(false);
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

        var records = _log.ReadAll();
        if (records.Count == 0) return new SyncReport(0, 0, 0, 0);

        // Walk in time order, so a correction always arrives after the thing it
        // corrects however the files were concatenated.
        var atoms = new Dictionary<string, MemoryAtom>(StringComparer.OrdinalIgnoreCase);
        var supersededBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var corrections = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var correctedAt = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in records)
        {
            var when = AtomLog.Time(record.Recorded);

            if (record.Supersedes is { Length: > 0 } old)
            {
                supersededBy[old] = record.Id;

                // The count carries down the chain, so an atom corrected on
                // three different machines reads as corrected three times
                // rather than once each.
                corrections[record.Id] = corrections.GetValueOrDefault(old) + 1;
                correctedAt[record.Id] = when;
            }

            atoms[record.Id] = Rehydrate(record, when);
        }

        ct.ThrowIfCancellationRequested();

        var stored = 0;
        foreach (var (id, atom) in atoms)
        {
            var final = new MemoryAtom
            {
                Id               = atom.Id,
                Kind             = atom.Kind,
                Text             = atom.Text,
                Subject          = atom.Subject,
                Challenge        = atom.Challenge,
                Outcome          = atom.Outcome,
                SourceEpisode    = atom.SourceEpisode,
                RecordedAtUtc    = atom.RecordedAtUtc,
                Verify           = atom.Verify,
                Corrections      = corrections.GetValueOrDefault(id),
                LastCorrectedUtc = correctedAt.TryGetValue(id, out var c) ? c : null,
                SupersededBy     = supersededBy.TryGetValue(id, out var next) && Guid.TryParseExact(next, "N", out var g)
                                     ? g
                                     : null,
            };

            await store.AddAsync(final, ct).ConfigureAwait(false);
            stored++;
        }

        return new SyncReport(
            Records: records.Count,
            Atoms: stored,
            Current: atoms.Keys.Count(id => !supersededBy.ContainsKey(id)),
            Machines: records.Select(r => r.Machine).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// A log line back into an atom.
    /// </summary>
    /// <remarks>
    /// Unknown kinds and outcomes fall back rather than throw: a newer machine
    /// may have written a kind this build has never heard of, and the right
    /// response is to keep the text - which is the part a person wrote - not to
    /// refuse the whole line.
    /// </remarks>
    private static MemoryAtom Rehydrate(AtomRecord record, DateTimeOffset when) => new()
    {
        Id            = Guid.TryParseExact(record.Id, "N", out var id) ? id : Guid.NewGuid(),
        Kind          = Enum.TryParse<AtomKind>(record.Kind, ignoreCase: true, out var kind) ? kind : AtomKind.Decision,
        Text          = record.Text,
        Subject       = record.Subject,
        Challenge     = record.Challenge,
        Outcome       = Enum.TryParse<DecisionOutcome>(record.Outcome, ignoreCase: true, out var o) ? o : null,
        SourceEpisode = Guid.TryParseExact(record.SourceEpisode ?? "", "N", out var src) ? src : null,
        RecordedAtUtc = when,
        Verify        = record.Verify,
    };
}
