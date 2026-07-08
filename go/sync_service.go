// sync_service.go
//
// Ports CircleAI.Sync.IMemorySyncService (IMemorySyncService.cs) and
// CircleAI.Sync.MemorySyncService (MemorySyncService.cs) — the push/receive
// orchestrator that serialises memory deltas, routes them through an
// ISyncChannel, and applies received deltas to the local IEpisodicMemoryStore.
//
// Also ports CircleAI.Sync.SyncPrimitives (SyncPrimitives.cs): VersionVector +
// SyncReconciliation (version-vector merge, dominance, last-writer-wins).
//
// The C# reference leaves the episodic apply as a placeholder comment. Per the
// "no stubs" rule the receive loop here is fully wired: episodic-domain deltas
// carry a JSON-encoded EpisodicMemoryEntry (see EncodeEpisodicDelta) that is
// deserialised and upserted into the local store.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"sync"
	"time"

	"github.com/google/uuid"
)

// ─────────────────────────────────────────────────────────────────────────────
// IMemorySyncService + MemorySyncService
// ─────────────────────────────────────────────────────────────────────────────

// IMemorySyncService pushes and receives memory deltas across all owned devices.
// The transport is determined by ISyncChannel — app code is identical whether
// the delta travels gRPC, BLE mesh, or DTN bundle.
type IMemorySyncService interface {
	// PushMemoryDelta pushes a memory delta for ownerID to all other devices.
	PushMemoryDelta(ctx context.Context, ownerID, domainKey string, delta []byte, mode SyncDeliveryMode) error

	// StartReceiving starts receiving and applying incoming deltas for ownerID.
	StartReceiving(ctx context.Context, ownerID string) error

	// StopReceiving stops receiving.
	StopReceiving(ctx context.Context) error
}

// MemorySyncService is the default IMemorySyncService implementation.
type MemorySyncService struct {
	channel       ISyncChannel
	store         IEpisodicMemoryStore
	localDeviceID string
	now           func() time.Time

	mu         sync.Mutex
	receiveCtx context.Context
	receiveFn  context.CancelFunc
	wg         sync.WaitGroup
}

// NewMemorySyncService wires a service over a sync channel, episodic store, and
// this device's id. All three are required.
func NewMemorySyncService(channel ISyncChannel, store IEpisodicMemoryStore, localDeviceID string) (*MemorySyncService, error) {
	if channel == nil {
		return nil, errors.New("channel required")
	}
	if store == nil {
		return nil, errors.New("store required")
	}
	if isBlank(localDeviceID) {
		return nil, errors.New("localDeviceId required")
	}
	return &MemorySyncService{
		channel:       channel,
		store:         store,
		localDeviceID: localDeviceID,
		now:           func() time.Time { return time.Now().UTC() },
	}, nil
}

// PushMemoryDelta builds a broadcast SyncDelta and pushes it via the channel.
// The Sequence is the current Unix-ms timestamp, mirroring the C# reference.
func (s *MemorySyncService) PushMemoryDelta(ctx context.Context, ownerID, domainKey string, delta []byte, mode SyncDeliveryMode) error {
	syncDelta := SyncDelta{
		OwnerID:        ownerID,
		SourceDeviceID: s.localDeviceID,
		TargetDeviceID: "", // broadcast to all owned devices
		DomainKey:      domainKey,
		Payload:        delta,
		Sequence:       s.now().UnixMilli(),
		DeliveryMode:   mode,
		TTL:            nil,
		CreatedAt:      s.now(),
	}
	return s.channel.PushDelta(ctx, syncDelta)
}

// StartReceiving launches the receive loop for ownerID. Deltas authored by this
// device are skipped; episodic-domain deltas are applied to the local store.
func (s *MemorySyncService) StartReceiving(ctx context.Context, ownerID string) error {
	if isBlank(ownerID) {
		return errors.New("ownerId required")
	}
	s.mu.Lock()
	if s.receiveFn != nil {
		s.receiveFn()
	}
	rctx, cancel := context.WithCancel(context.Background())
	s.receiveCtx = rctx
	s.receiveFn = cancel
	s.mu.Unlock()

	s.wg.Add(1)
	go func() {
		defer s.wg.Done()
		s.receiveLoop(rctx, ownerID)
	}()
	return nil
}

// StopReceiving cancels the receive loop and waits for it to finish.
func (s *MemorySyncService) StopReceiving(_ context.Context) error {
	s.mu.Lock()
	cancel := s.receiveFn
	s.receiveFn = nil
	s.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	s.wg.Wait()
	return nil
}

