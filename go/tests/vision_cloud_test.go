// vision_cloud_test.go
//
// Verifies the CircleAI.Vision.Cloud Go port (vision_cloud_image.go,
// vision_cloud_generators.go):
//   - NullImageGenerator (never configured, empty result)
//   - Options defaults (dall-e-3 / sd3.5-large / png)
//   - OpenAiImageGenerator over an injected doer: request body (model/prompt/n/size/
//     response_format), Count clamp 1..4, data[].url parse, unconfigured + non-2xx
//     fail-soft
//   - StabilityImageGenerator over an injected doer: per-image loop honouring Count,
//     bytes artifact, negative-prompt inclusion, per-call fail-soft skip
//   - ImageGeneratorFallbackChain: skip-unconfigured, first-non-empty-wins,
//     IsConfigured / StatusMessage / DisplayLabel

package circleai_test

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// recordingImageDoer captures the last request and returns a scripted response (or a
// queue of them, consumed in order).
type recordingImageDoer struct {
	last      *circleai.OutboundHTTPRequest
	all       []*circleai.OutboundHTTPRequest
	responses []*circleai.InboundHTTPResponse
	err       error
	calls     int
}

func (d *recordingImageDoer) Do(req *circleai.OutboundHTTPRequest) (*circleai.InboundHTTPResponse, error) {
	d.last = req
	d.all = append(d.all, req)
	d.calls++
	if d.err != nil {
		return nil, d.err
	}
	if len(d.responses) == 0 {
		return &circleai.InboundHTTPResponse{StatusCode: 200, Body: []byte("{}")}, nil
	}
	if len(d.responses) == 1 {
		return d.responses[0], nil
	}
	r := d.responses[0]
	d.responses = d.responses[1:]
	return r, nil
}

// ── NullImageGenerator + Options ────────────────────────────────────────────

func TestNullImageGenerator(t *testing.T) {
	ctx := context.Background()
	g := circleai.NullImageGeneratorInstance
	if g.GeneratorID() != "null" || g.IsConfigured() {
		t.Error("null generator id/config")
	}
	if g.DisplayLabel() != "No image generator" {
		t.Errorf("label = %q", g.DisplayLabel())
	}
	if !strings.Contains(g.StatusMessage(), "No image generator wired") {
		t.Errorf("status = %q", g.StatusMessage())
	}
	out, err := g.GenerateAsync(ctx, circleai.NewImageGenerationRequest("cat"))
	if err != nil || len(out) != 0 {
		t.Errorf("generate = %v,%v", out, err)
	}
}

func TestImageOptionDefaults(t *testing.T) {
	o := circleai.DefaultOpenAiImageOptions()
	if o.BaseAddress != "https://api.openai.com" || o.Model != "dall-e-3" {
		t.Errorf("openai defaults = %+v", o)
	}
	s := circleai.DefaultStabilityImageOptions()
	if s.BaseAddress != "https://api.stability.ai" || s.Model != "sd3.5-large" || s.OutputFormat != "png" {
		t.Errorf("stability defaults = %+v", s)
	}
}

func TestImageGenerationRequest_Defaults(t *testing.T) {
	r := circleai.NewImageGenerationRequest("a fox")
	if r.Size != 1024 || r.Count != 1 {
		t.Errorf("request defaults = %+v", r)
	}
}

// ── OpenAiImageGenerator ────────────────────────────────────────────────────

