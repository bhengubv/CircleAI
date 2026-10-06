// ISyncableEntryStore.cs

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sync;

/// <summary>
/// The seat the sync engine reads from and writes to. Implementations track
/// the local view of all known syncable entries plus their version stamps.
///
/// Apply rules — implementations MUST enforce these for convergence:
///   • Higher Version wins
///   • On tie (same Version), higher ContentHash (string compare) wins
///   • Tombstones replace any non-tombstone of equal-or-lower Version
/// </summary>
public interface ISyncableEntryStore
{
    /// <summary>
    /// Applies an incoming entry. Returns true when local state was actually
    /// updated (incoming was strictly newer / preferred). Returns false when
    /// the local entry was already at or beyond the incoming version.
    /// </summary>
    Task<bool> ApplyAsync(SyncableEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Returns the current entry for the given (type, id), or null when not
    /// known locally. Tombstones ARE returned — callers needing "is it
    /// deleted?" should check <see cref="SyncableEntry.IsTombstone"/>.
    /// </summary>
    Task<SyncableEntry?> GetAsync(string entityType, string entityId, CancellationToken ct = default);

    /// <summary>
    /// Returns every entry of the given type whose Version is strictly
    /// greater than <paramref name="sinceVersion"/>, ordered ascending by
    /// Version.
    /// </summary>
    Task<IReadOnlyList<SyncableEntry>> GetSinceAsync(
        string entityType, long sinceVersion, CancellationToken ct = default);

    /// <summary>
    /// Returns the highest known Version per entity type — the local node's
    /// state vector. Types with no entries are omitted.
    /// </summary>
    Task<IReadOnlyList<StateVectorEntry>> GetStateVectorAsync(CancellationToken ct = default);
}
