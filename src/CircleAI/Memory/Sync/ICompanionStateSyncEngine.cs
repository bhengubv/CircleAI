// ICompanionStateSyncEngine.cs

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sync;

/// <summary>
/// Engine that broadcasts local state vectors, fulfils peer Requests, and
/// applies inbound Push entries. Hosts call <see cref="StartAsync"/> once at
/// startup, then either rely on event-driven sync (handlers respond as
/// envelopes arrive) or trigger <see cref="SyncNowAsync"/> after notable
/// local writes to immediately propagate.
/// </summary>
public interface ICompanionStateSyncEngine : IAsyncDisposable
{
    /// <summary>Subscribes the engine to channel envelopes.</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Broadcasts the local state vector to all peers immediately.</summary>
    Task SyncNowAsync(CancellationToken ct = default);

    /// <summary>
    /// Convenience to apply a locally-authored entry: stamps it with a fresh
    /// HLC version, persists it to the local store, and (if started)
    /// broadcasts it via Push. Returns the resulting entry with its assigned
    /// Version.
    /// </summary>
    Task<SyncableEntry> WriteLocalAsync(
        string entityType, string entityId, string payload,
        bool isTombstone = false, CancellationToken ct = default);
}
