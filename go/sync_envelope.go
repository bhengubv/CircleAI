// sync_envelope.go
//
// Ports CircleAI.Memory.Sync.SyncEnvelope (SyncEnvelope.cs) and
// CircleAI.Memory.Sync.SyncableEntry (SyncableEntry.cs).
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

package circleai

import "time"

// SyncEnvelopeKind is the kind of sync envelope.
type SyncEnvelopeKind int

const (
	// SyncEnvelopeAnnounce broadcasts the sender's per-entity-type
	// high-watermark versions.
	SyncEnvelopeAnnounce SyncEnvelopeKind = iota

	// SyncEnvelopeRequest replies to an Announce asking for entries newer than
	// a known version.
	SyncEnvelopeRequest

	// SyncEnvelopePush is an unsolicited or replied delivery of syncable entries.
	SyncEnvelopePush
)

// String returns the C# name of the envelope kind.
func (k SyncEnvelopeKind) String() string {
	switch k {
	case SyncEnvelopeAnnounce:
		return "Announce"
	case SyncEnvelopeRequest:
		return "Request"
	case SyncEnvelopePush:
		return "Push"
	default:
		return "Unknown"
	}
}

// StateVectorEntry is a per-entity-type high-watermark — used in
// Announce/Request payloads.
type StateVectorEntry struct {
	// EntityType is the logical type this watermark is for.
	EntityType string
	// MaxKnownVersion is the highest version the sender holds for EntityType.
	MaxKnownVersion int64
}

// RequestItem is a reply-side request item — "send me entries of EntityType
// strictly newer than SinceVersion".
type RequestItem struct {
	// EntityType is the logical type requested.
	EntityType string
	// SinceVersion is the exclusive lower bound; entries must be strictly newer.
	SinceVersion int64
}

// SyncEnvelope is the message unit that crosses the channel.
//
// StateVector is populated for Announce; Requests for Request; Entries for
// Push. The unused slices are nil for a given kind.
type SyncEnvelope struct {
	// Kind is the envelope kind.
	Kind SyncEnvelopeKind
	// FromNodeID identifies the node that sent this envelope.
	FromNodeID string
	// StateVector carries per-type high-watermarks (Announce only; else nil).
	StateVector []StateVectorEntry
	// Requests carries reply-side request items (Request only; else nil).
	Requests []RequestItem
	// Entries carries syncable entries (Push only; else nil).
	Entries []SyncableEntry
}

// SyncableEntry is a single syncable item — the smallest unit the engine moves
// between peers. Payload is opaque; type adapters serialise their own records
// into Payload and back.
//
// ContentHash is SHA-256 of the Payload — used as the tiebreaker when two peers
// happen to write the same Version (impossibly rare with HLC, but the system
// must still converge deterministically).
type SyncableEntry struct {
	// EntityType is the logical type — e.g. "PersonaState", "CoreMemory".
	EntityType string
	// EntityID is the identifier within the type — e.g. a user id.
	EntityID string
	// Version is the HLC-produced monotonic version stamp.
	Version int64
	// IsTombstone is true when this entry represents a deletion; Payload empty.
	IsTombstone bool
	// ContentHash is the SHA-256 hex of Payload — content tiebreaker on ties.
	ContentHash string
	// Payload is the opaque payload — type-specific JSON or any adapter string.
	Payload string
	// SourceNodeID is the node that authored this version (provenance).
	SourceNodeID string
	// AuthoredAt is the UTC wall-clock when authored (display, not ordering).
	AuthoredAt time.Time
}
