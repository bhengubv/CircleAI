// memory_consolidation_stores.go
//
// The four consolidation-tier store interfaces and their in-memory
// implementations, plus an in-memory IPersonaStore. Ported from
// CircleAI.Memory.Consolidation (InMemoryStores) — the C# reference — and
// mirrors the TypeScript pilot (memory/consolidation.ts stores + stores.ts
// InMemoryPersonaStore) 1:1.
//
// All data is lost when the process exits. Every store is mutex-guarded and
// safe for concurrent use. Ranking uses the FULL cosine (cosineFull) — distinct
// from the episodic store's dot-only cosine.

package circleai

import (
	"context"
	"errors"
	"sort"
	"sync"
	"time"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// Store interfaces
// ---------------------------------------------------------------------------

// IDailyMemoryStore is a persistent store for tier-2 daily summaries.
type IDailyMemoryStore interface {
	// Upsert adds a daily summary, replacing any existing entry for the same day.
	Upsert(ctx context.Context, summary DailyMemorySummary) error
	// Get returns the summary for the given day, or nil when none exists.
	Get(ctx context.Context, day CivilDate) (*DailyMemorySummary, error)
	// GetRange returns all summaries between fromInclusive and toInclusive,
	// ordered by day ascending.
	GetRange(ctx context.Context, fromInclusive, toInclusive CivilDate) ([]DailyMemorySummary, error)
	// PruneOlderThan removes summaries whose day is before cutoff. Returns the count removed.
	PruneOlderThan(ctx context.Context, cutoff CivilDate) (int, error)
	// Count returns the total summaries currently stored.
	Count(ctx context.Context) (int, error)
}

// ISemanticMemoryStore is a persistent store for tier-3 semantic memory clusters.
type ISemanticMemoryStore interface {
	// Add adds a cluster.
	Add(ctx context.Context, cluster SemanticMemoryCluster) error
	// GetWeek returns all clusters for the given week, ordered by TopicWeight desc.
	GetWeek(ctx context.Context, weekStartingMonday CivilDate) ([]SemanticMemoryCluster, error)
	// Search returns the topK clusters by centroid cosine similarity; recency
	// fallback when queryEmbedding is nil.
	Search(ctx context.Context, queryEmbedding []float32, topK int) ([]SemanticMemoryCluster, error)
	// PruneOlderThan removes clusters whose week start is before cutoff. Returns the count removed.
	PruneOlderThan(ctx context.Context, cutoff CivilDate) (int, error)
	// Count returns the total clusters currently stored.
	Count(ctx context.Context) (int, error)
}

// IPersonaDeltaStore is a persistent store for tier-4 persona-delta snapshots.
// Retained forever.
type IPersonaDeltaStore interface {
	// Add adds a delta snapshot.
	Add(ctx context.Context, snapshot PersonaDeltaSnapshot) error
	// GetForUser returns all snapshots for the given user, ordered by PeriodStart.
	GetForUser(ctx context.Context, userID string) ([]PersonaDeltaSnapshot, error)
	// Count returns the total snapshots currently stored.
	Count(ctx context.Context) (int, error)
}

// ICoreMemoryStore is a persistent store for tier-5 core memories — things the
// AI will not forget.
type ICoreMemoryStore interface {
	// Add adds a core memory.
	Add(ctx context.Context, memory CoreMemory) error
	// Get returns a core memory by id, or nil when not found.
	Get(ctx context.Context, id uuid.UUID) (*CoreMemory, error)
	// Search returns the topK core memories by embedding cosine; reinforcement-order fallback when nil.
	Search(ctx context.Context, queryEmbedding []float32, topK int) ([]CoreMemory, error)
	// ListAll returns all core memories in reinforcement order (most reinforced first).
	ListAll(ctx context.Context) ([]CoreMemory, error)
	// Reinforce increments ReinforcementCount and bumps LastReinforcedUtc. No-op when unknown.
	Reinforce(ctx context.Context, id uuid.UUID) error
	// Remove removes a core memory. Returns whether it existed.
	Remove(ctx context.Context, id uuid.UUID) (bool, error)
	// Count returns the total core memories currently stored.
	Count(ctx context.Context) (int, error)
}

// ---------------------------------------------------------------------------
// InMemoryDailyMemoryStore
// ---------------------------------------------------------------------------

// InMemoryDailyMemoryStore is an in-memory IDailyMemoryStore keyed by day.
type InMemoryDailyMemoryStore struct {
	mu    sync.Mutex
	store map[CivilDate]DailyMemorySummary
}

// NewInMemoryDailyMemoryStore returns an empty daily store.
func NewInMemoryDailyMemoryStore() *InMemoryDailyMemoryStore {
	return &InMemoryDailyMemoryStore{store: make(map[CivilDate]DailyMemorySummary)}
}

// Upsert adds a summary, replacing any existing entry for the same day.
func (s *InMemoryDailyMemoryStore) Upsert(_ context.Context, summary DailyMemorySummary) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.store[summary.Day] = summary
	return nil
}

