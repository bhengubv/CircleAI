// companion_session_test.go
//
// Verifies the concrete CompanionSession end-to-end: a turn recalls fused memory
// + the user's own facts into the system prompt, calls the generator, persists
// the exchange, hands it to the background encoder, recalls it on a later turn,
// and streams. Mirrors the TS pilot suite tests/companion_session.test.ts 1:1.

package circleai_test

import (
	"context"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"

	circleai "github.com/bhengubv/CircleAI/go"
)

// capturingGenerator records the prompt it was handed and returns a canned reply
// / chunks.
type capturingGenerator struct {
	reply    string
	chunks   []string
	lastMsgs []circleai.ChatMessage
}

func (g *capturingGenerator) Generate(_ context.Context, messages []circleai.ChatMessage, _ *circleai.GenerationOptions) (string, error) {
	g.lastMsgs = append([]circleai.ChatMessage(nil), messages...)
	return g.reply, nil
}

func (g *capturingGenerator) Stream(_ context.Context, messages []circleai.ChatMessage, _ *circleai.GenerationOptions) (<-chan string, <-chan error) {
	g.lastMsgs = append([]circleai.ChatMessage(nil), messages...)
	tokens := make(chan string)
	errs := make(chan error, 1)
	chunks := g.chunks
	if chunks == nil {
		chunks = []string{g.reply}
	}
	go func() {
		defer close(tokens)
		defer close(errs)
		for _, c := range chunks {
			tokens <- c
		}
	}()
	return tokens, errs
}

func (g *capturingGenerator) Close() error { return nil }

func recordSelfFact(t *testing.T, beliefs *circleai.SelfBeliefStore, text string) {
	t.Helper()
	bx := &circleai.HeuristicBeliefExtractor{}
	bs, err := bx.Extract(context.Background(), text, strptr("t0"))
	if err != nil {
		t.Fatalf("Extract: %v", err)
	}
	for _, b := range bs {
		if err := beliefs.Record(b); err != nil {
			t.Fatalf("Record: %v", err)
		}
	}
}

type sessionExtras struct {
	beliefs *circleai.SelfBeliefStore
	encoder *circleai.CompanionMemoryEncoder
}

func makeSession(t *testing.T, gen circleai.IChatGenerator, episodic *circleai.InMemoryEpisodicStore, extras sessionExtras) *circleai.CompanionSession {
	t.Helper()
	recall, err := circleai.NewFusedRecall(episodic, nil, nil)
	if err != nil {
		t.Fatalf("NewFusedRecall: %v", err)
	}
	session, err := circleai.NewCompanionSession(gen, episodic, recall, circleai.CompanionSessionOptions{
		SessionID:  "s1",
		IdentityID: "u1",
		Interface:  circleai.InterfaceKindMobile,
		Beliefs:    extras.beliefs,
		Encoder:    extras.encoder,
	})
	if err != nil {
		t.Fatalf("NewCompanionSession: %v", err)
	}
	return session
}

func seedEntry(userText, assistantText string) circleai.EpisodicMemoryEntry {
	return circleai.EpisodicMemoryEntry{
		ID:            uuid.New(),
		RecordedAtUTC: time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC),
		UserText:      userText,
		AssistantText: assistantText,
	}
}

