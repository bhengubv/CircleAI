// llm_extractor_test.go
//
// Verifies LlmKnowledgeGraphExtractor: parses a clean JSON array of triples,
// tolerates prose/markdown-fence-wrapped JSON, defaults confidence when "c" is
// missing/invalid, clamps out-of-range confidence, skips objects with blank
// s/p/o, and returns nil on garbage / on an empty turn / on a failing generator.
// Mirrors the TS pilot suite tests/llm_extractor.test.ts 1:1.

package circleai_test

import (
	"context"
	"errors"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// fakeChatGenerator returns a canned reply and records the messages it was
// handed. Minimal IChatGenerator for the extractor tests.
type fakeChatGenerator struct {
	reply    string
	lastMsgs []circleai.ChatMessage
}

func (g *fakeChatGenerator) Generate(_ context.Context, messages []circleai.ChatMessage, _ *circleai.GenerationOptions) (string, error) {
	g.lastMsgs = append([]circleai.ChatMessage(nil), messages...)
	return g.reply, nil
}

func (g *fakeChatGenerator) Stream(_ context.Context, _ []circleai.ChatMessage, _ *circleai.GenerationOptions) (<-chan string, <-chan error) {
	tokens := make(chan string)
	errs := make(chan error, 1)
	go func() {
		defer close(tokens)
		defer close(errs)
		tokens <- g.reply
	}()
	return tokens, errs
}

func (g *fakeChatGenerator) Close() error { return nil }

// throwingChatGenerator always errors — exercises graceful degradation.
type throwingChatGenerator struct{}

func (g *throwingChatGenerator) Generate(_ context.Context, _ []circleai.ChatMessage, _ *circleai.GenerationOptions) (string, error) {
	return "", errors.New("model offline")
}

func (g *throwingChatGenerator) Stream(_ context.Context, _ []circleai.ChatMessage, _ *circleai.GenerationOptions) (<-chan string, <-chan error) {
	tokens := make(chan string)
	errs := make(chan error, 1)
	close(tokens)
	close(errs)
	return tokens, errs
}

func (g *throwingChatGenerator) Close() error { return nil }

func newLlmExtractor(t *testing.T, gen circleai.IChatGenerator) *circleai.LlmKnowledgeGraphExtractor {
	t.Helper()
	ex, err := circleai.NewLlmKnowledgeGraphExtractor(gen)
	if err != nil {
		t.Fatalf("NewLlmKnowledgeGraphExtractor: %v", err)
	}
	return ex
}

func mustExtractLlm(t *testing.T, ex *circleai.LlmKnowledgeGraphExtractor, user, assistant string, src *string) []circleai.KnowledgeTriple {
	t.Helper()
	triples, err := ex.ExtractFromTurn(context.Background(), user, assistant, src)
	if err != nil {
		t.Fatalf("ExtractFromTurn: %v", err)
	}
	return triples
}

func TestLlmExtractor_CleanJSON(t *testing.T) {
	t.Run("parses a plain JSON array of triples", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: `[{"s":"Tony","p":"has_daughter","o":"Alex","c":0.9},` +
			`{"s":"Alex","p":"lives_in","o":"Durban","c":0.5}]`}
		ex := newLlmExtractor(t, gen)
		triples := mustExtractLlm(t, ex, "hi", "ok", strptr("ep1"))

		if len(triples) != 2 {
			t.Fatalf("len: got %d want 2", len(triples))
		}
		if triples[0].Subject != "Tony" {
			t.Errorf("subject: got %q want Tony", triples[0].Subject)
		}
		if triples[0].Predicate != "has_daughter" {
			t.Errorf("predicate: got %q want has_daughter", triples[0].Predicate)
		}
		if triples[0].Object != "Alex" {
			t.Errorf("object: got %q want Alex", triples[0].Object)
		}
		if triples[0].Confidence != 0.9 {
			t.Errorf("confidence: got %v want 0.9", triples[0].Confidence)
		}
		if triples[0].Source == nil || *triples[0].Source != "ep1" {
			t.Errorf("source: got %v want ep1", triples[0].Source)
		}
		if triples[0].RecordedAtUTC.IsZero() {
			t.Errorf("recordedAtUtc should be set")
		}
		if triples[1].Object != "Durban" {
			t.Errorf("object: got %q want Durban", triples[1].Object)
		}
		if triples[1].Confidence != 0.5 {
			t.Errorf("confidence: got %v want 0.5", triples[1].Confidence)
		}
	})

	t.Run("sends the verbatim system prompt + USER/ASSISTANT-framed user message", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: "[]"}
		ex := newLlmExtractor(t, gen)
		_ = mustExtractLlm(t, ex, "the weather", "is sunny", strptr("ep1"))

		if len(gen.lastMsgs) != 2 {
			t.Fatalf("lastMsgs len: got %d want 2", len(gen.lastMsgs))
		}
		if gen.lastMsgs[0].Role != "system" {
			t.Errorf("role[0]: got %q want system", gen.lastMsgs[0].Role)
		}
		const wantPrefix = "You are a knowledge-graph extractor."
		if len(gen.lastMsgs[0].Content) < len(wantPrefix) || gen.lastMsgs[0].Content[:len(wantPrefix)] != wantPrefix {
			t.Errorf("system prompt does not start with %q: got %q", wantPrefix, gen.lastMsgs[0].Content)
		}
		if gen.lastMsgs[1].Role != "user" {
			t.Errorf("role[1]: got %q want user", gen.lastMsgs[1].Role)
		}
		if gen.lastMsgs[1].Content != "USER:\nthe weather\nASSISTANT:\nis sunny\n" {
			t.Errorf("user message: got %q", gen.lastMsgs[1].Content)
		}
	})
}

