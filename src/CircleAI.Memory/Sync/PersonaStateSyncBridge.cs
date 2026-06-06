// PersonaStateSyncBridge.cs
//
// Demonstrator: wires the existing IPersonaStore through the sync engine so
// PersonaState updates on one device automatically appear on every paired
// device. This is the FIRST concrete user-visible type to ride the sync
// engine — the same bridge pattern extends to CoreMemory, PersonaDelta,
// MultimodalMemoryEntry in follow-up commits.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Memory.Sync;

/// <summary>
/// Bridges <see cref="IPersonaStore"/> ↔ <see cref="ICompanionStateSyncEngine"/>.
/// On <see cref="SaveAsync"/>, the persona is JSON-serialised and pushed.
/// </summary>
public sealed class PersonaStateSyncBridge
{
    /// <summary>EntityType used on the wire for PersonaState entries.</summary>
    public const string EntityType = "PersonaState";

    private readonly IPersonaStore _store;
    private readonly ICompanionStateSyncEngine _engine;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public PersonaStateSyncBridge(IPersonaStore store, ICompanionStateSyncEngine engine)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>
    /// Persists <paramref name="persona"/> locally AND broadcasts it via sync.
    /// </summary>
    public async Task SaveAsync(PersonaState persona, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);
        await _store.SaveAsync(persona, ct).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(persona, JsonOpts);
        await _engine.WriteLocalAsync(
            EntityType, persona.UserId, payload, isTombstone: false, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Decodes a <see cref="SyncableEntry"/> back into a <see cref="PersonaState"/>.
    /// Useful for handlers that subscribe to inbound updates.
    /// </summary>
    public static PersonaState? TryDecode(SyncableEntry entry)
    {
        if (entry.IsTombstone) return null;
        if (!string.Equals(entry.EntityType, EntityType, StringComparison.Ordinal)) return null;
        return JsonSerializer.Deserialize<PersonaState>(entry.Payload, JsonOpts);
    }
}