func TestCompanionSession_SendPath(t *testing.T) {
	ctx := context.Background()

	t.Run("injects recalled memories AND user facts into the system prompt", func(t *testing.T) {
		episodic := circleai.NewInMemoryEpisodicStoreDefault()
		mustAdd(t, episodic, seedEntry("I have a peanut allergy", "Noted"))
		beliefs := circleai.NewSelfBeliefStore()
		recordSelfFact(t, beliefs, "i am vegetarian")

		gen := &capturingGenerator{reply: "Here are some options"}
		session := makeSession(t, gen, episodic, sessionExtras{beliefs: beliefs})

		reply, err := session.Send(ctx, "what can I eat?")
		if err != nil {
			t.Fatalf("Send: %v", err)
		}
		if reply != "Here are some options" {
			t.Errorf("reply: got %q", reply)
		}

		system := gen.lastMsgs[0]
		if system.Role != "system" {
			t.Errorf("first message role: got %q want system", system.Role)
		}
		if !strings.Contains(system.Content, "peanut allergy") {
			t.Errorf("recalled memory should be in the prompt:\n%s", system.Content)
		}
		if !strings.Contains(system.Content, "vegetarian") {
			t.Errorf("user fact should be in the prompt:\n%s", system.Content)
		}
		last := gen.lastMsgs[len(gen.lastMsgs)-1]
		if last.Content != "what can I eat?" {
			t.Errorf("last message: got %q want the user message", last.Content)
		}
	})

	t.Run("persists the turn and grows the history", func(t *testing.T) {
		episodic := circleai.NewInMemoryEpisodicStoreDefault()
		session := makeSession(t, &capturingGenerator{reply: "ok"}, episodic, sessionExtras{})

		if _, err := session.Send(ctx, "hello"); err != nil {
			t.Fatalf("Send: %v", err)
		}
		count, _ := episodic.Count(ctx)
		if count != 1 {
			t.Errorf("episodic count: got %d want 1", count)
		}
		hist := session.History()
		if len(hist) != 2 {
			t.Fatalf("history len: got %d want 2", len(hist))
		}
		if hist[0].Role != "user" || hist[1].Role != "assistant" {
			t.Errorf("history roles: got %q,%q", hist[0].Role, hist[1].Role)
		}
	})

	t.Run("recalls a prior turn on a later turn (memory persists across the session)", func(t *testing.T) {
		episodic := circleai.NewInMemoryEpisodicStoreDefault()
		gen := &capturingGenerator{reply: "noted"}
		session := makeSession(t, gen, episodic, sessionExtras{})

		if _, err := session.Send(ctx, "my favourite colour is blue"); err != nil {
			t.Fatalf("Send: %v", err)
		}
		if _, err := session.Send(ctx, "what's my favourite colour?"); err != nil {
			t.Fatalf("Send: %v", err)
		}

		system := gen.lastMsgs[0]
		if !strings.Contains(system.Content, "favourite colour is blue") {
			t.Errorf("the earlier turn should be recalled:\n%s", system.Content)
		}
	})

	t.Run("hands the turn to the background encoder, filling the graph", func(t *testing.T) {
		episodic := circleai.NewInMemoryEpisodicStoreDefault()
		graph := circleai.NewKnowledgeGraph()
		encoder := newEncoder(t, &circleai.HeuristicKnowledgeGraphExtractor{}, graph, nil, nil, 0)
		session := makeSession(t, &capturingGenerator{reply: "ok"}, episodic, sessionExtras{encoder: encoder})

		if _, err := session.Send(ctx, "remember my dentist appointment"); err != nil {
			t.Fatalf("Send: %v", err)
		}
		if err := encoder.Close(); err != nil {
			t.Fatalf("encoder.Close: %v", err)
		}

		found := false
		for _, tr := range graph.AllTriples() {
			if tr.Object == "dentist" {
				found = true
			}
		}
		if !found {
			t.Errorf("the encoder should have extracted the turn into the graph")
		}
	})
}

func TestCompanionSession_StreamAndContext(t *testing.T) {
	ctx := context.Background()

	t.Run("streams chunks and still persists the full reply", func(t *testing.T) {
		episodic := circleai.NewInMemoryEpisodicStoreDefault()
		gen := &capturingGenerator{reply: "unused", chunks: []string{"Hel", "lo"}}
		session := makeSession(t, gen, episodic, sessionExtras{})

		tokens, errs := session.Stream(ctx, "hi")
		var chunks []string
		for c := range tokens {
			chunks = append(chunks, c)
		}
		if err := <-errs; err != nil {
			t.Fatalf("stream error: %v", err)
		}

		assertTexts(t, chunks, []string{"Hel", "lo"})
		count, _ := episodic.Count(ctx)
		if count != 1 {
			t.Errorf("episodic count: got %d want 1", count)
		}
		hist := session.History()
		if len(hist) != 2 || hist[1].Content != "Hello" {
			t.Errorf("accumulated reply should be persisted; got %+v", hist)
		}
	})

	t.Run("GetContext reflects the memories recalled on the last turn", func(t *testing.T) {
		episodic := circleai.NewInMemoryEpisodicStoreDefault()
		mustAdd(t, episodic, seedEntry("I live in Durban", "Nice"))
		session := makeSession(t, &capturingGenerator{reply: "ok"}, episodic, sessionExtras{})

		if _, err := session.Send(ctx, "where do I live?"); err != nil {
			t.Fatalf("Send: %v", err)
		}
		snippets := session.GetContext().RecentMemorySnippets
		if !contains(snippets, "I live in Durban") {
			t.Errorf("context snippets should include the recalled memory: %v", snippets)
		}
	})

	t.Run("Agent returns a reply and persists (no tool loop in the pilot)", func(t *testing.T) {
		episodic := circleai.NewInMemoryEpisodicStoreDefault()
		session := makeSession(t, &capturingGenerator{reply: "done"}, episodic, sessionExtras{})
		reply, err := session.Agent(ctx, "do the thing")
		if err != nil {
			t.Fatalf("Agent: %v", err)
		}
		if reply != "done" {
			t.Errorf("reply: got %q want done", reply)
		}
		count, _ := episodic.Count(ctx)
		if count != 1 {
			t.Errorf("episodic count: got %d want 1", count)
		}
	})
}
