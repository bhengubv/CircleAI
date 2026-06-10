// models_v15.go
//
// 1.5.0 parity additions to the models layer:
//   * ChatMessage gains optional ImageBytes
//   * ChatResponse + FinishReason
//   * BundleFile, InstalledManifest, UpgradeInfo, UpgradeReason
//
// Kept in a separate file from the original models.go to avoid touching
// the existing API surface beyond adding the new types.

package circleai

import "time"

// VisionChatMessage is ChatMessage + optional ImageBytes (JPEG/PNG/WebP).
//
// Go's ChatMessage struct stays exactly as it was for backward
// compatibility — adding a new field would change zero-value semantics
// for callers. New code that needs vision should use VisionChatMessage;
// the renderer adapts via a type switch.
type VisionChatMessage struct {
	Role       string
	Content    string
	ImageBytes []byte
}

// FinishReason mirrors CircleAI.Inference.FinishReason.
type FinishReason int

const (
	FinishReasonStop      FinishReason = 0
	FinishReasonLength    FinishReason = 1
	FinishReasonCancelled FinishReason = 2
	FinishReasonError     FinishReason = 3
	FinishReasonUnknown   FinishReason = 4
)

// ChatResponse is the structured response from GenerateResponse.
// Carries the text alongside token counts, latency, and finish reason.
type ChatResponse struct {
	Text         string
	TokensIn     int
	TokensOut    int
	Latency      time.Duration
	FinishReason FinishReason
}

// BundleFile is one file inside a model bundle.
type BundleFile struct {
	Name      string `json:"name"`
	Sha256    string `json:"sha256"`
	SizeBytes int64  `json:"size_bytes"`
}

// InstalledManifest is the on-disk record of what was installed for a
// given model. Written by the downloader after every successful bundle
// install; read by ModelRegistryService.CheckForUpgrades to detect drift.
type InstalledManifest struct {
	ModelID        string       `json:"model_id"`
	Version        string       `json:"version"`
	Repo           string       `json:"repo,omitempty"`
	TotalBytes     int64        `json:"total_bytes"`
	Files          []BundleFile `json:"files"`
	InstalledAtUTC time.Time    `json:"installed_at_utc"`
}

// UpgradeReason classifies why CheckForUpgrades flagged a model.
type UpgradeReason int

const (
	UpgradeReasonVersionChanged UpgradeReason = 0
	UpgradeReasonShaChanged     UpgradeReason = 1
	UpgradeReasonBoth           UpgradeReason = 2
	UpgradeReasonUnknown        UpgradeReason = 3
)

// UpgradeInfo is one detected upgrade for a locally-installed model.
type UpgradeInfo struct {
	ModelID                string
	InstalledVersion       string // empty when no manifest existed
	AvailableVersion       string
	Reason                 UpgradeReason
	EstimatedDownloadBytes int64
	DetectedAt             time.Time
}
