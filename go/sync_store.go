// sync_store.go
//
// Ports CircleAI.Memory.Sync.ISyncableEntryStore (ISyncableEntryStore.cs) and
// CircleAI.Memory.Sync.InMemorySyncableEntryStore (InMemorySyncableEntryStore.cs).
//
// The seat the sync engine reads from and writes to. Implementations track the
// local view of all known syncable entries plus their version stamps.
//
// Apply rules — implementations MUST enforce these for convergence:
//   • Higher Version wins
//   • On tie (same Version), higher ContentHash (string compare) wins
//   • Tombstones replace any non-tombstone of equal-or-lower Version

package circleai

import (
	"context"
	"errors"
	"sort"
	"sync"
)

// ISyncableEntryStore is the store the sync engine operates over.
type ISyncableEntryStore interface {
	// Apply applies an incoming entry. Returns true when local state was
	// actually updated (incoming was strictly newer / preferred). Returns false
	// when the local entry was already at or beyond the incoming version.
	Apply(ctx context.Context, entry SyncableEntry) (bool, error)

	// Get returns the current entry for the given (type, id), or nil when not
	// known locally. Tombstones ARE returned — callers needing "is it deleted?"
	// should check SyncableEntry.IsTombstone.
	Get(ctx context.Context, entityType, entityID string) (*SyncableEntry, error)

	// GetSince returns every entry of the given type whose Version is strictly
	// greater than sinceVersion, ordered ascending by Version.
	GetSince(ctx context.Context, entityType string, sinceVersion int64) ([]SyncableEntry, error)

	// GetStateVector returns the highest known Version per entity type — the
	// local node's state vector. Types with no entries are omitted.
	GetStateVector(ctx context.Context) ([]StateVectorEntry, error)
}

// syncKey is the (type, id) composite key for stored entries.
type syncKey struct {
	Type string
	ID   string
}

// InMemorySyncableEntryStore is an in-memory ISyncableEntryStore. Safe for
// concurrent use.
type InMemorySyncableEntryStore struct {
	mu             sync.Mutex
	entries        map[syncKey]SyncableEntry
	maxVersionByTy map[string]int64
}

// NewInMemorySyncableEntryStore returns an empty store.
func NewInMemorySyncableEntryStore() *InMemorySyncableEntryStore {
	return &InMemorySyncableEntryStore{
		entries:        make(map[syncKey]SyncableEntry),
		maxVersionByTy: make(map[string]int64),
	}
}

// Apply applies entry per the convergence rules.
func (s *InMemorySyncableEntryStore) Apply(_ context.Context, entry SyncableEntry) (bool, error) {
	if entry.EntityType == "" {
		return false, errors.New("entry.EntityType required")
	}
	key := syncKey{Type: entry.EntityType, ID: entry.EntityID}

	s.mu.Lock()
	defer s.mu.Unlock()

	applied := false
	if existing, ok := s.entries[key]; ok {
		if shouldApplySyncable(existing, entry) {
			s.entries[key] = entry
			applied = true
		}
	} else {
		s.entries[key] = entry
		applied = true
	}

	if applied {
		if cur, ok := s.maxVersionByTy[entry.EntityType]; !ok || entry.Version > cur {
			s.maxVersionByTy[entry.EntityType] = entry.Version
		}
	}
	return applied, nil
}

// Get returns the entry for (entityType, entityID) or nil when absent.
func (s *InMemorySyncableEntryStore) Get(_ context.Context, entityType, entityID string) (*SyncableEntry, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if e, ok := s.entries[syncKey{Type: entityType, ID: entityID}]; ok {
		cp := e
		return &cp, nil
	}
	return nil, nil
}

// GetSince returns entries of entityType strictly newer than sinceVersion,
// ordered ascending by Version.
func (s *InMemorySyncableEntryStore) GetSince(_ context.Context, entityType string, sinceVersion int64) ([]SyncableEntry, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	result := make([]SyncableEntry, 0)
	for _, e := range s.entries {
		if e.EntityType == entityType && e.Version > sinceVersion {
			result = append(result, e)
		}
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Version < result[j].Version })
	return result, nil
}

// GetStateVector returns the highest known Version per entity type, ordered by
// EntityType (ordinal/bytewise, matching the C# reference).
func (s *InMemorySyncableEntryStore) GetStateVector(_ context.Context) ([]StateVectorEntry, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	vector := make([]StateVectorEntry, 0, len(s.maxVersionByTy))
	for ty, v := range s.maxVersionByTy {
		vector = append(vector, StateVectorEntry{EntityType: ty, MaxKnownVersion: v})
	}
	sort.Slice(vector, func(i, j int) bool { return vector[i].EntityType < vector[j].EntityType })
	return vector, nil
}

// shouldApplySyncable implements the apply rule: higher Version wins; on tie,
// tombstone-of-non-tombstone wins; else higher ContentHash (ordinal) wins.
func shouldApplySyncable(existing, incoming SyncableEntry) bool {
	if incoming.Version > existing.Version {
		return true
	}
	if incoming.Version < existing.Version {
		return false
	}
	// Equal versions — tombstone-of-non-tombstone wins.
	if incoming.IsTombstone && !existing.IsTombstone {
		return true
	}
	if !incoming.IsTombstone && existing.IsTombstone {
		return false
	}
	// Same tombstone state, same version — content hash tiebreaker (ordinal).
	return incoming.ContentHash > existing.ContentHash
}

var _ ISyncableEntryStore = (*InMemorySyncableEntryStore)(nil)
