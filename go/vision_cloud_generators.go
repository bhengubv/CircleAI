// vision_cloud_generators.go
//
// Ports the two real CircleAI.Vision.Cloud generators:
//   OpenAiImageGenerator.cs   -> OpenAiImageGenerator
//   StabilityImageGenerator.cs -> StabilityImageGenerator
//
// The C# generators wire System.Net.Http.HttpClient. Per the porting rules the HTTP
// transport is injected behind the package's HTTPDoer seam (the same one the cloud
// chat generator uses), so the wire contract — endpoint, auth header, request body,
// response parse, the Math.Clamp(Count, 1, 4) safety, the url-vs-bytes split — is
// ported EXACTLY and exercised deterministically with a fake doer (no live endpoint).
// Both are fail-soft: unconfigured (blank key) or a non-2xx response yields an empty
// artifact list so a fallback chain can move on.

package circleai

import (
	"context"
	"encoding/json"
	"strconv"
	"strings"
	"time"
)

// clampImageCount ports the C# Math.Clamp(request.Count, 1, 4) used by both
// generators.
func clampImageCount(count int) int {
	if count < 1 {
		return 1
	}
	if count > 4 {
		return 4
	}
	return count
}

// ---------------------------------------------------------------------------
// OpenAiImageGenerator (OpenAiImageGenerator.cs)
// ---------------------------------------------------------------------------

// OpenAiImageGenerator is an IImageGenerator backed by OpenAI's
// /v1/images/generations endpoint (response_format=url). Ports OpenAiImageGenerator.
type OpenAiImageGenerator struct {
	options OpenAiImageOptions
	doer    HTTPDoer
	now     func() time.Time
}

// NewOpenAiImageGenerator builds the generator against an injected doer. options
// carries the base address, key and model; now (optional) fixes GeneratedAtUtc for
// deterministic tests — pass nil for time.Now.
func NewOpenAiImageGenerator(options OpenAiImageOptions, doer HTTPDoer, now func() time.Time) *OpenAiImageGenerator {
	if now == nil {
		now = time.Now
	}
	return &OpenAiImageGenerator{options: options, doer: doer, now: now}
}

// GeneratorID returns "openai-images".
func (g *OpenAiImageGenerator) GeneratorID() string { return "openai-images" }

// DisplayLabel returns "OpenAI · <model>".
func (g *OpenAiImageGenerator) DisplayLabel() string { return "OpenAI · " + g.options.Model }

// IsConfigured is true when the API key is present (non-blank).
func (g *OpenAiImageGenerator) IsConfigured() bool { return !isBlank(g.options.APIKey) }

// StatusMessage reports readiness or the missing-key message.
func (g *OpenAiImageGenerator) StatusMessage() string {
	if g.IsConfigured() {
		return "Ready · " + g.options.Model
	}
	return "OpenAI API key not configured — set OpenAI:ApiKey to enable."
}

// openAiImagesResponse is the subset of the OpenAI images response the C# reads.
type openAiImagesResponse struct {
	Data []struct {
		Url string `json:"url"`
	} `json:"data"`
}

// GenerateAsync ports OpenAiImageGenerator.GenerateAsync. Unconfigured -> empty. On a
// non-2xx status -> empty (fail-soft). On success it maps each data[].url to an
// artifact with MimeType image/png.
func (g *OpenAiImageGenerator) GenerateAsync(ctx context.Context, request ImageGenerationRequest) ([]ImageArtifact, error) {
	if !g.IsConfigured() {
		return []ImageArtifact{}, nil
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}

	sizeStr := strconv.Itoa(request.Size) + "x" + strconv.Itoa(request.Size)
	// DefaultIgnoreCondition = WhenWritingNull in the C#; the request shape here has
	// no nullable members, so a plain marshal matches the emitted JSON.
	body, err := json.Marshal(map[string]any{
		"model":           g.options.Model,
		"prompt":          request.Prompt,
		"n":               clampImageCount(request.Count),
		"size":            sizeStr,
		"response_format": "url",
	})
	if err != nil {
		return nil, err
	}

	req := &OutboundHTTPRequest{
		URL:  joinBaseAndPath(g.options.BaseAddress, "/v1/images/generations"),
		Body: body,
		Headers: map[string]string{
			"Authorization": "Bearer " + g.options.APIKey,
			"Content-Type":  "application/json",
		},
	}
	resp, err := g.doer.Do(req)
	if err != nil {
		return nil, err
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		// Fail-soft: log-and-empty in the C#.
		return []ImageArtifact{}, nil
	}

	var parsed openAiImagesResponse
	if err := json.Unmarshal(resp.Body, &parsed); err != nil {
		return nil, err
	}
	artifacts := make([]ImageArtifact, 0, len(parsed.Data))
	for _, item := range parsed.Data {
		if item.Url == "" {
			continue
		}
		artifacts = append(artifacts, ImageArtifact{
			GeneratorID:    g.GeneratorID(),
			Prompt:         request.Prompt,
			MimeType:       "image/png",
			Url:            item.Url,
			Bytes:          nil,
			GeneratedAtUtc: g.now(),
		})
	}
	return artifacts, nil
}

