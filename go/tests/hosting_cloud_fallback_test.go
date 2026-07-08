// hosting_cloud_fallback_test.go
//
// Verifies CircleAI.Hosting.CloudFallback ports:
//   CloudFallbackChain (skip-unconfigured, skip-fail-soft-frame, first-ready-wins)
//   BackupBrainOrchestrator (failover, degrade-after-N, cool-down half-open)
//   OpenAiCompatibleChatGenerator over an injected HTTP doer (SSE parse)
//   BrainHealth ordinals

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestCloudFallbackChain_SkipsUnconfigured(t *testing.T) {
	ctx := context.Background()
	notReady := circleai.NewFakeConfigurableGenerator("cloud", false) // unconfigured
	ready := circleai.NewFakeConfigurableGenerator("local", true).WithReply("from-local")
	chain := circleai.NewCloudFallbackChain([]circleai.IChatGenerator{notReady, ready})

	out, err := chain.Generate(ctx, []circleai.ChatMessage{{Role: "user", Content: "hi"}}, nil)
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if out != "from-local" {
		t.Errorf("out = %q, want from-local", out)
	}
}

func TestCloudFallbackChain_NoneConfigured(t *testing.T) {
	ctx := context.Background()
	chain := circleai.NewCloudFallbackChain([]circleai.IChatGenerator{
		circleai.NewFakeConfigurableGenerator("a", false),
		circleai.NewFakeConfigurableGenerator("b", false),
	})
	out, _ := chain.Generate(ctx, nil, nil)
	if out != "[CloudFallbackChain: no configured generator could serve the request]" {
		t.Errorf("out = %q", out)
	}
}

func TestCloudFallbackChain_Stream_SkipsFailSoftFrame(t *testing.T) {
	ctx := context.Background()
	// First generator "configured" but streams a fail-soft frame; chain skips it.
	failSoft := circleai.NewFakeConfigurableGenerator("cloud", true).WithChunks("[cloud not configured]")
	real := circleai.NewFakeConfigurableGenerator("local", true).WithChunks("real", "-frame")
	chain := circleai.NewCloudFallbackChain([]circleai.IChatGenerator{failSoft, real})

	chunks, errc := chain.Stream(ctx, nil, nil)
	var got []string
	for c := range chunks {
		got = append(got, c)
	}
	if err := <-errc; err != nil {
		t.Fatalf("stream err: %v", err)
	}
	if len(got) != 2 || got[0] != "real" || got[1] != "-frame" {
		t.Errorf("stream = %v, want [real -frame]", got)
	}
}

func TestBackupBrainOrchestrator_FailoverThenSecond(t *testing.T) {
	ctx := context.Background()
	failing := circleai.NewFakeConfigurableGenerator("primary", true).WithFailure()
	backup := circleai.NewFakeConfigurableGenerator("backup", true).WithReply("backup-answer")
	orch, err := circleai.NewBackupBrainOrchestrator(
		[]circleai.IChatGenerator{failing, backup}, nil, nil)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	out, err := orch.Generate(ctx, []circleai.ChatMessage{{Role: "user", Content: "q"}}, nil)
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if out != "backup-answer" {
		t.Errorf("out = %q, want backup-answer", out)
	}
}

