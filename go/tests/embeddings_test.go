// embeddings_test.go
//
// Verifies the Embeddings + Embeddings.Local ports:
//   - TextEmbedder lazy init (resolve+verify via IModelManager, build backend
//     once), L2 normalisation, disposal — TextEmbedder / IEmbeddingBackend.
//   - InMemoryEmbeddingStore add/search/remove + TurboQuant round-trip + save/
//     load round-trip + dim guards — InMemoryEmbeddingStore.
//   - InMemoryEmbeddingIndex add returns insertion id, cosine search ordering,
//     save/load, dim/bitwidth guards — TurboVecEmbeddingIndex contract.
//   - IndexedEmbeddingStore add-only + soft delete + over-fetch search + docs
//     sidecar save/load — HnswEmbeddingStore.

package circleai_test

import (
	"context"
	"math"
	"path/filepath"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── fakes ─────────────────────────────────────────────────────────────────

// hashEncoder is a deterministic IEmbeddingEncoder: it maps text to a fixed-dim
// vector via a simple rolling hash, then L2-normalises. Same text → same vector.
type hashEncoder struct{ dim int }

func (e *hashEncoder) Dimension() int { return e.dim }
func (e *hashEncoder) Encode(_ context.Context, text string) ([]float32, error) {
	v := make([]float32, e.dim)
	var h uint32 = 2166136261
	for _, b := range []byte(text) {
		h ^= uint32(b)
		h *= 16777619
		v[int(h)%e.dim] += 1.0
	}
	// Guarantee non-zero.
	v[len(text)%e.dim] += 0.5
	circleai.L2NormalizeEmbedding(v)
	return v, nil
}

// fakeManager resolves a fixed path and verifies against a fixed checksum flag.
type fakeManager struct {
	path     string
	verifyOK bool
}

func (m *fakeManager) GetModelPath(context.Context, string) (string, error) { return m.path, nil }
func (m *fakeManager) VerifyModel(context.Context, string, []byte) (bool, error) {
	return m.verifyOK, nil
}

// fakeBackend embeds text as a constant vector (pre-L2-normalised via helper).
type fakeBackend struct {
	dim    int
	built  *int
	closed *bool
}

func (b *fakeBackend) Dimension() int { return b.dim }
func (b *fakeBackend) Embed(text string) ([]float32, error) {
	v := make([]float32, b.dim)
	for i := range v {
		v[i] = float32(len(text) + i + 1)
	}
	circleai.L2NormalizeEmbedding(v)
	return v, nil
}
func (b *fakeBackend) Close() error {
	if b.closed != nil {
		*b.closed = true
	}
	return nil
}

// ── TextEmbedder ──────────────────────────────────────────────────────────

func TestTextEmbedder_LazyInitAndNormalises(t *testing.T) {
	ctx := context.Background()
	built := 0
	closed := false
	factory := func(path string) (circleai.EmbeddingBackend, error) {
		built++
		return &fakeBackend{dim: 8, built: &built, closed: &closed}, nil
	}
	mgr := &fakeManager{path: "/models/emb", verifyOK: true}
	emb, err := circleai.NewTextEmbedder(mgr, []byte{1, 2, 3}, factory)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	v1, err := emb.Generate(ctx, "hello")
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if len(v1) != 8 {
		t.Errorf("dim: got %d", len(v1))
	}
	// L2-normalised → unit length.
	var mag float64
	for _, x := range v1 {
		mag += float64(x) * float64(x)
	}
	if math.Abs(math.Sqrt(mag)-1.0) > 1e-5 {
		t.Errorf("vector not unit length: |v|=%v", math.Sqrt(mag))
	}
	// Second call reuses the backend (built once).
	if _, err := emb.Generate(ctx, "world"); err != nil {
		t.Fatalf("generate 2: %v", err)
	}
	if built != 1 {
		t.Errorf("backend should be built exactly once, built=%d", built)
	}

	if err := emb.Close(); err != nil {
		t.Fatalf("close: %v", err)
	}
	if !closed {
		t.Error("backend should be closed on embedder close")
	}
	// Empty text rejected.
	if _, err := circleai.NewTextEmbedder(mgr, []byte{1}, factory); err != nil {
		t.Fatal(err)
	}
}

func TestTextEmbedder_ChecksumFailureAborts(t *testing.T) {
	factory := func(string) (circleai.EmbeddingBackend, error) {
		t.Fatal("factory must not be called when verification fails")
		return nil, nil
	}
	mgr := &fakeManager{path: "/m", verifyOK: false}
	emb, _ := circleai.NewTextEmbedder(mgr, []byte{9}, factory)
	if _, err := emb.Generate(context.Background(), "x"); err == nil {
		t.Fatal("failed checksum verification should error")
	}
}

func TestTextEmbedder_EmptyTextRejected(t *testing.T) {
	mgr := &fakeManager{path: "/m", verifyOK: true}
	emb, _ := circleai.NewTextEmbedder(mgr, []byte{1}, func(string) (circleai.EmbeddingBackend, error) {
		return &fakeBackend{dim: 4}, nil
	})
	if _, err := emb.Generate(context.Background(), "   "); err == nil {
		t.Fatal("blank text should error")
	}
}

// ── InMemoryEmbeddingStore ────────────────────────────────────────────────

func TestInMemoryEmbeddingStore_AddSearchRemove(t *testing.T) {
	ctx := context.Background()
	enc := &hashEncoder{dim: 64}
	store, err := circleai.NewInMemoryEmbeddingStore(enc, 4)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	defer store.Close()

	docs := []circleai.EmbeddingDocument{
		{ID: "a", Text: "the quick brown fox"},
		{ID: "b", Text: "lazy dog sleeps"},
		{ID: "c", Text: "the quick brown fox jumps"},
	}
	for _, d := range docs {
		if err := store.Add(ctx, d); err != nil {
			t.Fatalf("add %s: %v", d.ID, err)
		}
	}
	if store.Count() != 3 {
		t.Errorf("count: got %d", store.Count())
	}

	// Query text identical to doc "a" — "a" should be the top hit (self-match).
	hits, err := store.Search(ctx, "the quick brown fox", 2)
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(hits) == 0 {
		t.Fatal("expected hits")
	}
	if hits[0].Document.ID != "a" {
		t.Errorf("top hit should be self-match 'a', got %q (score %v)", hits[0].Document.ID, hits[0].Score)
	}
	// Scores must be sorted descending.
	for i := 1; i < len(hits); i++ {
		if hits[i].Score > hits[i-1].Score {
			t.Errorf("hits not sorted descending at %d", i)
		}
	}

	removed, _ := store.Remove(ctx, "a")
	if !removed {
		t.Error("remove should report true for existing id")
	}
	if store.Count() != 2 {
		t.Errorf("count after remove: got %d", store.Count())
	}
	removedAgain, _ := store.Remove(ctx, "a")
	if removedAgain {
		t.Error("second remove should report false")
	}
}

func TestInMemoryEmbeddingStore_SaveLoadRoundTrip(t *testing.T) {
	ctx := context.Background()
	enc := &hashEncoder{dim: 32}
	store, _ := circleai.NewInMemoryEmbeddingStore(enc, 4)
	_ = store.AddVector(ctx, circleai.EmbeddingDocument{
		ID: "doc1", Text: "payload text", Metadata: map[string]string{"lang": "en", "src": "test"},
	}, mustEncode(t, enc, "payload text"))
	_ = store.AddVector(ctx, circleai.EmbeddingDocument{ID: "doc2", Text: "second"}, mustEncode(t, enc, "second"))

	path := filepath.Join(t.TempDir(), "store.celq")
	if err := store.Save(ctx, path); err != nil {
		t.Fatalf("save: %v", err)
	}

	store2, _ := circleai.NewInMemoryEmbeddingStore(&hashEncoder{dim: 32}, 4)
	if err := store2.Load(ctx, path); err != nil {
		t.Fatalf("load: %v", err)
	}
	if store2.Count() != 2 {
		t.Errorf("loaded count: got %d want 2", store2.Count())
	}
	// Metadata survives the round-trip: search doc1's text and confirm.
	hits, _ := store2.SearchVector(ctx, mustEncode(t, enc, "payload text"), 1)
	if len(hits) != 1 || hits[0].Document.ID != "doc1" {
		t.Fatalf("expected doc1 top hit after load, got %+v", hits)
	}
	if hits[0].Document.Metadata["lang"] != "en" || hits[0].Document.Metadata["src"] != "test" {
		t.Errorf("metadata not preserved: %+v", hits[0].Document.Metadata)
	}
}

func TestInMemoryEmbeddingStore_DimMismatchGuards(t *testing.T) {
	store, _ := circleai.NewInMemoryEmbeddingStore(&hashEncoder{dim: 16}, 4)
	if err := store.AddVector(context.Background(), circleai.EmbeddingDocument{ID: "x", Text: "t"}, make([]float32, 8)); err == nil {
		t.Error("wrong vector dim should error")
	}
	if _, err := store.SearchVector(context.Background(), make([]float32, 8), 3); err == nil {
		t.Error("wrong query dim should error")
	}
	if _, err := circleai.NewInMemoryEmbeddingStore(&hashEncoder{dim: 16}, 9); err == nil {
		t.Error("bitsPerDim=9 should error")
	}
}

// ── InMemoryEmbeddingIndex ────────────────────────────────────────────────

func TestInMemoryEmbeddingIndex_AddSearch(t *testing.T) {
	ctx := context.Background()
	idx, err := circleai.NewInMemoryEmbeddingIndex(8, 4)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	defer idx.Close()

	vecs := [][]float32{
		{1, 0, 0, 0, 0, 0, 0, 0},
		{0, 1, 0, 0, 0, 0, 0, 0},
		{0.9, 0.1, 0, 0, 0, 0, 0, 0},
	}
	for i, v := range vecs {
		id, err := idx.Add(ctx, v)
		if err != nil {
			t.Fatalf("add: %v", err)
		}
		if id != int64(i) {
			t.Errorf("Add should return insertion id %d, got %d", i, id)
		}
	}
	if idx.Count() != 3 {
		t.Errorf("count: got %d", idx.Count())
	}

	// Query along axis 0 → vec 0 closest, then vec 2, then vec 1.
	hits, err := idx.Search(ctx, []float32{1, 0, 0, 0, 0, 0, 0, 0}, 3)
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(hits) != 3 {
		t.Fatalf("expected 3 hits, got %d", len(hits))
	}
	if hits[0].InternalID != 0 {
		t.Errorf("closest should be id 0, got %d", hits[0].InternalID)
	}
	if hits[1].InternalID != 2 {
		t.Errorf("second closest should be id 2, got %d", hits[1].InternalID)
	}
	for i := 1; i < len(hits); i++ {
		if hits[i].Score > hits[i-1].Score {
			t.Errorf("hits not descending at %d", i)
		}
	}
}

func TestInMemoryEmbeddingIndex_EmptyAndGuards(t *testing.T) {
	ctx := context.Background()
	idx, _ := circleai.NewInMemoryEmbeddingIndex(8, 4)
	hits, err := idx.Search(ctx, make([]float32, 8), 5)
	if err != nil || len(hits) != 0 {
		t.Errorf("empty index search should be empty: %d hits, err=%v", len(hits), err)
	}
	if _, err := idx.Add(ctx, make([]float32, 4)); err == nil {
		t.Error("wrong add dim should error")
	}
	if _, err := circleai.NewInMemoryEmbeddingIndex(7, 4); err == nil {
		t.Error("non-multiple-of-8 dim should error")
	}
	if _, err := circleai.NewInMemoryEmbeddingIndex(8, 5); err == nil {
		t.Error("bitWidth 5 should error")
	}
	if _, err := circleai.NewInMemoryEmbeddingIndex(8, 1); err == nil {
		t.Error("bitWidth 1 should error")
	}
}

func TestInMemoryEmbeddingIndex_SaveLoad(t *testing.T) {
	ctx := context.Background()
	idx, _ := circleai.NewInMemoryEmbeddingIndex(8, 3)
	for i := 0; i < 5; i++ {
		v := make([]float32, 8)
		v[i%8] = float32(i + 1)
		_, _ = idx.Add(ctx, v)
	}
	path := filepath.Join(t.TempDir(), "idx.ivex")
	if err := idx.Save(ctx, path); err != nil {
		t.Fatalf("save: %v", err)
	}
	idx2, _ := circleai.NewInMemoryEmbeddingIndex(8, 3)
	if err := idx2.Load(ctx, path); err != nil {
		t.Fatalf("load: %v", err)
	}
	if idx2.Count() != 5 {
		t.Errorf("loaded count: got %d want 5", idx2.Count())
	}
	if idx2.BitWidth() != 3 {
		t.Errorf("loaded bitWidth: got %d want 3", idx2.BitWidth())
	}
	// Dim-mismatch on load must error.
	idx3, _ := circleai.NewInMemoryEmbeddingIndex(16, 3)
	if err := idx3.Load(ctx, path); err == nil {
		t.Error("dim-mismatch load should error")
	}
}

// ── IndexedEmbeddingStore ─────────────────────────────────────────────────

func TestIndexedEmbeddingStore_AddOnlyAndSoftDelete(t *testing.T) {
	ctx := context.Background()
	enc := &hashEncoder{dim: 16}
	store, err := circleai.NewIndexedEmbeddingStore(enc, 4)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	defer store.Close()

	if err := store.Add(ctx, circleai.EmbeddingDocument{ID: "x", Text: "alpha beta"}); err != nil {
		t.Fatalf("add: %v", err)
	}
	// Add-only: duplicate id errors.
	if err := store.Add(ctx, circleai.EmbeddingDocument{ID: "x", Text: "again"}); err == nil {
		t.Error("duplicate id should error (add-only)")
	}
	_ = store.Add(ctx, circleai.EmbeddingDocument{ID: "y", Text: "gamma delta"})
	if store.Count() != 2 || store.LiveCount() != 2 {
		t.Errorf("counts: total=%d live=%d", store.Count(), store.LiveCount())
	}

	removed, _ := store.Remove(ctx, "x")
	if !removed {
		t.Error("remove existing should be true")
	}
	// Soft delete: total unchanged, live drops.
	if store.Count() != 2 {
		t.Errorf("total count should stay 2 after soft delete, got %d", store.Count())
	}
	if store.LiveCount() != 1 {
		t.Errorf("live count should be 1 after delete, got %d", store.LiveCount())
	}

	// Search must not return the removed doc.
	hits, _ := store.Search(ctx, "alpha beta", 5)
	for _, h := range hits {
		if h.Document.ID == "x" {
			t.Error("removed doc must not appear in results")
		}
	}
}

func TestIndexedEmbeddingStore_SearchSelfMatch(t *testing.T) {
	ctx := context.Background()
	enc := &hashEncoder{dim: 24}
	store, _ := circleai.NewIndexedEmbeddingStore(enc, 4)
	_ = store.Add(ctx, circleai.EmbeddingDocument{ID: "p", Text: "machine learning models"})
	_ = store.Add(ctx, circleai.EmbeddingDocument{ID: "q", Text: "cooking recipes dinner"})
	_ = store.Add(ctx, circleai.EmbeddingDocument{ID: "r", Text: "machine learning models advanced"})

	hits, err := store.Search(ctx, "machine learning models", 2)
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(hits) == 0 || hits[0].Document.ID != "p" {
		t.Errorf("expected self-match 'p' first, got %+v", hits)
	}
}

func TestIndexedEmbeddingStore_SaveLoadWithSidecar(t *testing.T) {
	ctx := context.Background()
	enc := &hashEncoder{dim: 16}
	store, _ := circleai.NewIndexedEmbeddingStore(enc, 4)
	_ = store.Add(ctx, circleai.EmbeddingDocument{ID: "a", Text: "first doc", Metadata: map[string]string{"k": "v"}})
	_ = store.Add(ctx, circleai.EmbeddingDocument{ID: "b", Text: "second doc"})
	_, _ = store.Remove(ctx, "b") // soft-delete b so live flag is exercised

	path := filepath.Join(t.TempDir(), "hnsw.idx")
	if err := store.Save(ctx, path); err != nil {
		t.Fatalf("save: %v", err)
	}

	store2, _ := circleai.NewIndexedEmbeddingStore(&hashEncoder{dim: 16}, 4)
	if err := store2.Load(ctx, path); err != nil {
		t.Fatalf("load: %v", err)
	}
	if store2.Count() != 2 {
		t.Errorf("loaded total count: got %d want 2", store2.Count())
	}
	if store2.LiveCount() != 1 {
		t.Errorf("loaded live count: got %d want 1 (b was soft-deleted)", store2.LiveCount())
	}
	// 'a' still searchable; 'b' not.
	hits, _ := store2.Search(ctx, "first doc", 5)
	if len(hits) == 0 || hits[0].Document.ID != "a" {
		t.Errorf("expected 'a' after load, got %+v", hits)
	}
	if hits[0].Document.Metadata["k"] != "v" {
		t.Errorf("metadata lost on load: %+v", hits[0].Document.Metadata)
	}
}

func mustEncode(t *testing.T, enc circleai.IEmbeddingEncoder, text string) []float32 {
	t.Helper()
	v, err := enc.Encode(context.Background(), text)
	if err != nil {
		t.Fatalf("encode %q: %v", text, err)
	}
	return v
}
