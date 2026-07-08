// model_download_service.go
//
// Ports CircleAI.Inference.IModelDownloadService + BundleFileSpec
// (IModelDownloadService.cs) and CircleAI.Inference.ModelDownloadService
// (ModelDownloadService.cs), including WriteInstalledManifest and the
// StripShaAlgorithmPrefix / URL-builder helpers.
//
// Per the port NOTE, network I/O is injected behind the existing
// ContentProvider seam rather than reaching modelscope.cn over HttpClient:
// EnsureModel/EnsureBundle pull bytes for a URL from the provider, write them
// atomically via a .tmp then rename, and verify the pinned SHA-256. All path
// layout, checksum forms (bare hex + "sha256:<hex>"), bundle URL building,
// per-file skip-when-cached-and-valid, and progress semantics match the C#
// reference. Free-disk-space reporting uses the host filesystem.

package circleai

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// BundleFileSpec is one file in a model bundle (compatible with BundleFile).
// Ports CircleAI.Inference.BundleFileSpec.
type BundleFileSpec struct {
	// Name is the filename relative to the model directory (e.g. config.json).
	Name string
	// Sha256 is the pinned hash in "sha256:<hex>" or bare-hex form. The verify
	// path strips the optional prefix before comparing.
	Sha256 string
	// SizeBytes is the expected file size for diagnostics / progress weighting.
	SizeBytes int64
}

// IModelDownloadService downloads and manages model files on disk. Supports the
// legacy single-file shape and the bundle shape. Ports
// CircleAI.Inference.IModelDownloadService.
type IModelDownloadService interface {
	// EnsureModel ensures a single model file is present and matches
	// expectedSha256 (empty = skip verification). Returns the cached path.
	EnsureModel(ctx context.Context, modelID, downloadURL, expectedSha256 string, progress func(float64)) (string, error)

	// EnsureBundle ensures every file in bundleFiles is present under a per-model
	// directory and matches its pinned SHA-256. Returns the model directory.
	EnsureBundle(ctx context.Context, modelID, repo string, bundleFiles []BundleFileSpec, progress func(float64)) (string, error)

	// IsModelCached reports whether the single-file weight or the bundle dir exists.
	IsModelCached(ctx context.Context, modelID string) (bool, error)

	// DeleteModel deletes the model file or directory if present. No-op when absent.
	DeleteModel(ctx context.Context, modelID string) error

	// GetAvailableDiskSpaceBytes returns free bytes on the storage drive.
	GetAvailableDiskSpaceBytes(ctx context.Context) (int64, error)
}

// ModelDownloadService is the default IModelDownloadService. Single-file entries
// land at {storage}/{modelId}.gguf; bundle entries land at {storage}/{modelId}/
// with every file under that directory. Ports CircleAI.Inference.ModelDownloadService.
type ModelDownloadService struct {
	storageDir string
	provider   ContentProvider
}

// NewModelDownloadService builds a service over a storage directory and a
// content provider (the injected replacement for HttpClient). The directory is
// created on demand.
func NewModelDownloadService(storageDir string, provider ContentProvider) (*ModelDownloadService, error) {
	if strings.TrimSpace(storageDir) == "" {
		return nil, errors.New("storage directory must not be empty")
	}
	if provider == nil {
		return nil, errors.New("content provider is required")
	}
	if err := os.MkdirAll(storageDir, 0o755); err != nil {
		return nil, err
	}
	return &ModelDownloadService{storageDir: storageDir, provider: provider}, nil
}

// EnsureModel ports EnsureModelAsync.
func (s *ModelDownloadService) EnsureModel(
	ctx context.Context, modelID, downloadURL, expectedSha256 string, progress func(float64),
) (string, error) {
	if err := validateModelID(modelID); err != nil {
		return "", err
	}
	if strings.TrimSpace(downloadURL) == "" {
		return "", errors.New("downloadUri is required")
	}

	filePath := s.singleFilePath(modelID)

	if fileExists(filePath) && expectedSha256 != "" {
		ok, err := verifyFileSha256(filePath, expectedSha256)
		if err != nil {
			return "", err
		}
		if ok {
			report(progress, 1.0)
			return filePath, nil
		}
		_ = os.Remove(filePath)
	} else if fileExists(filePath) && expectedSha256 == "" {
		report(progress, 1.0)
		return filePath, nil
	}

	tempPath := filePath + ".tmp"
	if err := s.downloadToFile(ctx, downloadURL, tempPath, progress); err != nil {
		_ = os.Remove(tempPath)
		return "", err
	}
	if expectedSha256 != "" {
		ok, err := verifyFileSha256(tempPath, expectedSha256)
		if err != nil {
			_ = os.Remove(tempPath)
			return "", err
		}
		if !ok {
			_ = os.Remove(tempPath)
			return "", fmt.Errorf("SHA-256 mismatch for model %q. The downloaded file has been deleted", modelID)
		}
	}
	if fileExists(filePath) {
		_ = os.Remove(filePath)
	}
	if err := os.Rename(tempPath, filePath); err != nil {
		_ = os.Remove(tempPath)
		return "", err
	}
	return filePath, nil
}

