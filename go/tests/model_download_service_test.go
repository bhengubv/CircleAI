// model_download_service_test.go
//
// Verifies ModelDownloadService + PrefixCacheService (ported from
// ModelDownloadService.cs / IModelDownloadService.cs / PrefixCacheService.cs):
//   - EnsureModel single-file: download, verify, cache-hit skip, SHA mismatch,
//     bare-hex + "sha256:"-prefixed checksum forms.
//   - EnsureBundle: per-file fetch + verify, primary→fallback fallthrough,
//     skip-when-cached, WriteInstalledManifest stamps installed.json.
//   - IsModelCached / DeleteModel / free-disk-space.
//   - StripShaAlgorithmPrefix edge cases.
//   - PrefixCacheKeyFor determinism + PathFor + Touch + eviction cap behaviour.

package circleai_test

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestModelDownloadService_EnsureModel_VerifyAndCache(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	url := "https://modelscope.cn/models/acme/m/model.gguf"
	payload := []byte("weights-bytes-here")
	provider := circleai.NewMapContentProvider(map[string][]byte{url: payload})
	svc, err := circleai.NewModelDownloadService(dir, provider)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}

	sum := sha256HexOf(payload)
	var frac float64
	path, err := svc.EnsureModel(ctx, "acme-m", url, sum, func(f float64) { frac = f })
	if err != nil {
		t.Fatalf("ensure: %v", err)
	}
	if frac != 1.0 {
		t.Errorf("progress should reach 1.0, got %v", frac)
	}
	got, _ := os.ReadFile(path)
	if string(got) != string(payload) {
		t.Errorf("downloaded payload mismatch")
	}
	if filepath.Base(path) != "acme-m.gguf" {
		t.Errorf("single-file path should be <id>.gguf, got %q", filepath.Base(path))
	}

	// Cache hit: provider emptied — cached+valid file is reused.
	svc2, _ := circleai.NewModelDownloadService(dir, circleai.NewMapContentProvider(nil))
	if _, err := svc2.EnsureModel(ctx, "acme-m", url, sum, nil); err != nil {
		t.Fatalf("cache-hit ensure: %v", err)
	}

	// "sha256:"-prefixed form accepted.
	if _, err := svc2.EnsureModel(ctx, "acme-m", url, "sha256:"+sum, nil); err != nil {
		t.Fatalf("prefixed checksum ensure: %v", err)
	}
}

func TestModelDownloadService_EnsureModel_ShaMismatch(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	url := "https://modelscope.cn/m.gguf"
	provider := circleai.NewMapContentProvider(map[string][]byte{url: []byte("real")})
	svc, _ := circleai.NewModelDownloadService(dir, provider)

	if _, err := svc.EnsureModel(ctx, "bad", url, sha256HexOf([]byte("different")), nil); err == nil {
		t.Fatal("SHA mismatch should error")
	}
	// The partial file must have been cleaned up.
	if _, err := os.Stat(filepath.Join(dir, "bad.gguf")); err == nil {
		t.Error("mismatched download should be deleted")
	}
}

func TestModelDownloadService_EnsureBundle_FallbackAndManifest(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	repo := "MNN/Qwen3-0.6B-MNN"

	cfg := []byte(`{"config":true}`)
	weight := []byte("weight-bytes")
	// config.json served on the PRIMARY url; llm.mnn served only on the FALLBACK.
	primaryCfg := "https://modelscope.cn/api/v1/models/" + repo + "/repo?Revision=master&FilePath=config.json"
	fallbackWeight := "https://modelscope.cn/models/" + repo + "/resolve/master/llm.mnn"
	provider := circleai.NewMapContentProvider(map[string][]byte{
		primaryCfg:     cfg,
		fallbackWeight: weight,
	})
	svc, _ := circleai.NewModelDownloadService(dir, provider)

	specs := []circleai.BundleFileSpec{
		{Name: "config.json", Sha256: sha256HexOf(cfg), SizeBytes: int64(len(cfg))},
		{Name: "llm.mnn", Sha256: "sha256:" + sha256HexOf(weight), SizeBytes: int64(len(weight))},
	}
	var lastFrac float64
	modelDir, err := svc.EnsureBundle(ctx, "qwen", repo, specs, func(f float64) { lastFrac = f })
	if err != nil {
		t.Fatalf("ensure bundle: %v", err)
	}
	if lastFrac != 1.0 {
		t.Errorf("bundle progress should reach 1.0, got %v", lastFrac)
	}
	if b, _ := os.ReadFile(filepath.Join(modelDir, "config.json")); string(b) != string(cfg) {
		t.Error("config.json content mismatch")
	}
	if b, _ := os.ReadFile(filepath.Join(modelDir, "llm.mnn")); string(b) != string(weight) {
		t.Error("llm.mnn content mismatch (fallback URL should have been used)")
	}

	// installed.json manifest.
	svc.WriteInstalledManifest(modelDir, "qwen", "1.0.0", repo, specs)
	if _, err := os.Stat(filepath.Join(modelDir, "installed.json")); err != nil {
		t.Errorf("installed.json should be written: %v", err)
	}

	// Second EnsureBundle is a no-download cache hit (provider emptied).
	svc2, _ := circleai.NewModelDownloadService(dir, circleai.NewMapContentProvider(nil))
	if _, err := svc2.EnsureBundle(ctx, "qwen", repo, specs, nil); err != nil {
		t.Fatalf("cached bundle should skip download: %v", err)
	}
}

