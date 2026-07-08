// model_source.go
//
// Ports CircleAI.Core.IModelSource + DownloadProgress (IModelSource.cs),
// CircleAI.Core.Sources.ModelScopeSource (ModelScopeSource.cs),
// CircleAI.Core.Sources.HuggingFaceSource tombstone (HuggingFaceSource.cs),
// and the shared streaming helper CircleAI.Core.Sources.SourceDownloadHelper
// (SourceDownloadHelper.cs).
//
// Per the port NOTE, network I/O is injected behind an interface: an
// IModelSource is fed bytes by a deterministic in-memory content provider
// (ContentProvider) rather than reaching modelscope.cn. The host wires a
// provider that returns the byte payload for a URL; the source streams it
// into localPath in BufferSize chunks, reports progress with ETA exactly
// like SourceDownloadHelper, and supports Range-style resume.

package circleai

import (
	"context"
	"errors"
	"fmt"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// SourceDownloadProgress is a snapshot of an in-flight download.
//
// Named SourceDownloadProgress (not DownloadProgress) because the flat
// package already has a leaner DownloadProgress in models.go. This mirrors
// C# CircleAI.Core.DownloadProgress field-for-field.
type SourceDownloadProgress struct {
	FileName               string
	BytesReceived          int64
	TotalBytes             int64 // -1 when unknown
	BytesPerSecond         float64
	EstimatedTimeRemaining time.Duration
}

// IModelSource abstracts a model-file source with a fallback-friendly probe +
// download surface. Ports CircleAI.Core.IModelSource.
type IModelSource interface {
	// Name is the friendly source name (e.g. "ModelScope"). Used in logs and
	// URL→source matching.
	Name() string

	// IsAvailable is a lightweight reachability probe. Returns false on any
	// failure rather than an error.
	IsAvailable(ctx context.Context) bool

	// Download fetches the file at url into localPath, reporting progress.
	// progress may be nil.
	Download(ctx context.Context, url, localPath string, progress func(SourceDownloadProgress)) error
}

// ContentProvider yields the byte payload served at a URL. This is the
// injection point that replaces real HTTP: a deterministic in-memory map,
// a fixture loader, or a test fake. Return (nil, false) for an unknown URL —
// the source surfaces that as a "not reachable" error, mirroring a 404.
type ContentProvider interface {
	Fetch(ctx context.Context, url string) ([]byte, bool)
}

// MapContentProvider is a ContentProvider backed by an in-memory URL→bytes map.
type MapContentProvider struct {
	content map[string][]byte
}

// NewMapContentProvider builds a provider from a URL→bytes map. The map is
// copied so later mutation of the argument does not affect the provider.
func NewMapContentProvider(content map[string][]byte) *MapContentProvider {
	cp := &MapContentProvider{content: make(map[string][]byte, len(content))}
	for k, v := range content {
		b := make([]byte, len(v))
		copy(b, v)
		cp.content[k] = b
	}
	return cp
}

// Put registers (or replaces) the payload served at url.
func (m *MapContentProvider) Put(url string, payload []byte) {
	b := make([]byte, len(payload))
	copy(b, payload)
	m.content[url] = b
}

// Fetch returns the payload for url, or (nil,false) if none is registered.
func (m *MapContentProvider) Fetch(_ context.Context, url string) ([]byte, bool) {
	b, ok := m.content[url]
	if !ok {
		return nil, false
	}
	out := make([]byte, len(b))
	copy(out, b)
	return out, true
}

// sourceDownloadBufferSize mirrors SourceDownloadHelper.BufferSize (8192).
const sourceDownloadBufferSize = 8192

// sourceDownloadWithProgress streams the provider's payload for url into
// localPath in BufferSize chunks, honouring an existing partial file as a
// resume point and emitting a final progress report. This is the Go analogue
// of SourceDownloadHelper.DownloadWithProgressAsync — the timing-based 500ms
// throttle is replaced by a per-chunk-final report so results are
// deterministic and test-stable, while preserving the byte math (totalBytes =
// existing + remaining) and ETA computation.
func sourceDownloadWithProgress(
	ctx context.Context,
	provider ContentProvider,
	url, localPath string,
	progress func(SourceDownloadProgress),
) error {
	fileName := filepath.Base(localPath)

	full, ok := provider.Fetch(ctx, url)
	if !ok {
		return fmt.Errorf("source: no content for %q (unreachable)", url)
	}

	// Resume: if a partial exists and is a strict prefix of the full payload,
	// continue from there; otherwise restart from scratch (mirrors the C#
	// "server ignored Range → FileMode.Create" branch).
	var existing int64
	if fi, err := os.Stat(localPath); err == nil {
		existing = fi.Size()
	}
	startBytes := int64(0)
	appendMode := false
	if existing > 0 && existing < int64(len(full)) && bytesHavePrefix(full, localPath, existing) {
		startBytes = existing
		appendMode = true
	}

	totalBytes := int64(len(full))

	flags := os.O_CREATE | os.O_WRONLY
	if appendMode {
		flags |= os.O_APPEND
	} else {
		flags |= os.O_TRUNC
	}
	f, err := os.OpenFile(localPath, flags, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()

	remaining := full[startBytes:]
	bytesRead := startBytes
	start := time.Now()
	for off := 0; off < len(remaining); off += sourceDownloadBufferSize {
		if err := ctx.Err(); err != nil {
			return err
		}
		end := off + sourceDownloadBufferSize
		if end > len(remaining) {
			end = len(remaining)
		}
		n, werr := f.Write(remaining[off:end])
		if werr != nil {
			return werr
		}
		bytesRead += int64(n)
	}

	if progress != nil {
		elapsed := time.Since(start)
		var bps float64
		if elapsed.Seconds() > 0 {
			bps = float64(bytesRead-startBytes) / elapsed.Seconds()
		}
		var eta time.Duration
		if totalBytes > 0 && bps > 0 {
			rem := totalBytes - bytesRead
			if rem > 0 {
				eta = time.Duration(float64(rem) / bps * float64(time.Second))
			}
		}
		progress(SourceDownloadProgress{
			FileName:               fileName,
			BytesReceived:          bytesRead,
			TotalBytes:             totalBytes,
			BytesPerSecond:         bps,
			EstimatedTimeRemaining: eta,
		})
	}
	return nil
}

// bytesHavePrefix reports whether the first n bytes of the file at path equal
// the first n bytes of full (used to validate a resume candidate).
func bytesHavePrefix(full []byte, path string, n int64) bool {
	if n > int64(len(full)) {
		return false
	}
	f, err := os.Open(path)
	if err != nil {
		return false
	}
	defer f.Close()
	buf := make([]byte, n)
	read, _ := f.Read(buf)
	if int64(read) != n {
		return false
	}
	for i := int64(0); i < n; i++ {
		if buf[i] != full[i] {
			return false
		}
	}
	return true
}

// ── ModelScopeSource ──────────────────────────────────────────────────────

const (
	modelScopeHostName = "modelscope.cn"
	modelScopeProbeURL = "https://modelscope.cn/"
)

// ModelScopeSource is the IModelSource backed by modelscope.cn (Alibaba).
// Ports CircleAI.Core.Sources.ModelScopeSource. The real HttpClient is
// replaced by an injected ContentProvider.
type ModelScopeSource struct {
	provider ContentProvider
}

// NewModelScopeSource builds a source over the given content provider. Passing
// nil yields a source that reports unavailable and fails every download — the
// deterministic analogue of "no network".
func NewModelScopeSource(provider ContentProvider) *ModelScopeSource {
	return &ModelScopeSource{provider: provider}
}

// Name returns "ModelScope".
func (s *ModelScopeSource) Name() string { return "ModelScope" }

// IsAvailable probes the provider for the ModelScope root URL.
func (s *ModelScopeSource) IsAvailable(ctx context.Context) bool {
	if s.provider == nil {
		return false
	}
	_, ok := s.provider.Fetch(ctx, modelScopeProbeURL)
	return ok
}

// Download enforces the modelscope.cn host restriction then streams the file.
func (s *ModelScopeSource) Download(ctx context.Context, rawURL, localPath string, progress func(SourceDownloadProgress)) error {
	if s.provider == nil {
		return errors.New("ModelScopeSource: no content provider configured")
	}
	if strings.TrimSpace(rawURL) == "" {
		return errors.New("url is required")
	}
	if strings.TrimSpace(localPath) == "" {
		return errors.New("localPath is required")
	}
	u, err := url.Parse(rawURL)
	if err != nil || u.Host == "" || !strings.EqualFold(hostSuffix(u.Host, modelScopeHostName), modelScopeHostName) {
		return fmt.Errorf("URL host must be on %s for %s source. Got: %s", modelScopeHostName, s.Name(), rawURL)
	}
	if dir := filepath.Dir(localPath); dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
	}
	return sourceDownloadWithProgress(ctx, s.provider, rawURL, localPath, progress)
}

// hostSuffix returns want if host ends with want (case-insensitive), else host.
// Lets "www.modelscope.cn" and "modelscope.cn" both match the suffix check.
func hostSuffix(host, want string) string {
	if strings.HasSuffix(strings.ToLower(host), strings.ToLower(want)) {
		return want
	}
	return host
}

// ── HuggingFaceSource tombstone ───────────────────────────────────────────

// ErrHuggingFaceSourceRemoved is returned by NewHuggingFaceSource. HuggingFace
// (a US company) was removed; all downloads route through ModelScope. Ports
// the [Obsolete(error:true)] tombstone in HuggingFaceSource.cs.
var ErrHuggingFaceSourceRemoved = errors.New(
	"HuggingFaceSource has been removed. Use ModelScopeSource (modelscope.cn).")

// NewHuggingFaceSource always fails — kept as a loud tombstone so callers that
// still reference it break at construction rather than silently at runtime.
func NewHuggingFaceSource() (IModelSource, error) {
	return nil, ErrHuggingFaceSourceRemoved
}

var _ IModelSource = (*ModelScopeSource)(nil)
