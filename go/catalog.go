// catalog.go
//
// ModelScope catalog client + signature verifier. Port of
// CircleAI.Core.Models.ModelScopeCatalogClient.
//
// HTTP via the stdlib net/http; no external dep. Disk cache as JSON.

package circleai

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// CatalogSignatureResult is the outcome of a catalog payload signature check.
type CatalogSignatureResult int

const (
	CatalogSigValid         CatalogSignatureResult = 0
	CatalogSigInvalid       CatalogSignatureResult = 1
	CatalogSigMissing       CatalogSignatureResult = 2
	CatalogSigNotConfigured CatalogSignatureResult = 3
)

// ICatalogSignatureVerifier verifies a catalog payload against an embedded public key.
type ICatalogSignatureVerifier interface {
	Verify(payload []byte, signatureBase64 string) CatalogSignatureResult
}

// NullCatalogSignatureVerifier always returns NotConfigured (fail-closed).
type NullCatalogSignatureVerifier struct{}

// Verify implements ICatalogSignatureVerifier.
func (NullCatalogSignatureVerifier) Verify(_ []byte, _ string) CatalogSignatureResult {
	return CatalogSigNotConfigured
}

// CatalogRefreshCadence controls how often the catalog refreshes.
type CatalogRefreshCadence int

const (
	CatalogCadenceOnStartup CatalogRefreshCadence = 0
	CatalogCadenceDaily     CatalogRefreshCadence = 1
	CatalogCadenceManual    CatalogRefreshCadence = 2
	CatalogCadenceNever     CatalogRefreshCadence = 3
)

// ModelScopeCatalogOptions configures the catalog client.
type ModelScopeCatalogOptions struct {
	BaseURI        string
	CacheDirectory string
	Cadence        CatalogRefreshCadence
	Filter         string
	PageSize       int
	UserAgent      string
}

// DefaultCatalogOptions returns recommended defaults.
func DefaultCatalogOptions() ModelScopeCatalogOptions {
	home, _ := os.UserHomeDir()
	return ModelScopeCatalogOptions{
		BaseURI:        "https://www.modelscope.cn",
		CacheDirectory: filepath.Join(home, ".circleai", "catalog"),
		Cadence:        CatalogCadenceOnStartup,
		Filter:         "MNN",
		PageSize:       100,
		UserAgent:      "Mozilla/5.0 (Circle AI SDK) CircleAI-Go/1.5",
	}
}

// ModelScopeCatalogClient discovers models on ModelScope + caches the catalog.
type ModelScopeCatalogClient struct {
	options             ModelScopeCatalogOptions
	verifier            ICatalogSignatureVerifier
	networkTypeProvider func() string // returns "online" / "none" / ""
	httpClient          *http.Client
	refreshedThisRun    bool
}

// NewModelScopeCatalogClient builds a client with defaults.
func NewModelScopeCatalogClient(opts ModelScopeCatalogOptions, verifier ICatalogSignatureVerifier, networkTypeProvider func() string) *ModelScopeCatalogClient {
	if opts.BaseURI == "" {
		opts = DefaultCatalogOptions()
	}
	if verifier == nil {
		verifier = NullCatalogSignatureVerifier{}
	}
	_ = os.MkdirAll(opts.CacheDirectory, 0o755)
	return &ModelScopeCatalogClient{
		options:             opts,
		verifier:            verifier,
		networkTypeProvider: networkTypeProvider,
		httpClient:          &http.Client{Timeout: 10 * time.Second},
	}
}

// CacheFilePath is the on-disk path of the cached catalog.
func (c *ModelScopeCatalogClient) CacheFilePath() string {
	return filepath.Join(c.options.CacheDirectory, "catalog.json")
}

// SignatureFilePath is the on-disk path of the catalog signature.
func (c *ModelScopeCatalogClient) SignatureFilePath() string {
	return filepath.Join(c.options.CacheDirectory, "catalog.sig")
}

// IsRefreshDue reports whether the cache should be refreshed now.
func (c *ModelScopeCatalogClient) IsRefreshDue() bool {
	switch c.options.Cadence {
	case CatalogCadenceNever, CatalogCadenceManual:
		return false
	}
	if c.networkTypeProvider != nil {
		net := strings.ToLower(c.networkTypeProvider())
		if net == "none" {
			return false
		}
	}
	info, err := os.Stat(c.CacheFilePath())
	if err != nil {
		return true
	}
	if c.options.Cadence == CatalogCadenceOnStartup {
		return !c.refreshedThisRun
	}
	// Daily — refresh on different UTC date.
	mtime := info.ModTime().UTC()
	now := time.Now().UTC()
	return mtime.Format("2006-01-02") < now.Format("2006-01-02")
}