// EnsureBundle ports EnsureBundleAsync.
func (s *ModelDownloadService) EnsureBundle(
	ctx context.Context, modelID, repo string, bundleFiles []BundleFileSpec, progress func(float64),
) (string, error) {
	if err := validateModelID(modelID); err != nil {
		return "", err
	}
	if strings.TrimSpace(repo) == "" {
		return "", errors.New("repo path is required for bundle entries")
	}
	if len(bundleFiles) == 0 {
		return "", errors.New("bundle file list must not be empty")
	}

	modelDir := filepath.Join(s.storageDir, modelID)
	if err := os.MkdirAll(modelDir, 0o755); err != nil {
		return "", err
	}

	var totalBytes int64
	for _, f := range bundleFiles {
		totalBytes += maxInt64(0, f.SizeBytes)
	}
	var doneBytes int64

	for _, file := range bundleFiles {
		if err := ctx.Err(); err != nil {
			return "", err
		}
		if strings.TrimSpace(file.Name) == "" {
			return "", fmt.Errorf("bundle for %q contains a file with no Name", modelID)
		}

		destPath := filepath.Join(modelDir, file.Name)
		if err := os.MkdirAll(filepath.Dir(destPath), 0o755); err != nil {
			return "", err
		}

		// Skip when cached + valid.
		if fileExists(destPath) {
			ok, _ := verifyFileSha256(destPath, file.Sha256)
			if ok {
				doneBytes += file.SizeBytes
				reportOverall(progress, doneBytes, totalBytes)
				continue
			}
			_ = os.Remove(destPath)
		}

		tempPath := destPath + ".tmp"
		fileBase := doneBytes
		perFile := func(p float64) {
			reportOverall(progress, fileBase+int64(float64(file.SizeBytes)*p), totalBytes)
		}

		// PrimaryUrl (API form) → FallbackUrl (CDN form). Either is the same bytes.
		primary := buildBundlePrimaryURL(repo, file.Name)
		fallback := buildBundleFallbackURL(repo, file.Name)
		if err := s.downloadToFile(ctx, primary, tempPath, perFile); err != nil {
			_ = os.Remove(tempPath)
			if ferr := s.downloadToFile(ctx, fallback, tempPath, perFile); ferr != nil {
				_ = os.Remove(tempPath)
				return "", ferr
			}
		}

		ok, err := verifyFileSha256(tempPath, file.Sha256)
		if err != nil {
			_ = os.Remove(tempPath)
			return "", err
		}
		if !ok {
			_ = os.Remove(tempPath)
			return "", fmt.Errorf(
				"SHA-256 mismatch for bundle file %q of model %q. The downloaded file has been deleted",
				file.Name, modelID)
		}
		if fileExists(destPath) {
			_ = os.Remove(destPath)
		}
		if err := os.Rename(tempPath, destPath); err != nil {
			_ = os.Remove(tempPath)
			return "", err
		}
		doneBytes += file.SizeBytes
		reportOverall(progress, doneBytes, totalBytes)
	}

	report(progress, 1.0)
	return modelDir, nil
}

// WriteInstalledManifest stamps an installed.json file in modelDir describing
// what's on disk. Best-effort — silent on failure. Ports WriteInstalledManifestAsync.
func (s *ModelDownloadService) WriteInstalledManifest(
	modelDir, modelID, version, repo string, bundleFiles []BundleFileSpec,
) {
	if strings.TrimSpace(modelDir) == "" || strings.TrimSpace(modelID) == "" || bundleFiles == nil {
		return
	}
	var totalBytes int64
	files := make([]BundleFile, 0, len(bundleFiles))
	for _, f := range bundleFiles {
		files = append(files, BundleFile{Name: f.Name, Sha256: f.Sha256, SizeBytes: f.SizeBytes})
		totalBytes += maxInt64(0, f.SizeBytes)
	}
	manifest := InstalledManifest{
		ModelID:        modelID,
		Version:        version,
		Repo:           repo,
		TotalBytes:     totalBytes,
		Files:          files,
		InstalledAtUTC: time.Now().UTC(),
	}
	bytes, err := json.MarshalIndent(manifest, "", "  ")
	if err != nil {
		return
	}
	_ = os.WriteFile(filepath.Join(modelDir, "installed.json"), bytes, 0o644)
}

// IsModelCached ports IsModelCachedAsync.
func (s *ModelDownloadService) IsModelCached(ctx context.Context, modelID string) (bool, error) {
	if err := validateModelID(modelID); err != nil {
		return false, err
	}
	if err := ctx.Err(); err != nil {
		return false, err
	}
	if fileExists(s.singleFilePath(modelID)) {
		return true, nil
	}
	info, err := os.Stat(filepath.Join(s.storageDir, modelID))
	return err == nil && info.IsDir(), nil
}

