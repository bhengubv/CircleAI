// belief_integrity_test.go
//
// Verifies the memory-integrity core: attribution discipline (self/other/world),
// and SelfBeliefStore filtering, revision (supersede), correction (retract), and
// provenance. The headline guarantee: "my mother is diabetic" never becomes a
// fact about the user. Mirrors the TS pilot suite tests/belief_integrity.test.ts 1:1.

package circleai_test

import (
	"context"
	"sort"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func oneBelief(t *testing.T, ex *circleai.HeuristicBeliefExtractor, text string) circleai.PersonalBelief {
	t.Helper()
	beliefs, err := ex.Extract(context.Background(), text, strptr("turn"))
	if err != nil {
		t.Fatalf("Extract: %v", err)
	}
	if len(beliefs) != 1 {
		t.Fatalf("expected one belief from %q, got %d", text, len(beliefs))
	}
	return beliefs[0]
}

func TestBeliefExtractor_Attribution(t *testing.T) {
	ex := &circleai.HeuristicBeliefExtractor{}

	t.Run("'my mother is diabetic' -> Other, about the mother", func(t *testing.T) {
		b := oneBelief(t, ex, "my mother is diabetic")
		if b.Attribution != circleai.AttributionOther {
			t.Errorf("attribution: got %v want Other", b.Attribution)
		}
		if b.Subject != "mother" {
			t.Errorf("subject: got %q want mother", b.Subject)
		}
		if b.Object != "diabetic" {
			t.Errorf("object: got %q want diabetic", b.Object)
		}
	})

	t.Run("'i am vegetarian' -> Self, about the user", func(t *testing.T) {
		b := oneBelief(t, ex, "i am vegetarian")
		if b.Attribution != circleai.AttributionSelf {
			t.Errorf("attribution: got %v want Self", b.Attribution)
		}
		if b.Subject != "user" {
			t.Errorf("subject: got %q want user", b.Subject)
		}
		if b.Object != "vegetarian" {
			t.Errorf("object: got %q want vegetarian", b.Object)
		}
	})

	t.Run("'my car is fast' (my + non-relation) -> Self", func(t *testing.T) {
		b := oneBelief(t, ex, "my car is fast")
		if b.Attribution != circleai.AttributionSelf {
			t.Errorf("attribution: got %v want Self", b.Attribution)
		}
		if b.Subject != "user" {
			t.Errorf("subject: got %q want user", b.Subject)
		}
	})

	t.Run("a bare relation as subject -> Other", func(t *testing.T) {
		b := oneBelief(t, ex, "brother lives in Cape Town")
		if b.Attribution != circleai.AttributionOther {
			t.Errorf("attribution: got %v want Other", b.Attribution)
		}
		if b.Subject != "brother" {
			t.Errorf("subject: got %q want brother", b.Subject)
		}
	})

	t.Run("a general statement -> World", func(t *testing.T) {
		b := oneBelief(t, ex, "paris is beautiful")
		if b.Attribution != circleai.AttributionWorld {
			t.Errorf("attribution: got %v want World", b.Attribution)
		}
		if b.Subject != "paris" {
			t.Errorf("subject: got %q want paris", b.Subject)
		}
	})
}

func TestSelfBeliefStore(t *testing.T) {
	ctx := context.Background()
	ex := &circleai.HeuristicBeliefExtractor{}

	t.Run("only Self beliefs become user facts; Other/World are audited", func(t *testing.T) {
		store := circleai.NewSelfBeliefStore()
		recordAll(t, store, ex, ctx, "my mother is diabetic", strptr("t1"))
		recordAll(t, store, ex, ctx, "i am vegetarian", strptr("t2"))

		facts := store.SelfFacts()
		if len(facts) != 1 {
			t.Fatalf("facts len: got %d want 1", len(facts))
		}
		if facts[0].Object != "vegetarian" {
			t.Errorf("facts[0]: got %q want vegetarian", facts[0].Object)
		}
		for _, f := range facts {
			if strings.Contains(f.Object, "diabetic") {
				t.Errorf("mother's fact leaked into user facts")
			}
		}
		if !nonSelfHas(store, "diabetic") {
			t.Errorf("mother's fact should be remembered in the audit trail")
		}
	})

	t.Run("a newer self-belief supersedes the older one on the same predicate", func(t *testing.T) {
		store := circleai.NewSelfBeliefStore()
		mk := func(obj string) circleai.PersonalBelief {
			return circleai.PersonalBelief{
				Attribution:   circleai.AttributionSelf,
				Subject:       "user",
				Predicate:     "isAbout",
				Object:        obj,
				Confidence:    0.6,
				Source:        strptr("t"),
				RecordedAtUTC: time.Now().UTC(),
			}
		}
		mustRecord(t, store, mk("vegetarian"))
		mustRecord(t, store, mk("vegan"))

		facts := store.SelfFacts()
		if len(facts) != 1 {
			t.Fatalf("facts len: got %d want 1", len(facts))
		}
		if facts[0].Object != "vegan" {
			t.Errorf("facts[0]: got %q want vegan", facts[0].Object)
		}
	})

	t.Run("retract removes user facts mentioning the text", func(t *testing.T) {
		store := circleai.NewSelfBeliefStore()
		recordAll(t, store, ex, ctx, "i am vegetarian", strptr("t1"))
		removed := store.Retract("vegetarian")
		if removed != 1 {
			t.Errorf("removed: got %d want 1", removed)
		}
		if len(store.SelfFacts()) != 0 {
			t.Errorf("expected no self facts after retract")
		}
	})

	t.Run("provenance returns the distinct source turns behind user facts", func(t *testing.T) {
		store := circleai.NewSelfBeliefStore()
		mk := func(obj, predicate, source string) circleai.PersonalBelief {
			return circleai.PersonalBelief{
				Attribution:   circleai.AttributionSelf,
				Subject:       "user",
				Predicate:     predicate,
				Object:        obj,
				Confidence:    0.6,
				Source:        strptr(source),
				RecordedAtUTC: time.Now().UTC(),
			}
		}
		mustRecord(t, store, mk("vegetarian", "diet", "t1"))
		mustRecord(t, store, mk("hiking", "hobby", "t2"))
		prov := store.Provenance()
		sort.Strings(prov)
		assertTexts(t, prov, []string{"t1", "t2"})
	})
}

func recordAll(t *testing.T, store *circleai.SelfBeliefStore, ex *circleai.HeuristicBeliefExtractor, ctx context.Context, text string, src *string) {
	t.Helper()
	bs, err := ex.Extract(ctx, text, src)
	if err != nil {
		t.Fatalf("Extract: %v", err)
	}
	for _, b := range bs {
		mustRecord(t, store, b)
	}
}

func mustRecord(t *testing.T, store *circleai.SelfBeliefStore, b circleai.PersonalBelief) {
	t.Helper()
	if err := store.Record(b); err != nil {
		t.Fatalf("Record: %v", err)
	}
}

func nonSelfHas(store *circleai.SelfBeliefStore, obj string) bool {
	for _, b := range store.NonSelf() {
		if b.Object == obj {
			return true
		}
	}
	return false
}
