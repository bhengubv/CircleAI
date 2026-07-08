// model_runtime_test.go
//
// Verifies the Core model-management runtime ports:
//   - MapContentProvider / ModelScopeSource (host guard, availability, download,
//     resume) — SourceDownloadHelper + ModelScopeSource.
//   - ModelDownloader candidate fallback, source matching, bundle rejection,
//     unknown-model error, progress bridge — ModelDownloader.
//   - LocalModelManager path resolution + SHA-256 verify — LocalModelManager.
//   - LocalModelLoader single-file download, checksum forms, bundle reject,
//     ModelExists, CheckForCriticalUpdate — LocalModelLoader.
//   - HuggingFaceSource tombstone — HuggingFaceSource.
//   - SafeModelHandle release-once + PlatformInterop.LoadModel — SafeModelHandle
//     / PlatformInterop.

package circleai_test

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func sha256HexOf(b []byte) string {
	h := sha256.Sum256(b)
	return hex.EncodeToString(h[:])
}

// ── ModelScopeSource + provider ───────────────────────────────────────────

func TestModelScopeSource_DownloadsFromProvider(t *testing.T) {
	ctx := context.Background()
	url := "https://modelscope.cn/models/acme/model/file.bin"
	payload := []byte("hello-modelscope-payload")
	provider := circleai.NewMapContentProvider(map[string][]byte{
		url:                      payload,
		"https://modelscope.cn/": {},
	})
	src := circleai.NewModelScopeSource(provider)

	if src.Name() != "ModelScope" {
		t.Errorf("Name: got %q", src.Name())
	}
	if !src.IsAvailable(ctx) {
		t.Error("source should report available when probe URL is registered")
	}

	dst := filepath.Join(t.TempDir(), "sub", "file.bin")
	var lastProgress circleai.SourceDownloadProgress
	err := src.Download(ctx, url, dst, func(p circleai.SourceDownloadProgress) { lastProgress = p })
	if err != nil {
		t.Fatalf("download: %v", err)
	}
	got, _ := os.ReadFile(dst)
	if string(got) != string(payload) {
		t.Errorf("downloaded bytes mismatch: got %q", string(got))
	}
	if lastProgress.BytesReceived != int64(len(payload)) || lastProgress.TotalBytes != int64(len(payload)) {
		t.Errorf("final progress wrong: %+v", lastProgress)
	}
	if lastProgress.FileName != "file.bin" {
		t.Errorf("progress FileName: got %q", lastProgress.FileName)
	}
}

func TestModelScopeSource_RejectsNonModelScopeHost(t *testing.T) {
	provider := circleai.NewMapContentProvider(nil)
	src := circleai.NewModelScopeSource(provider)
	err := src.Download(context.Background(), "https://huggingface.co/foo", filepath.Join(t.TempDir(), "x"), nil)
	if err == nil {
		t.Fatal("expected host-restriction error")
	}
}

func TestModelScopeSource_ResumesPartialFile(t *testing.T) {
	ctx := context.Background()
	url := "https://modelscope.cn/big.bin"
	full := make([]byte, 20000) // > BufferSize so multiple chunks
	for i := range full {
		full[i] = byte(i % 251)
	}
	provider := circleai.NewMapContentProvider(map[string][]byte{url: full})
	src := circleai.NewModelScopeSource(provider)

	dst := filepath.Join(t.TempDir(), "big.bin")
	// Pre-seed a valid prefix (first 5000 bytes).
	if err := os.WriteFile(dst, full[:5000], 0o644); err != nil {
		t.Fatal(err)
	}
	if err := src.Download(ctx, url, dst, nil); err != nil {
		t.Fatalf("resume download: %v", err)
	}
	got, _ := os.ReadFile(dst)
	if len(got) != len(full) {
		t.Fatalf("resumed file length: got %d want %d", len(got), len(full))
	}
	if sha256HexOf(got) != sha256HexOf(full) {
		t.Error("resumed file content mismatch")
	}
}

// ── ModelDownloader ───────────────────────────────────────────────────────

