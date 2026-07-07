// memory_stores.go
//
// Concrete in-memory episodic store for the memory-brain. Ported from
// CircleAI.Memory (InMemoryEpisodicStore) — the C# reference — and mirrors the
// TypeScript pilot (memory/stores.ts) 1:1.
//
// All data is lost when the process exits; a persistent (SQLite) backend is a
// later slice. The algorithms (cosine similarity, recency fallback, FIFO cap)
// are identical to the reference. cosine == dot product because both vectors
// are L2-normalised at write time.

package circleai

import (
	"context"
	"errors"
	"sort"
	"sync"
	"time"
)

// InMemoryEpisodicStore is an in-memory IEpisodicMemoryStore. Capacity is
// capped (FIFO eviction) to prevent unbounded growth on long-running
// processes. All methods are safe for concurrent use.
type InMemoryEpisodicStore struct {
	mu         sync.Mutex
	maxEntries int
	entries    []EpisodicMemoryEntry
}

// NewInMemoryEpisodicStore creates a store capped at maxEntries. When the cap
// is exceeded the oldest entries are evicted (FIFO). maxEntries must be
// positive.
func NewInMemoryEpisodicStore(maxEntries int) (*InMemoryEpisodicStore, error) {
	if maxEntries <= 0 {
		return nil, errors.New("maxEntries must be positive")
	}
	return &InMemoryEpisodicStore{maxEntries: maxEntries}, nil
}

// NewInMemoryEpisodicStoreDefault creates a store with the default cap of 1000.
func NewInMemoryEpisodicStoreDefault() *InMemoryEpisodicStore {
	return &InMemoryEpisodicStore{maxEntries: 1000}
}

// Add appends a new entry, evicting the oldest entries once the cap is
// exceeded (FIFO).
func (s *InMemoryEpisodicStore) Add(_ context.Context, entry EpisodicMemoryEntry) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.entries = append(s.entries, entry)
	for len(s.entries) > s.maxEntries {
		s.entries = s.entries[1:]
	}
	return nil
}

// Search returns the topK entries most similar (cosine) to queryEmbedding.
// When queryEmbedding is nil or empty, it falls back to recency (newest-first).
// Only entries whose embedding dimension matches the query take part in the
// cosine ranking.
func (s *InMemoryEpisodicStore) Search(_ context.Context, queryEmbedding []float32, topK int) ([]EpisodicMemoryEntry, error) {
	s.mu.Lock()
	snapshot := make([]EpisodicMemoryEntry, len(s.entries))
	copy(snapshot, s.entries)
	s.mu.Unlock()

	if len(queryEmbedding) == 0 {
		// No embedding — return most recent.
		sortByRecencyDesc(snapshot)
		return takeEntries(snapshot, topK), nil
	}

	// Cosine similarity, only against entries whose embedding matches the query
	// dimension. Both vectors are L2-normalised, so cosine == dot product.
	type scored struct {
		entry EpisodicMemoryEntry
		score float32
	}
	var candidates []scored
	for _, e := range snapshot {
		if e.Embedding != nil && len(e.Embedding) == len(queryEmbedding) {
			candidates = append(candidates, scored{entry: e, score: cosineSimilarity(queryEmbedding, e.Embedding)})
		}
	}
	sort.SliceStable(candidates, func(i, j int) bool {
		return candidates[i].score > candidates[j].score
	})
	limit := topK
	if limit > len(candidates) {
		limit = len(candidates)
	}
	out := make([]EpisodicMemoryEntry, 0, limit)
	for i := 0; i < limit; i++ {
		out = append(out, candidates[i].entry)
	}
	return out, nil
}

// GetRecent returns the most recent count entries, newest-first.
func (s *InMemoryEpisodicStore) GetRecent(_ context.Context, count int) ([]EpisodicMemoryEntry, error) {
	s.mu.Lock()
	snapshot := make([]EpisodicMemoryEntry, len(s.entries))
	copy(snapshot, s.entries)
	s.mu.Unlock()

	sortByRecencyDesc(snapshot)
	return takeEntries(snapshot, count), nil
}

// Count returns the number of entries currently stored.
func (s *InMemoryEpisodicStore) Count(_ context.Context) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.entries), nil
}

// PruneOlderThan removes all entries recorded strictly before cutoff and
// returns the number removed.
func (s *InMemoryEpisodicStore) PruneOlderThan(_ context.Context, cutoff time.Time) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	before := len(s.entries)
	kept := s.entries[:0]
	for _, e := range s.entries {
		if !e.RecordedAtUTC.Before(cutoff) {
			kept = append(kept, e)
		}
	}
	s.entries = kept
	return before - len(s.entries), nil
}

// sortByRecencyDesc sorts entries newest-first (stable).
func sortByRecencyDesc(entries []EpisodicMemoryEntry) {
	sort.SliceStable(entries, func(i, j int) bool {
		return entries[i].RecordedAtUTC.After(entries[j].RecordedAtUTC)
	})
}

// takeEntries returns the first n entries (or all when fewer exist).
func takeEntries(entries []EpisodicMemoryEntry, n int) []EpisodicMemoryEntry {
	if n < 0 {
		n = 0
	}
	if n > len(entries) {
		n = len(entries)
	}
	out := make([]EpisodicMemoryEntry, n)
	copy(out, entries[:n])
	return out
}

// cosineSimilarity returns the cosine similarity of two equal-length,
// L2-normalised vectors (== dot product).
func cosineSimilarity(a, b []float32) float32 {
	var dot float32
	for i := 0; i < len(a) && i < len(b); i++ {
		dot += a[i] * b[i]
	}
	return dot
}

// Compile-time assertion that the concrete store satisfies the interface.
var _ IEpisodicMemoryStore = (*InMemoryEpisodicStore)(nil)
