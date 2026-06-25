// CompanionConversationSyncBridge.cs
//
// (Phase A2 — HER/Jarvis parity) Bridges live conversation state to the
// sync engine so a session that starts on the phone can be picked up on
// the laptop mid-stream.
//
// Each "conversation delta" is a strongly-typed snapshot of the active
// turn: session id, last user utterance, last assistant text-so-far, the
// timestamp, and whether the turn has completed. The receiving device's
// session handler can resume from the partial assistant text without
// losing context.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sync;

/// <summary>
/// (Phase A2) Wire-format payload of an in-flight conversation turn. The
/// EntityId is the SessionId so multiple sessions converge independently.
/// </summary>
/// <param name="SessionId">Stable identifier the originating device uses for this conversation.</param>
/// <param name="UserText">The latest user utterance for this turn (may be partial transcript).</param>
/// <param name="AssistantText">Assistant reply so far — empty until the model starts emitting tokens.</param>
/// <param name="IsTurnComplete">True once the turn finished; false during streaming.</param>
/// <param name="StartedAtUtc">When the originating device started the turn.</param>
/// <param name="UpdatedAtUtc">When this delta was authored.</param>
public sealed record ConversationStateDelta(
    string         SessionId,
    string         UserText,
    string         AssistantText,
    bool           IsTurnComplete,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc);

/// <summary>
/// (Phase A2) Bridges live <see cref="ConversationStateDelta"/> snapshots to
/// the existing <see cref="ICompanionStateSyncEngine"/> wire so any peer
/// device subscribing to the "ConversationState" entity type can mirror or
/// hand off the conversation.
/// </summary>
public sealed class CompanionConversationSyncBridge
{
    /// <summary>EntityType used on the wire for conversation-state entries.</summary>
    public const string EntityType = "ConversationState";

    private readonly ICompanionStateSyncEngine _engine;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public CompanionConversationSyncBridge(ICompanionStateSyncEngine engine)
        => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>
    /// Broadcast a conversation-state snapshot to peer devices. The
    /// receiving device's bridge subscribes via
    /// <see cref="ICompanionStateChannel"/> and routes the delta into its
    /// own <see cref="CompanionSession"/>-equivalent runtime.
    /// </summary>
    public Task PublishAsync(ConversationStateDelta delta, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (string.IsNullOrWhiteSpace(delta.SessionId))
            throw new ArgumentException("SessionId required", nameof(delta));
        var payload = JsonSerializer.Serialize(delta, JsonOpts);
        return _engine.WriteLocalAsync(
            EntityType, delta.SessionId, payload, isTombstone: false, ct);
    }

    /// <summary>
    /// Mark the session as ended so peers can clean up shadow state. Uses
    /// the sync-layer tombstone primitive — peers receive an empty payload.
    /// </summary>
    public Task TerminateAsync(string sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("sessionId required", nameof(sessionId));
        return _engine.WriteLocalAsync(EntityType, sessionId, payload: "", isTombstone: true, ct);
    }

    /// <summary>Decode a sync-layer entry back to a typed delta.</summary>
    public static ConversationStateDelta? TryDecode(SyncableEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsTombstone) return null;
        if (!string.Equals(entry.EntityType, EntityType, StringComparison.Ordinal)) return null;
        try { return JsonSerializer.Deserialize<ConversationStateDelta>(entry.Payload, JsonOpts); }
        catch (JsonException) { return null; }
    }
}
