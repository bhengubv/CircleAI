// model_downloader.go
//
// Ports CircleAI.Core.IModelDownloader (IModelDownloader.cs) and
// CircleAI.Core.ModelDownloader (+ nested DownloadProgressReport)
// (ModelDownloader.cs).
//
// Source-agnostic downloader: walks a list of IModelSource in order, falling
// through on failure so one supplier going dark does not break bootstrap.
//
// Divergence from C#, intentional and behaviour-preserving: C# reads the model
// set from an assembly-embedded registry.json. Go has no embedded-resource
// concept, so the registry is injected as a map[string]DownloaderModelEntry via
// the constructor (the flat package already owns ModelEntry/ModelRegistry for
// the richer catalog path). All routing, fallback, bundle-rejection, and the
// progress-bridge logic match the C# reference exactly.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// DownloadProgressReport mirrors ModelDownloader.DownloadProgressReport — the
// class+callback progress shape emitted to consumers of the high-level
// DownloadModel entry point.
type DownloadProgressReport struct {
	FileName               string
	BytesReceived          int64
	TotalBytes             int64
	BytesPerSecond         float64
	EstimatedTimeRemaining time.Duration
}

// DownloaderModelEntry is the registry row the downloader resolves a modelId
// to. Mirrors ModelDownloader.ModelEntry (single-file legacy shape + bundle
// shape). Named DownloaderModelEntry to avoid colliding with the catalog-level
// ModelEntry already in registry.go.
type DownloaderModelEntry struct {
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
func (e DownloaderModelEntry) IsBundle() bool { return len(e.BundleFiles) > 0 }

// IModelDownloader downloads a model (file or set) to local storage.
// Ports CircleAI.Core.IModelDownloader.
type IModelDownloader interface {
	// DownloadModel downloads the model identified by modelId to localPath,
	// resolving the URL set internally.
	DownloadModel(ctx context.Context, modelId, localPath string) error

	// DownloadFromCandidates downloads a single file by trying each candidate
	// URL in order (first = primary, rest = fallbacks). Returns the name of the
	// source that succeeded.
	DownloadFromCandidates(
		ctx context.Context,
		candidateURLs []string,
		localFilePath string,
		progress func(SourceDownloadProgress),
	) (string, error)
}

// ModelDownloader is the source-agnostic IModelDownloader implementation.
// Ports CircleAI.Core.ModelDownloader.
type ModelDownloader struct {
	sources  []IModelSource
	registry map[string]DownloaderModelEntry

	// ProgressChanged, when non-nil, receives high-level progress reports from
	// DownloadModel (mirrors the C# ProgressChanged event).
	ProgressChanged func(DownloadProgressReport)
}

// NewModelDownloader builds a downloader over the given sources and registry.
// At least one source is required (mirrors the C# ctor guard). The registry map
// may be nil/empty; DownloadModel then reports the model as unknown.
func NewModelDownloader(sources []IModelSource, registry map[string]DownloaderModelEntry) (*ModelDownloader, error) {
	if len(sources) == 0 {
		return nil, errors.New("at least one model source is required")
	}
	reg := make(map[string]DownloaderModelEntry, len(registry))
	for k, v := range registry {
		reg[k] = v
	}
	return &ModelDownloader{sources: sources, registry: reg}, nil
}

// DownloadModel resolves modelId in the registry and downloads its file,
// trying PrimaryUrl then FallbackUrl. Bundle entries are rejected with a
// steer-to-the-right-path error, exactly like the C# reference.
func (d *ModelDownloader) DownloadModel(ctx context.Context, modelId, localPath string) error {
	if strings.TrimSpace(modelId) == "" {
		return errors.New("modelId is required")
	}
	if strings.TrimSpace(localPath) == "" {
		return errors.New("localPath is required")
	}

	entry, ok := d.registry[modelId]
	if !ok {
		return fmt.Errorf("model %q is not in the registry. Known models: %s",
			modelId, strings.Join(d.knownModelIds(), ", "))
	}

	if err := os.MkdirAll(localPath, 0o755); err != nil {
		return err
	}

	if entry.IsBundle() {
		return fmt.Errorf(
			"model %q is a multi-file bundle (registry entry has BundleFiles[]); "+
				"use the multi-file bundle downloader instead — this legacy single-file "+
				"downloader cannot fetch a multi-file bundle", modelId)
	}

	targetFile := filepath.Join(localPath, entry.FileName)
	candidates := buildCandidateList(entry)
	if len(candidates) == 0 {
		return fmt.Errorf("model %q has no PrimaryUrl or FallbackUrl configured", modelId)
	}

	bridge := func(p SourceDownloadProgress) {
		if d.ProgressChanged != nil {
			d.ProgressChanged(DownloadProgressReport{
				FileName:               p.FileName,
				BytesReceived:          p.BytesReceived,
				TotalBytes:             p.TotalBytes,
				BytesPerSecond:         p.BytesPerSecond,
				EstimatedTimeRemaining: p.EstimatedTimeRemaining,
			})
		}
	}

	if _, err := d.DownloadFromCandidates(ctx, candidates, targetFile, bridge); err != nil {
		cleanupPartialFile(targetFile)
		return err
	}
	return nil
}

// DownloadFromCandidates tries each candidate URL against the first source
// whose Name/host matches, falling through on failure and cleaning up the
// partial file between attempts. Ports DownloadFromCandidatesAsync.
func (d *ModelDownloader) DownloadFromCandidates(
	ctx context.Context,
	candidateURLs []string,
	localFilePath string,
	progress func(SourceDownloadProgress),
) (string, error) {
	if len(candidateURLs) == 0 {
		return "", errors.New("at least one candidate URL is required")
	}
	if strings.TrimSpace(localFilePath) == "" {
		return "", errors.New("localFilePath is required")
	}
	if dir := filepath.Dir(localFilePath); dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return "", err
		}
	}

	var failures []string
	for _, u := range candidateURLs {
		if err := ctx.Err(); err != nil {
			return "", err
		}
		if strings.TrimSpace(u) == "" {
			continue
		}
		src := d.matchSource(u)
		if src == nil {
			failures = append(failures, fmt.Sprintf("(no registered source for %q)", u))
			continue
		}
		if err := src.Download(ctx, u, localFilePath, progress); err != nil {
			if ctx.Err() != nil {
				return "", ctx.Err()
			}
			failures = append(failures, fmt.Sprintf("%s: %v", src.Name(), err))
			cleanupPartialFile(localFilePath)
			continue
		}
		return src.Name(), nil
	}
	return "", fmt.Errorf("all model sources failed:\n  %s", strings.Join(failures, "\n  "))
}

