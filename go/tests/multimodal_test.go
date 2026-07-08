// multimodal_test.go
//
// Exercises the multimodal memory pipeline: HeuristicMultimodalCaptioner,
// InMemoryMultimodalMemoryStore, and MultimodalMemoryIngester (dedup + caption
// + persist). Mirrors the TS pilot suite tests/multimodal.test.ts 1:1 and the
// C# MultimodalMemoryTests. Bytes are synthesised inline so the tests run
// identically on every box.

package circleai_test

import (
	"context"
	"regexp"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── Test helpers ─────────────────────────────────────────────────────────────

// strptr is defined in knowledge_graph_test.go (same test package); reused here.

func intptr(i int) *int { return &i }

func fakeJpeg(extraBytes int) []byte {
	buf := make([]byte, 2+extraBytes)
	buf[0] = 0xff
	buf[1] = 0xd8
	for i := 2; i < len(buf); i++ {
		buf[i] = byte(i % 251)
	}
	return buf
}

func fakePng(extraBytes int) []byte {
	buf := make([]byte, 4+extraBytes)
	buf[0] = 0x89
	buf[1] = 0x50
	buf[2] = 0x4e
	buf[3] = 0x47
	for i := 4; i < len(buf); i++ {
		buf[i] = byte(i % 251)
	}
	return buf
}

func wireIngester(t *testing.T, custom circleai.IMultimodalCaptioner) (*circleai.MultimodalMemoryIngester, *circleai.InMemoryMultimodalMemoryStore) {
	t.Helper()
	store := circleai.NewInMemoryMultimodalMemoryStore()
	var captioners []circleai.IMultimodalCaptioner
	if custom != nil {
		captioners = []circleai.IMultimodalCaptioner{custom, circleai.HeuristicMultimodalCaptioner{}}
	} else {
		captioners = []circleai.IMultimodalCaptioner{circleai.HeuristicMultimodalCaptioner{}}
	}
	ing, err := circleai.NewMultimodalMemoryIngester(captioners, store)
	if err != nil {
		t.Fatalf("NewMultimodalMemoryIngester: %v", err)
	}
	return ing, store
}

// fakeRichCaptioner only handles Image, returns a rich caption + embedding.
type fakeRichCaptioner struct{}

func (fakeRichCaptioner) CanCaption(modality circleai.MediaModality, _ *string) bool {
	return modality == circleai.MediaImage
}

func (fakeRichCaptioner) Caption(_ context.Context, _ circleai.MediaModality, _ []byte, _ *string) (circleai.CaptionResult, error) {
	return circleai.CaptionResult{
		Caption:   "A blue sky with two clouds.",
		Embedding: []float32{0.1, 0.2, 0.3},
		WidthPx:   intptr(1920),
		HeightPx:  intptr(1080),
	}, nil
}

func mustIngest(t *testing.T, ing *circleai.MultimodalMemoryIngester, modality circleai.MediaModality, bytes []byte, opts circleai.IngestOptions) circleai.IngestionResult {
	t.Helper()
	r, err := ing.Ingest(context.Background(), modality, bytes, opts)
	if err != nil {
		t.Fatalf("Ingest: %v", err)
	}
	return r
}

func mustCaption(t *testing.T, c circleai.IMultimodalCaptioner, modality circleai.MediaModality, bytes []byte, mime *string) circleai.CaptionResult {
	t.Helper()
	r, err := c.Caption(context.Background(), modality, bytes, mime)
	if err != nil {
		t.Fatalf("Caption: %v", err)
	}
	return r
}

func mustAddMM(t *testing.T, store circleai.IMultimodalMemoryStore, e circleai.MultimodalMemoryEntry) {
	t.Helper()
	if err := store.Add(context.Background(), e); err != nil {
		t.Fatalf("Add: %v", err)
	}
}

func mmEntry(sha, caption string, embedding []float32, recorded time.Time) circleai.MultimodalMemoryEntry {
	e := circleai.NewMultimodalMemoryEntry()
	e.SourceSha256 = sha
	e.Caption = caption
	e.Embedding = embedding
	if !recorded.IsZero() {
		e.RecordedAtUTC = recorded
	}
	return e
}

// ── HeuristicMultimodalCaptioner ─────────────────────────────────────────────

func TestHeuristicCaptioner_AlwaysCanCaption(t *testing.T) {
	c := circleai.HeuristicMultimodalCaptioner{}
	if !c.CanCaption(circleai.MediaImage, strptr("image/jpeg")) {
		t.Error("should caption image/jpeg")
	}
	if !c.CanCaption(circleai.MediaAudio, nil) {
		t.Error("should caption audio/nil")
	}
	if !c.CanCaption(circleai.MediaVideo, strptr("video/mp4")) {
		t.Error("should caption video/mp4")
	}
	if !c.CanCaption(circleai.MediaTextDocument, strptr("application/pdf")) {
		t.Error("should caption application/pdf")
	}
}

func TestHeuristicCaptioner_DetectsJpegNoEmbedding(t *testing.T) {
	c := circleai.HeuristicMultimodalCaptioner{}
	r := mustCaption(t, c, circleai.MediaImage, fakeJpeg(100), nil)
	if !strings.Contains(r.Caption, "image/jpeg") {
		t.Errorf("caption missing image/jpeg: %q", r.Caption)
	}
	if r.Embedding != nil {
		t.Errorf("embedding: got %v want nil", r.Embedding)
	}
}

func TestHeuristicCaptioner_MagicBytes(t *testing.T) {
	c := circleai.HeuristicMultimodalCaptioner{}
	if !strings.Contains(mustCaption(t, c, circleai.MediaImage, fakePng(100), nil).Caption, "image/png") {
		t.Error("png not detected")
	}
	if !strings.Contains(mustCaption(t, c, circleai.MediaImage, []byte{0x47, 0x49, 0x46, 0x38}, nil).Caption, "image/gif") {
		t.Error("gif not detected")
	}
	if !strings.Contains(mustCaption(t, c, circleai.MediaAudio, []byte{0x52, 0x49, 0x46, 0x46}, nil).Caption, "audio/wav") {
		t.Error("wav not detected")
	}
	if !strings.Contains(mustCaption(t, c, circleai.MediaTextDocument, []byte{0x25, 0x50, 0x44, 0x46}, nil).Caption, "application/pdf") {
		t.Error("pdf not detected")
	}
}

func TestHeuristicCaptioner_UnknownMagic(t *testing.T) {
	c := circleai.HeuristicMultimodalCaptioner{}
	r := mustCaption(t, c, circleai.MediaAudio, []byte{1, 2, 3, 4}, nil)
	if !strings.Contains(r.Caption, "application/octet-stream") {
		t.Errorf("caption missing octet-stream: %q", r.Caption)
	}
}

func TestHeuristicCaptioner_DeclaredMime(t *testing.T) {
	c := circleai.HeuristicMultimodalCaptioner{}
	r := mustCaption(t, c, circleai.MediaImage, fakePng(100), strptr("image/heic"))
	if !strings.Contains(r.Caption, "image/heic") {
		t.Errorf("caption missing image/heic: %q", r.Caption)
	}
}

func TestHeuristicCaptioner_FallbackLabelAndByteCount(t *testing.T) {
	c := circleai.HeuristicMultimodalCaptioner{}
	bytes := fakeJpeg(100)
	r := mustCaption(t, c, circleai.MediaImage, bytes, nil)
	if !strings.Contains(r.Caption, "no captioner wired") {
		t.Errorf("caption missing fallback marker: %q", r.Caption)
	}
	if !strings.Contains(r.Caption, "102 bytes") {
		t.Errorf("caption missing byte count (want 102): %q", r.Caption)
	}
}

func TestHeuristicCaptioner_ModalityLabels(t *testing.T) {
	c := circleai.HeuristicMultimodalCaptioner{}
	if !strings.HasPrefix(mustCaption(t, c, circleai.MediaImage, fakeJpeg(100), nil).Caption, "[Image") {
		t.Error("image label wrong")
	}
	if !strings.HasPrefix(mustCaption(t, c, circleai.MediaAudio, fakeJpeg(100), strptr("audio/wav")).Caption, "[Audio") {
		t.Error("audio label wrong")
	}
	if !strings.HasPrefix(mustCaption(t, c, circleai.MediaVideo, fakeJpeg(100), strptr("video/mp4")).Caption, "[Video") {
		t.Error("video label wrong")
	}
	if !strings.HasPrefix(mustCaption(t, c, circleai.MediaTextDocument, fakeJpeg(100), strptr("application/pdf")).Caption, "[Document") {
		t.Error("document label wrong")
	}
}

// ── Ingester — happy path ────────────────────────────────────────────────────

func TestIngester_FirstTimeAddsEntry(t *testing.T) {
	ctx := context.Background()
	ing, store := wireIngester(t, nil)
	bytes := fakeJpeg(100)
	r := mustIngest(t, ing, circleai.MediaImage, bytes, circleai.IngestOptions{MimeType: strptr("image/jpeg")})

	if r.WasDeduplicated {
		t.Error("first ingest should not be deduplicated")
	}
	if n, _ := store.Count(ctx); n != 1 {
		t.Errorf("count: got %d want 1", n)
	}
	if r.Entry.SourceByteCount != int64(len(bytes)) {
		t.Errorf("byteCount: got %d want %d", r.Entry.SourceByteCount, len(bytes))
	}
	if r.Entry.SourceMimeType == nil || *r.Entry.SourceMimeType != "image/jpeg" {
		t.Errorf("mimeType: got %v want image/jpeg", r.Entry.SourceMimeType)
	}
	if strings.TrimSpace(r.Entry.SourceSha256) == "" {
		t.Error("sha256 is empty")
	}
}

func TestIngester_SecondTimeDeduplicates(t *testing.T) {
	ctx := context.Background()
	ing, store := wireIngester(t, nil)
	bytes := fakeJpeg(100)
	first := mustIngest(t, ing, circleai.MediaImage, bytes, circleai.IngestOptions{MimeType: strptr("image/jpeg")})
	second := mustIngest(t, ing, circleai.MediaImage, bytes, circleai.IngestOptions{MimeType: strptr("image/jpeg")})

	if first.WasDeduplicated {
		t.Error("first should not dedup")
	}
	if !second.WasDeduplicated {
		t.Error("second should dedup")
	}
	if n, _ := store.Count(ctx); n != 1 {
		t.Errorf("count: got %d want 1", n)
	}
	if first.Entry.SourceSha256 != second.Entry.SourceSha256 {
		t.Error("sha256 differs across dedup")
	}
	if second.Entry.ReferenceCount != 2 {
		t.Errorf("referenceCount: got %d want 2", second.Entry.ReferenceCount)
	}
}

func TestIngester_DifferentBytesDistinctEntries(t *testing.T) {
	ctx := context.Background()
	ing, store := wireIngester(t, nil)
	ra := mustIngest(t, ing, circleai.MediaImage, fakeJpeg(50), circleai.IngestOptions{})
	rb := mustIngest(t, ing, circleai.MediaImage, fakeJpeg(60), circleai.IngestOptions{})
	if ra.Entry.SourceSha256 == rb.Entry.SourceSha256 {
		t.Error("distinct bytes should produce distinct hashes")
	}
	if n, _ := store.Count(ctx); n != 2 {
		t.Errorf("count: got %d want 2", n)
	}
}

func TestIngester_EmptyBytesThrow(t *testing.T) {
	ing, _ := wireIngester(t, nil)
	if _, err := ing.Ingest(context.Background(), circleai.MediaImage, []byte{}, circleai.IngestOptions{}); err == nil {
		t.Error("expected error for empty bytes")
	}
}

func TestIngester_RecordsUriAndTags(t *testing.T) {
	ing, _ := wireIngester(t, nil)
	bytes := fakePng(100)
	r := mustIngest(t, ing, circleai.MediaImage, bytes, circleai.IngestOptions{
		MimeType:  strptr("image/png"),
		SourceURI: strptr("file:///photos/IMG_001.png"),
		Tags:      map[string]string{"location": "home", "person": "alex"},
	})
	if r.Entry.SourceURI == nil || *r.Entry.SourceURI != "file:///photos/IMG_001.png" {
		t.Errorf("sourceURI: got %v", r.Entry.SourceURI)
	}
	if r.Entry.Tags["location"] != "home" || r.Entry.Tags["person"] != "alex" {
		t.Errorf("tags: got %v", r.Entry.Tags)
	}
}

func TestIngester_HexLowerSha256(t *testing.T) {
	ing, _ := wireIngester(t, nil)
	r := mustIngest(t, ing, circleai.MediaImage, fakeJpeg(0), circleai.IngestOptions{})
	if !regexp.MustCompile(`^[0-9a-f]{64}$`).MatchString(r.Entry.SourceSha256) {
		t.Errorf("sha256 not hex-lower-64: %q", r.Entry.SourceSha256)
	}
}

// ── Captioner selection ──────────────────────────────────────────────────────

func TestIngester_PrefersRichCaptioner(t *testing.T) {
	ing, _ := wireIngester(t, fakeRichCaptioner{})
	r := mustIngest(t, ing, circleai.MediaImage, fakeJpeg(100), circleai.IngestOptions{MimeType: strptr("image/jpeg")})
	if r.Entry.Caption != "A blue sky with two clouds." {
		t.Errorf("caption: got %q", r.Entry.Caption)
	}
	if len(r.Entry.Embedding) == 0 {
		t.Error("expected embedding from rich captioner")
	}
	if r.Entry.WidthPx == nil || *r.Entry.WidthPx != 1920 {
		t.Errorf("widthPx: got %v want 1920", r.Entry.WidthPx)
	}
	if r.Entry.HeightPx == nil || *r.Entry.HeightPx != 1080 {
		t.Errorf("heightPx: got %v want 1080", r.Entry.HeightPx)
	}
}

func TestIngester_FallsBackWhenRichDeclines(t *testing.T) {
	ing, _ := wireIngester(t, fakeRichCaptioner{})
	r := mustIngest(t, ing, circleai.MediaAudio, fakePng(100), circleai.IngestOptions{MimeType: strptr("audio/wav")})
	if !strings.Contains(r.Entry.Caption, "no captioner wired") {
		t.Errorf("caption should be heuristic fallback: %q", r.Entry.Caption)
	}
	if r.Entry.Embedding != nil {
		t.Errorf("embedding: got %v want nil", r.Entry.Embedding)
	}
}

func TestIngester_RejectsZeroCaptioners(t *testing.T) {
	if _, err := circleai.NewMultimodalMemoryIngester(nil, circleai.NewInMemoryMultimodalMemoryStore()); err == nil {
		t.Error("expected error for zero captioners")
	}
}

// ── Store: search, prune, recent, reinforce ──────────────────────────────────

func TestMultimodalStore_SearchByEmbedding(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryMultimodalMemoryStore()
	mustAddMM(t, store, mmEntry("near", "near", []float32{1, 0.1, 0}, time.Time{}))
	mustAddMM(t, store, mmEntry("far", "far", []float32{0, 0, 1}, time.Time{}))

	ranked, err := store.Search(ctx, []float32{1, 0, 0}, 2)
	if err != nil {
		t.Fatalf("Search: %v", err)
	}
	if len(ranked) != 2 {
		t.Fatalf("len: got %d want 2", len(ranked))
	}
	if ranked[0].SourceSha256 != "near" {
		t.Errorf("ranked[0]: got %q want near", ranked[0].SourceSha256)
	}
	if ranked[1].SourceSha256 != "far" {
		t.Errorf("ranked[1]: got %q want far", ranked[1].SourceSha256)
	}
}

func TestMultimodalStore_SearchNullQueryRecency(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryMultimodalMemoryStore()
	now := time.Now().UTC()
	mustAddMM(t, store, mmEntry("older", "older", nil, now.Add(-10*24*time.Hour)))
	mustAddMM(t, store, mmEntry("newer", "newer", nil, now))
	recent, err := store.Search(ctx, nil, 2)
	if err != nil {
		t.Fatalf("Search: %v", err)
	}
	if recent[0].SourceSha256 != "newer" {
		t.Errorf("recent[0]: got %q want newer", recent[0].SourceSha256)
	}
}

func TestMultimodalStore_Prune(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryMultimodalMemoryStore()
	now := time.Now().UTC()
	mustAddMM(t, store, mmEntry("old", "old", nil, now.Add(-10*24*time.Hour)))
	mustAddMM(t, store, mmEntry("new", "new", nil, now))

	removed, err := store.PruneOlderThan(ctx, now.Add(-5*24*time.Hour))
	if err != nil {
		t.Fatalf("PruneOlderThan: %v", err)
	}
	if removed != 1 {
		t.Errorf("removed: got %d want 1", removed)
	}
	if n, _ := store.Count(ctx); n != 1 {
		t.Errorf("count: got %d want 1", n)
	}
	if got, _ := store.GetByHash(ctx, "new"); got == nil {
		t.Error("'new' should survive prune")
	}
	if got, _ := store.GetByHash(ctx, "old"); got != nil {
		t.Error("'old' should be pruned")
	}
}

func TestMultimodalStore_Reinforce(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryMultimodalMemoryStore()
	mustAddMM(t, store, mmEntry("x", "x", nil, time.Time{}))
	if err := store.Reinforce(ctx, "x"); err != nil {
		t.Fatalf("Reinforce: %v", err)
	}
	if err := store.Reinforce(ctx, "x"); err != nil {
		t.Fatalf("Reinforce: %v", err)
	}
	got, _ := store.GetByHash(ctx, "x")
	if got == nil {
		t.Fatal("entry missing")
	}
	if got.ReferenceCount != 3 { // initial 1 + 2 reinforce
		t.Errorf("referenceCount: got %d want 3", got.ReferenceCount)
	}
}

func TestMultimodalStore_ReinforceUnknownNoOp(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryMultimodalMemoryStore()
	if err := store.Reinforce(ctx, "missing"); err != nil {
		t.Fatalf("Reinforce should not error: %v", err)
	}
	if n, _ := store.Count(ctx); n != 0 {
		t.Errorf("count: got %d want 0", n)
	}
}

func TestMultimodalStore_AddWithoutHashThrows(t *testing.T) {
	store := circleai.NewInMemoryMultimodalMemoryStore()
	if err := store.Add(context.Background(), mmEntry("", "x", nil, time.Time{})); err == nil {
		t.Error("expected error adding without a hash")
	}
}

func TestMultimodalStore_CaseInsensitiveHash(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryMultimodalMemoryStore()
	mustAddMM(t, store, mmEntry("ABCDEF", "x", nil, time.Time{}))
	got, _ := store.GetByHash(ctx, "abcdef")
	if got == nil {
		t.Error("case-insensitive hash lookup failed")
	}
}
