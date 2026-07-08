// hosting_ai_service_test.go
//
// Verifies CircleAI.Hosting core service ports:
//   AIService: Start/warmup, Ask/Chat system-prompt injection, agentic tool
//     loop (<tool_call> parse + bridge dispatch + re-prompt), feedback, observer.
//   FallbackAIService: local-vs-cloud selection by RAM threshold.
//   PushAIObserver / AetherAIObserver.
//   InProcessEndpoint + HttpLoopbackEndpoint + AIHttpClient round-trip.

package circleai_test

import (
	"context"
	"strings"
	"sync"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// scriptGenerator is a deterministic IChatGenerator whose reply is a function of
// the rendered messages. It records the prepared message lists it saw.
type scriptGenerator struct {
	mu       sync.Mutex
	fn       func(messages []circleai.ChatMessage) string
	captured [][]circleai.ChatMessage
	closed   bool
}

func (g *scriptGenerator) Generate(_ context.Context, messages []circleai.ChatMessage, _ *circleai.GenerationOptions) (string, error) {
	g.mu.Lock()
	cp := make([]circleai.ChatMessage, len(messages))
	copy(cp, messages)
	g.captured = append(g.captured, cp)
	fn := g.fn
	g.mu.Unlock()
	return fn(messages), nil
}

func (g *scriptGenerator) Stream(ctx context.Context, messages []circleai.ChatMessage, opts *circleai.GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string, 1)
	errc := make(chan error, 1)
	text, _ := g.Generate(ctx, messages, opts)
	out <- text
	close(out)
	close(errc)
	return out, errc
}

func (g *scriptGenerator) Close() error { g.closed = true; return nil }

func newAIService(fn func([]circleai.ChatMessage) string, opts ...circleai.AIServiceOption) (*circleai.AIService, *scriptGenerator) {
	gen := &scriptGenerator{fn: fn}
	base := []circleai.AIServiceOption{circleai.WithSystemPrompt("SYS"), circleai.WithWarmOnStart(false)}
	svc := circleai.NewAIService(func() (circleai.IChatGenerator, error) { return gen, nil }, append(base, opts...)...)
	return svc, gen
}

func TestAIService_AskInjectsSystemPrompt(t *testing.T) {
	ctx := context.Background()
	svc, gen := newAIService(func(m []circleai.ChatMessage) string { return "ok" })
	if err := svc.Start(ctx); err != nil {
		t.Fatalf("start: %v", err)
	}
	if _, err := svc.Ask(ctx, "hello"); err != nil {
		t.Fatalf("ask: %v", err)
	}
	prepared := gen.captured[0]
	if len(prepared) != 2 || prepared[0].Role != "system" || prepared[0].Content != "SYS" {
		t.Fatalf("system prompt not injected: %+v", prepared)
	}
	if prepared[1].Role != "user" || prepared[1].Content != "hello" {
		t.Fatalf("user turn wrong: %+v", prepared[1])
	}
}

func TestAIService_ChatHonoursCallerSystemMessage(t *testing.T) {
	ctx := context.Background()
	svc, gen := newAIService(func([]circleai.ChatMessage) string { return "ok" })
	_ = svc.Start(ctx)
	msgs := []circleai.ChatMessage{
		{Role: "system", Content: "CALLER SYS"},
		{Role: "user", Content: "hi"},
	}
	if _, err := svc.Chat(ctx, msgs, nil); err != nil {
		t.Fatalf("chat: %v", err)
	}
	prepared := gen.captured[0]
	// Caller's system message is honoured as-is; no injected "SYS".
	if len(prepared) != 2 || prepared[0].Content != "CALLER SYS" {
		t.Fatalf("caller system message not honoured: %+v", prepared)
	}
}

func TestAIService_Enricher(t *testing.T) {
	ctx := context.Background()
	enricher := func(_ context.Context, base, userQuery string) string {
		return base + " [q=" + userQuery + "]"
	}
	svc, gen := newAIService(func([]circleai.ChatMessage) string { return "ok" },
		circleai.WithSystemPromptEnricher(enricher))
	_ = svc.Start(ctx)
	_, _ = svc.Ask(ctx, "weather")
	if gen.captured[0][0].Content != "SYS [q=weather]" {
		t.Errorf("enricher not applied: %q", gen.captured[0][0].Content)
	}
}

// echoToolBridge returns a fixed result for a named tool.
type echoToolBridge struct {
	tool   string
	result circleai.ToolResult
	calls  int
}

