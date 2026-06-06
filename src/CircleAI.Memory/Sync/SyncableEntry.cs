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
public sealed record SyncableEntry(
    /// <summary>Logical type — e.g. "PersonaState", "CoreMemory", "DailyMemorySummary".</summary>
    string EntityType,

    /// <summary>Identifier within the type — e.g. a user ID, a GUID-N format string.</summary>
    string EntityId,

    /// <summary>HLC-produced monotonic version stamp.</summary>
    long Version,

    /// <summary>True when this entry represents a deletion. Payload is empty in that case.</summary>
    bool IsTombstone,

    /// <summary>SHA-256 hex of <see cref="Payload"/> — content tiebreaker when versions collide.</summary>
    string ContentHash,

    /// <summary>Opaque payload — type-specific JSON or any string the adapter chose.</summary>
    string Payload,

    /// <summary>Identifier of the node that authored this version (for debugging + provenance).</summary>
    string SourceNodeId,

    /// <summary>UTC wall-clock when authored — for human-facing display, not for ordering.</summary>
    DateTimeOffset AuthoredAt);