// Get returns the summary for the given day, or nil.
func (s *InMemoryDailyMemoryStore) Get(_ context.Context, day CivilDate) (*DailyMemorySummary, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if v, ok := s.store[day]; ok {
		cp := v
		return &cp, nil
	}
	return nil, nil
}

// GetRange returns all summaries in [fromInclusive, toInclusive], day-ascending.
func (s *InMemoryDailyMemoryStore) GetRange(_ context.Context, fromInclusive, toInclusive CivilDate) ([]DailyMemorySummary, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []DailyMemorySummary
	for _, v := range s.store {
		if v.Day.Compare(fromInclusive) >= 0 && v.Day.Compare(toInclusive) <= 0 {
			out = append(out, v)
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].Day.Compare(out[j].Day) < 0 })
	return out, nil
}

// PruneOlderThan removes summaries strictly before cutoff.
func (s *InMemoryDailyMemoryStore) PruneOlderThan(_ context.Context, cutoff CivilDate) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	var toRemove []CivilDate
	for d := range s.store {
		if d.Compare(cutoff) < 0 {
			toRemove = append(toRemove, d)
		}
	}
	for _, d := range toRemove {
		delete(s.store, d)
	}
	return len(toRemove), nil
}

// Count returns the number of summaries stored.
func (s *InMemoryDailyMemoryStore) Count(_ context.Context) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.store), nil
}

// ---------------------------------------------------------------------------
// InMemorySemanticMemoryStore
// ---------------------------------------------------------------------------

// InMemorySemanticMemoryStore is an in-memory ISemanticMemoryStore.
type InMemorySemanticMemoryStore struct {
	mu    sync.Mutex
	store []SemanticMemoryCluster
}

// NewInMemorySemanticMemoryStore returns an empty semantic store.
func NewInMemorySemanticMemoryStore() *InMemorySemanticMemoryStore {
	return &InMemorySemanticMemoryStore{}
}

// Add appends a cluster.
func (s *InMemorySemanticMemoryStore) Add(_ context.Context, cluster SemanticMemoryCluster) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.store = append(s.store, cluster)
	return nil
}

// GetWeek returns clusters for the week, TopicWeight desc.
func (s *InMemorySemanticMemoryStore) GetWeek(_ context.Context, weekStartingMonday CivilDate) ([]SemanticMemoryCluster, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []SemanticMemoryCluster
	for _, c := range s.store {
		if c.WeekStartingMonday == weekStartingMonday {
			out = append(out, c)
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].TopicWeight > out[j].TopicWeight })
	return out, nil
}

// Search returns the topK clusters by centroid cosine, or recency when nil.
func (s *InMemorySemanticMemoryStore) Search(_ context.Context, queryEmbedding []float32, topK int) ([]SemanticMemoryCluster, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if queryEmbedding == nil {
		out := make([]SemanticMemoryCluster, len(s.store))
		copy(out, s.store)
		sort.SliceStable(out, func(i, j int) bool { return out[i].GeneratedAtUTC.After(out[j].GeneratedAtUTC) })
		return takeClusters(out, topK), nil
	}

	type scored struct {
		c     SemanticMemoryCluster
		score float32
	}
	var candidates []scored
	for _, c := range s.store {
		if c.CentroidEmbedding != nil {
			candidates = append(candidates, scored{c: c, score: cosineFull(queryEmbedding, c.CentroidEmbedding)})
		}
	}
	sort.SliceStable(candidates, func(i, j int) bool { return candidates[i].score > candidates[j].score })
	limit := topK
	if limit > len(candidates) {
		limit = len(candidates)
	}
	out := make([]SemanticMemoryCluster, 0, limit)
	for i := 0; i < limit; i++ {
		out = append(out, candidates[i].c)
	}
	return out, nil
}