// matchSource picks the source whose Name appears in the URL host, then falls
// back to a modelscope substring rule. Ports ModelDownloader.MatchSource.
func (d *ModelDownloader) matchSource(rawURL string) IModelSource {
	u, err := url.Parse(rawURL)
	if err != nil || u.Host == "" {
		return nil
	}
	host := u.Host
	for _, s := range d.sources {
		if strings.Contains(strings.ToLower(host), strings.ToLower(s.Name())) {
			return s
		}
	}
	if strings.Contains(strings.ToLower(host), "modelscope") {
		for _, s := range d.sources {
			if strings.EqualFold(s.Name(), "ModelScope") {
				return s
			}
		}
	}
	return nil
}

func (d *ModelDownloader) knownModelIds() []string {
	ids := make([]string, 0, len(d.registry))
	for k := range d.registry {
		ids = append(ids, k)
	}
	sort.Strings(ids)
	return ids
}

func buildCandidateList(e DownloaderModelEntry) []string {
	list := make([]string, 0, 2)
	if strings.TrimSpace(e.PrimaryURL) != "" {
		list = append(list, e.PrimaryURL)
	}
	if strings.TrimSpace(e.FallbackURL) != "" {
		list = append(list, e.FallbackURL)
	}
	return list
}

func cleanupPartialFile(path string) {
	if _, err := os.Stat(path); err == nil {
		_ = os.Remove(path)
	}
}

var _ IModelDownloader = (*ModelDownloader)(nil)