// DeleteModel ports DeleteModelAsync.
func (s *ModelDownloadService) DeleteModel(ctx context.Context, modelID string) error {
	if err := validateModelID(modelID); err != nil {
		return err
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	single := s.singleFilePath(modelID)
	if fileExists(single) {
		if err := os.Remove(single); err != nil {
			return err
		}
	}
	dir := filepath.Join(s.storageDir, modelID)
	if info, err := os.Stat(dir); err == nil && info.IsDir() {
		if err := os.RemoveAll(dir); err != nil {
			return err
		}
	}
	return nil
}

// GetAvailableDiskSpaceBytes ports GetAvailableDiskSpaceBytesAsync using the
// host filesystem. Returns the free bytes on the storage drive.
func (s *ModelDownloadService) GetAvailableDiskSpaceBytes(ctx context.Context) (int64, error) {
	if err := ctx.Err(); err != nil {
		return 0, err
	}
	abs, err := filepath.Abs(s.storageDir)
	if err != nil {
		return 0, err
	}
	return availableDiskSpaceBytes(abs)
}

// ── helpers ────────────────────────────────────────────────────────────────

func (s *ModelDownloadService) singleFilePath(modelID string) string {
	return filepath.Join(s.storageDir, modelID+".gguf")
}

// downloadToFile streams the provider's bytes for rawURL into destPath, emitting
// coarse progress. Mirrors DownloadToFileAsync's success/EnsureSuccessStatusCode
// semantics: an unknown URL is the analogue of a non-success HTTP status.
func (s *ModelDownloadService) downloadToFile(ctx context.Context, rawURL, destPath string, progress func(float64)) error {
	if err := ctx.Err(); err != nil {
		return err
	}
	payload, ok := s.provider.Fetch(ctx, rawURL)
	if !ok {
		return fmt.Errorf("download failed: no content for %q (unreachable)", rawURL)
	}
	if err := os.WriteFile(destPath, payload, 0o644); err != nil {
		return err
	}
	report(progress, 1.0)
	return nil
}

func validateModelID(modelID string) error {
	if strings.TrimSpace(modelID) == "" {
		return errors.New("model ID must not be empty")
	}
	return nil
}

func report(progress func(float64), v float64) {
	if progress != nil {
		progress(v)
	}
}

func reportOverall(progress func(float64), done, total int64) {
	if progress == nil {
		return
	}
	if total <= 0 {
		progress(0.0)
		return
	}
	ratio := float64(done) / float64(total)
	if ratio > 0.999 {
		ratio = 0.999
	}
	progress(ratio)
}

func buildBundlePrimaryURL(repo, fileName string) string {
	return fmt.Sprintf(
		"https://modelscope.cn/api/v1/models/%s/repo?Revision=master&FilePath=%s",
		repo, url.QueryEscape(fileName))
}

func buildBundleFallbackURL(repo, fileName string) string {
	return fmt.Sprintf(
		"https://modelscope.cn/models/%s/resolve/master/%s",
		repo, url.QueryEscape(fileName))
}

// verifyFileSha256 hashes the file and compares against expected (case-insensitive),
// stripping an optional "sha256:" algorithm prefix. Ports VerifySha256Async.
func verifyFileSha256(filePath, expected string) (bool, error) {
	f, err := os.Open(filePath)
	if err != nil {
		return false, err
	}
	defer f.Close()
	h := sha256.New()
	buf := make([]byte, 81920)
	for {
		n, rerr := f.Read(buf)
		if n > 0 {
			h.Write(buf[:n])
		}
		if rerr != nil {
			break
		}
	}
	actual := hex.EncodeToString(h.Sum(nil))
	return strings.EqualFold(actual, StripShaAlgorithmPrefix(expected)), nil
}

// StripShaAlgorithmPrefix returns the hex portion of a SHA-256 checksum,
// stripping an optional leading "sha256:" (or "SHA-256:", etc.) token. Ports
// ModelDownloadService.StripShaAlgorithmPrefix.
func StripShaAlgorithmPrefix(raw string) string {
	if raw == "" {
		return ""
	}
	trimmed := strings.TrimSpace(raw)
	colon := strings.IndexByte(trimmed, ':')
	if colon < 0 {
		return trimmed
	}
	prefix := trimmed[:colon]
	if len(prefix) > 0 && len(prefix) <= 16 {
		isAlgName := true
		for _, c := range prefix {
			if !(isLetterOrDigit(c) || c == '-' || c == '_') {
				isAlgName = false
				break
			}
		}
		if isAlgName {
			return strings.TrimSpace(trimmed[colon+1:])
		}
	}
	return trimmed
}

func isLetterOrDigit(c rune) bool {
	return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
}

var _ IModelDownloadService = (*ModelDownloadService)(nil)
