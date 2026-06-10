// registry.go
//
// ModelEntry + ModelRegistry + ModelRegistryService.
// CheckForUpgrades walks installed.json files in a storage directory
// and compares against the active registry, returning UpgradeInfo per drift.
//
// WriteInstalledManifest stamps installed.json after a successful install.

package circleai

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// ModelEntry is one model in the catalog.
type ModelEntry struct {
	Name         string       `json:"name"`
	Version      string       `json:"version"`
	Quantization string       `json:"quantization,omitempty"`
	URL          string       `json:"url,omitempty"`
	Checksum     string       `json:"checksum,omitempty"`
	Repo         string       `json:"repo,omitempty"`
	TotalBytes   int64        `json:"total_bytes,omitempty"`
	BundleFiles  []BundleFile `json:"bundle_files,omitempty"`
	MinRAMGB     float64      `json:"min_ram_gb,omitempty"`
	MinStorageGB float64      `json:"min_storage_gb,omitempty"`
	Capabilities []string     `json:"capabilities,omitempty"`
	QualityRank  int          `json:"quality_rank,omitempty"`
}

// IsBundle reports whether this entry is a multi-file bundle.
func (m ModelEntry) IsBundle() bool {
	return len(m.BundleFiles) > 0
}

// ModelRegistry is the active catalog snapshot.
type ModelRegistry struct {
	RegistryURL string       `json:"registry_url"`
	LastUpdated time.Time    `json:"last_updated"`
	Models      []ModelEntry `json:"models"`
}

// ModelRegistryService holds the active registry and exposes the upgrade detector.
type ModelRegistryService struct {
	registry *ModelRegistry
}

// NewModelRegistryService builds a service. Use SetRegistry to inject a registry.
func NewModelRegistryService() *ModelRegistryService {
	return &ModelRegistryService{}
}

// SetRegistry injects a registry (mainly for tests / synchronous workflows).
func (s *ModelRegistryService) SetRegistry(reg ModelRegistry) {
	s.registry = &reg
}

// AllModels returns the current registry contents.
func (s *ModelRegistryService) AllModels() []ModelEntry {
	if s.registry == nil {
		return nil
	}
	return s.registry.Models
}

// GetLatestModel returns the entry whose Name matches `modelName`, case-insensitive.
func (s *ModelRegistryService) GetLatestModel(modelName string) (ModelEntry, bool) {
	if s.registry == nil || modelName == "" {
		return ModelEntry{}, false
	}
	low := strings.ToLower(modelName)
	for _, m := range s.registry.Models {
		if strings.ToLower(m.Name) == low {
			return m, true
		}
	}
	return ModelEntry{}, false
}

// CheckForUpgrades walks every installed model under storageDirectory and
// returns one UpgradeInfo per detected drift (Version, file SHA, or both).
func (s *ModelRegistryService) CheckForUpgrades(storageDirectory string) ([]UpgradeInfo, error) {
	if storageDirectory == "" {
		return nil, errors.New("storageDirectory is required")
	}
	now := time.Now().UTC()
	out := make([]UpgradeInfo, 0)

	for _, entry := range s.AllModels() {
		modelDir := filepath.Join(storageDirectory, entry.Name)
		if !isDir(modelDir) {
			continue
		}
		manifestPath := filepath.Join(modelDir, "installed.json")
		manifest, ok := readManifest(manifestPath)
		if !ok {
			out = append(out, UpgradeInfo{
				ModelID:                entry.Name,
				InstalledVersion:       "",
				AvailableVersion:       entry.Version,
				Reason:                 UpgradeReasonUnknown,
				EstimatedDownloadBytes: entry.TotalBytes,
				DetectedAt:             now,
			})
			continue
		}

		versionChanged := manifest.Version != entry.Version
		shaChanged, driftBytes := compareBundleSha(manifest.Files, entry.BundleFiles)
		if !versionChanged && !shaChanged {
			continue
		}

		var reason UpgradeReason
		switch {
		case versionChanged && shaChanged:
			reason = UpgradeReasonBoth
		case versionChanged:
			reason = UpgradeReasonVersionChanged
		default:
			reason = UpgradeReasonShaChanged
		}

		out = append(out, UpgradeInfo{
			ModelID:                entry.Name,
			InstalledVersion:       manifest.Version,
			AvailableVersion:       entry.Version,
			Reason:                 reason,
			EstimatedDownloadBytes: driftBytes,
			DetectedAt:             now,
		})
	}

	return out, nil
}

// WriteInstalledManifest stamps installed.json into `modelDir`.
// Best-effort — silent failures so a manifest hiccup never breaks an install.
func WriteInstalledManifest(modelDir, modelID, version, repo string, bundleFiles []BundleFile) error {
	if modelDir == "" || modelID == "" {
		return errors.New("modelDir and modelID are required")
	}
	if err := os.MkdirAll(modelDir, 0o755); err != nil {
		return err
	}
	var total int64
	for _, f := range bundleFiles {
		if f.SizeBytes > 0 {
			total += f.SizeBytes
		}
	}
	m := InstalledManifest{
		ModelID:        modelID,
		Version:        version,
		Repo:           repo,
		TotalBytes:     total,
		Files:          bundleFiles,
		InstalledAtUTC: time.Now().UTC(),
	}
	b, err := json.MarshalIndent(m, "", "  ")
	if err != nil {
		return fmt.Errorf("marshal manifest: %w", err)
	}
	return os.WriteFile(filepath.Join(modelDir, "installed.json"), b, 0o644)
}

// ── helpers ─────────────────────────────────────────────────────────────

func isDir(p string) bool {
	info, err := os.Stat(p)
	return err == nil && info.IsDir()
}

func readManifest(p string) (InstalledManifest, bool) {
	b, err := os.ReadFile(p)
	if err != nil {
		return InstalledManifest{}, false
	}
	var m InstalledManifest
	if err := json.Unmarshal(b, &m); err != nil {
		return InstalledManifest{}, false
	}
	return m, true
}

func compareBundleSha(installed, available []BundleFile) (drift bool, bytes int64) {
	if len(available) == 0 {
		return false, 0
	}
	byName := make(map[string]BundleFile, len(installed))
	for _, f := range installed {
		byName[f.Name] = f
	}
	for _, av := range available {
		inst, found := byName[av.Name]
		if !found || !strings.EqualFold(inst.Sha256, av.Sha256) {
			drift = true
			bytes += av.SizeBytes
		}
	}
	return drift, bytes
}