func (s *MemorySyncService) receiveLoop(ctx context.Context, ownerID string) {
	deltas, errs := s.channel.ReceiveDeltas(ctx, ownerID, 0)
	for {
		select {
		case <-ctx.Done():
			return
		case <-errs:
			// At most one error then the stream closes; stop the loop.
			return
		case delta, ok := <-deltas:
			if !ok {
				return
			}
			if delta.SourceDeviceID == s.localDeviceID {
				continue // skip own echoes
			}
			if delta.DomainKey == SyncDomainKeys.MemoryEpisodic {
				if entry, decodeErr := DecodeEpisodicDelta(delta.Payload); decodeErr == nil {
					_ = s.store.Add(ctx, entry)
				}
			}
			// Additional domain handlers (affect, persona, goals) go here.
		}
	}
}

var _ IMemorySyncService = (*MemorySyncService)(nil)

// ─────────────────────────────────────────────────────────────────────────────
// Episodic delta wire format
// ─────────────────────────────────────────────────────────────────────────────

// episodicDeltaDTO is the JSON wire shape of an episodic memory delta.
type episodicDeltaDTO struct {
	ID            string            `json:"id"`
	RecordedAtUTC time.Time         `json:"recordedAtUtc"`
	UserText      string            `json:"userText"`
	AssistantText string            `json:"assistantText"`
	AppContext    *string           `json:"appContext,omitempty"`
	Embedding     []float32         `json:"embedding,omitempty"`
	Tags          map[string]string `json:"tags,omitempty"`
}

// EncodeEpisodicDelta serialises an EpisodicMemoryEntry to the episodic-domain
// wire payload used by PushMemoryDelta.
func EncodeEpisodicDelta(entry EpisodicMemoryEntry) ([]byte, error) {
	return json.Marshal(episodicDeltaDTO{
		ID:            entry.ID.String(),
		RecordedAtUTC: entry.RecordedAtUTC,
		UserText:      entry.UserText,
		AssistantText: entry.AssistantText,
		AppContext:    entry.AppContext,
		Embedding:     entry.Embedding,
		Tags:          entry.Tags,
	})
}

// DecodeEpisodicDelta parses an episodic-domain wire payload back into an
// EpisodicMemoryEntry. A blank or invalid id yields a fresh UUID.
func DecodeEpisodicDelta(payload []byte) (EpisodicMemoryEntry, error) {
	var dto episodicDeltaDTO
	if err := json.Unmarshal(payload, &dto); err != nil {
		return EpisodicMemoryEntry{}, err
	}
	id, err := uuid.Parse(dto.ID)
	if err != nil {
		id = uuid.New()
	}
	return EpisodicMemoryEntry{
		ID:            id,
		RecordedAtUTC: dto.RecordedAtUTC,
		UserText:      dto.UserText,
		AssistantText: dto.AssistantText,
		AppContext:    dto.AppContext,
		Embedding:     dto.Embedding,
		Tags:          dto.Tags,
	}, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// SyncPrimitives — VersionVector + SyncReconciliation
// ─────────────────────────────────────────────────────────────────────────────

// VersionVector maps a node id to its logical clock value.
type VersionVector struct {
	// Clocks holds the per-node logical clock values.
	Clocks map[string]int64
}

// NewVersionVector wraps clocks (a nil map is treated as empty).
func NewVersionVector(clocks map[string]int64) VersionVector {
	if clocks == nil {
		clocks = map[string]int64{}
	}
	return VersionVector{Clocks: clocks}
}

// MergeVersionVectors returns the pointwise maximum of a and b — the
// least-upper-bound merge used to reconcile two peers' clocks.
func MergeVersionVectors(a, b VersionVector) VersionVector {
	merged := make(map[string]int64)
	for k, v := range a.Clocks {
		merged[k] = v
	}
	for k, bv := range b.Clocks {
		if av, ok := merged[k]; !ok || bv > av {
			merged[k] = bv
		}
	}
	return VersionVector{Clocks: merged}
}

// VersionVectorADominatesB reports whether a dominates b: a is >= b on every key
// and strictly greater on at least one.
func VersionVectorADominatesB(a, b VersionVector) bool {
	keys := make(map[string]struct{}, len(a.Clocks)+len(b.Clocks))
	for k := range a.Clocks {
		keys[k] = struct{}{}
	}
	for k := range b.Clocks {
		keys[k] = struct{}{}
	}
	anyStrictlyGreater := false
	for k := range keys {
		av := a.Clocks[k]
		bv := b.Clocks[k]
		if av < bv {
			return false
		}
		if av > bv {
			anyStrictlyGreater = true
		}
	}
	return anyStrictlyGreater
}

// LastWriterWins returns the value with the later timestamp; ties favour a
// (a.At >= b.At), matching the C# reference.
func LastWriterWins[T any](aAt time.Time, aVal T, bAt time.Time, bVal T) (time.Time, T) {
	if !aAt.Before(bAt) {
		return aAt, aVal
	}
	return bAt, bVal
}