func TestLlmExtractor_DefensiveParsing(t *testing.T) {
	t.Run("extracts JSON embedded in prose / markdown fences", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: "Sure! Here are the triples:\n```json\n" +
			`[{"s":"Paris","p":"capital_of","o":"France","c":0.95}]` +
			"\n```\nHope that helps."}
		ex := newLlmExtractor(t, gen)
		triples := mustExtractLlm(t, ex, "u", "a", strptr("ep2"))

		if len(triples) != 1 {
			t.Fatalf("len: got %d want 1", len(triples))
		}
		if triples[0].Subject != "Paris" || triples[0].Predicate != "capital_of" ||
			triples[0].Object != "France" || triples[0].Confidence != 0.95 {
			t.Errorf("unexpected triple: %+v", triples[0])
		}
	})

	t.Run("defaults confidence to 0.75 when c is missing", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: `[{"s":"a","p":"b","o":"c"}]`}
		ex := newLlmExtractor(t, gen)
		triples := mustExtractLlm(t, ex, "u", "a", strptr("ep3"))
		if len(triples) != 1 {
			t.Fatalf("len: got %d want 1", len(triples))
		}
		if triples[0].Confidence != 0.75 {
			t.Errorf("confidence: got %v want 0.75", triples[0].Confidence)
		}
	})

	t.Run("defaults confidence to 0.75 when c is non-numeric", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: `[{"s":"a","p":"b","o":"c","c":"high"}]`}
		ex := newLlmExtractor(t, gen)
		triples := mustExtractLlm(t, ex, "u", "a", strptr("ep3"))
		if len(triples) != 1 {
			t.Fatalf("len: got %d want 1", len(triples))
		}
		if triples[0].Confidence != 0.75 {
			t.Errorf("confidence: got %v want 0.75", triples[0].Confidence)
		}
	})

	t.Run("clamps confidence into [0,1]", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: `[{"s":"a","p":"b","o":"c","c":5},{"s":"d","p":"e","o":"f","c":-2}]`}
		ex := newLlmExtractor(t, gen)
		triples := mustExtractLlm(t, ex, "u", "a", strptr("ep3"))
		if len(triples) != 2 {
			t.Fatalf("len: got %d want 2", len(triples))
		}
		if triples[0].Confidence != 1 {
			t.Errorf("confidence[0]: got %v want 1", triples[0].Confidence)
		}
		if triples[1].Confidence != 0 {
			t.Errorf("confidence[1]: got %v want 0", triples[1].Confidence)
		}
	})

	t.Run("skips objects whose s/p/o are blank or missing", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: `[{"s":"","p":"b","o":"c"},{"s":"a","p":"  ","o":"c"},{"s":"a","p":"b"},{"s":"keep","p":"p","o":"o"}]`}
		ex := newLlmExtractor(t, gen)
		triples := mustExtractLlm(t, ex, "u", "a", strptr("ep3"))
		if len(triples) != 1 {
			t.Fatalf("len: got %d want 1", len(triples))
		}
		if triples[0].Subject != "keep" {
			t.Errorf("subject: got %q want keep", triples[0].Subject)
		}
	})

	t.Run("skips non-object array entries", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: `[1, "two", null, {"s":"a","p":"b","o":"c"}]`}
		ex := newLlmExtractor(t, gen)
		triples := mustExtractLlm(t, ex, "u", "a", strptr("ep3"))
		if len(triples) != 1 {
			t.Fatalf("len: got %d want 1", len(triples))
		}
		if triples[0].Subject != "a" {
			t.Errorf("subject: got %q want a", triples[0].Subject)
		}
	})
}

func TestLlmExtractor_EmptyResults(t *testing.T) {
	t.Run("returns nothing on pure garbage (no brackets)", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: "I could not find any facts, sorry."}
		ex := newLlmExtractor(t, gen)
		if triples := mustExtractLlm(t, ex, "u", "a", strptr("ep4")); len(triples) != 0 {
			t.Errorf("len: got %d want 0", len(triples))
		}
	})

	t.Run("returns nothing on malformed JSON inside brackets", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: `[{"s":"a", "p": }]`}
		ex := newLlmExtractor(t, gen)
		if triples := mustExtractLlm(t, ex, "u", "a", strptr("ep4")); len(triples) != 0 {
			t.Errorf("len: got %d want 0", len(triples))
		}
	})

	t.Run("returns nothing when the JSON is an object, not an array", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: `{"s":"a","p":"b","o":"c"}`}
		ex := newLlmExtractor(t, gen)
		// No '[' before ']' — object braces only, so no valid slice.
		if triples := mustExtractLlm(t, ex, "u", "a", strptr("ep4")); len(triples) != 0 {
			t.Errorf("len: got %d want 0", len(triples))
		}
	})

	t.Run("returns nothing when both user and assistant text are blank (no LLM call)", func(t *testing.T) {
		gen := &fakeChatGenerator{reply: `[{"s":"a","p":"b","o":"c"}]`}
		ex := newLlmExtractor(t, gen)
		if triples := mustExtractLlm(t, ex, "   ", "", nil); len(triples) != 0 {
			t.Errorf("len: got %d want 0", len(triples))
		}
		if gen.lastMsgs != nil {
			t.Errorf("generator should not have been called")
		}
	})

	t.Run("returns nothing when the generator errors", func(t *testing.T) {
		ex := newLlmExtractor(t, &throwingChatGenerator{})
		if triples := mustExtractLlm(t, ex, "u", "a", strptr("ep5")); len(triples) != 0 {
			t.Errorf("len: got %d want 0", len(triples))
		}
	})
}
