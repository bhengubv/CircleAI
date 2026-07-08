// sync_engine.go
//
// Ports CircleAI.Memory.Sync.ICompanionStateSyncEngine
// (ICompanionStateSyncEngine.cs) and CircleAI.Memory.Sync.CompanionStateSyncEngine
// (CompanionStateSyncEngine.cs).
//
// Orchestration loop. Subscribes to the channel, responds to envelopes, and
// exposes WriteLocal + SyncNow for the host.
//
// Protocol — convergent in <= 2 round-trips per peer pair:
//   1. SyncNow        → broadcast Announce(localStateVector)
//   2. Peer receives Announce → diff against own vector → reply Request(missing)
//   3. We receive Request → gather entries via store.GetSince → Push
//   4. Peer receives Push → Apply for each entry
//   5. Peer broadcasts Announce again if anything applied — converges.
//
// All entries are content-hashed (SHA-256 of payload) at write time so the
// tiebreaker for equal-Version conflicts is deterministic everywhere.

package circleai

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"sync"
	"time"
)

// ICompanionStateSyncEngine broadcasts local state vectors, fulfils peer
// Requests, and applies inbound Push entries.
type ICompanionStateSyncEngine interface {
	// Start subscribes the engine to channel envelopes.
	Start(ctx context.Context) error

	// SyncNow broadcasts the local state vector to all peers immediately.
	SyncNow(ctx context.Context) error

	// WriteLocal stamps a locally-authored entry with a fresh HLC version,
	// persists it to the local store, and (if started) broadcasts it via Push.
	// Returns the resulting entry with its assigned Version.
	WriteLocal(ctx context.Context, entityType, entityID, payload string, isTombstone bool) (SyncableEntry, error)

	// Close unsubscribes the engine from the channel. Idempotent.
	Close() error
}

// CompanionStateSyncEngine is the default ICompanionStateSyncEngine.
type CompanionStateSyncEngine struct {
	channel   ICompanionStateChannel
	store     ISyncableEntryStore
	clock     *HybridLogicalClock
	wallClock func() time.Time

	mu          sync.Mutex
	unsubscribe func()
	subscribed  bool
	disposed    bool
}

// NewCompanionStateSyncEngine wires an engine over a channel, store, and HLC.
// wallClock is the source of AuthoredAt timestamps; pass nil for UTC wall clock.
func NewCompanionStateSyncEngine(
	channel ICompanionStateChannel,
	store ISyncableEntryStore,
	clock *HybridLogicalClock,
	wallClock func() time.Time,
) (*CompanionStateSyncEngine, error) {
	if channel == nil {
		return nil, errors.New("channel required")
	}
	if store == nil {
		return nil, errors.New("store required")
	}
	if clock == nil {
		return nil, errors.New("clock required")
	}
	wc := wallClock
	if wc == nil {
		wc = func() time.Time { return time.Now().UTC() }
	}
	return &CompanionStateSyncEngine{
		channel:   channel,
		store:     store,
		clock:     clock,
		wallClock: wc,
	}, nil
}

// Start subscribes the engine to channel envelopes. Idempotent.
func (e *CompanionStateSyncEngine) Start(_ context.Context) error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.disposed {
		return errors.New("engine disposed")
	}
	if e.subscribed {
		return nil
	}
	unsub, err := e.channel.Subscribe(e.handleEnvelope)
	if err != nil {
		return err
	}
	e.unsubscribe = unsub
	e.subscribed = true
	return nil
}

// SyncNow broadcasts the local state vector to all peers immediately.
func (e *CompanionStateSyncEngine) SyncNow(ctx context.Context) error {
	if err := e.checkDisposed(); err != nil {
		return err
	}
	vector, err := e.store.GetStateVector(ctx)
	if err != nil {
		return err
	}
	return e.channel.Send(ctx, SyncEnvelope{
		Kind:        SyncEnvelopeAnnounce,
		FromNodeID:  e.channel.LocalNodeID(),
		StateVector: vector,
	})
}