// PruneOlderThan removes clusters whose week start is before cutoff.
func (s *InMemorySemanticMemoryStore) PruneOlderThan(_ context.Context, cutoff CivilDate) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	kept := s.store[:0]
	removed := 0
	for _, c := range s.store {
		if c.WeekStartingMonday.Compare(cutoff) < 0 {
			removed++
		} else {
			kept = append(kept, c)
		}
	}
	s.store = kept
	return removed, nil
}

// Count returns the number of clusters stored.
func (s *InMemorySemanticMemoryStore) Count(_ context.Context) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.store), nil
}

func takeClusters(in []SemanticMemoryCluster, n int) []SemanticMemoryCluster {
	if n < 0 {
		n = 0
	}
	if n > len(in) {
		n = len(in)
	}
	out := make([]SemanticMemoryCluster, n)
	copy(out, in[:n])
	return out
}

// ---------------------------------------------------------------------------
// InMemoryPersonaDeltaStore
// ---------------------------------------------------------------------------

// InMemoryPersonaDeltaStore is an in-memory IPersonaDeltaStore.
type InMemoryPersonaDeltaStore struct {
	mu    sync.Mutex
	store []PersonaDeltaSnapshot
}

// NewInMemoryPersonaDeltaStore returns an empty persona-delta store.
func NewInMemoryPersonaDeltaStore() *InMemoryPersonaDeltaStore {
	return &InMemoryPersonaDeltaStore{}
}

// Add appends a snapshot.
func (s *InMemoryPersonaDeltaStore) Add(_ context.Context, snapshot PersonaDeltaSnapshot) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.store = append(s.store, snapshot)
	return nil
}

// GetForUser returns snapshots for the user, PeriodStart ascending.
func (s *InMemoryPersonaDeltaStore) GetForUser(_ context.Context, userID string) ([]PersonaDeltaSnapshot, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []PersonaDeltaSnapshot
	for _, d := range s.store {
		if d.UserID == userID {
			out = append(out, d)
		}
	}
	sort.SliceStable(out, func(i, j int) bool { return out[i].PeriodStart.Compare(out[j].PeriodStart) < 0 })
	return out, nil
}

// Count returns the number of snapshots stored.
func (s *InMemoryPersonaDeltaStore) Count(_ context.Context) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.store), nil
}

// ---------------------------------------------------------------------------
// InMemoryCoreMemoryStore
// ---------------------------------------------------------------------------

// InMemoryCoreMemoryStore is an in-memory ICoreMemoryStore keyed by id.
// Entries are held by pointer so Reinforce mutates in place (matching the C#
// class-reference semantics).
type InMemoryCoreMemoryStore struct {
	mu    sync.Mutex
	store map[uuid.UUID]*CoreMemory
	clock func() time.Time
}

// NewInMemoryCoreMemoryStore returns an empty core store using the wall clock.
func NewInMemoryCoreMemoryStore() *InMemoryCoreMemoryStore {
	return &InMemoryCoreMemoryStore{store: make(map[uuid.UUID]*CoreMemory), clock: time.Now}
}

// Add adds a core memory (stored by copy so later caller mutations don't leak in).
func (s *InMemoryCoreMemoryStore) Add(_ context.Context, memory CoreMemory) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	cp := memory
	s.store[memory.ID] = &cp
	return nil
}

// Get returns a core memory by id, or nil.
func (s *InMemoryCoreMemoryStore) Get(_ context.Context, id uuid.UUID) (*CoreMemory, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if m, ok := s.store[id]; ok {
		cp := *m
		return &cp, nil
	}
	return nil, nil
}

// Search returns the topK by embedding cosine, or reinforcement order when nil.
func (s *InMemoryCoreMemoryStore) Search(_ context.Context, queryEmbedding []float32, topK int) ([]CoreMemory, error) {
	s.mu.Lock()
	defer s.mu.Unlock()

	if queryEmbedding == nil {
		all := s.snapshotLocked()
		sortByReinforcement(all)
		return takeCore(all, topK), nil
	}

	type scored struct {
		m     CoreMemory
		score float32
	}
	var candidates []scored
	for _, m := range s.store {
		if m.Embedding != nil {
			candidates = append(candidates, scored{m: *m, score: cosineFull(queryEmbedding, m.Embedding)})
		}
	}
	sort.SliceStable(candidates, func(i, j int) bool { return candidates[i].score > candidates[j].score })
	limit := topK
	if limit > len(candidates) {
		limit = len(candidates)
	}
	out := make([]CoreMemory, 0, limit)
	for i := 0; i < limit; i++ {
		out = append(out, candidates[i].m)
	}
	return out, nil
}

