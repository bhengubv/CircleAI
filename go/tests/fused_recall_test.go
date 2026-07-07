// fused_recall_test.go
//
// Verifies FusedRecall: Reciprocal Rank Fusion order, cross-source
// reinforcement, cold-start degradation to episodic, the graph confidence gate,
// empty-query short-circuit, and dedup by normalised text. Mirrors the TS pilot
// suite tests/fused_recall.test.ts 1:1.

package circleai_test

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/google/uuid"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── Test doubles ────────────────────────────────────────────────────────────

func epEntry(id, userText string) circleai.EpisodicMemoryEntry {
	return circleai.EpisodicMemoryEntry{
		ID:            uuid.NewSHA1(uuid.NameSpaceOID, []byte("fused-test:"+id)),
		UserText:      userText,
		AssistantText: "",
		RecordedAtUTC: time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC),
	}
}

// fakeEpisodic returns a fixed, pre-ranked list from Search.
type fakeEpisodic struct {
	hits []circleai.EpisodicMemoryEntry
}

func (f *fakeEpisodic) Add(context.Context, circleai.EpisodicMemoryEntry) error { return nil }
func (f *fakeEpisodic) Search(_ context.Context, _ []float32, topK int) ([]circleai.EpisodicMemoryEntry, error) {
	return takeEntriesTest(f.hits, topK), nil
}
func (f *fakeEpisodic) GetRecent(_ context.Context, count int) ([]circleai.EpisodicMemoryEntry, error) {
	return takeEntriesTest(f.hits, count), nil
}
func (f *fakeEpisodic) Count(context.Context) (int, error) { return len(f.hits), nil }
func (f *fakeEpisodic) PruneOlderThan(context.Context, time.Time) (int, error) {
	return 0, nil
}

// fakeHippo returns a fixed, pre-ranked list from MultiHopRecall.
type fakeHippo struct {
	hits []circleai.MemoryHit
}

func (f *fakeHippo) BackendId() string                                   { return "fake-hippo" }
func (f *fakeHippo) Index(context.Context, circleai.MemoryItem) error    { return nil }
func (f *fakeHippo) MultiHopRecall(_ context.Context, _ string, topK int) ([]circleai.MemoryHit, error) {
	return takeHitsTest(f.hits, topK), nil
}

// throwingHippo always errors from MultiHopRecall.
type throwingHippo struct{}

func (throwingHippo) BackendId() string                                { return "boom" }
func (throwingHippo) Index(context.Context, circleai.MemoryItem) error { return nil }
func (throwingHippo) MultiHopRecall(context.Context, string, int) ([]circleai.MemoryHit, error) {
	return nil, errors.New("graph unavailable")
}

func graphHitT(id, text string, confidence *string) circleai.MemoryHit {
	var meta map[string]string
	if confidence != nil {
		meta = map[string]string{"confidence": *confidence}
	}
	return circleai.MemoryHit{Item: circleai.MemoryItem{ID: id, Text: text, Metadata: meta}, Score: 1}
}

func takeEntriesTest(in []circleai.EpisodicMemoryEntry, n int) []circleai.EpisodicMemoryEntry {
	if n > len(in) {
		n = len(in)
	}
	return append([]circleai.EpisodicMemoryEntry(nil), in[:n]...)
}
func takeHitsTest(in []circleai.MemoryHit, n int) []circleai.MemoryHit {
	if n > len(in) {
		n = len(in)
	}
	return append([]circleai.MemoryHit(nil), in[:n]...)
}

func hitTexts(hits []circleai.MemoryHit) []string {
	out := make([]string, len(hits))
	for i, h := range hits {
		out[i] = h.Item.Text
	}
	return out
}

func mustRecall(t *testing.T, ep circleai.IEpisodicMemoryStore, g circleai.IHippoRagStore) *circleai.FusedRecall {
	t.Helper()
	r, err := circleai.NewFusedRecall(ep, g, nil)
	if err != nil {
		t.Fatalf("NewFusedRecall: %v", err)
	}
	return r
}

// ── Tests ───────────────────────────────────────────────────────────────────