func TestOpenAiImageGenerator_RequestAndParse(t *testing.T) {
	ctx := context.Background()
	fixed := time.Unix(1700000000, 0).UTC()
	doer := &recordingImageDoer{responses: []*circleai.InboundHTTPResponse{{
		StatusCode: 200,
		Body:       []byte(`{"data":[{"url":"https://img/1.png"},{"url":"https://img/2.png"}]}`),
	}}}
	opts := circleai.DefaultOpenAiImageOptions()
	opts.APIKey = "sk-test"
	g := circleai.NewOpenAiImageGenerator(opts, doer, func() time.Time { return fixed })

	if g.GeneratorID() != "openai-images" || !g.IsConfigured() {
		t.Fatal("id/config")
	}
	if g.DisplayLabel() != "OpenAI · dall-e-3" {
		t.Errorf("label = %q", g.DisplayLabel())
	}

	req := circleai.NewImageGenerationRequest("a fox in snow")
	req.Count = 10 // must clamp to 4
	req.Size = 512
	out, err := g.GenerateAsync(ctx, req)
	if err != nil {
		t.Fatal(err)
	}
	if len(out) != 2 {
		t.Fatalf("artifacts = %d want 2", len(out))
	}
	if out[0].Url != "https://img/1.png" || out[0].MimeType != "image/png" || out[0].GeneratorID != "openai-images" {
		t.Errorf("artifact[0] = %+v", out[0])
	}
	if !out[0].GeneratedAtUtc.Equal(fixed) {
		t.Errorf("timestamp = %v want %v", out[0].GeneratedAtUtc, fixed)
	}
	if out[0].Bytes != nil {
		t.Error("url artifact must not carry bytes")
	}

	// Inspect the request body.
	if doer.last.URL != "https://api.openai.com/v1/images/generations" {
		t.Errorf("url = %q", doer.last.URL)
	}
	if doer.last.Headers["Authorization"] != "Bearer sk-test" {
		t.Errorf("auth = %q", doer.last.Headers["Authorization"])
	}
	var body map[string]any
	if err := json.Unmarshal(doer.last.Body, &body); err != nil {
		t.Fatal(err)
	}
	if body["model"] != "dall-e-3" || body["prompt"] != "a fox in snow" {
		t.Errorf("body model/prompt = %v/%v", body["model"], body["prompt"])
	}
	if body["n"].(float64) != 4 {
		t.Errorf("n = %v want 4 (clamped)", body["n"])
	}
	if body["size"] != "512x512" {
		t.Errorf("size = %v want 512x512", body["size"])
	}
	if body["response_format"] != "url" {
		t.Errorf("response_format = %v", body["response_format"])
	}
}

func TestOpenAiImageGenerator_Unconfigured(t *testing.T) {
	ctx := context.Background()
	doer := &recordingImageDoer{}
	g := circleai.NewOpenAiImageGenerator(circleai.DefaultOpenAiImageOptions(), doer, nil) // no key
	if g.IsConfigured() {
		t.Fatal("must be unconfigured")
	}
	if !strings.Contains(g.StatusMessage(), "not configured") {
		t.Errorf("status = %q", g.StatusMessage())
	}
	out, err := g.GenerateAsync(ctx, circleai.NewImageGenerationRequest("x"))
	if err != nil || len(out) != 0 {
		t.Errorf("unconfigured = %v,%v", out, err)
	}
	if doer.calls != 0 {
		t.Error("unconfigured generator must not call the doer")
	}
}

func TestOpenAiImageGenerator_NonSuccessFailSoft(t *testing.T) {
	ctx := context.Background()
	doer := &recordingImageDoer{responses: []*circleai.InboundHTTPResponse{{StatusCode: 429, Body: []byte("rate limited")}}}
	opts := circleai.DefaultOpenAiImageOptions()
	opts.APIKey = "sk"
	g := circleai.NewOpenAiImageGenerator(opts, doer, nil)
	out, err := g.GenerateAsync(ctx, circleai.NewImageGenerationRequest("x"))
	if err != nil || len(out) != 0 {
		t.Errorf("429 = %v,%v want empty,nil", out, err)
	}
}

// ── StabilityImageGenerator ─────────────────────────────────────────────────