// ListAll returns all core memories in reinforcement order.
func (s *InMemoryCoreMemoryStore) ListAll(_ context.Context) ([]CoreMemory, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	all := s.snapshotLocked()
	sortByReinforcement(all)
	return all, nil
}

// Reinforce increments ReinforcementCount and bumps LastReinforcedUtc in place.
func (s *InMemoryCoreMemoryStore) Reinforce(_ context.Context, id uuid.UUID) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if m, ok := s.store[id]; ok {
		m.ReinforcementCount++
		m.LastReinforcedUTC = s.clock().UTC()
	}
	return nil
}

// Remove removes a core memory, returning whether it existed.
func (s *InMemoryCoreMemoryStore) Remove(_ context.Context, id uuid.UUID) (bool, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.store[id]; ok {
		delete(s.store, id)
		return true, nil
	}
	return false, nil
}

// Count returns the number of core memories stored.
func (s *InMemoryCoreMemoryStore) Count(_ context.Context) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.store), nil
}

// snapshotLocked copies the stored values out. Caller must hold s.mu.
func (s *InMemoryCoreMemoryStore) snapshotLocked() []CoreMemory {
	out := make([]CoreMemory, 0, len(s.store))
	for _, m := range s.store {
		out = append(out, *m)
	}
	return out
}

// sortByReinforcement orders by ReinforcementCount desc, then LastReinforcedUtc desc.
func sortByReinforcement(in []CoreMemory) {
	sort.SliceStable(in, func(i, j int) bool {
		if in[i].ReinforcementCount != in[j].ReinforcementCount {
			return in[i].ReinforcementCount > in[j].ReinforcementCount
		}
		return in[i].LastReinforcedUTC.After(in[j].LastReinforcedUTC)
	})
}

func takeCore(in []CoreMemory, n int) []CoreMemory {
	if n < 0 {
		n = 0
	}
	if n > len(in) {
		n = len(in)
	}
	out := make([]CoreMemory, n)
	copy(out, in[:n])
	return out
}

// ---------------------------------------------------------------------------
// InMemoryPersonaStore
// ---------------------------------------------------------------------------

// InMemoryPersonaStore is an in-memory IPersonaStore keyed by userID. Load
// returns a fresh default PersonaState (stamped with the requested userID) when
// no persona has been persisted for that user. Ported from the C# reference and
// mirrors the TS InMemoryPersonaStore.
type InMemoryPersonaStore struct {
	mu    sync.Mutex
	store map[string]PersonaState
}

// NewInMemoryPersonaStore returns an empty persona store.
func NewInMemoryPersonaStore() *InMemoryPersonaStore {
	return &InMemoryPersonaStore{store: make(map[string]PersonaState)}
}

// Load returns the stored persona for userID, or a fresh default when absent.
func (s *InMemoryPersonaStore) Load(_ context.Context, userID string) (PersonaState, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if p, ok := s.store[userID]; ok {
		return p, nil
	}
	fresh := NewPersonaState(userID)
	return fresh, nil
}

// Save persists the persona.
func (s *InMemoryPersonaStore) Save(_ context.Context, persona PersonaState) error {
	if persona.UserID == "" {
		return errors.New("persona.UserID required")
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	s.store[persona.UserID] = persona
	return nil
}

// Compile-time assertions that the concrete stores satisfy their interfaces.
var (
	_ IDailyMemoryStore    = (*InMemoryDailyMemoryStore)(nil)
	_ ISemanticMemoryStore = (*InMemorySemanticMemoryStore)(nil)
	_ IPersonaDeltaStore   = (*InMemoryPersonaDeltaStore)(nil)
	_ ICoreMemoryStore     = (*InMemoryCoreMemoryStore)(nil)
	_ IPersonaStore        = (*InMemoryPersonaStore)(nil)
)
