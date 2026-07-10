// sync.go
//
// Ports the CircleAI.Networking cross-device continuity primitives:
//   NetworkTypes.cs   -> SyncDeliveryMode (enum)
//   SyncDomainKeys    -> SyncDomainKeys well-known constants
//   SyncDelta.cs      -> SyncDelta (incl. optional SchedulingHint advisory)
//   SchedulingHint.cs -> SchedulingHint
//   ISyncChannel.cs   -> ISyncChannel
//
// The cross-device continuity primitive. Pushes memory/state deltas across
// whatever transport is available: gRPC over 5G, BLE mesh via a neighbour,
// DTN bundle arriving 6 hours later. App code is identical in every case.
//
// This is the primitive that makes Circle AI HER + JARVIS:
// memory follows the person, not the device.

package circleai

import (
	"context"
	"time"
)

// ---------------------------------------------------------------------------
// SyncDeliveryMode
// ---------------------------------------------------------------------------

// SyncDeliveryMode controls delivery guarantees for a SyncDelta.
type SyncDeliveryMode int

const (
	// SyncDeliveryModeBestEffort delivers on a best-effort basis.
	SyncDeliveryModeBestEffort SyncDeliveryMode = iota

	// SyncDeliveryModeGuaranteed retries until acknowledged.
	SyncDeliveryModeGuaranteed

	// SyncDeliveryModeUrgent delivers with the highest priority.
	SyncDeliveryModeUrgent
)

// ---------------------------------------------------------------------------
// SyncDomainKeys — well-known domain key constants
// ---------------------------------------------------------------------------

// SyncDomainKeys holds the well-known domain key strings for SyncDelta.
// Custom domains may use any string not in this list. Mirrors the C#
// CircleAI.Sync.SyncDomainKeys constants.
var SyncDomainKeys = struct {
	MemoryEpisodic string
	AffectState    string
	Persona        string
	Goals          string
	Skills         string
	Preferences    string
}{
	MemoryEpisodic: "memory.episodic",
	AffectState:    "affect.state",
	Persona:        "persona",
	Goals:          "goals",
	Skills:         "skills",
	Preferences:    "preferences",
}

// ---------------------------------------------------------------------------
// SchedulingHint — SchedulingHint.cs
// ---------------------------------------------------------------------------

// SchedulingHint is advisory scheduling information attached to a SyncDelta by
// the Circle AI reasoning layer. The Aether transport is free to disregard these
// hints — they are never a correctness constraint, only a performance advisory —
// but honouring them minimises unnecessary wakeups and battery drain on
// constrained devices. Ports the C# `sealed record SchedulingHint`.
type SchedulingHint struct {
	// PreferredPeerIds are device IDs strongly preferred as the first delivery
	// targets (typically recently-active or nearby peers, derived from affect
	// state or episodic memory). Empty means "no preference".
	PreferredPeerIds []string

	// SuggestedWindowUtc is the earliest UTC timestamp at which the transport
	// should attempt delivery. When nil, the delta should be forwarded
	// immediately; used to batch non-urgent syncs outside peak windows.
	SuggestedWindowUtc *time.Time

	// ConfidenceScore is how confident the AI layer is that these hints are
	// accurate, in [0.0, 1.0]. Below 0.5 is a weak advisory (apply normal
	// routing); above 0.8 is a strong advisory.
	ConfidenceScore float32
}

// ---------------------------------------------------------------------------
// SyncDelta
// ---------------------------------------------------------------------------

// SyncDelta is an incremental state change that must reach every device owned
// by OwnerID. This is the primitive that makes Circle AI cross-device
// continuous — HER + JARVIS memory following the person.
type SyncDelta struct {
	// OwnerID is the identity whose state this delta belongs to.
	OwnerID string

	// SourceDeviceID is the device that produced this delta.
	SourceDeviceID string

	// TargetDeviceID is the intended recipient device.
	// Empty string means broadcast to all devices owned by OwnerID.
	TargetDeviceID string

	// DomainKey categorises the state domain:
	// "memory.episodic" | "affect.state" | "persona" | custom.
	DomainKey string

	// Payload is the serialised state change.
	Payload []byte

	// Sequence is a monotonically increasing counter per owner+domain.
	Sequence int64

	// DeliveryMode controls delivery guarantees.
	DeliveryMode SyncDeliveryMode

	// TTL is the optional time-to-live. nil means no expiry.
	TTL *time.Duration

	// CreatedAt is the UTC time when this delta was created.
	CreatedAt time.Time

	// SchedulingHint is an optional AI-layer routing advisory. nil means no
	// hint (the C# `SchedulingHint? SchedulingHint = null` default). The
	// transport may honour or ignore it; it is never a correctness constraint.
	SchedulingHint *SchedulingHint
}

// ---------------------------------------------------------------------------
// ISyncChannel
// ---------------------------------------------------------------------------

// ISyncChannel is the cross-device continuity primitive.
// Pushes memory/state deltas across whatever transport is available.
type ISyncChannel interface {
	// PushDelta pushes a delta. The channel selects transport and handles retries.
	// Returns when accepted (not necessarily delivered for DTN/LocalStore).
	PushDelta(ctx context.Context, delta SyncDelta) error

	// ReceiveDeltas returns a channel of deltas for ownerID received after
	// afterSeq. The channel is closed when ctx is cancelled or the stream ends.
	// The errs channel receives at most one error then is closed.
	ReceiveDeltas(ctx context.Context, ownerID string, afterSeq int64) (<-chan SyncDelta, <-chan error)

	// GetLastSequence returns the highest sequence number seen for the given
	// owner and domain key.
	GetLastSequence(ctx context.Context, ownerID, domainKey string) (int64, error)
}
