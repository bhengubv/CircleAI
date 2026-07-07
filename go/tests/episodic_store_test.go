// episodic_store_test.go
//
// Verifies InMemoryEpisodicStore: cosine similarity search, recency fallback,
// FIFO capacity eviction, prune, and count. Mirrors the TS pilot suite
// tests/episodic_store.test.ts 1:1.

package circleai_test

import (
	"context"
	"sort"
	"testing"
	"time"

	"github.com/google/uuid"

	circleai "github.com/bhengubv/CircleAI/go"
)

// labelID maps a short test label to a stable UUID so entries can be identified
// by label (the TS suite uses string ids like 'x'/'a').
func labelID(label string) uuid.UUID {
	return uuid.NewSHA1(uuid.NameSpaceOID, []byte("episodic-test:"+label))
}

type entryOpts struct {
	id        string
	userText  string
	embedding []float32
	recorded  time.Time
}

func mkEntry(o entryOpts) circleai.EpisodicMemoryEntry {
	rec := o.recorded
	if rec.IsZero() {
		rec = time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	}
	ut := o.userText
	if ut == "" {
		ut = "u"
	}
	return circleai.EpisodicMemoryEntry{
		ID:            labelID(o.id),
		RecordedAtUTC: rec,
		UserText:      ut,
		AssistantText: "a",
		Embedding:     o.embedding,
	}
}

func TestEpisodicStore_CosineSearch(t *testing.T) {
	ctx := context.Background()

	t.Run("ranks the nearest embedding first", func(t *testing.T) {
		store := circleai.NewInMemoryEpisodicStoreDefault()
		mustAdd(t, store, mkEntry(entryOpts{id: "x", userText: "x-axis", embedding: []float32{1, 0}}))
		mustAdd(t, store, mkEntry(entryOpts{id: "y", userText: "y-axis", embedding: []float32{0, 1}}))

		hits, err := store.Search(ctx, []float32{1, 0}, 2)
		if err != nil {
			t.Fatalf("Search: %v", err)
		}
		if len(hits) != 2 {
			t.Fatalf("len: got %d want 2", len(hits))
		}
		if hits[0].ID != labelID("x") {
			t.Errorf("hits[0]: got %v want x", hits[0].ID)
		}
		if hits[1].ID != labelID("y") {
			t.Errorf("hits[1]: got %v want y", hits[1].ID)
		}
	})

	t.Run("respects topK", func(t *testing.T) {
		store := circleai.NewInMemoryEpisodicStoreDefault()
		mustAdd(t, store, mkEntry(entryOpts{id: "a", embedding: []float32{1, 0}}))
		mustAdd(t, store, mkEntry(entryOpts{id: "b", embedding: []float32{0.9, 0.1}}))
		mustAdd(t, store, mkEntry(entryOpts{id: "c", embedding: []float32{0, 1}}))

		hits, err := store.Search(ctx, []float32{1, 0}, 1)
		if err != nil {
			t.Fatalf("Search: %v", err)
		}
		if len(hits) != 1 {
			t.Fatalf("len: got %d want 1", len(hits))
		}
		if hits[0].ID != labelID("a") {
			t.Errorf("hits[0]: got %v want a", hits[0].ID)
		}
	})

	t.Run("ignores entries whose embedding dimension differs from the query", func(t *testing.T) {
		store := circleai.NewInMemoryEpisodicStoreDefault()
		mustAdd(t, store, mkEntry(entryOpts{id: "ok", embedding: []float32{1, 0}}))
		mustAdd(t, store, mkEntry(entryOpts{id: "wrongdim", embedding: []float32{1, 0, 0}}))

		hits, err := store.Search(ctx, []float32{1, 0}, 5)
		if err != nil {
			t.Fatalf("Search: %v", err)
		}
		if len(hits) != 1 {
			t.Fatalf("len: got %d want 1", len(hits))
		}
		if hits[0].ID != labelID("ok") {
			t.Errorf("hits[0]: got %v want ok", hits[0].ID)
		}
	})
}

