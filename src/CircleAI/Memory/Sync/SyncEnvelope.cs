// SyncEnvelope.cs
//
// Three envelope kinds drive the convergence protocol:
//
//   Announce  — "I am node N. For each entity type, my highest version is V."
//   Request   — "I see you have version > mine for type T since version X.
//                Send me everything you have for T newer than X."
//   Push      — "Here are entries you asked for (or that I want you to apply)."
//
// The protocol is deliberately simple. Two peers exchange Announce; whoever
// is behind sends a Request; the other replies with a Push; the receiver
// upserts. Repeating Announce always converges.

using System.Collections.Generic;

namespace CircleAI.Memory.Sync;

/// <summary>Kind of sync envelope.</summary>
public enum SyncEnvelopeKind
{
    /// <summary>Broadcast of the sender's per-entity-type high-watermark versions.</summary>
    Announce,

    /// <summary>Reply to an Announce asking for entries newer than a known version.</summary>
    Request,

    /// <summary>Unsolicited or replied delivery of syncable entries.</summary>
    Push,
}

/// <summary>
/// Per-entity-type high-watermark — used in Announce/Request payloads.
/// </summary>
public sealed record StateVectorEntry(string EntityType, long MaxKnownVersion);

/// <summary>
/// Reply-side request item — "send me entries of <see cref="EntityType"/>
/// strictly newer than <see cref="SinceVersion"/>".
/// </summary>
public sealed record RequestItem(string EntityType, long SinceVersion);

/// <summary>
/// A sync envelope — the message unit that crosses the channel.
/// </summary>
public sealed record SyncEnvelope(
    SyncEnvelopeKind Kind,
    string FromNodeId,
    IReadOnlyList<StateVectorEntry>? StateVector,
    IReadOnlyList<RequestItem>? Requests,
    IReadOnlyList<SyncableEntry>? Entries);
