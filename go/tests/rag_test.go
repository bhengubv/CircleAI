// rag_test.go
//
// Exercises RagContextBuilder + RagPipelineBuilder. Mirrors the TS pilot suite
// tests/rag.test.ts 1:1 plus the C# RagContextBuilderTests, the fluent-builder
// surface, and the embedder ranking path.

package circleai_test

import (
	"context"
	"errors"
	"strings"
	"testing"
	"time"

	"github.com/google/uuid"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── Helpers ──────────────────────────────────────────────────────────────────

type ragEntryOpts struct {
	userText      string
	assistantText string
	appContext    string
	embedding     []float32
	recorded      time.Time
}

func ragEpisodic(o ragEntryOpts) circleai.EpisodicMemoryEntry {
	rec := o.recorded
	if rec.IsZero() {
		rec = time.Date(2026, 6, 1, 12, 34, 0, 0, time.UTC)
	}
	ut := o.userText
	if ut == "" {
		ut = "u"
	}
	at := o.assistantText
	if at == "" {
		at = "a"
	}
	e := circleai.EpisodicMemoryEntry{
		ID:            uuid.New(),
		RecordedAtUTC: rec,
		UserText:      ut,
		AssistantText: at,
		Embedding:     o.embedding,
	}
	if o.appContext != "" {
		ac := o.appContext
		e.AppContext = &ac
	}
	return e
}

func countOccurrences(text, token string) int {
	return strings.Count(text, token)
}

// fixedEmbedder maps any query to a fixed vector.
type fixedEmbedder struct {
	vec []float32
}

func (f fixedEmbedder) Generate(_ context.Context, _ string) ([]float32, error) {
	return f.vec, nil
}

// throwingEmbedder always fails.
type throwingEmbedder struct{}

func (throwingEmbedder) Generate(_ context.Context, _ string) ([]float32, error) {
	return nil, errors.New("embedder offline")
}

// throwingEpisodicStore always returns an error — used to test resilience.
type throwingEpisodicStore struct{}

func (throwingEpisodicStore) Add(context.Context, circleai.EpisodicMemoryEntry) error {
	return errors.New("store failure")
}
func (throwingEpisodicStore) Search(context.Context, []float32, int) ([]circleai.EpisodicMemoryEntry, error) {
	return nil, errors.New("store failure")
}
func (throwingEpisodicStore) GetRecent(context.Context, int) ([]circleai.EpisodicMemoryEntry, error) {
	return nil, errors.New("store failure")
}
func (throwingEpisodicStore) Count(context.Context) (int, error) {
	return 0, errors.New("store failure")
}
func (throwingEpisodicStore) PruneOlderThan(context.Context, time.Time) (int, error) {
	return 0, errors.New("store failure")
}

func mustBuilder(t *testing.T, store circleai.IEpisodicMemoryStore, embedder circleai.ITextEmbedder, topK, maxChars int) *circleai.RagContextBuilder {
	t.Helper()
	b, err := circleai.NewRagContextBuilder(store, embedder, topK, maxChars)
	if err != nil {
		t.Fatalf("NewRagContextBuilder: %v", err)
	}
	return b
}

// ── Constructor guards ───────────────────────────────────────────────────────

func TestRagContextBuilder_NilStore(t *testing.T) {
	if _, err := circleai.NewRagContextBuilder(nil, nil, 5, 300); err == nil {
		t.Errorf("expected error for nil store")
	}
}

// ── Empty / missing query ────────────────────────────────────────────────────

func TestRagContextBuilder_EmptyQuery(t *testing.T) {
	ctx := context.Background()
	b := mustBuilder(t, circleai.NewInMemoryEpisodicStoreDefault(), nil, 5, 300)
	if got := b.BuildContext(ctx, ""); got != "" {
		t.Errorf("empty query: got %q want empty", got)
	}
	if got := b.BuildContext(ctx, "   "); got != "" {
		t.Errorf("whitespace query: got %q want empty", got)
	}
}

// ── Empty store ──────────────────────────────────────────────────────────────

func TestRagContextBuilder_EmptyStore(t *testing.T) {
	ctx := context.Background()
	b := mustBuilder(t, circleai.NewInMemoryEpisodicStoreDefault(), nil, 5, 300)
	if got := b.BuildContext(ctx, "hello"); got != "" {
		t.Errorf("empty store: got %q want empty", got)
	}
}

// ── Formatting (recency fallback, no embedder) ───────────────────────────────

func TestRagContextBuilder_FormattedBlock(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	mustAdd(t, store, ragEpisodic(ragEntryOpts{
		userText:      "What is SDPKT?",
		assistantText: "SDPKT is the TGN wallet.",
		recorded:      time.Date(2026, 6, 1, 11, 0, 0, 0, time.UTC),
	}))

	b := mustBuilder(t, store, nil, 3, 300)
	result := b.BuildContext(ctx, "tell me about the wallet")

	if result == "" {
		t.Fatal("result is empty")
	}
	for _, want := range []string{"What is SDPKT?", "SDPKT is the TGN wallet.", "[Relevant past exchanges"} {
		if !strings.Contains(result, want) {
			t.Errorf("result missing %q; got:\n%s", want, result)
		}
	}
}

func TestRagContextBuilder_UTCTimestampAndLabels(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	mustAdd(t, store, ragEpisodic(ragEntryOpts{
		userText:      "q",
		assistantText: "r",
		recorded:      time.Date(2026, 6, 1, 9, 5, 0, 0, time.UTC),
	}))
	b := mustBuilder(t, store, nil, 1, 300)
	result := b.BuildContext(ctx, "anything")
	for _, want := range []string{"[2026-06-01 09:05 UTC]", "User: q", "B!: r"} {
		if !strings.Contains(result, want) {
			t.Errorf("result missing %q; got:\n%s", want, result)
		}
	}
}

func TestRagContextBuilder_RespectsTopK(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	for i := 0; i < 10; i++ {
		mustAdd(t, store, ragEpisodic(ragEntryOpts{userText: "question", assistantText: "answer"}))
	}
	b := mustBuilder(t, store, nil, 2, 300)
	result := b.BuildContext(ctx, "any question")
	if n := countOccurrences(result, "• ["); n != 2 {
		t.Errorf("bullet count: got %d want 2", n)
	}
}

func TestRagContextBuilder_IncludesAppContext(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	mustAdd(t, store, ragEpisodic(ragEntryOpts{userText: "bid query", assistantText: "bid answer", appContext: "tgn.bidbaas"}))
	b := mustBuilder(t, store, nil, 3, 300)
	result := b.BuildContext(ctx, "bidding")
	if !strings.Contains(result, "tgn.bidbaas") {
		t.Errorf("result missing app context; got:\n%s", result)
	}
}

func TestRagContextBuilder_TruncatesLongText(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	longText := strings.Repeat("x", 500)
	mustAdd(t, store, ragEpisodic(ragEntryOpts{userText: longText, assistantText: "a"}))
	// maxCharsPerEntry 100 → half 50 → truncate to 49 chars + "…"
	b := mustBuilder(t, store, nil, 1, 100)
	result := b.BuildContext(ctx, "q")
	if !strings.Contains(result, strings.Repeat("x", 49)+"…") {
		t.Errorf("expected 49 x's + ellipsis; got:\n%s", result)
	}
	if strings.Contains(result, strings.Repeat("x", 51)) {
		t.Errorf("text not truncated; got:\n%s", result)
	}
}

// ── Embedder ranking path ────────────────────────────────────────────────────

func TestRagContextBuilder_RanksByEmbedding(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	mustAdd(t, store, ragEpisodic(ragEntryOpts{userText: "near", assistantText: "n", embedding: []float32{1, 0}}))
	mustAdd(t, store, ragEpisodic(ragEntryOpts{userText: "far", assistantText: "f", embedding: []float32{0, 1}}))

	// Embedder maps any query to the x-axis, so "near" should rank first.
	b := mustBuilder(t, store, fixedEmbedder{vec: []float32{1, 0}}, 1, 300)
	result := b.BuildContext(ctx, "anything")
	if !strings.Contains(result, "near") {
		t.Errorf("result missing 'near'; got:\n%s", result)
	}
	if strings.Contains(result, "far") {
		t.Errorf("result should not contain 'far'; got:\n%s", result)
	}
}

func TestRagContextBuilder_EmbedderThrowsFallsBackToRecency(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	mustAdd(t, store, ragEpisodic(ragEntryOpts{
		userText:      "only",
		assistantText: "entry",
		recorded:      time.Date(2026, 6, 1, 0, 0, 0, 0, time.UTC),
	}))
	b := mustBuilder(t, store, throwingEmbedder{}, 3, 300)
	result := b.BuildContext(ctx, "q")
	if !strings.Contains(result, "only") {
		t.Errorf("result missing 'only'; got:\n%s", result)
	}
}

// ── Resilience ───────────────────────────────────────────────────────────────

func TestRagContextBuilder_StoreThrowsReturnsEmpty(t *testing.T) {
	ctx := context.Background()
	b := mustBuilder(t, throwingEpisodicStore{}, nil, 5, 300)
	if got := b.BuildContext(ctx, "query"); got != "" {
		t.Errorf("store throws: got %q want empty", got)
	}
}

// ── RagPipelineBuilder ───────────────────────────────────────────────────────

func TestRagPipelineBuilder_FromStore(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	mustAdd(t, store, ragEpisodic(ragEntryOpts{userText: "hi", assistantText: "hello"}))
	rag, err := circleai.NewRagPipelineBuilder().WithStore(store).WithTopK(2).WithMaxCharsPerEntry(500).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if ctxStr := rag.BuildContext(ctx, "greeting"); !strings.Contains(ctxStr, "hi") {
		t.Errorf("result missing 'hi'; got:\n%s", ctxStr)
	}
}

func TestRagPipelineBuilder_WithInMemoryStore(t *testing.T) {
	ctx := context.Background()
	rag, err := circleai.NewRagPipelineBuilder().WithInMemoryStore().Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if got := rag.BuildContext(ctx, "nothing stored"); got != "" {
		t.Errorf("got %q want empty", got)
	}
}

func TestRagPipelineBuilder_BuildWithoutStoreThrows(t *testing.T) {
	if _, err := circleai.NewRagPipelineBuilder().Build(); err == nil {
		t.Errorf("expected error building without a store")
	}
}

func TestRagPipelineBuilder_TopKRejectsBelowOne(t *testing.T) {
	if _, err := circleai.NewRagPipelineBuilder().WithInMemoryStore().WithTopK(0).Build(); err == nil {
		t.Errorf("expected error for topK=0")
	}
}

func TestRagPipelineBuilder_MaxCharsRejectsBelow50(t *testing.T) {
	if _, err := circleai.NewRagPipelineBuilder().WithInMemoryStore().WithMaxCharsPerEntry(49).Build(); err == nil {
		t.Errorf("expected error for maxChars=49")
	}
}

func TestRagPipelineBuilder_WithEmbedder(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryEpisodicStoreDefault()
	mustAdd(t, store, ragEpisodic(ragEntryOpts{userText: "near", assistantText: "n", embedding: []float32{1, 0}}))
	mustAdd(t, store, ragEpisodic(ragEntryOpts{userText: "far", assistantText: "f", embedding: []float32{0, 1}}))
	rag, err := circleai.NewRagPipelineBuilder().WithStore(store).WithEmbedder(fixedEmbedder{vec: []float32{1, 0}}).WithTopK(1).Build()
	if err != nil {
		t.Fatalf("Build: %v", err)
	}
	if ctxStr := rag.BuildContext(ctx, "q"); !strings.Contains(ctxStr, "near") {
		t.Errorf("result missing 'near'; got:\n%s", ctxStr)
	}
}