// WriteLocal stamps, persists, and (if started) pushes a locally-authored entry.
func (e *CompanionStateSyncEngine) WriteLocal(ctx context.Context, entityType, entityID, payload string, isTombstone bool) (SyncableEntry, error) {
	if err := e.checkDisposed(); err != nil {
		return SyncableEntry{}, err
	}
	if isBlank(entityType) {
		return SyncableEntry{}, errors.New("entityType required")
	}
	if isBlank(entityID) {
		return SyncableEntry{}, errors.New("entityId required")
	}

	entry := SyncableEntry{
		EntityType:   entityType,
		EntityID:     entityID,
		Version:      e.clock.Tick(),
		IsTombstone:  isTombstone,
		ContentHash:  computeSyncHash(payload),
		Payload:      payload,
		SourceNodeID: e.channel.LocalNodeID(),
		AuthoredAt:   e.wallClock(),
	}

	if _, err := e.store.Apply(ctx, entry); err != nil {
		return SyncableEntry{}, err
	}

	e.mu.Lock()
	subscribed := e.subscribed
	e.mu.Unlock()

	if subscribed {
		if err := e.channel.Send(ctx, SyncEnvelope{
			Kind:       SyncEnvelopePush,
			FromNodeID: e.channel.LocalNodeID(),
			Entries:    []SyncableEntry{entry},
		}); err != nil {
			return SyncableEntry{}, err
		}
	}
	return entry, nil
}

// Close unsubscribes the engine. Idempotent.
func (e *CompanionStateSyncEngine) Close() error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.disposed {
		return nil
	}
	e.disposed = true
	if e.unsubscribe != nil {
		e.unsubscribe()
		e.unsubscribe = nil
	}
	e.subscribed = false
	return nil
}

// ── Inbound envelope handling ────────────────────────────────────────────────

func (e *CompanionStateSyncEngine) handleEnvelope(ctx context.Context, envelope SyncEnvelope) error {
	switch envelope.Kind {
	case SyncEnvelopeAnnounce:
		return e.handleAnnounce(ctx, envelope)
	case SyncEnvelopeRequest:
		return e.handleRequest(ctx, envelope)
	case SyncEnvelopePush:
		return e.handlePush(ctx, envelope)
	}
	return nil
}

func (e *CompanionStateSyncEngine) handleAnnounce(ctx context.Context, envelope SyncEnvelope) error {
	if envelope.StateVector == nil {
		return nil
	}
	local, err := e.store.GetStateVector(ctx)
	if err != nil {
		return err
	}
	localMap := make(map[string]int64, len(local))
	for _, v := range local {
		localMap[v.EntityType] = v.MaxKnownVersion
	}

	requests := make([]RequestItem, 0)
	for _, peer := range envelope.StateVector {
		ourMax := localMap[peer.EntityType]
		if peer.MaxKnownVersion > ourMax {
			requests = append(requests, RequestItem{EntityType: peer.EntityType, SinceVersion: ourMax})
		}
	}
	if len(requests) == 0 {
		return nil
	}

	return e.channel.Send(ctx, SyncEnvelope{
		Kind:       SyncEnvelopeRequest,
		FromNodeID: e.channel.LocalNodeID(),
		Requests:   requests,
	})
}

func (e *CompanionStateSyncEngine) handleRequest(ctx context.Context, envelope SyncEnvelope) error {
	if len(envelope.Requests) == 0 {
		return nil
	}
	collected := make([]SyncableEntry, 0)
	for _, req := range envelope.Requests {
		newer, err := e.store.GetSince(ctx, req.EntityType, req.SinceVersion)
		if err != nil {
			return err
		}
		collected = append(collected, newer...)
	}
	if len(collected) == 0 {
		return nil
	}

	return e.channel.Send(ctx, SyncEnvelope{
		Kind:       SyncEnvelopePush,
		FromNodeID: e.channel.LocalNodeID(),
		Entries:    collected,
	})
}

func (e *CompanionStateSyncEngine) handlePush(ctx context.Context, envelope SyncEnvelope) error {
	if envelope.Entries == nil {
		return nil
	}
	anyApplied := false
	for _, entry := range envelope.Entries {
		e.clock.Observe(entry.Version)
		applied, err := e.store.Apply(ctx, entry)
		if err != nil {
			return err
		}
		anyApplied = anyApplied || applied
	}
	// If anything applied, re-announce so other peers can converge too.
	if anyApplied {
		return e.SyncNow(ctx)
	}
	return nil
}

// ── Helpers ──────────────────────────────────────────────────────────────────

func (e *CompanionStateSyncEngine) checkDisposed() error {
	e.mu.Lock()
	defer e.mu.Unlock()
	if e.disposed {
		return errors.New("engine disposed")
	}
	return nil
}

// computeSyncHash returns the lowercase SHA-256 hex of payload's UTF-8 bytes.
func computeSyncHash(payload string) string {
	sum := sha256.Sum256([]byte(payload))
	return hex.EncodeToString(sum[:])
}

var _ ICompanionStateSyncEngine = (*CompanionStateSyncEngine)(nil)