func TestModelDownloadService_CacheAndDelete(t *testing.T) {
	ctx := context.Background()
	dir := t.TempDir()
	url := "https://modelscope.cn/x.gguf"
	provider := circleai.NewMapContentProvider(map[string][]byte{url: []byte("x")})
	svc, _ := circleai.NewModelDownloadService(dir, provider)

	cached, _ := svc.IsModelCached(ctx, "x")
	if cached {
		t.Error("should not be cached before download")
	}
	if _, err := svc.EnsureModel(ctx, "x", url, "", nil); err != nil {
		t.Fatal(err)
	}
	cached, _ = svc.IsModelCached(ctx, "x")
	if !cached {
		t.Error("should be cached after download")
	}
	if err := svc.DeleteModel(ctx, "x"); err != nil {
		t.Fatalf("delete: %v", err)
	}
	cached, _ = svc.IsModelCached(ctx, "x")
	if cached {
		t.Error("should not be cached after delete")
	}

	free, err := svc.GetAvailableDiskSpaceBytes(ctx)
	if err != nil {
		t.Fatalf("disk space: %v", err)
	}
	if free <= 0 {
		t.Errorf("free disk space should be positive, got %d", free)
	}
}

func TestStripShaAlgorithmPrefix(t *testing.T) {
	cases := map[string]string{
		"sha256:abc123": "abc123",
		"SHA-256: abc":  "abc",
		"abc123":        "abc123",
		"":              "",
		"deadbeef":      "deadbeef",
		"  sha256:xy  ": "xy",
	}
	for in, want := range cases {
		if got := circleai.StripShaAlgorithmPrefix(in); got != want {
			t.Errorf("StripShaAlgorithmPrefix(%q): got %q want %q", in, got, want)
		}
	}
}

func TestPrefixCacheService_KeyAndTouch(t *testing.T) {
	// KeyFor is deterministic and empty without a system prompt.
	k1 := circleai.PrefixCacheKeyFor("modelA", "sys prompt")
	k2 := circleai.PrefixCacheKeyFor("modelA", "sys prompt")
	if k1 == "" || k1 != k2 {
		t.Errorf("KeyFor should be deterministic non-empty: %q %q", k1, k2)
	}
	if circleai.PrefixCacheKeyFor("modelA", "") != "" {
		t.Error("no system prompt → empty key")
	}
	if circleai.PrefixCacheKeyFor("", "x") != "" {
		t.Error("no model id → empty key")
	}

	pc, err := circleai.NewPrefixCacheService(t.TempDir())
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	if pc.HasEntry(k1) {
		t.Error("fresh cache should have no entry")
	}
	if err := os.WriteFile(pc.PathFor(k1), []byte("snap"), 0o644); err != nil {
		t.Fatal(err)
	}
	if !pc.HasEntry(k1) {
		t.Error("entry should exist after write")
	}
	pc.Touch(k1)       // must not panic / error
	pc.EvictIfNeeded() // under cap → entry stays
	if !pc.HasEntry(k1) {
		t.Error("entry under cap should survive eviction")
	}

	if _, err := circleai.NewPrefixCacheService("  "); err == nil {
		t.Error("blank root should error")
	}
}
