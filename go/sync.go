// sync.go
//
// SyncDeliveryMode, SyncDomainKeys, SyncDelta, ISyncChannel.
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