func TestFusedRecall_RRFOrdering(t *testing.T) {
	ctx := context.Background()

	t.Run("a memory surfaced by BOTH sources outranks one surfaced by only one", func(t *testing.T) {
		episodic := &fakeEpisodic{hits: []circleai.EpisodicMemoryEntry{epEntry("a", "A"), epEntry("b", "B"), epEntry("c", "C")}}
		graph := &fakeHippo{hits: []circleai.MemoryHit{graphHitT("g", "B", nil)}} // reinforces B
		recall := mustRecall(t, episodic, graph)

		hits, err := recall.Recall(ctx, "q", nil, 5)
		if err != nil {
			t.Fatalf("Recall: %v", err)
		}
		assertTexts(t, hitTexts(hits), []string{"B", "A", "C"})
	})

	t.Run("cold-start (no graph) yields the episodic order unchanged", func(t *testing.T) {
		episodic := &fakeEpisodic{hits: []circleai.EpisodicMemoryEntry{epEntry("a", "A"), epEntry("b", "B"), epEntry("c", "C")}}
		recall := mustRecall(t, episodic, nil)

		hits, err := recall.Recall(ctx, "q", nil, 5)
		if err != nil {
			t.Fatalf("Recall: %v", err)
		}
		assertTexts(t, hitTexts(hits), []string{"A", "B", "C"})
	})

	t.Run("respects topK", func(t *testing.T) {
		episodic := &fakeEpisodic{hits: []circleai.EpisodicMemoryEntry{epEntry("a", "A"), epEntry("b", "B"), epEntry("c", "C")}}
		recall := mustRecall(t, episodic, nil)

		hits, err := recall.Recall(ctx, "q", nil, 2)
		if err != nil {
			t.Fatalf("Recall: %v", err)
		}
		if len(hits) != 2 {
			t.Fatalf("len: got %d want 2", len(hits))
		}
		assertTexts(t, hitTexts(hits), []string{"A", "B"})
	})
}

func TestFusedRecall_IntegrityGates(t *testing.T) {
	ctx := context.Background()

	t.Run("drops graph hits below the confidence threshold", func(t *testing.T) {
		episodic := &fakeEpisodic{}
		graph := &fakeHippo{hits: []circleai.MemoryHit{graphHitT("low", "LOW", strptr("0.2")), graphHitT("high", "HIGH", strptr("0.9"))}}
		recall := mustRecall(t, episodic, graph)

		hits, err := recall.Recall(ctx, "q", nil, 5)
		if err != nil {
			t.Fatalf("Recall: %v", err)
		}
		texts := hitTexts(hits)
		if contains(texts, "LOW") {
			t.Errorf("below-threshold hit must be dropped")
		}
		if !contains(texts, "HIGH") {
			t.Errorf("HIGH must be kept")
		}
	})

	t.Run("keeps graph hits that carry no confidence metadata (gate is a no-op)", func(t *testing.T) {
		episodic := &fakeEpisodic{}
		graph := &fakeHippo{hits: []circleai.MemoryHit{graphHitT("g", "NOCONF", nil)}}
		recall := mustRecall(t, episodic, graph)

		hits, err := recall.Recall(ctx, "q", nil, 5)
		if err != nil {
			t.Fatalf("Recall: %v", err)
		}
		assertTexts(t, hitTexts(hits), []string{"NOCONF"})
	})

	t.Run("skips the graph entirely for an empty/whitespace query", func(t *testing.T) {
		episodic := &fakeEpisodic{hits: []circleai.EpisodicMemoryEntry{epEntry("a", "A")}}
		graph := &fakeHippo{hits: []circleai.MemoryHit{graphHitT("g", "GRAPH", nil)}}
		recall := mustRecall(t, episodic, graph)

		hits, err := recall.Recall(ctx, "   ", nil, 5)
		if err != nil {
			t.Fatalf("Recall: %v", err)
		}
		texts := hitTexts(hits)
		assertTexts(t, texts, []string{"A"})
		if contains(texts, "GRAPH") {
			t.Errorf("graph must be skipped for empty query")
		}
	})

	t.Run("degrades to episodic when the graph errors", func(t *testing.T) {
		episodic := &fakeEpisodic{hits: []circleai.EpisodicMemoryEntry{epEntry("a", "A")}}
		recall := mustRecall(t, episodic, throwingHippo{})

		hits, err := recall.Recall(ctx, "q", nil, 5)
		if err != nil {
			t.Fatalf("Recall: %v", err)
		}
		assertTexts(t, hitTexts(hits), []string{"A"})
	})
}

func TestFusedRecall_Dedup(t *testing.T) {
	ctx := context.Background()

	t.Run("fuses two hits with the same normalised text into one entry", func(t *testing.T) {
		episodic := &fakeEpisodic{hits: []circleai.EpisodicMemoryEntry{epEntry("a", "Durban  Weather")}}
		graph := &fakeHippo{hits: []circleai.MemoryHit{graphHitT("g", "durban weather", nil)}} // same key
		recall := mustRecall(t, episodic, graph)

		hits, err := recall.Recall(ctx, "q", nil, 5)
		if err != nil {
			t.Fatalf("Recall: %v", err)
		}
		if len(hits) != 1 {
			t.Errorf("len: got %d want 1", len(hits))
		}
	})
}

func assertTexts(t *testing.T, got, want []string) {
	t.Helper()
	if len(got) != len(want) {
		t.Fatalf("texts: got %v want %v", got, want)
	}
	for i := range want {
		if got[i] != want[i] {
			t.Fatalf("texts: got %v want %v", got, want)
		}
	}
}

func contains(list []string, s string) bool {
	for _, x := range list {
		if x == s {
			return true
		}
	}
	return false
}