func TestModelDownloader_CandidateFallback(t *testing.T) {
	ctx := context.Background()
	primary := "https://modelscope.cn/primary/model.bin"   // unregistered → source download fails
	fallback := "https://modelscope.cn/fallback/model.bin" // registered → succeeds
	payload := []byte("fallback-wins")
	provider := circleai.NewMapContentProvider(map[string][]byte{fallback: payload})
	src := circleai.NewModelScopeSource(provider)

	reg := map[string]circleai.DownloaderModelEntry{
		"m1": {FileName: "model.bin", PrimaryURL: primary, FallbackURL: fallback},
	}
	dl, err := circleai.NewModelDownloader([]circleai.IModelSource{src}, reg)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	var reports int
	dl.ProgressChanged = func(circleai.DownloadProgressReport) { reports++ }

	dir := t.TempDir()
	if err := dl.DownloadModel(ctx, "m1", dir); err != nil {
		t.Fatalf("download: %v", err)
	}
	got, _ := os.ReadFile(filepath.Join(dir, "model.bin"))
	if string(got) != string(payload) {
		t.Errorf("expected fallback payload, got %q", string(got))
	}
	if reports == 0 {
		t.Error("expected at least one progress report")
	}
}

func TestModelDownloader_RejectsBundleEntry(t *testing.T) {
	src := circleai.NewModelScopeSource(circleai.NewMapContentProvider(nil))
	reg := map[string]circleai.DownloaderModelEntry{
		"bundle": {Repo: "MNN/Foo", BundleFiles: []circleai.BundleFile{{Name: "llm.mnn.weight", Sha256: "ab", SizeBytes: 1}}},
	}
	dl, _ := circleai.NewModelDownloader([]circleai.IModelSource{src}, reg)
	err := dl.DownloadModel(context.Background(), "bundle", t.TempDir())
	if err == nil {
		t.Fatal("bundle entry should be rejected by the single-file downloader")
	}
}

func TestModelDownloader_UnknownModel(t *testing.T) {
	src := circleai.NewModelScopeSource(circleai.NewMapContentProvider(nil))
	dl, _ := circleai.NewModelDownloader([]circleai.IModelSource{src}, nil)
	if err := dl.DownloadModel(context.Background(), "nope", t.TempDir()); err == nil {
		t.Fatal("unknown model should error")
	}
}

func TestModelDownloader_RequiresAtLeastOneSource(t *testing.T) {
	if _, err := circleai.NewModelDownloader(nil, nil); err == nil {
		t.Fatal("no sources should error")
	}
}

func TestModelDownloader_DownloadFromCandidates_AllFail(t *testing.T) {
	// A source registered, but neither URL has content → all sources fail.
	provider := circleai.NewMapContentProvider(nil)
	src := circleai.NewModelScopeSource(provider)
	dl, _ := circleai.NewModelDownloader([]circleai.IModelSource{src}, nil)
	_, err := dl.DownloadFromCandidates(context.Background(),
		[]string{"https://modelscope.cn/a", "https://modelscope.cn/b"},
		filepath.Join(t.TempDir(), "out.bin"), nil)
	if err == nil {
		t.Fatal("expected all-sources-failed error")
	}
}

// ── LocalModelManager ─────────────────────────────────────────────────────

// stubDownloader writes a fixed anchor payload into the model dir on demand.
type stubDownloader struct {
	payload []byte
	calls   int
}

func (s *stubDownloader) DownloadModel(_ context.Context, _ string, localPath string) error {
	s.calls++
	if err := os.MkdirAll(localPath, 0o755); err != nil {
		return err
	}
	return os.WriteFile(filepath.Join(localPath, "pytorch_model.bin"), s.payload, 0o644)
}

func (s *stubDownloader) DownloadFromCandidates(context.Context, []string, string, func(circleai.SourceDownloadProgress)) (string, error) {
	return "stub", nil
}

func TestLocalModelManager_DownloadsThenVerifies(t *testing.T) {
	ctx := context.Background()
	payload := []byte("weights-abc")
	dl := &stubDownloader{payload: payload}
	dir := t.TempDir()
	mgr, err := circleai.NewLocalModelManager(dl, filepath.Join(dir, "models"))
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	defer mgr.Close()

	path, err := mgr.GetModelPath(ctx, "org/model")
	if err != nil {
		t.Fatalf("GetModelPath: %v", err)
	}
	if dl.calls != 1 {
		t.Errorf("expected 1 download, got %d", dl.calls)
	}
	// Second call: anchor now exists → no re-download.
	if _, err := mgr.GetModelPath(ctx, "org/model"); err != nil {
		t.Fatalf("GetModelPath 2: %v", err)
	}
	if dl.calls != 1 {
		t.Errorf("expected still 1 download after cache hit, got %d", dl.calls)
	}
	// Path is sanitised (/, \ → _).
	if filepath.Base(path) != "org_model" {
		t.Errorf("expected sanitised dir org_model, got %q", filepath.Base(path))
	}

	sum := sha256.Sum256(payload)
	ok, err := mgr.VerifyModel(ctx, path, sum[:])
	if err != nil || !ok {
		t.Errorf("VerifyModel should pass: ok=%v err=%v", ok, err)
	}
	bad := sha256.Sum256([]byte("tampered"))
	ok, _ = mgr.VerifyModel(ctx, path, bad[:])
	if ok {
		t.Error("VerifyModel should fail on wrong checksum")
	}
}

