// memory_encoder_test.go
//
// Verifies CompanionMemoryEncoder end-to-end: a turn handed to the background
// encoder fills the knowledge graph so associative recall can later reach the
// episode; attributed beliefs are formed off the hot path (a third party's fact
// never becomes the user's); the queue drops rather than blocks when full;
// Close drains remaining work; and an extractor failure is captured, not fatal.
// Mirrors the TS pilot suite tests/memory_encoder.test.ts 1:1.

package circleai_test

import (
	"context"
	"errors"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// throwingExtractor always errors from ExtractFromTurn.
type throwingExtractor struct{}

func (throwingExtractor) ExtractFromTurn(context.Context, string, string, *string) ([]circleai.KnowledgeTriple, error) {
	return nil, errors.New("boom")
}

func newEncoder(t *testing.T, ex circleai.IKnowledgeGraphExtractor, g *circleai.KnowledgeGraph, bx circleai.IBeliefExtractor, beliefs *circleai.SelfBeliefStore, capacity int) *circleai.CompanionMemoryEncoder {
	t.Helper()
	enc, err := circleai.NewCompanionMemoryEncoder(ex, g, bx, beliefs, capacity)
	if err != nil {
		t.Fatalf("NewCompanionMemoryEncoder: %v", err)
	}
	return enc
}

func TestMemoryEncoder_EndToEnd(t *testing.T) {
	ctx := context.Background()

	t.Run("encodes a turn so associative recall can reach the episode by a content word", func(t *testing.T) {
		graph := circleai.NewKnowledgeGraph()
		enc := newEncoder(t, &circleai.HeuristicKnowledgeGraphExtractor{}, graph, nil, nil, 0)

		enc.Enqueue("I love hiking in Drakensberg", "Sounds wonderful", "ep-hike")
		if err := enc.Close(); err != nil {
			t.Fatalf("Close: %v", err)
		}

		if len(graph.AllTriples()) == 0 {
			t.Fatalf("graph should have filled from the turn")
		}

		hippo := mustHippo(t, graph)
		hits, err := hippo.MultiHopRecall(ctx, "drakensberg", 5)
		if err != nil {
			t.Fatalf("MultiHopRecall: %v", err)
		}
		episode := findHit(hits, "ep-hike")
		if episode == nil {
			t.Fatalf("recall should reach the episode via the extracted edges")
		}
		if episode.Item.Text != "I love hiking in Drakensberg" {
			t.Errorf("episode text: got %q", episode.Item.Text)
		}
	})

	t.Run("forms attributed beliefs off the hot path — the mother's fact never becomes the user's", func(t *testing.T) {
		graph := circleai.NewKnowledgeGraph()
		beliefs := circleai.NewSelfBeliefStore()
		enc := newEncoder(t, &circleai.HeuristicKnowledgeGraphExtractor{}, graph, &circleai.HeuristicBeliefExtractor{}, beliefs, 0)

		enc.Enqueue("my mother is diabetic", "Noted", "ep1")
		enc.Enqueue("i am vegetarian", "Got it", "ep2")
		if err := enc.Close(); err != nil {
			t.Fatalf("Close: %v", err)
		}

		facts := beliefs.SelfFacts()
		for _, f := range facts {
			if strings.Contains(f.Object, "diabetic") {
				t.Errorf("mother's condition must never be a user fact")
			}
		}
		hasVeg := false
		for _, f := range facts {
			if f.Object == "vegetarian" {
				hasVeg = true
			}
		}
		if !hasVeg {
			t.Errorf("vegetarian should be a user fact")
		}
		if !nonSelfHas(beliefs, "diabetic") {
			t.Errorf("diabetic should still be remembered as an audit fact")
		}
	})
}

func TestMemoryEncoder_QueueBehaviour(t *testing.T) {
	t.Run("drops writes beyond capacity rather than blocking", func(t *testing.T) {
		graph := circleai.NewKnowledgeGraph()
		enc := newEncoder(t, &circleai.HeuristicKnowledgeGraphExtractor{}, graph, nil, nil, 2)

		// Enqueued before the drain is released (it drains on Close): the 3rd
		// overflows a capacity-2 queue and is dropped.
		enc.Enqueue("alpha", "", "e1")
		enc.Enqueue("bravo", "", "e2")
		enc.Enqueue("charlie", "", "e3")
		if err := enc.Close(); err != nil {
			t.Fatalf("Close: %v", err)
		}

		if graph.GetNode("e1") == nil {
			t.Errorf("e1 should be present")
		}
		if graph.GetNode("e2") == nil {
			t.Errorf("e2 should be present")
		}
		if graph.GetNode("e3") != nil {
			t.Errorf("the overflow write should have been dropped")
		}
	})

	t.Run("ignores an enqueue with a blank episode id", func(t *testing.T) {
		graph := circleai.NewKnowledgeGraph()
		enc := newEncoder(t, &circleai.HeuristicKnowledgeGraphExtractor{}, graph, nil, nil, 0)
		enc.Enqueue("hello", "", "")
		enc.Enqueue("hello", "", "   ")
		if err := enc.Close(); err != nil {
			t.Fatalf("Close: %v", err)
		}
		if len(graph.AllTriples()) != 0 {
			t.Errorf("blank episode ids should be ignored")
		}
	})

	t.Run("captures an extractor failure without crashing the drain", func(t *testing.T) {
		graph := circleai.NewKnowledgeGraph()
		enc := newEncoder(t, throwingExtractor{}, graph, nil, nil, 0)
		enc.Enqueue("x", "", "e1")
		if err := enc.Close(); err != nil {
			t.Fatalf("Close: %v", err)
		}

		last := enc.LastError()
		if last == nil || last.Error() != "boom" {
			t.Errorf("lastError: got %v want boom", last)
		}
		// The node was upserted before the extractor ran, so it survives.
		if graph.GetNode("e1") == nil {
			t.Errorf("e1 node should survive (upserted before the extractor ran)")
		}
	})
}