func TestStabilityImageGenerator_LoopsForCount(t *testing.T) {
	ctx := context.Background()
	fixed := time.Unix(1700000001, 0).UTC()
	// Three successful calls, each returns distinct bytes.
	doer := &recordingImageDoer{responses: []*circleai.InboundHTTPResponse{
		{StatusCode: 200, Body: []byte{0xAA}},
		{StatusCode: 200, Body: []byte{0xBB}},
		{StatusCode: 200, Body: []byte{0xCC}},
	}}
	opts := circleai.DefaultStabilityImageOptions()
	opts.APIKey = "st-key"
	g := circleai.NewStabilityImageGenerator(opts, doer, func() time.Time { return fixed })

	if g.GeneratorID() != "stability" || g.DisplayLabel() != "Stability AI · sd3.5-large" {
		t.Errorf("id/label")
	}

	req := circleai.NewImageGenerationRequest("cyberpunk city")
	req.Count = 3
	req.NegativePrompt = "blurry"
	out, err := g.GenerateAsync(ctx, req)
	if err != nil {
		t.Fatal(err)
	}
	if len(out) != 3 {
		t.Fatalf("artifacts = %d want 3", len(out))
	}
	if doer.calls != 3 {
		t.Errorf("doer calls = %d want 3", doer.calls)
	}
	if out[0].Bytes == nil || out[0].Bytes[0] != 0xAA || out[2].Bytes[0] != 0xCC {
		t.Errorf("bytes = %v / %v", out[0].Bytes, out[2].Bytes)
	}
	if out[0].MimeType != "image/png" || out[0].Url != "" {
		t.Errorf("artifact meta = %+v", out[0])
	}
	if !out[0].GeneratedAtUtc.Equal(fixed) {
		t.Errorf("timestamp = %v", out[0].GeneratedAtUtc)
	}
	// Verify negative_prompt made it into the serialised form body + endpoint/auth.
	last := doer.all[0]
	if last.URL != "https://api.stability.ai/v2beta/stable-image/generate/sd3" {
		t.Errorf("url = %q", last.URL)
	}
	if last.Headers["Accept"] != "image/png" || last.Headers["Authorization"] != "Bearer st-key" {
		t.Errorf("headers = %v", last.Headers)
	}
	bodyStr := string(last.Body)
	if !strings.Contains(bodyStr, "prompt=cyberpunk city") || !strings.Contains(bodyStr, "negative_prompt=blurry") ||
		!strings.Contains(bodyStr, "model=sd3.5-large") || !strings.Contains(bodyStr, "output_format=png") {
		t.Errorf("form body = %q", bodyStr)
	}
}

func TestStabilityImageGenerator_PerCallFailSoftSkip(t *testing.T) {
	ctx := context.Background()
	// Count 3 → 3 calls; middle one 500 (skipped), other two succeed.
	doer := &recordingImageDoer{responses: []*circleai.InboundHTTPResponse{
		{StatusCode: 200, Body: []byte{1}},
		{StatusCode: 500, Body: []byte("err")},
		{StatusCode: 200, Body: []byte{3}},
	}}
	opts := circleai.DefaultStabilityImageOptions()
	opts.APIKey = "k"
	g := circleai.NewStabilityImageGenerator(opts, doer, nil)
	req := circleai.NewImageGenerationRequest("p")
	req.Count = 3
	out, err := g.GenerateAsync(ctx, req)
	if err != nil {
		t.Fatal(err)
	}
	if len(out) != 2 {
		t.Errorf("artifacts = %d want 2 (middle failed)", len(out))
	}
	if doer.calls != 3 {
		t.Errorf("still makes all 3 calls, got %d", doer.calls)
	}
}

func TestStabilityImageGenerator_Unconfigured(t *testing.T) {
	ctx := context.Background()
	doer := &recordingImageDoer{}
	g := circleai.NewStabilityImageGenerator(circleai.DefaultStabilityImageOptions(), doer, nil)
	out, err := g.GenerateAsync(ctx, circleai.NewImageGenerationRequest("x"))
	if err != nil || len(out) != 0 || doer.calls != 0 {
		t.Errorf("unconfigured = %v,%v calls=%d", out, err, doer.calls)
	}
}