func TestLocalModelManager_NoDownloaderMissingModel(t *testing.T) {
	mgr, _ := circleai.NewLocalModelManager(nil, filepath.Join(t.TempDir(), "m"))
	if _, err := mgr.GetModelPath(context.Background(), "x"); err == nil {
		t.Fatal("missing model with no downloader should error")
	}
}

func TestLocalModelManager_GetModelPathVerified_ChecksumMismatch(t *testing.T) {
	payload := []byte("weights")
	dl := &stubDownloader{payload: payload}
	mgr, _ := circleai.NewLocalModelManager(dl, filepath.Join(t.TempDir(), "m"))
	bad := sha256.Sum256([]byte("nope"))
	if _, err := mgr.GetModelPathVerified(context.Background(), "id", bad[:]); err == nil {
		t.Fatal("checksum mismatch should error")
	}
}

// ── LocalModelLoader ──────────────────────────────────────────────────────

func TestLocalModelLoader_DownloadAndVerifyChecksum(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	url := "https://modelscope.cn/models/acme/emb/model.mnn"
	payload := []byte("model-weights-xyz")
	provider := circleai.NewMapContentProvider(map[string][]byte{url: payload})

	// Both bare-hex and sha256:-prefixed forms must be accepted.
	bareHex := sha256HexOf(payload)
	reg := map[string]circleai.LoaderModelInfo{
		"emb-bare":   {FileName: "model.mnn", PrimaryURL: url, Checksum: bareHex},
		"emb-prefix": {FileName: "model2.mnn", PrimaryURL: url, Checksum: "sha256:" + bareHex},
	}
	loader, err := circleai.NewLocalModelLoader(dir, reg, provider)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	defer loader.Close()

	var frac float32
	p, err := loader.DownloadModel(ctx, "emb-bare", func(f float32) { frac = f })
	if err != nil {
		t.Fatalf("download bare: %v", err)
	}
	if frac != 1.0 {
		t.Errorf("progress should reach 1.0, got %v", frac)
	}
	got, _ := os.ReadFile(p)
	if string(got) != string(payload) {
		t.Errorf("bare payload mismatch")
	}
	if !loader.ModelExists("emb-bare") {
		t.Error("ModelExists should be true after verified download")
	}

	if _, err := loader.DownloadModel(ctx, "emb-prefix", nil); err != nil {
		t.Fatalf("download prefix: %v", err)
	}
	if !loader.ModelExists("emb-prefix") {
		t.Error("ModelExists should be true for sha256:-prefixed checksum")
	}
}

func TestLocalModelLoader_CachedFileSkipsRedownload(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	url := "https://modelscope.cn/m.mnn"
	payload := []byte("cached")
	// Pre-place the file so DownloadModel returns it without a provider hit.
	if err := os.WriteFile(filepath.Join(dir, "m.mnn"), payload, 0o644); err != nil {
		t.Fatal(err)
	}
	reg := map[string]circleai.LoaderModelInfo{
		"m": {FileName: "m.mnn", PrimaryURL: url, Checksum: sha256HexOf(payload)},
	}
	// Provider has NO content — proves the cached, checksum-valid file is used.
	loader, _ := circleai.NewLocalModelLoader(dir, reg, circleai.NewMapContentProvider(nil))
	p, err := loader.DownloadModel(ctx, "m", nil)
	if err != nil {
		t.Fatalf("cached download: %v", err)
	}
	if filepath.Base(p) != "m.mnn" {
		t.Errorf("unexpected path %q", p)
	}
}

func TestLocalModelLoader_RejectsBundle(t *testing.T) {
	dir := t.TempDir()
	reg := map[string]circleai.LoaderModelInfo{
		"b": {Repo: "MNN/B", BundleFiles: []circleai.BundleFile{{Name: "llm.mnn.weight", Sha256: "aa", SizeBytes: 1}}},
	}
	loader, _ := circleai.NewLocalModelLoader(dir, reg, circleai.NewMapContentProvider(nil))
	if _, err := loader.DownloadModel(context.Background(), "b", nil); err == nil {
		t.Fatal("bundle should be rejected by single-file loader")
	}
	// GetModelPath for a bundle resolves to <dir>/<name>/llm.mnn.weight.
	p, err := loader.GetModelPath("b")
	if err != nil {
		t.Fatalf("GetModelPath bundle: %v", err)
	}
	if filepath.Base(p) != "llm.mnn.weight" {
		t.Errorf("bundle anchor path wrong: %q", p)
	}
}

