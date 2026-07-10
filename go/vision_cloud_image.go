// vision_cloud_image.go
//
// Ports the CircleAI.Vision.Cloud contract surface:
//   Contracts.cs                 -> ImageGenerationRequest, ImageArtifact,
//                                   IImageGenerator, NullImageGenerator
//   ImageGeneratorFallbackChain.cs -> ImageGeneratorFallbackChain
//   Options.cs                   -> OpenAiImageOptions, StabilityImageOptions
//
// The two real generators (OpenAI DALL-E, Stability AI) are HTTP-backed; their wire
// path is ported in vision_cloud_generators.go against the package's injected
// HTTPDoer. This file holds the contract types, the fail-soft Null default, and the
// pure-logic fallback chain (a faithful transliteration of the C#).
//
// MAPPING: Task<IReadOnlyList<ImageArtifact>> -> (…, error) with a ctx first param;
// IReadOnlyList<ImageArtifact> -> []ImageArtifact; byte[]? Bytes -> []byte (nil when
// the artifact carries a Url instead); string? -> "" for the null case.

package circleai

import (
	"context"
	"strings"
	"time"
)

// ---------------------------------------------------------------------------
// Contracts.cs — records
// ---------------------------------------------------------------------------

// ImageGenerationRequest is one image-generation request. Ports the
// ImageGenerationRequest record with its C# default values.
//
//	Size  square size in pixels — typical 512 / 768 / 1024 / 1536 (default 1024).
//	Count number of images to produce (default 1).
type ImageGenerationRequest struct {
	Prompt         string
	NegativePrompt string // "" when the C# NegativePrompt is null
	Size           int
	Count          int
	Style          string // "" when the C# Style is null
}

// NewImageGenerationRequest builds a request applying the C# record defaults
// (Size 1024, Count 1) when those fields are left zero.
func NewImageGenerationRequest(prompt string) ImageGenerationRequest {
	return ImageGenerationRequest{Prompt: prompt, Size: 1024, Count: 1}
}

// ImageArtifact is one generated image. Either Url OR Bytes is set, never both.
// Ports the ImageArtifact record.
type ImageArtifact struct {
	GeneratorID    string
	Prompt         string
	MimeType       string
	Url            string // "" when the artifact carries Bytes
	Bytes          []byte // nil when the artifact carries a Url
	GeneratedAtUtc time.Time
}

// IImageGenerator generates images from a text prompt. Ports IImageGenerator.
type IImageGenerator interface {
	// GeneratorID is the backend self-identification — "openai-images" / "stability"
	// / "null" / "fallback-chain".
	GeneratorID() string
	// DisplayLabel is the display label for the UI selector.
	DisplayLabel() string
	// IsConfigured is true when the generator has the credentials it needs.
	IsConfigured() bool
	// StatusMessage is a status message for the UI.
	StatusMessage() string
	// GenerateAsync generates images. Fail-soft: empty list when not configured.
	GenerateAsync(ctx context.Context, request ImageGenerationRequest) ([]ImageArtifact, error)
}

// ---------------------------------------------------------------------------
// Contracts.cs — NullImageGenerator
// ---------------------------------------------------------------------------

// NullImageGenerator is the empty generator — always returns no images. Ports
// NullImageGenerator.
type NullImageGenerator struct{}

// NullImageGeneratorInstance mirrors NullImageGenerator.Instance.
var NullImageGeneratorInstance = NullImageGenerator{}

// GeneratorID returns "null".
func (NullImageGenerator) GeneratorID() string { return "null" }

// DisplayLabel returns "No image generator".
func (NullImageGenerator) DisplayLabel() string { return "No image generator" }

// IsConfigured returns false.
func (NullImageGenerator) IsConfigured() bool { return false }

// StatusMessage returns the fixed "no generator wired" message.
func (NullImageGenerator) StatusMessage() string {
	return "No image generator wired. Configure OpenAI:ApiKey or Stability:ApiKey to enable."
}

// GenerateAsync returns an empty slice.
func (NullImageGenerator) GenerateAsync(context.Context, ImageGenerationRequest) ([]ImageArtifact, error) {
	return []ImageArtifact{}, nil
}