// ── ImageGeneratorFallbackChain ─────────────────────────────────────────────

// stubImageGen is a minimal configurable IImageGenerator for chain tests.
type stubImageGen struct {
	id         string
	configured bool
	result     []circleai.ImageArtifact
	called     bool
}

func (s *stubImageGen) GeneratorID() string   { return s.id }
func (s *stubImageGen) DisplayLabel() string  { return s.id }
func (s *stubImageGen) IsConfigured() bool    { return s.configured }
func (s *stubImageGen) StatusMessage() string { return s.id }
func (s *stubImageGen) GenerateAsync(_ context.Context, _ circleai.ImageGenerationRequest) ([]circleai.ImageArtifact, error) {
	s.called = true
	return s.result, nil
}

func TestImageFallbackChain_SkipUnconfiguredFirstNonEmptyWins(t *testing.T) {
	ctx := context.Background()
	unconf := &stubImageGen{id: "openai-images", configured: false, result: []circleai.ImageArtifact{{Url: "should-not-see"}}}
	emptyReady := &stubImageGen{id: "empty", configured: true, result: []circleai.ImageArtifact{}}
	winner := &stubImageGen{id: "stability", configured: true, result: []circleai.ImageArtifact{{Url: "win"}}}
	never := &stubImageGen{id: "never", configured: true, result: []circleai.ImageArtifact{{Url: "nope"}}}

	chain := circleai.NewImageGeneratorFallbackChain([]circleai.IImageGenerator{unconf, emptyReady, winner, never})

	if chain.GeneratorID() != "fallback-chain" {
		t.Errorf("id = %q", chain.GeneratorID())
	}
	if chain.DisplayLabel() != "Fallback (4)" {
		t.Errorf("label = %q", chain.DisplayLabel())
	}
	if !chain.IsConfigured() {
		t.Error("chain with configured children must be configured")
	}
	status := chain.StatusMessage()
	if !strings.Contains(status, "empty → stability → never") || !strings.HasPrefix(status, "Ready") {
		t.Errorf("status = %q", status)
	}

	out, err := chain.GenerateAsync(ctx, circleai.NewImageGenerationRequest("x"))
	if err != nil {
		t.Fatal(err)
	}
	if len(out) != 1 || out[0].Url != "win" {
		t.Fatalf("out = %+v want [win]", out)
	}
	if unconf.called {
		t.Error("unconfigured child must be skipped")
	}
	if !emptyReady.called || !winner.called {
		t.Error("empty-then-winner both should be tried")
	}
	if never.called {
		t.Error("generator after the winner must not be called")
	}
}

func TestImageFallbackChain_NoneConfigured(t *testing.T) {
	ctx := context.Background()
	chain := circleai.NewImageGeneratorFallbackChain([]circleai.IImageGenerator{
		&stubImageGen{id: "a", configured: false},
		&stubImageGen{id: "b", configured: false},
	})
	if chain.IsConfigured() {
		t.Error("must be unconfigured")
	}
	if chain.StatusMessage() != "No configured generator in chain." {
		t.Errorf("status = %q", chain.StatusMessage())
	}
	out, err := chain.GenerateAsync(ctx, circleai.NewImageGenerationRequest("x"))
	if err != nil || len(out) != 0 {
		t.Errorf("out = %v,%v", out, err)
	}
}

func TestImageFallbackChain_NilSafe(t *testing.T) {
	ctx := context.Background()
	chain := circleai.NewImageGeneratorFallbackChain(nil)
	if chain.DisplayLabel() != "Fallback (0)" || chain.IsConfigured() {
		t.Errorf("nil chain = %q configured=%v", chain.DisplayLabel(), chain.IsConfigured())
	}
	out, err := chain.GenerateAsync(ctx, circleai.NewImageGenerationRequest("x"))
	if err != nil || len(out) != 0 {
		t.Errorf("out = %v,%v", out, err)
	}
}
