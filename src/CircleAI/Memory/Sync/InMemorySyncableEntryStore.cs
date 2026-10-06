// InMemorySyncableEntryStore.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sync;

/// <summary>In-memory <see cref="ISyncableEntryStore"/>.</summary>
public sealed class InMemorySyncableEntryStore : ISyncableEntryStore
{
    // Keyed by (type, id) so writes are O(1).
    private readonly ConcurrentDictionary<(string Type, string Id), SyncableEntry> _entries
        = new();
    private readonly object _vectorLock = new();
    private readonly Dictionary<string, long> _maxVersionByType = new(StringComparer.Ordinal);

    public Task<bool> ApplyAsync(SyncableEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var key = (entry.EntityType, entry.EntityId);

        bool applied = false;
        _entries.AddOrUpdate(
            key,
            _ =>
            {
                applied = true;
                return entry;
            },
            (_, existing) =>
            {
                if (ShouldApply(existing, entry))
                {
                    applied = true;
                    return entry;
                }
                return existing;
            });

        if (applied)
        {
            lock (_vectorLock)
            {
                _maxVersionByType.TryGetValue(entry.EntityType, out var current);
                if (entry.Version > current)
                    _maxVersionByType[entry.EntityType] = entry.Version;
            }
        }
        return Task.FromResult(applied);
    }

    public Task<SyncableEntry?> GetAsync(string entityType, string entityId, CancellationToken ct = default) =>
        Task.FromResult(_entries.TryGetValue((entityType, entityId), out var e) ? e : null);

    public Task<IReadOnlyList<SyncableEntry>> GetSinceAsync(
        string entityType, long sinceVersion, CancellationToken ct = default)
    {
        IReadOnlyList<SyncableEntry> result = _entries.Values
            .Where(e => string.Equals(e.EntityType, entityType, StringComparison.Ordinal)
                     && e.Version > sinceVersion)
            .OrderBy(e => e.Version)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<StateVectorEntry>> GetStateVectorAsync(CancellationToken ct = default)
    {
        lock (_vectorLock)
        {
            IReadOnlyList<StateVectorEntry> vector = _maxVersionByType
                .Select(kv => new StateVectorEntry(kv.Key, kv.Value))
                .OrderBy(e => e.EntityType, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(vector);
        }
    }

    /// <summary>
    /// Apply rule: higher Version wins; on tie, higher ContentHash (string
    /// compare) wins; tombstone replaces a non-tombstone of equal version.
    /// </summary>
    private static bool ShouldApply(SyncableEntry existing, SyncableEntry incoming)
    {
        if (incoming.Version > existing.Version) return true;
        if (incoming.Version < existing.Version) return false;
        // Equal versions — tombstone-of-non-tombstone wins.
        if (incoming.IsTombstone && !existing.IsTombstone) return true;
        if (!incoming.IsTombstone && existing.IsTombstone) return false;
        // Same tombstone state, same version — content hash tiebreaker.
        return string.CompareOrdinal(incoming.ContentHash, existing.ContentHash) > 0;
    }
}
