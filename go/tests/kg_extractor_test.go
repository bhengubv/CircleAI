// kg_extractor_test.go
//
// Verifies HeuristicKnowledgeGraphExtractor: bidirectional mentions/seenin
// triples on content words, stop-word + short-word filtering, dedup, and the
// memory-id fallback to userText when no episode id is given. Mirrors the TS
// pilot suite tests/kg_extractor.test.ts 1:1.

package circleai_test

import (
	"context"
	"sort"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestKnowledgeGraphExtractor(t *testing.T) {
	ctx := context.Background()
	ex := &circleai.HeuristicKnowledgeGraphExtractor{}

	t.Run("emits a two-way link per content word, keyed by the episode id", func(t *testing.T) {
		triples := mustExtract(t, ex, ctx, "Durban weather is sunny", "", strptr("ep1"))
		// content words: durban, weather, sunny ("is" is a short stop word)
		if len(triples) != 6 {
			t.Fatalf("len: got %d want 6", len(triples))
		}
		has := func(s, p, o string) bool {
			for _, tr := range triples {
				if tr.Subject == s && tr.Predicate == p && tr.Object == o {
					return true
				}
			}
			return false
		}
		if !has("ep1", "mentions", "durban") {
			t.Errorf("missing ep1 mentions durban")
		}
		if !has("durban", "seenin", "ep1") {
			t.Errorf("missing durban seenin ep1")
		}
		if !has("ep1", "mentions", "weather") {
			t.Errorf("missing ep1 mentions weather")
		}
		if !has("ep1", "mentions", "sunny") {
			t.Errorf("missing ep1 mentions sunny")
		}
	})

	t.Run("drops stop words and words shorter than 3 chars", func(t *testing.T) {
		triples := mustExtract(t, ex, ctx, "I am at the shop", "", strptr("ep2"))
		objects := mentionsObjects(triples)
		// "i","am","at","the" are all stop/short; only "shop" survives.
		assertTexts(t, objects, []string{"shop"})
	})

	t.Run("dedupes a repeated word", func(t *testing.T) {
		triples := mustExtract(t, ex, ctx, "test test test", "", strptr("ep3"))
		if len(triples) != 2 { // one mentions + one seenin for "test"
			t.Errorf("len: got %d want 2", len(triples))
		}
	})

	t.Run("includes assistant-side content words", func(t *testing.T) {
		triples := mustExtract(t, ex, ctx, "tell me about", "Johannesburg traffic", strptr("ep4"))
		objects := mentionsObjects(triples)
		sort.Strings(objects)
		assertTexts(t, objects, []string{"johannesburg", "tell", "traffic"})
	})

	t.Run("falls back to userText as the memory id when no episode id is given", func(t *testing.T) {
		triples := mustExtract(t, ex, ctx, "hello world", "", nil)
		found := false
		for _, tr := range triples {
			if tr.Subject == "hello world" && tr.Predicate == "mentions" {
				found = true
			}
		}
		if !found {
			t.Errorf("expected memory id to fall back to userText")
		}
	})

	t.Run("returns nothing for an empty turn", func(t *testing.T) {
		triples := mustExtract(t, ex, ctx, "", "", nil)
		if len(triples) != 0 {
			t.Errorf("len: got %d want 0", len(triples))
		}
	})

	t.Run("tags every triple with the source episode id and default confidence", func(t *testing.T) {
		triples := mustExtract(t, ex, ctx, "coffee", "", strptr("ep5"))
		if len(triples) == 0 {
			t.Fatalf("expected triples")
		}
		for _, tr := range triples {
			if tr.Source == nil || *tr.Source != "ep5" {
				t.Errorf("source: got %v want ep5", tr.Source)
			}
			if tr.Confidence != 0.6 {
				t.Errorf("confidence: got %v want 0.6", tr.Confidence)
			}
		}
	})
}

func mustExtract(t *testing.T, ex *circleai.HeuristicKnowledgeGraphExtractor, ctx context.Context, user, assistant string, src *string) []circleai.KnowledgeTriple {
	t.Helper()
	triples, err := ex.ExtractFromTurn(ctx, user, assistant, src)
	if err != nil {
		t.Fatalf("ExtractFromTurn: %v", err)
	}
	return triples
}

func mentionsObjects(triples []circleai.KnowledgeTriple) []string {
	var out []string
	for _, tr := range triples {
		if tr.Predicate == "mentions" {
			out = append(out, tr.Object)
		}
	}
	return out
}
