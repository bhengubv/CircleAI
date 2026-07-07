// knowledge_graph_test.go
//
// Verifies KnowledgeGraph (triples + nodes) and HippoRagStore (Personalised
// PageRank multi-hop recall) — including the three precision guarantees:
// no-seed→empty, seeds excluded from results, confidence-weighting. Mirrors the
// TS pilot suite tests/knowledge_graph.test.ts 1:1.

package circleai_test

import (
	"context"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func strptr(s string) *string { return &s }

func TestKnowledgeGraph(t *testing.T) {
	t.Run("stores and returns triples", func(t *testing.T) {
		kg := circleai.NewKnowledgeGraph()
		mustTriple(t, kg, "a", "rel", "b", strptr("ep1"), 1.0)
		all := kg.AllTriples()
		if len(all) != 1 {
			t.Fatalf("len: got %d want 1", len(all))
		}
		if all[0].Subject != "a" || all[0].Object != "b" || all[0].Confidence != 1.0 {
			t.Errorf("triple: got %+v", all[0])
		}
	})

	t.Run("replaces a triple with the same (subject, predicate, object)", func(t *testing.T) {
		kg := circleai.NewKnowledgeGraph()
		mustTriple(t, kg, "a", "rel", "b", strptr("ep1"), 0.5)
		mustTriple(t, kg, "a", "rel", "b", strptr("ep2"), 0.9)
		all := kg.AllTriples()
		if len(all) != 1 {
			t.Fatalf("len: got %d want 1", len(all))
		}
		if all[0].Confidence != 0.9 {
			t.Errorf("confidence: got %v want 0.9", all[0].Confidence)
		}
		if all[0].Source == nil || *all[0].Source != "ep2" {
			t.Errorf("source: got %v want ep2", all[0].Source)
		}
	})

	t.Run("upserts and fetches nodes", func(t *testing.T) {
		kg := circleai.NewKnowledgeGraph()
		if err := kg.UpsertNode(circleai.KnowledgeNode{ID: "heart", Kind: "organ", Name: "the heart"}); err != nil {
			t.Fatalf("UpsertNode: %v", err)
		}
		n := kg.GetNode("heart")
		if n == nil || n.Name != "the heart" {
			t.Errorf("GetNode(heart): got %v", n)
		}
		if kg.GetNode("missing") != nil {
			t.Errorf("GetNode(missing): expected nil")
		}
	})

	t.Run("rejects out-of-range confidence", func(t *testing.T) {
		kg := circleai.NewKnowledgeGraph()
		if err := kg.AddTriple("a", "r", "b", nil, 1.5); err == nil {
			t.Errorf("expected error for confidence 1.5")
		}
	})
}

func TestHippoRagStore_MultiHopRecall(t *testing.T) {
	ctx := context.Background()

	t.Run("reaches associated nodes across hops and excludes the seed", func(t *testing.T) {
		// chest → heart → father_cardiac_event
		kg := circleai.NewKnowledgeGraph()
		mustTriple(t, kg, "chest", "relates", "heart", strptr("ep1"), 1.0)
		mustTriple(t, kg, "heart", "relates", "father_cardiac_event", strptr("ep2"), 1.0)
		hippo := mustHippo(t, kg)

		hits, err := hippo.MultiHopRecall(ctx, "chest tightness", 5)
		if err != nil {
			t.Fatalf("MultiHopRecall: %v", err)
		}
		ids := hitIDs(hits)
		if ids["chest"] {
			t.Errorf("seed node must be excluded")
		}
		if !ids["heart"] {
			t.Errorf("one-hop node should be recalled")
		}
		if !ids["father_cardiac_event"] {
			t.Errorf("two-hop node should be recalled")
		}
		heart := findHit(hits, "heart")
		father := findHit(hits, "father_cardiac_event")
		if heart == nil || father == nil {
			t.Fatalf("expected both hits present")
		}
		if !(heart.Score >= father.Score) {
			t.Errorf("one hop should carry >= mass: heart=%v father=%v", heart.Score, father.Score)
		}
	})

	t.Run("returns empty when no query term touches the graph (no fabricated association)", func(t *testing.T) {
		kg := circleai.NewKnowledgeGraph()
		mustTriple(t, kg, "chest", "relates", "heart", strptr("ep1"), 1.0)
		hippo := mustHippo(t, kg)

		hits, err := hippo.MultiHopRecall(ctx, "banana apple", 5)
		if err != nil {
			t.Fatalf("MultiHopRecall: %v", err)
		}
		if len(hits) != 0 {
			t.Errorf("len: got %d want 0", len(hits))
		}
	})

	t.Run("returns empty on an empty graph", func(t *testing.T) {
		hippo := mustHippo(t, circleai.NewKnowledgeGraph())
		hits, err := hippo.MultiHopRecall(ctx, "anything", 5)
		if err != nil {
			t.Fatalf("MultiHopRecall: %v", err)
		}
		if len(hits) != 0 {
			t.Errorf("len: got %d want 0", len(hits))
		}
	})

	t.Run("confidence-weights edge spread: a stated fact outranks a guess", func(t *testing.T) {
		// root → alpha (stated, 1.0) and root → beta (guessed, 0.1)
		kg := circleai.NewKnowledgeGraph()
		mustTriple(t, kg, "root", "r", "alpha", strptr("ep1"), 1.0)
		mustTriple(t, kg, "root", "r", "beta", strptr("ep2"), 0.1)
		hippo := mustHippo(t, kg)

		hits, err := hippo.MultiHopRecall(ctx, "root", 5)
		if err != nil {
			t.Fatalf("MultiHopRecall: %v", err)
		}
		ids := hitIDs(hits)
		if ids["root"] {
			t.Errorf("seed excluded")
		}
		if len(hits) < 2 {
			t.Fatalf("expected at least 2 hits, got %d", len(hits))
		}
		if hits[0].Item.ID != "alpha" {
			t.Errorf("hits[0]: got %v want alpha", hits[0].Item.ID)
		}
		if hits[1].Item.ID != "beta" {
			t.Errorf("hits[1]: got %v want beta", hits[1].Item.ID)
		}
		if !(hits[0].Score > hits[1].Score) {
			t.Errorf("alpha should outrank beta: %v vs %v", hits[0].Score, hits[1].Score)
		}
	})

	t.Run("uses the node name as recall text when a node is present", func(t *testing.T) {
		kg := circleai.NewKnowledgeGraph()
		mustTriple(t, kg, "chest", "relates", "heart", strptr("ep1"), 1.0)
		if err := kg.UpsertNode(circleai.KnowledgeNode{ID: "heart", Kind: "organ", Name: "the heart"}); err != nil {
			t.Fatalf("UpsertNode: %v", err)
		}
		hippo := mustHippo(t, kg)

		hits, err := hippo.MultiHopRecall(ctx, "chest", 5)
		if err != nil {
			t.Fatalf("MultiHopRecall: %v", err)
		}
		heart := findHit(hits, "heart")
		if heart == nil || heart.Item.Text != "the heart" {
			t.Errorf("heart text: got %v want 'the heart'", heart)
		}
	})

	t.Run("Index registers the item + its metadata as graph triples", func(t *testing.T) {
		kg := circleai.NewKnowledgeGraph()
		hippo := mustHippo(t, kg)
		if err := hippo.Index(ctx, circleai.MemoryItem{ID: "note1", Text: "durban weather", Metadata: map[string]string{"topic": "durban"}}); err != nil {
			t.Fatalf("Index: %v", err)
		}
		triples, err := kg.ReadTriples("note1")
		if err != nil {
			t.Fatalf("ReadTriples: %v", err)
		}
		preds := map[string]bool{}
		for _, tr := range triples {
			preds[tr.Predicate] = true
		}
		if !preds["memory_text"] || !preds["topic"] || len(preds) != 2 {
			t.Errorf("predicates: got %v want {memory_text, topic}", preds)
		}
	})

	t.Run("recalls a memory node reached from a query-term seed (reverse edge)", func(t *testing.T) {
		kg := circleai.NewKnowledgeGraph()
		mustTriple(t, kg, "durban", "seenin", "note1", strptr("ep1"), 1.0)
		if err := kg.UpsertNode(circleai.KnowledgeNode{ID: "note1", Kind: "memory", Name: "durban weather"}); err != nil {
			t.Fatalf("UpsertNode: %v", err)
		}
		hippo := mustHippo(t, kg)

		hits, err := hippo.MultiHopRecall(ctx, "durban", 5)
		if err != nil {
			t.Fatalf("MultiHopRecall: %v", err)
		}
		ids := hitIDs(hits)
		if ids["durban"] {
			t.Errorf("seed excluded")
		}
		if !ids["note1"] {
			t.Errorf("note1 should be recalled")
		}
		note := findHit(hits, "note1")
		if note == nil || note.Item.Text != "durban weather" {
			t.Errorf("note1 text: got %v want 'durban weather'", note)
		}
	})
}

// ── helpers ──────────────────────────────────────────────────────────────────

func mustTriple(t *testing.T, kg *circleai.KnowledgeGraph, s, p, o string, src *string, conf float64) {
	t.Helper()
	if err := kg.AddTriple(s, p, o, src, conf); err != nil {
		t.Fatalf("AddTriple(%s,%s,%s): %v", s, p, o, err)
	}
}

func mustHippo(t *testing.T, kg *circleai.KnowledgeGraph) *circleai.HippoRagStore {
	t.Helper()
	h, err := circleai.NewHippoRagStore(kg)
	if err != nil {
		t.Fatalf("NewHippoRagStore: %v", err)
	}
	return h
}

func hitIDs(hits []circleai.MemoryHit) map[string]bool {
	out := map[string]bool{}
	for _, h := range hits {
		out[h.Item.ID] = true
	}
	return out
}

func findHit(hits []circleai.MemoryHit, id string) *circleai.MemoryHit {
	for i := range hits {
		if hits[i].Item.ID == id {
			return &hits[i]
		}
	}
	return nil
}