// ---------------------------------------------------------------------------
// Options.cs
// ---------------------------------------------------------------------------

// OpenAiImageOptions mirrors the OpenAiImageOptions class (init-only properties).
type OpenAiImageOptions struct {
	// BaseAddress defaults to https://api.openai.com.
	BaseAddress string
	APIKey      string
	// Model id. Default "dall-e-3".
	Model string
}

// DefaultOpenAiImageOptions returns the C# defaults (BaseAddress api.openai.com,
// Model dall-e-3, no key).
func DefaultOpenAiImageOptions() OpenAiImageOptions {
	return OpenAiImageOptions{BaseAddress: "https://api.openai.com", Model: "dall-e-3"}
}

// StabilityImageOptions mirrors the StabilityImageOptions class (init-only props).
type StabilityImageOptions struct {
	// BaseAddress defaults to https://api.stability.ai.
	BaseAddress string
	APIKey      string
	// Model id. Default "sd3.5-large".
	Model string
	// OutputFormat. Default "png".
	OutputFormat string
}

// DefaultStabilityImageOptions returns the C# defaults (BaseAddress api.stability.ai,
// Model sd3.5-large, OutputFormat png, no key).
func DefaultStabilityImageOptions() StabilityImageOptions {
	return StabilityImageOptions{
		BaseAddress:  "https://api.stability.ai",
		Model:        "sd3.5-large",
		OutputFormat: "png",
	}
}

// ---------------------------------------------------------------------------
// ImageGeneratorFallbackChain.cs
// ---------------------------------------------------------------------------

// ImageGeneratorFallbackChain is a composite IImageGenerator — it tries each child in
// order, skipping those reporting IsConfigured()=false, and returns the first
// non-empty artifact list (or empty if everyone failed). Ports
// ImageGeneratorFallbackChain.
type ImageGeneratorFallbackChain struct {
	chain []IImageGenerator
}

// NewImageGeneratorFallbackChain builds a chain from the given generators (nil-safe:
// a nil slice becomes an empty chain, matching the C# `?? new List<>`).
func NewImageGeneratorFallbackChain(chain []IImageGenerator) *ImageGeneratorFallbackChain {
	cp := make([]IImageGenerator, len(chain))
	copy(cp, chain)
	return &ImageGeneratorFallbackChain{chain: cp}
}

// GeneratorID returns "fallback-chain".
func (c *ImageGeneratorFallbackChain) GeneratorID() string { return "fallback-chain" }

// DisplayLabel returns "Fallback (N)" where N is the chain length.
func (c *ImageGeneratorFallbackChain) DisplayLabel() string {
	return "Fallback (" + itoa(len(c.chain)) + ")"
}

// IsConfigured is true when any child is configured.
func (c *ImageGeneratorFallbackChain) IsConfigured() bool {
	for _, g := range c.chain {
		if g.IsConfigured() {
			return true
		}
	}
	return false
}

// StatusMessage lists the configured children in order, or reports none configured.
func (c *ImageGeneratorFallbackChain) StatusMessage() string {
	if !c.IsConfigured() {
		return "No configured generator in chain."
	}
	ids := make([]string, 0, len(c.chain))
	for _, g := range c.chain {
		if g.IsConfigured() {
			ids = append(ids, g.GeneratorID())
		}
	}
	return "Ready · " + strings.Join(ids, " → ")
}

// GenerateAsync tries each configured child in order and returns the first non-empty
// result. Ports ImageGeneratorFallbackChain.GenerateAsync.
func (c *ImageGeneratorFallbackChain) GenerateAsync(ctx context.Context, request ImageGenerationRequest) ([]ImageArtifact, error) {
	for _, g := range c.chain {
		if !g.IsConfigured() {
			continue
		}
		result, err := g.GenerateAsync(ctx, request)
		if err != nil {
			return nil, err
		}
		if len(result) > 0 {
			return result, nil
		}
	}
	return []ImageArtifact{}, nil
}

// Interface guards.
var (
	_ IImageGenerator = NullImageGenerator{}
	_ IImageGenerator = (*ImageGeneratorFallbackChain)(nil)
)
