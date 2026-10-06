// SyncableEntry.cs
//
// The wire format. Every piece of companion state that crosses devices is
// wrapped in one of these. Payload is opaque JSON (or any string); type
// adapters serialise their own records into the Payload field and back.
//
// ContentHash is SHA-256 of the Payload — used as the tiebreaker when two
// peers happen to write the same Version (impossibly rare with HLC, but
// the system must still converge deterministically).

using System;

namespace CircleAI.Memory.Sync;

/// <summary>
/// A single syncable item — the smallest unit the engine moves between peers.
/// </summary>
/// <param name="EntityType">Logical type — e.g. "PersonaState", "CoreMemory", "DailyMemorySummary".</param>
/// <param name="EntityId">Identifier within the type — e.g. a user ID, a GUID-N format string.</param>
/// <param name="Version">HLC-produced monotonic version stamp.</param>
/// <param name="IsTombstone">True when this entry represents a deletion. Payload is empty in that case.</param>
/// <param name="ContentHash">SHA-256 hex of <see cref="Payload"/> — content tiebreaker when versions collide.</param>
/// <param name="Payload">Opaque payload — type-specific JSON or any string the adapter chose.</param>
/// <param name="SourceNodeId">Identifier of the node that authored this version (for debugging + provenance).</param>
/// <param name="AuthoredAt">UTC wall-clock when authored — for human-facing display, not for ordering.</param>
public sealed record SyncableEntry(
    string EntityType,
    string EntityId,
    long Version,
    bool IsTombstone,
    string ContentHash,
    string Payload,
    string SourceNodeId,
    DateTimeOffset AuthoredAt);