func TestEpisodicStore_RecencyFallback(t *testing.T) {
	ctx := context.Background()
	old := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	recent := time.Date(2026, 6, 1, 0, 0, 0, 0, time.UTC)

	t.Run("returns newest-first when the query embedding is nil", func(t *testing.T) {
		store := circleai.NewInMemoryEpisodicStoreDefault()
		mustAdd(t, store, mkEntry(entryOpts{id: "old", recorded: old}))
		mustAdd(t, store, mkEntry(entryOpts{id: "new", recorded: recent}))

		hits, err := store.Search(ctx, nil, 5)
		if err != nil {
			t.Fatalf("Search: %v", err)
		}
		if hits[0].ID != labelID("new") {
			t.Errorf("hits[0]: got %v want new", hits[0].ID)
		}
		if hits[1].ID != labelID("old") {
			t.Errorf("hits[1]: got %v want old", hits[1].ID)
		}
	})

	t.Run("treats an empty embedding as no embedding (recency)", func(t *testing.T) {
		store := circleai.NewInMemoryEpisodicStoreDefault()
		mustAdd(t, store, mkEntry(entryOpts{id: "old", recorded: old}))
		mustAdd(t, store, mkEntry(entryOpts{id: "new", recorded: recent}))

		hits, err := store.Search(ctx, []float32{}, 1)
		if err != nil {
			t.Fatalf("Search: %v", err)
		}
		if hits[0].ID != labelID("new") {
			t.Errorf("hits[0]: got %v want new", hits[0].ID)
		}
	})
}

func TestEpisodicStore_CapacityAndMaintenance(t *testing.T) {
	ctx := context.Background()

	t.Run("evicts oldest entries beyond maxEntries (FIFO)", func(t *testing.T) {
		store, err := circleai.NewInMemoryEpisodicStore(2)
		if err != nil {
			t.Fatalf("New: %v", err)
		}
		mustAdd(t, store, mkEntry(entryOpts{id: "a"}))
		mustAdd(t, store, mkEntry(entryOpts{id: "b"}))
		mustAdd(t, store, mkEntry(entryOpts{id: "c"}))

		count, _ := store.Count(ctx)
		if count != 2 {
			t.Fatalf("count: got %d want 2", count)
		}
		recent, err := store.GetRecent(ctx, 10)
		if err != nil {
			t.Fatalf("GetRecent: %v", err)
		}
		ids := []string{}
		for _, e := range recent {
			ids = append(ids, e.ID.String())
		}
		sort.Strings(ids)
		want := []string{labelID("b").String(), labelID("c").String()}
		sort.Strings(want)
		if ids[0] != want[0] || ids[1] != want[1] {
			t.Errorf("remaining ids: got %v want %v (a should be evicted)", ids, want)
		}
	})

	t.Run("prunes entries older than the cutoff and returns the removed count", func(t *testing.T) {
		store := circleai.NewInMemoryEpisodicStoreDefault()
		mustAdd(t, store, mkEntry(entryOpts{id: "old", recorded: time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)}))
		mustAdd(t, store, mkEntry(entryOpts{id: "new", recorded: time.Date(2026, 6, 1, 0, 0, 0, 0, time.UTC)}))

		removed, err := store.PruneOlderThan(ctx, time.Date(2026, 3, 1, 0, 0, 0, 0, time.UTC))
		if err != nil {
			t.Fatalf("PruneOlderThan: %v", err)
		}
		if removed != 1 {
			t.Errorf("removed: got %d want 1", removed)
		}
		count, _ := store.Count(ctx)
		if count != 1 {
			t.Errorf("count: got %d want 1", count)
		}
		remaining, _ := store.GetRecent(ctx, 10)
		if remaining[0].ID != labelID("new") {
			t.Errorf("remaining[0]: got %v want new", remaining[0].ID)
		}
	})

	t.Run("rejects a non-positive maxEntries", func(t *testing.T) {
		if _, err := circleai.NewInMemoryEpisodicStore(0); err == nil {
			t.Errorf("expected error for maxEntries=0")
		}
	})
}

func mustAdd(t *testing.T, store circleai.IEpisodicMemoryStore, e circleai.EpisodicMemoryEntry) {
	t.Helper()
	if err := store.Add(context.Background(), e); err != nil {
		t.Fatalf("Add: %v", err)
	}
}