// LoadFromDisk reads the cached catalog. Returns nil if not present.
func (c *ModelScopeCatalogClient) LoadFromDisk() *ModelRegistry {
	b, err := os.ReadFile(c.CacheFilePath())
	if err != nil {
		return nil
	}
	var reg ModelRegistry
	if err := json.Unmarshal(b, &reg); err != nil {
		return nil
	}
	return &reg
}

// GetCachedCatalog refreshes if due, otherwise returns whatever is on disk.
func (c *ModelScopeCatalogClient) GetCachedCatalog(ctx context.Context, acceptStaleOnError bool) (*ModelRegistry, error) {
	if c.IsRefreshDue() {
		reg, err := c.Refresh(ctx)
		if err == nil {
			return reg, nil
		}
		if !acceptStaleOnError {
			return nil, err
		}
	}
	return c.LoadFromDisk(), nil
}

// Refresh pulls the live catalog, verifies signature, writes to disk.
func (c *ModelScopeCatalogClient) Refresh(ctx context.Context) (*ModelRegistry, error) {
	reg, err := c.fetchLive(ctx)
	if err != nil {
		return nil, err
	}
	b, err := json.MarshalIndent(reg, "", "  ")
	if err != nil {
		return nil, err
	}

	// Existing signature, if any.
	var existingSig string
	if data, err := os.ReadFile(c.SignatureFilePath()); err == nil {
		existingSig = strings.TrimSpace(string(data))
	}

	sigResult := c.verifier.Verify(b, existingSig)
	if sigResult == CatalogSigInvalid {
		return nil, fmt.Errorf("catalog signature did not verify against configured public key")
	}

	if err := os.MkdirAll(c.options.CacheDirectory, 0o755); err != nil {
		return nil, err
	}
	if err := os.WriteFile(c.CacheFilePath(), b, 0o644); err != nil {
		return nil, err
	}
	c.refreshedThisRun = true
	return reg, nil
}

func (c *ModelScopeCatalogClient) fetchLive(ctx context.Context) (*ModelRegistry, error) {
	listingURL := fmt.Sprintf(
		"%s/api/v1/models?Name=%s&PageSize=%d",
		c.options.BaseURI,
		url.QueryEscape(c.options.Filter),
		c.options.PageSize,
	)
	listing, err := c.httpGetJSON(ctx, listingURL)
	if err != nil {
		return nil, err
	}

	type listingItem struct {
		Name         string `json:"Name"`
		Path         string `json:"Path"`
		Revision     string `json:"Revision"`
		Quantization string `json:"Quantization"`
	}
	type listingData struct {
		Model []listingItem `json:"Model"`
	}
	type listingResp struct {
		Data listingData `json:"Data"`
	}

	var resp listingResp
	if err := json.Unmarshal(listing, &resp); err != nil {
		return nil, err
	}

	entries := make([]ModelEntry, 0, len(resp.Data.Model))
	for _, m := range resp.Data.Model {
		if m.Name == "" || m.Path == "" {
			continue
		}
		filesURL := fmt.Sprintf("%s/api/v1/models/%s/repo/files?Revision=master", c.options.BaseURI, m.Path)
		filesRaw, err := c.httpGetJSON(ctx, filesURL)
		if err != nil {
			continue
		}
		type fileItem struct {
			Name   string `json:"Name"`
			Path   string `json:"Path"`
			Sha256 string `json:"Sha256"`
			Size   int64  `json:"Size"`
		}
		type filesData struct {
			Files []fileItem `json:"Files"`
		}
		type filesResp struct {
			Data filesData `json:"Data"`
		}
		var fr filesResp
		if err := json.Unmarshal(filesRaw, &fr); err != nil {
			continue
		}
		var bundle []BundleFile
		var total int64
		for _, f := range fr.Data.Files {
			name := f.Path
			if name == "" {
				name = f.Name
			}
			if name == "" {
				continue
			}
			bundle = append(bundle, BundleFile{Name: name, Sha256: f.Sha256, SizeBytes: f.Size})
			total += f.Size
		}
		version := m.Revision
		if version == "" {
			version = "master"
		}
		entries = append(entries, ModelEntry{
			Name:         m.Name,
			Version:      version,
			Quantization: m.Quantization,
			Repo:         m.Path,
			TotalBytes:   total,
			BundleFiles:  bundle,
		})
	}

	return &ModelRegistry{
		RegistryURL: c.options.BaseURI,
		LastUpdated: time.Now().UTC(),
		Models:      entries,
	}, nil
}

func (c *ModelScopeCatalogClient) httpGetJSON(ctx context.Context, u string) ([]byte, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("User-Agent", c.options.UserAgent)
	resp, err := c.httpClient.Do(req)
	if err != nil {
		return nil, err
	}
	defer func() { _ = resp.Body.Close() }()
	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("HTTP %d fetching %s", resp.StatusCode, u)
	}
	return io.ReadAll(resp.Body)
}
