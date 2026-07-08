// model_loader.go
//
// Ports CircleAI.Core.IModelLoader (IModelLoader.cs) and
// CircleAI.Core.LocalModelLoader (LocalModelLoader.cs).
//
// LocalModelLoader owns a registry of models, downloads legacy single-file
// entries (bundles are rejected and steered to the bundle path), verifies
// SHA-256 checksums (accepting both "sha256:<hex>" and bare-hex), and answers
// GetModelPath / ModelExists. Two injected dependencies replace the C# HttpClient
// + embedded resource:
//   - registry map[string]LoaderModelInfo (was the embedded registry.json)
//   - ContentProvider for downloads + the critical-update probe (was HttpClient)

package circleai

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

// bundleAnchorFileName is the canonical weight file used to identify + verify a
// bundle model. Mirrors LocalModelLoader.BundleAnchorFileName.
const bundleAnchorFileName = "llm.mnn.weight"

// criticalUpdateProbeURL is the versions manifest checked by CheckForCriticalUpdate.
const criticalUpdateProbeURL = "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt"

// LoaderModelInfo is a registry row for LocalModelLoader. Mirrors the nested
// LocalModelLoader.ModelInfo record — supports both the legacy single-file shape
// and the bundle shape; IsBundle selects which.
type LoaderModelInfo struct {
	// Legacy single-file shape.
	FileName         string
	PrimaryURL       string
	FallbackURL      string
	Checksum         string
	SizeBytes        int64
	Version          string
	Architecture     string
	QuantizationType string

	// Bundle shape.
	Repo        string
	TotalBytes  int64
	BundleFiles []BundleFile
}

// IsBundle reports whether this entry is a multi-file bundle.
func (i LoaderModelInfo) IsBundle() bool { return len(i.BundleFiles) > 0 }

// IModelLoader acquires + locates model files. Ports CircleAI.Core.IModelLoader.
type IModelLoader interface {
	// DownloadModel downloads the named model (single-file entries only) and
	// returns its local path. progress may be nil.
	DownloadModel(ctx context.Context, modelName string, progress func(float32)) (string, error)

	// GetModelPath returns the expected local path for modelName.
	GetModelPath(modelName string) (string, error)

	// ModelExists reports whether the model file is present AND passes its
	// checksum.
	ModelExists(modelName string) bool

	// CheckForCriticalUpdate reports whether the remote versions manifest flags
	// a "[CRITICAL]" update.
	CheckForCriticalUpdate(ctx context.Context) bool

	// Close releases any held resources.
	Close() error
}

// LocalModelLoader is the disk-backed IModelLoader. Ports
// CircleAI.Core.LocalModelLoader.
type LocalModelLoader struct {
	modelDir string
	registry map[string]LoaderModelInfo
	provider ContentProvider
	disposed bool
}

// NewLocalModelLoader builds a loader. modelDir is created eagerly; registry is
// the injected model set (was the embedded registry.json); provider services
// downloads + the update probe (was HttpClient) and may be nil for a
// registry-only loader.
func NewLocalModelLoader(modelDir string, registry map[string]LoaderModelInfo, provider ContentProvider) (*LocalModelLoader, error) {
	if modelDir == "" {
		return nil, errors.New("modelDir is required")
	}
	if err := os.MkdirAll(modelDir, 0o755); err != nil {
		return nil, err
	}
	reg := make(map[string]LoaderModelInfo, len(registry))
	for k, v := range registry {
		reg[k] = v
	}
	return &LocalModelLoader{modelDir: modelDir, registry: reg, provider: provider}, nil
}

// lookup does a case-insensitive registry lookup (the C# dict is
// StringComparer.OrdinalIgnoreCase).
func (l *LocalModelLoader) lookup(modelName string) (LoaderModelInfo, bool) {
	if info, ok := l.registry[modelName]; ok {
		return info, true
	}
	for k, v := range l.registry {
		if strings.EqualFold(k, modelName) {
			return v, true
		}
	}
	return LoaderModelInfo{}, false
}