func TestBackupBrainOrchestrator_DegradeAndCoolDown(t *testing.T) {
	ctx := context.Background()
	now := time.Date(2026, 7, 8, 12, 0, 0, 0, time.UTC)
	clock := func() time.Time { return now }

	failing := circleai.NewFakeConfigurableGenerator("primary", true).WithFailure()
	backup := circleai.NewFakeConfigurableGenerator("backup", true).WithReply("ok")
	policy := circleai.BackupBrainPolicy{DegradedAfterFailures: 2, CoolDownDuration: 30 * time.Second, MaxRetriesPerTurn: 3}
	orch, _ := circleai.NewBackupBrainOrchestrator(
		[]circleai.IChatGenerator{failing, backup}, &policy, clock)

	// Two turns: the primary fails each turn (tried once per turn), so after two
	// turns its consecutive-failure count reaches the degrade threshold.
	_, _ = orch.Generate(ctx, nil, nil)
	_, _ = orch.Generate(ctx, nil, nil)

	statuses := orch.Statuses()
	if len(statuses) != 2 {
		t.Fatalf("expected 2 statuses, got %d", len(statuses))
	}
	if statuses[0].Health != circleai.BrainDegraded {
		t.Errorf("primary health = %d, want Degraded", statuses[0].Health)
	}
	if statuses[0].ConsecutiveFailures < 2 {
		t.Errorf("primary consecutive failures = %d, want >=2", statuses[0].ConsecutiveFailures)
	}

	// Advance past the cool-down: primary becomes CoolingDown (half-open).
	now = now.Add(31 * time.Second)
	statuses = orch.Statuses()
	if statuses[0].Health != circleai.BrainCoolingDown {
		t.Errorf("after cooldown health = %d, want CoolingDown", statuses[0].Health)
	}
}

func TestBackupBrainOrchestrator_RequiresBrain(t *testing.T) {
	if _, err := circleai.NewBackupBrainOrchestrator(nil, nil, nil); err == nil {
		t.Error("expected error with no brains")
	}
}

func TestBrainHealth_Ordinals(t *testing.T) {
	if int(circleai.BrainHealthy) != 0 || int(circleai.BrainDegraded) != 1 || int(circleai.BrainCoolingDown) != 2 {
		t.Errorf("BrainHealth ordinals wrong: %d %d %d",
			circleai.BrainHealthy, circleai.BrainDegraded, circleai.BrainCoolingDown)
	}
}

// recordingDoer returns a canned response and records the request it saw.
type recordingDoer struct {
	resp   *circleai.InboundHTTPResponse
	sawURL string
	sawKey string
}

func (d *recordingDoer) Do(req *circleai.OutboundHTTPRequest) (*circleai.InboundHTTPResponse, error) {
	d.sawURL = req.URL
	d.sawKey = req.Headers["Authorization"]
	if d.resp != nil {
		return d.resp, nil
	}
	return &circleai.InboundHTTPResponse{StatusCode: 200, Body: []byte("")}, nil
}

func newHTTPResponse(status int, body string) *circleai.InboundHTTPResponse {
	return &circleai.InboundHTTPResponse{StatusCode: status, Body: []byte(body)}
}

func TestOpenAiCompatibleChatGenerator_Configured(t *testing.T) {
	ctx := context.Background()
	// Two content deltas then [DONE].
	sse := "data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n" +
		"data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
		"data: [DONE]\n\n"
	doer := &recordingDoer{resp: newHTTPResponse(200, sse)}

	gen := circleai.NewOpenAiCompatibleChatGenerator(circleai.OpenAiChatConfig{
		ProviderID:         "openai",
		BaseURL:            "https://api.openai.com",
		APIKey:             "sk-test",
		Model:              "gpt-4o-mini",
		DefaultTemperature: 0.7,
		DefaultMaxTokens:   256,
	}, doer)

	if !gen.IsConfigured() {
		t.Fatal("should be configured with an API key")
	}
	out, err := gen.Generate(ctx, []circleai.ChatMessage{{Role: "user", Content: "hi"}}, nil)
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if out != "Hello" {
		t.Errorf("out = %q, want Hello", out)
	}
	if doer.sawKey != "Bearer sk-test" {
		t.Errorf("auth header = %q", doer.sawKey)
	}
}

func TestOpenAiCompatibleChatGenerator_Unconfigured(t *testing.T) {
	ctx := context.Background()
	gen := circleai.NewOpenAiCompatibleChatGenerator(circleai.OpenAiChatConfig{
		ProviderID: "openai", Model: "gpt-4o-mini",
	}, &recordingDoer{})
	if gen.IsConfigured() {
		t.Fatal("no key → not configured")
	}
	out, _ := gen.Generate(ctx, nil, nil)
	if out == "" || out[0] != '[' {
		t.Errorf("expected fail-soft frame, got %q", out)
	}
}