func (b *echoToolBridge) AvailableTools() []circleai.ToolDefinition { return nil }
func (b *echoToolBridge) GetAvailableTools(context.Context) ([]circleai.ToolDefinition, error) {
	return nil, nil
}
func (b *echoToolBridge) Invoke(_ context.Context, inv circleai.ToolInvocation) (circleai.ToolResult, error) {
	b.calls++
	return b.result, nil
}

func TestAIService_AgenticToolLoop(t *testing.T) {
	ctx := context.Background()
	bridge := &echoToolBridge{
		tool:   "get_weather",
		result: circleai.ToolResult{ToolName: "get_weather", Success: true, Result: "sunny"},
	}

	// First generation emits a tool call; after the tool result is in history,
	// the second generation emits a plain answer (loop terminates).
	callCount := 0
	fn := func(m []circleai.ChatMessage) string {
		callCount++
		hasToolResult := false
		for _, msg := range m {
			if msg.Role == "tool" {
				hasToolResult = true
			}
		}
		if hasToolResult {
			return "The weather is sunny."
		}
		return `<tool_call>{"name":"get_weather","arguments":{"city":"CPT"}}</tool_call>`
	}

	svc, _ := newAIService(fn, circleai.WithToolBridge(bridge), circleai.WithAgenticMaxIterations(4))
	_ = svc.Start(ctx)

	out, err := svc.AgenticChat(ctx, "weather in Cape Town?", nil)
	if err != nil {
		t.Fatalf("agentic: %v", err)
	}
	if out != "The weather is sunny." {
		t.Errorf("final answer = %q", out)
	}
	if bridge.calls != 1 {
		t.Errorf("tool invoked %d times, want 1", bridge.calls)
	}
	if callCount != 2 {
		t.Errorf("generator called %d times, want 2 (tool turn + answer)", callCount)
	}
}

func TestAIService_AgenticNoBridge(t *testing.T) {
	ctx := context.Background()
	// Model keeps emitting a tool call; with no bridge and maxIter=2 the loop
	// must terminate and return the last response.
	fn := func([]circleai.ChatMessage) string {
		return `<tool_call>{"name":"x","arguments":{}}</tool_call>`
	}
	svc, _ := newAIService(fn, circleai.WithAgenticMaxIterations(2))
	_ = svc.Start(ctx)
	out, err := svc.AgenticChat(ctx, "go", nil)
	if err != nil {
		t.Fatalf("agentic: %v", err)
	}
	if !strings.Contains(out, "tool_call") {
		t.Errorf("expected last raw response, got %q", out)
	}
}

func TestAIService_InvokeToolNoBridge(t *testing.T) {
	ctx := context.Background()
	svc, _ := newAIService(func([]circleai.ChatMessage) string { return "x" })
	_ = svc.Start(ctx)
	res, err := svc.InvokeTool(ctx, circleai.ToolInvocation{ToolName: "t"})
	if err != nil {
		t.Fatalf("invoke: %v", err)
	}
	if res.Success || res.Error != "No tool bridge configured." {
		t.Errorf("expected no-bridge failure, got %+v", res)
	}
}

func TestAIService_WarmupRunsOnStart(t *testing.T) {
	ctx := context.Background()
	gen := &scriptGenerator{fn: func([]circleai.ChatMessage) string { return "warm" }}
	svc := circleai.NewAIService(func() (circleai.IChatGenerator, error) { return gen, nil },
		circleai.WithWarmOnStart(true))
	if err := svc.Start(ctx); err != nil {
		t.Fatalf("start: %v", err)
	}
	if len(gen.captured) == 0 {
		t.Error("warm-up generation did not run")
	}
	if !svc.IsReady() {
		t.Error("service should be ready after start")
	}
}

func TestAIService_SubmitFeedback(t *testing.T) {
	ctx := context.Background()
	store := circleai.NewInMemoryFeedbackStoreDefault()
	svc, _ := newAIService(func([]circleai.ChatMessage) string { return "x" },
		circleai.WithFeedbackStore(store))
	_ = svc.Start(ctx)
	sig := circleai.NewFeedbackSignal("q", "a", circleai.FeedbackPositive)
	if err := svc.SubmitFeedback(ctx, sig); err != nil {
		t.Fatalf("feedback: %v", err)
	}
	n, _ := store.Count(ctx)
	if n != 1 {
		t.Errorf("stored %d signals, want 1", n)
	}
}

// recordingObserver captures observer callbacks.
type recordingObserver struct {
	circleai.HostAIObserverBase
	chats   int
	started int
}

func (o *recordingObserver) OnStarted(context.Context) error { o.started++; return nil }
func (o *recordingObserver) OnChatCompleted(context.Context, circleai.AIChatEvent) error {
	o.chats++
	return nil
}