// DownloadModel downloads modelName's single file, trying PrimaryUrl then
// FallbackUrl, verifying the checksum (skipped for sha256:TBD / empty). Bundle
// entries are rejected. Ports DownloadModelAsync.
func (l *LocalModelLoader) DownloadModel(ctx context.Context, modelName string, progress func(float32)) (string, error) {
	if l.disposed {
		return "", errors.New("LocalModelLoader is disposed")
	}
	info, ok := l.lookup(modelName)
	if !ok {
		return "", fmt.Errorf("model %s not supported", modelName)
	}
	if info.IsBundle() {
		return "", fmt.Errorf(
			"model %q is a multi-file bundle (registry entry has BundleFiles[]); "+
				"use the bundle download path instead — LocalModelLoader.DownloadModel "+
				"only handles legacy single-file entries", modelName)
	}

	localPath := filepath.Join(l.modelDir, info.FileName)

	if fileExists(localPath) {
		if checksumUnverifiable(info.Checksum) {
			return localPath, nil
		}
		if verifyChecksum(localPath, info.Checksum) {
			return localPath, nil
		}
		_ = os.Remove(localPath)
	}

	if l.provider == nil {
		return "", errors.New("no content provider configured for download")
	}

	sources := []string{info.PrimaryURL, info.FallbackURL}
	var lastErr error
	for _, u := range sources {
		if strings.TrimSpace(u) == "" {
			continue
		}
		if err := l.downloadFile(ctx, u, localPath, progress); err != nil {
			lastErr = err
			continue
		}
		if checksumUnverifiable(info.Checksum) {
			return localPath, nil
		}
		if verifyChecksum(localPath, info.Checksum) {
			return localPath, nil
		}
		_ = os.Remove(localPath)
		lastErr = errors.New("downloaded model failed checksum verification")
	}
	if lastErr == nil {
		lastErr = errors.New("all sources failed")
	}
	return "", lastErr
}

// downloadFile streams one URL's content into outputPath, reporting a 0..1
// fraction. Deterministic analogue of the C# HttpClient copy loop.
func (l *LocalModelLoader) downloadFile(ctx context.Context, url, outputPath string, progress func(float32)) error {
	payload, ok := l.provider.Fetch(ctx, url)
	if !ok {
		return fmt.Errorf("download: no content for %q", url)
	}
	if err := os.WriteFile(outputPath, payload, 0o644); err != nil {
		return err
	}
	if progress != nil {
		progress(1.0)
	}
	return nil
}

// GetModelPath returns the expected local path for modelName. Bundle entries
// resolve to <dir>/<modelName>/llm.mnn.weight. Ports GetModelPath.
func (l *LocalModelLoader) GetModelPath(modelName string) (string, error) {
	if l.disposed {
		return "", errors.New("LocalModelLoader is disposed")
	}
	info, ok := l.lookup(modelName)
	if !ok {
		return "", fmt.Errorf("model %s not found", modelName)
	}
	if info.IsBundle() {
		return filepath.Join(l.modelDir, modelName, bundleAnchorFileName), nil
	}
	return filepath.Join(l.modelDir, info.FileName), nil
}

// ModelExists reports whether the model file is present and passes its
// checksum. Ports ModelExists (never throws — any error yields false).
func (l *LocalModelLoader) ModelExists(modelName string) bool {
	info, ok := l.lookup(modelName)
	if !ok {
		return false
	}
	path, err := l.GetModelPath(modelName)
	if err != nil || !fileExists(path) {
		return false
	}
	if info.IsBundle() {
		var anchorSha string
		for _, f := range info.BundleFiles {
			if strings.EqualFold(f.Name, bundleAnchorFileName) {
				anchorSha = f.Sha256
				break
			}
		}
		if anchorSha == "" {
			return false
		}
		return verifyChecksum(path, anchorSha)
	}
	return info.Checksum != "" && verifyChecksum(path, info.Checksum)
}

// CheckForCriticalUpdate probes the versions manifest for "[CRITICAL]".
// Ports CheckForCriticalUpdateAsync (any failure → false).
func (l *LocalModelLoader) CheckForCriticalUpdate(ctx context.Context) bool {
	if l.provider == nil {
		return false
	}
	body, ok := l.provider.Fetch(ctx, criticalUpdateProbeURL)
	if !ok {
		return false
	}
	return strings.Contains(string(body), "[CRITICAL]")
}

// Close disposes the loader.
func (l *LocalModelLoader) Close() error {
	l.disposed = true
	return nil
}

// checksumUnverifiable mirrors the C# "Checksum is null || StartsWith(sha256:TBD)"
// guard — a missing or placeholder checksum means integrity can't be verified.
func checksumUnverifiable(checksum string) bool {
	c := strings.TrimSpace(checksum)
	return c == "" || strings.HasPrefix(c, "sha256:TBD")
}

// verifyChecksum computes SHA-256 of filePath and compares (case-insensitive)
// against expected, accepting both "sha256:<hex>" and bare-hex. Ports
// LocalModelLoader.VerifyChecksum.
func verifyChecksum(filePath, expectedChecksum string) bool {
	f, err := os.Open(filePath)
	if err != nil {
		return false
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return false
	}
	actual := hex.EncodeToString(h.Sum(nil))

	expected := strings.TrimSpace(expectedChecksum)
	if strings.HasPrefix(strings.ToLower(expected), "sha256:") {
		expected = strings.TrimSpace(expected[len("sha256:"):])
	}
	return strings.EqualFold(expected, actual)
}

var _ IModelLoader = (*LocalModelLoader)(nil)