func TestLocalModelLoader_UnsupportedModel(t *testing.T) {
	loader, _ := circleai.NewLocalModelLoader(t.TempDir(), nil, nil)
	if _, err := loader.DownloadModel(context.Background(), "ghost", nil); err == nil {
		t.Fatal("unsupported model should error")
	}
	if loader.ModelExists("ghost") {
		t.Error("ModelExists should be false for unknown model")
	}
}

func TestLocalModelLoader_CheckForCriticalUpdate(t *testing.T) {
	ctx := context.Background()
	const probe = "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt"

	loaderCrit, _ := circleai.NewLocalModelLoader(t.TempDir(), nil,
		circleai.NewMapContentProvider(map[string][]byte{probe: []byte("v1.2.3 [CRITICAL] update")}))
	if !loaderCrit.CheckForCriticalUpdate(ctx) {
		t.Error("should detect [CRITICAL]")
	}

	loaderNoCrit, _ := circleai.NewLocalModelLoader(t.TempDir(), nil,
		circleai.NewMapContentProvider(map[string][]byte{probe: []byte("v1.2.3 routine")}))
	if loaderNoCrit.CheckForCriticalUpdate(ctx) {
		t.Error("should not detect critical when absent")
	}

	// No provider → false, never panics.
	loaderNoProv, _ := circleai.NewLocalModelLoader(t.TempDir(), nil, nil)
	if loaderNoProv.CheckForCriticalUpdate(ctx) {
		t.Error("no provider should yield false")
	}
}

// ── HuggingFaceSource tombstone ───────────────────────────────────────────

func TestHuggingFaceSource_IsRemoved(t *testing.T) {
	src, err := circleai.NewHuggingFaceSource()
	if err == nil || src != nil {
		t.Fatal("HuggingFaceSource must be a removed tombstone")
	}
}

// ── SafeModelHandle + PlatformInterop ─────────────────────────────────────

func TestSafeModelHandle_ReleasesExactlyOnce(t *testing.T) {
	var freed int
	h, err := circleai.NewSafeModelHandle(0xDEAD, func(uintptr) { freed++ })
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if h.IsInvalid() {
		t.Error("handle with non-zero pointer should be valid")
	}
	if h.Handle() != 0xDEAD {
		t.Errorf("Handle(): got %x", h.Handle())
	}
	_ = h.Close()
	_ = h.Close() // idempotent — must not double-free
	if freed != 1 {
		t.Errorf("release callback should fire exactly once, fired %d", freed)
	}
	if !h.IsInvalid() {
		t.Error("handle should be invalid after close")
	}
}

func TestSafeModelHandle_RequiresReleaseCallback(t *testing.T) {
	if _, err := circleai.NewSafeModelHandle(1, nil); err == nil {
		t.Fatal("nil release callback should error")
	}
}

func TestPlatformInterop_LoadModel(t *testing.T) {
	// Deterministic native loader fake.
	var freed bool
	load := func(path string) (uintptr, func(uintptr), error) {
		return 0x1234, func(uintptr) { freed = true }, nil
	}
	pi, err := circleai.NewPlatformInterop(load)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	// Missing file → error before the loader is called.
	if _, err := pi.LoadModel(filepath.Join(t.TempDir(), "nope.gguf")); err == nil {
		t.Fatal("missing model file should error")
	}
	// Empty path → error.
	if _, err := pi.LoadModel("  "); err == nil {
		t.Fatal("empty path should error")
	}

	modelFile := filepath.Join(t.TempDir(), "model.gguf")
	if err := os.WriteFile(modelFile, []byte("GGUF"), 0o644); err != nil {
		t.Fatal(err)
	}
	h, err := pi.LoadModel(modelFile)
	if err != nil {
		t.Fatalf("LoadModel: %v", err)
	}
	if h.Handle() != 0x1234 {
		t.Errorf("handle: got %x", h.Handle())
	}
	_ = h.Close()
	if !freed {
		t.Error("native handle should be freed on close")
	}
}

func TestPlatformInterop_NullHandleFails(t *testing.T) {
	load := func(string) (uintptr, func(uintptr), error) { return 0, nil, nil }
	pi, _ := circleai.NewPlatformInterop(load)
	f := filepath.Join(t.TempDir(), "m.gguf")
	_ = os.WriteFile(f, []byte("x"), 0o644)
	if _, err := pi.LoadModel(f); err == nil {
		t.Fatal("null native handle should error")
	}
}