func TestAIService_ObserverFires(t *testing.T) {
	ctx := context.Background()
	obs := &recordingObserver{}
	svc, _ := newAIService(func([]circleai.ChatMessage) string { return "ok" },
		circleai.WithHostObserver(obs))
	_ = svc.Start(ctx)
	_, _ = svc.Ask(ctx, "hi")
	if obs.started != 1 {
		t.Errorf("OnStarted fired %d times, want 1", obs.started)
	}
	if obs.chats != 1 {
		t.Errorf("OnChatCompleted fired %d times, want 1", obs.chats)
	}
}

// ── FallbackAIService ───────────────────────────────────────────────────────

func TestFallbackAIService_UsesLocalWhenRamSufficient(t *testing.T) {
	ctx := context.Background()
	local := &fakeButler{askReply: "local"}
	cloud := &fakeButler{askReply: "cloud"}
	fb := circleai.NewFallbackAIService(local, cloud, 1024, func() int64 { return 4096 })
	if err := fb.Start(ctx); err != nil {
		t.Fatalf("start: %v", err)
	}
	if fb.ActiveIsCloud() {
		t.Error("should use local when RAM is sufficient")
	}
	out, _ := fb.Ask(ctx, "x")
	if out != "local" {
		t.Errorf("answer = %q, want local", out)
	}
}

func TestFallbackAIService_FallsBackToCloudWhenLowRam(t *testing.T) {
	ctx := context.Background()
	local := &fakeButler{askReply: "local"}
	cloud := &fakeButler{askReply: "cloud"}
	fb := circleai.NewFallbackAIService(local, cloud, 8192, func() int64 { return 1024 })
	if err := fb.Start(ctx); err != nil {
		t.Fatalf("start: %v", err)
	}
	if !fb.ActiveIsCloud() {
		t.Error("should fall back to cloud when RAM below threshold")
	}
	out, _ := fb.Ask(ctx, "x")
	if out != "cloud" {
		t.Errorf("answer = %q, want cloud", out)
	}
}

func TestFallbackAIService_NotStarted(t *testing.T) {
	fb := circleai.NewFallbackAIService(&fakeButler{}, &fakeButler{}, 0, func() int64 { return 0 })
	if _, err := fb.Ask(context.Background(), "x"); err == nil {
		t.Error("expected error before Start")
	}
}

// ── Observers ───────────────────────────────────────────────────────────────

// capturingPushSender records Send calls.
type capturingPushSender struct {
	token, title, body string
	calls              int
}

func (s *capturingPushSender) Send(_ context.Context, token, title, body string) error {
	s.token, s.title, s.body = token, title, body
	s.calls++
	return nil
}

func TestPushAIObserver(t *testing.T) {
	sender := &capturingPushSender{}
	obs, err := circleai.NewPushAIObserver(sender, "device-123")
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	_ = obs.OnChatCompleted(context.Background(), circleai.AIChatEvent{Response: "hello world"})
	if sender.calls != 1 || sender.title != "B!" || sender.body != "hello world" || sender.token != "device-123" {
		t.Errorf("push not delivered correctly: %+v", sender)
	}

	// Blank device token is rejected.
	if _, err := circleai.NewPushAIObserver(sender, "  "); err == nil {
		t.Error("blank device token should error")
	}
}

func TestPushAIObserver_TruncatesLongBody(t *testing.T) {
	sender := &capturingPushSender{}
	obs, _ := circleai.NewPushAIObserver(sender, "d")
	long := strings.Repeat("x", 250)
	_ = obs.OnChatCompleted(context.Background(), circleai.AIChatEvent{Response: long})
	if !strings.HasSuffix(sender.body, "…") || len([]rune(sender.body)) != 101 {
		t.Errorf("body not truncated to 100+ellipsis: len=%d", len([]rune(sender.body)))
	}
}

// capturingTransport records Publish calls.
type capturingTransport struct {
	topic   string
	payload []byte
}

func (tr *capturingTransport) Publish(_ context.Context, topic string, payload []byte) error {
	tr.topic = topic
	tr.payload = payload
	return nil
}

func TestAetherAIObserver(t *testing.T) {
	tr := &capturingTransport{}
	obs, err := circleai.NewAetherAIObserver(tr)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	_ = obs.OnChatCompleted(context.Background(), circleai.AIChatEvent{Response: "hi"})
	if tr.topic != "butler/response" {
		t.Errorf("topic = %q", tr.topic)
	}
	if !strings.Contains(string(tr.payload), `"response":"hi"`) {
		t.Errorf("payload = %s", tr.payload)
	}
	if _, err := circleai.NewAetherAIObserver(nil); err == nil {
		t.Error("nil transport should error")
	}
}
