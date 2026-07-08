// chat_generator_local_test.go
//
// Verifies LocalChatGenerator (deterministic port of QwenTextGenerator.cs /
// KimiVlGenerator.cs):
//   - BuildQwenChatPrompt byte layout matches the C# ChatML builder.
//   - Generate / Stream / StreamFragments produce content, split reasoning.
//   - GenerateResponse reports token counts + FinishReasonStop + reasoning.
//   - PowerBudget caps the output tokens (Low → ≤64 words).
//   - Stop sequences truncate output.
//   - Prefix cache is populated on opt-in and reloaded on the next call.
//   - Session save/load round-trips the marker.
//   - ctor guards + Close semantics.

package circleai_test

import (
	"context"
	"strings"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

func TestBuildQwenChatPrompt_Layout(t *testing.T) {
	msgs := []circleai.ChatMessage{
		{Role: "System", Content: "sys"},
		{Role: "user", Content: "hi"},
	}
	got := circleai.BuildQwenChatPrompt(msgs)
	want := "<|im_start|>system\nsys\n<|im_end|>\n<|im_start|>user\nhi\n<|im_end|>\n<|im_start|>assistant\n"
	if got != want {
		t.Errorf("prompt mismatch:\n got %q\nwant %q", got, want)
	}
}

func TestLocalChatGenerator_GenerateAndReasoningSplit(t *testing.T) {
	g, err := circleai.NewLocalChatGenerator("model.gguf", 4096)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	defer g.Close()
	ctx := context.Background()
	msgs := []circleai.ChatMessage{{Role: "user", Content: "hello world"}}

	text, err := g.Generate(ctx, msgs, nil)
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	if !strings.Contains(text, "You said: hello world") {
		t.Errorf("content should echo user text, got %q", text)
	}
	if strings.Contains(text, "<think>") || strings.Contains(text, "Considering") {
		t.Errorf("reasoning must be filtered out of content, got %q", text)
	}

	resp, err := g.GenerateResponse(ctx, msgs, nil)
	if err != nil {
		t.Fatalf("generateResponse: %v", err)
	}
	if resp.FinishReason != circleai.FinishReasonStop {
		t.Errorf("finish reason: got %v", resp.FinishReason)
	}
	if resp.ReasoningContent == "" {
		t.Error("reasoning content should be populated (default responder emits <think>)")
	}
	if strings.Contains(resp.Text, "<think>") {
		t.Errorf("response text must not contain <think>, got %q", resp.Text)
	}
	if resp.TokensIn <= 0 || resp.TokensOut <= 0 {
		t.Errorf("token counts should be positive: in=%d out=%d", resp.TokensIn, resp.TokensOut)
	}
}

func TestLocalChatGenerator_StreamConcatenates(t *testing.T) {
	g, _ := circleai.NewLocalChatGenerator("m.gguf", 512,
		circleai.WithResponder(func(string, []circleai.ChatMessage, bool) string { return "alpha beta gamma" }))
	defer g.Close()
	chunks, errs := g.Stream(context.Background(), []circleai.ChatMessage{{Role: "user", Content: "x"}}, nil)
	var sb strings.Builder
	for c := range chunks {
		sb.WriteString(c)
	}
	if err := <-errs; err != nil {
		t.Fatalf("stream err: %v", err)
	}
	if strings.TrimSpace(sb.String()) != "alpha beta gamma" {
		t.Errorf("stream concat mismatch: got %q", sb.String())
	}
}

func TestLocalChatGenerator_PowerBudgetLowCapsTokens(t *testing.T) {
	// Responder emits 200 words; PowerBudgetLow caps at 64.
	long := strings.TrimSpace(strings.Repeat("word ", 200))
	g, _ := circleai.NewLocalChatGenerator("m.gguf", 4096,
		circleai.WithResponder(func(string, []circleai.ChatMessage, bool) string { return long }))
	defer g.Close()

	opts := circleai.DefaultGenerationOptions()
	opts.Budget = circleai.PowerBudgetLow
	text, err := g.Generate(context.Background(), []circleai.ChatMessage{{Role: "user", Content: "x"}}, &opts)
	if err != nil {
		t.Fatalf("generate: %v", err)
	}
	words := strings.Fields(text)
	if len(words) > 64 {
		t.Errorf("PowerBudgetLow should cap at 64 words, got %d", len(words))
	}
	if len(words) == 0 {
		t.Error("expected some output")
	}
}

func TestLocalChatGenerator_StopSequenceTruncates(t *testing.T) {
	g, _ := circleai.NewLocalChatGenerator("m.gguf", 512,
		circleai.WithResponder(func(string, []circleai.ChatMessage, bool) string { return "keep this<|im_end|>drop this" }))
	defer g.Close()
	text, _ := g.Generate(context.Background(), []circleai.ChatMessage{{Role: "user", Content: "x"}}, nil)
	if strings.Contains(text, "drop this") {
		t.Errorf("text after stop sequence should be dropped, got %q", text)
	}
	if !strings.Contains(text, "keep this") {
		t.Errorf("text before stop sequence should remain, got %q", text)
	}
}

func TestLocalChatGenerator_PrefixCachePopulatedAndReloaded(t *testing.T) {
	pc, err := circleai.NewPrefixCacheService(t.TempDir())
	if err != nil {
		t.Fatalf("cache ctor: %v", err)
	}
	g, _ := circleai.NewLocalChatGenerator("modelX.gguf", 512, circleai.WithPrefixCache(pc))
	defer g.Close()

	msgs := []circleai.ChatMessage{
		{Role: "system", Content: "You are helpful."},
		{Role: "user", Content: "hi"},
	}
	opts := circleai.DefaultGenerationOptions()
	opts.UsePrefixCache = true

	key := circleai.PrefixCacheKeyFor("modelX.gguf", "You are helpful.")
	if key == "" {
		t.Fatal("expected a non-empty prefix key")
	}
	if pc.HasEntry(key) {
		t.Fatal("cache should be empty before first call")
	}
	if _, err := g.Generate(context.Background(), msgs, &opts); err != nil {
		t.Fatalf("generate 1: %v", err)
	}
	if !pc.HasEntry(key) {
		t.Error("cache entry should be populated after first opt-in generation")
	}
	// Second call reloads (Touch) — still succeeds.
	if _, err := g.Generate(context.Background(), msgs, &opts); err != nil {
		t.Fatalf("generate 2: %v", err)
	}
}

func TestLocalChatGenerator_SessionRoundTrip(t *testing.T) {
	g, _ := circleai.NewLocalChatGenerator("m.gguf", 512)
	defer g.Close()
	path := t.TempDir() + "/session.bin"
	ok, err := g.SaveSession(path)
	if err != nil || !ok {
		t.Fatalf("save: ok=%v err=%v", ok, err)
	}
	loaded, err := g.LoadSession(path)
	if err != nil || !loaded {
		t.Fatalf("load: ok=%v err=%v", loaded, err)
	}
	// Missing file → false, no error.
	missing, err := g.LoadSession(t.TempDir() + "/nope.bin")
	if err != nil || missing {
		t.Errorf("missing session should be (false,nil), got (%v,%v)", missing, err)
	}
}

func TestLocalChatGenerator_Guards(t *testing.T) {
	if _, err := circleai.NewLocalChatGenerator("  ", 512); err == nil {
		t.Error("empty model path should error")
	}
	if _, err := circleai.NewLocalChatGenerator("m.gguf", 0); err == nil {
		t.Error("zero context size should error")
	}
	g, _ := circleai.NewLocalChatGenerator("m.gguf", 512)
	_ = g.Close()
	if _, err := g.Generate(context.Background(), []circleai.ChatMessage{{Role: "user", Content: "x"}}, nil); err == nil {
		t.Error("generate after close should error")
	}
}