// ---------------------------------------------------------------------------
// StabilityImageGenerator (StabilityImageGenerator.cs)
// ---------------------------------------------------------------------------

// StabilityImageGenerator is an IImageGenerator backed by Stability AI's
// /v2beta/stable-image/generate/sd3 endpoint. Stability returns one image per call,
// so it loops on the caller's behalf to honour Count. Returns images inline as bytes.
// Ports StabilityImageGenerator.
type StabilityImageGenerator struct {
	options StabilityImageOptions
	doer    HTTPDoer
	now     func() time.Time
}

// NewStabilityImageGenerator builds the generator against an injected doer. now
// (optional) fixes GeneratedAtUtc for deterministic tests — pass nil for time.Now.
func NewStabilityImageGenerator(options StabilityImageOptions, doer HTTPDoer, now func() time.Time) *StabilityImageGenerator {
	if now == nil {
		now = time.Now
	}
	return &StabilityImageGenerator{options: options, doer: doer, now: now}
}

// GeneratorID returns "stability".
func (g *StabilityImageGenerator) GeneratorID() string { return "stability" }

// DisplayLabel returns "Stability AI · <model>".
func (g *StabilityImageGenerator) DisplayLabel() string { return "Stability AI · " + g.options.Model }

// IsConfigured is true when the API key is present (non-blank).
func (g *StabilityImageGenerator) IsConfigured() bool { return !isBlank(g.options.APIKey) }

// StatusMessage reports readiness or the missing-key message.
func (g *StabilityImageGenerator) StatusMessage() string {
	if g.IsConfigured() {
		return "Ready · " + g.options.Model
	}
	return "Stability AI API key not configured — set Stability:ApiKey to enable."
}

// GenerateAsync ports StabilityImageGenerator.GenerateAsync. Unconfigured -> empty.
// Loops clamp(Count,1,4) times; each successful call appends one bytes artifact; a
// non-2xx response for a given call is skipped (continue), matching the C#. The
// request carries the prompt + output_format + model (+ negative_prompt when set) as
// the multipart form fields Stability expects; the injected doer receives them as a
// deterministic serialised body plus the Accept header.
func (g *StabilityImageGenerator) GenerateAsync(ctx context.Context, request ImageGenerationRequest) ([]ImageArtifact, error) {
	if !g.IsConfigured() {
		return []ImageArtifact{}, nil
	}

	count := clampImageCount(request.Count)
	artifacts := make([]ImageArtifact, 0, count)
	mime := "image/" + g.options.OutputFormat
	for i := 0; i < count; i++ {
		if err := ctx.Err(); err != nil {
			return nil, err
		}

		// The multipart form fields, in the C#'s declaration order, serialised as a
		// stable body so the wire content is observable to the injected doer.
		form := [][2]string{
			{"prompt", request.Prompt},
			{"output_format", g.options.OutputFormat},
			{"model", g.options.Model},
		}
		if request.NegativePrompt != "" {
			form = append(form, [2]string{"negative_prompt", request.NegativePrompt})
		}

		req := &OutboundHTTPRequest{
			URL:  joinBaseAndPath(g.options.BaseAddress, "/v2beta/stable-image/generate/sd3"),
			Body: encodeStabilityForm(form),
			Headers: map[string]string{
				"Authorization": "Bearer " + g.options.APIKey,
				"Accept":        mime,
			},
		}
		resp, err := g.doer.Do(req)
		if err != nil {
			return nil, err
		}
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			// Fail-soft per call: skip this image, keep looping.
			continue
		}
		bytes := append([]byte(nil), resp.Body...)
		artifacts = append(artifacts, ImageArtifact{
			GeneratorID:    g.GeneratorID(),
			Prompt:         request.Prompt,
			MimeType:       mime,
			Url:            "",
			Bytes:          bytes,
			GeneratedAtUtc: g.now(),
		})
	}
	return artifacts, nil
}

// encodeStabilityForm serialises multipart form fields as deterministic
// "key=value\n" lines. This is not on any external wire (Stability's real transport
// is the injected doer's concern) — it exists so the request body is a stable,
// inspectable value in tests, preserving field order and content from the C#.
func encodeStabilityForm(fields [][2]string) []byte {
	var sb strings.Builder
	for _, f := range fields {
		sb.WriteString(f[0])
		sb.WriteByte('=')
		sb.WriteString(f[1])
		sb.WriteByte('\n')
	}
	return []byte(sb.String())
}

// joinBaseAndPath joins a base address and an absolute path (leading '/') the way
// HttpClient composes BaseAddress + a relative request URI: trim a trailing '/' on
// the base, keep the path's leading '/'.
func joinBaseAndPath(base, path string) string {
	base = strings.TrimRight(base, "/")
	if !strings.HasPrefix(path, "/") {
		path = "/" + path
	}
	return base + path
}

// Interface guards.
var (
	_ IImageGenerator = (*OpenAiImageGenerator)(nil)
	_ IImageGenerator = (*StabilityImageGenerator)(nil)
)
