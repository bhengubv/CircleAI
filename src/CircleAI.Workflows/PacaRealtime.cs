// PacaRealtime.cs
//
// (3.3.0) Realtime fan-out for paca workflows: pub/sub with
// permission-aware rooms, query-invalidation events, collaborative
// document editing, agent activity feed. The Socket.IO / Valkey
// transport is host-supplied via IRealtimeBroadcaster.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) Realtime event union.</summary>
public abstract record RealtimePacaEvent(string ProjectId, DateTimeOffset At);

public sealed record TaskUpdatedEvent       (string ProjectId, DateTimeOffset At, int TaskNumber) : RealtimePacaEvent(ProjectId, At);
public sealed record QueryInvalidationEvent (string ProjectId, DateTimeOffset At, string QueryKey) : RealtimePacaEvent(ProjectId, At);
public sealed record DocCursorMoveEvent     (string ProjectId, DateTimeOffset At, string DocId, string MemberId, int CursorOffset) : RealtimePacaEvent(ProjectId, At);
public sealed record AgentActivityEvent     (string ProjectId, DateTimeOffset At, string AgentMemberId, string Action, string DetailJson) : RealtimePacaEvent(ProjectId, At);
public sealed record ConversationStepEvent  (string ProjectId, DateTimeOffset At, string ConversationId, ConversationStep Step) : RealtimePacaEvent(ProjectId, At);

/// <summary>(3.3.0) Host-supplied broadcaster (Socket.IO / Valkey Streams / etc.).</summary>
public interface IRealtimeBroadcaster
{
    ValueTask BroadcastAsync(string room, RealtimePacaEvent ev, CancellationToken ct = default);
}

/// <summary>(3.3.0) Permission check delegate — return true if the member may join the room.</summary>
public delegate ValueTask<bool> PermissionCheck(string memberId, string room, CancellationToken ct);

/// <summary>(3.3.0) Realtime hub: routes events into rooms, gates joins with a permission check.</summary>
public sealed class PacaRealtimeHub
{
    private readonly IRealtimeBroadcaster _broadcaster;
    private readonly PermissionCheck _permission;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _membersByRoom = new();

    public PacaRealtimeHub(IRealtimeBroadcaster broadcaster, PermissionCheck? permission = null)
    {
        _broadcaster = broadcaster ?? throw new ArgumentNullException(nameof(broadcaster));
        _permission  = permission ?? ((_, _, _) => ValueTask.FromResult(true));
    }

    /// <summary>(3.3.0) Member tries to join a room. Returns true if permission allowed.</summary>
    public async ValueTask<bool> JoinAsync(string memberId, string room, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(memberId);
        ArgumentNullException.ThrowIfNull(room);
        if (!await _permission(memberId, room, ct).ConfigureAwait(false)) return false;
        var members = _membersByRoom.GetOrAdd(room, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        members[memberId] = 1;
        return true;
    }

    public void Leave(string memberId, string room)
    {
        if (_membersByRoom.TryGetValue(room, out var bucket)) bucket.TryRemove(memberId, out _);
    }

    public IReadOnlyList<string> Members(string room)
        => _membersByRoom.TryGetValue(room, out var bucket) ? bucket.Keys.ToList() : Array.Empty<string>();

    /// <summary>(3.3.0) Publish an event to the project's main room.</summary>
    public ValueTask PublishAsync(RealtimePacaEvent ev, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ev);
        return _broadcaster.BroadcastAsync($"project:{ev.ProjectId}", ev, ct);
    }

    /// <summary>(3.3.0) Publish to a doc collaboration sub-room.</summary>
    public ValueTask PublishToDocAsync(string docId, RealtimePacaEvent ev, CancellationToken ct = default)
        => _broadcaster.BroadcastAsync($"doc:{docId}", ev, ct);
}

/// <summary>(3.3.0) Helper that maps known events to query-invalidation keys for client UIs.</summary>
public static class QueryInvalidation
{
    public static IReadOnlyList<string> KeysFor(RealtimePacaEvent ev) => ev switch
    {
        TaskUpdatedEvent t      => new[] { $"tasks/{t.ProjectId}", $"task/{t.ProjectId}/{t.TaskNumber}" },
        AgentActivityEvent a    => new[] { $"activity/{a.ProjectId}", $"agent/{a.AgentMemberId}" },
        ConversationStepEvent c => new[] { $"conversation/{c.ConversationId}", $"conversations/{c.ProjectId}" },
        DocCursorMoveEvent d    => new[] { $"doc/{d.DocId}/cursors" },
        QueryInvalidationEvent q => new[] { q.QueryKey },
        _                       => Array.Empty<string>(),
    };
}
