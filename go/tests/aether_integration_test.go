// aether_integration_test.go
//
// Aether ↔ Circle AI integration test.
//
// Proves the plan's verification criterion:
//   "An Aether node (Go) can instantiate a Circle AI ICompanionSession using
//    native types, with no FFI overhead and no bridging."
//
// Simulates a minimal Aether node by:
//   1. Wiring a MockChatGenerator (in-process, no native libs)
//   2. Building a SimpleCompanionSession backed by it
//   3. Exercising Send / Stream / Agent / SignalFeedback / ProactiveEvents /
//      Close using native Circle AI Go types throughout.

package circleai_test

import (
	"context"
	"fmt"
	"strings"
	"sync"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// MockChatGenerator — in-process IChatGenerator, no native libs.
// ---------------------------------------------------------------------------

type MockChatGenerator struct {
	Response string
	mu       sync.Mutex
	closed   bool
	calls    [][]circleai.ChatMessage
}

func (m *MockChatGenerator) Generate(
	_ context.Context,
	messages []circleai.ChatMessage,
	_ *circleai.GenerationOptions,
) (string, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.closed {
		return "", fmt.Errorf("generator is closed")
	}
	cp := make([]circleai.ChatMessage, len(messages))
	copy(cp, messages)
	m.calls = append(m.calls, cp)
	return m.Response, nil
}

func (m *MockChatGenerator) Stream(
	ctx context.Context,
	messages []circleai.ChatMessage,
	_ *circleai.GenerationOptions,
) (<-chan string, <-chan error) {
	tokens := make(chan string)
	errs := make(chan error, 1)

	go func() {
		defer close(tokens)
		defer close(errs)

		m.mu.Lock()
		closed := m.closed
		resp := m.Response
		cp := make([]circleai.ChatMessage, len(messages))
		copy(cp, messages)
		m.calls = append(m.calls, cp)
		m.mu.Unlock()

		if closed {
			errs <- fmt.Errorf("generator is closed")
			return
		}
		// Emit word-by-word for a realistic stream simulation.
		words := strings.Fields(resp)
		for i, w := range words {
			chunk := w
			if i < len(words)-1 {
				chunk += " "
			}
			select {
			case <-ctx.Done():
				errs <- ctx.Err()
				return
			case tokens <- chunk:
			}
		}
	}()

	return tokens, errs
}

func (m *MockChatGenerator) Close() error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.closed = true
	return nil
}

func (m *MockChatGenerator) CallCount() int {
	m.mu.Lock()
	defer m.mu.Unlock()
	return len(m.calls)
}

func (m *MockChatGenerator) LastMessages() []circleai.ChatMessage {
	m.mu.Lock()
	defer m.mu.Unlock()
	if len(m.calls) == 0 {
		return nil
	}
	return m.calls[len(m.calls)-1]
}

// ---------------------------------------------------------------------------
// SimpleCompanionSession — minimal Aether-node ICompanionSession implementation
// ---------------------------------------------------------------------------

type SimpleCompanionSession struct {
	sessionID  string
	identityID string
	iface      circleai.InterfaceKind
	generator  circleai.IChatGenerator
	compCtx    circleai.CompanionContext
	mu         sync.Mutex
	history    []circleai.CompanionTurn
	proactive  chan circleai.CompanionProactiveEvent
	closed     bool
}

// Compile-time assertion: SimpleCompanionSession satisfies ICompanionSession.
var _ circleai.ICompanionSession = (*SimpleCompanionSession)(nil)

func NewSimpleCompanionSession(
	sessionID string,
	ctx circleai.CompanionContext,
	generator circleai.IChatGenerator,
) *SimpleCompanionSession {
	return &SimpleCompanionSession{
		sessionID:  sessionID,
		identityID: ctx.IdentityID,
		iface:      ctx.Interface,
		generator:  generator,
		compCtx:    ctx,
		proactive:  make(chan circleai.CompanionProactiveEvent, 8),
	}
}

func (s *SimpleCompanionSession) SessionID() string                     { return s.sessionID }
func (s *SimpleCompanionSession) IdentityID() string                    { return s.identityID }
func (s *SimpleCompanionSession) Interface() circleai.InterfaceKind     { return s.iface }
func (s *SimpleCompanionSession) GetContext() circleai.CompanionContext { return s.compCtx }

func (s *SimpleCompanionSession) buildSystemPrompt() string {
	var sb strings.Builder
	sb.WriteString("You are Circle AI, a personal companion.\n")
	sb.WriteString("Identity: " + s.compCtx.DisplayName + "\n")
	if s.compCtx.PersonaHints != "" {
		sb.WriteString(s.compCtx.PersonaHints + "\n")
	}
	if s.compCtx.AffectSummary != "" {
		sb.WriteString(s.compCtx.AffectSummary + "\n")
	}
	if len(s.compCtx.ActiveGoals) > 0 {
		sb.WriteString("Active goals: " + strings.Join(s.compCtx.ActiveGoals, ", ") + "\n")
	}
	return sb.String()
}

func (s *SimpleCompanionSession) buildMessages(userMessage string) []circleai.ChatMessage {
	msgs := []circleai.ChatMessage{
		{Role: "system", Content: s.buildSystemPrompt()},
	}
	s.mu.Lock()
	for _, t := range s.history {
		msgs = append(msgs, circleai.ChatMessage{Role: t.Role, Content: t.Content})
	}
	s.mu.Unlock()
	msgs = append(msgs, circleai.ChatMessage{Role: "user", Content: userMessage})
	return msgs
}

func (s *SimpleCompanionSession) Send(ctx context.Context, message string) (string, error) {
	msgs := s.buildMessages(message)
	reply, err := s.generator.Generate(ctx, msgs, nil)
	if err != nil {
		return "", err
	}
	now := time.Now().UTC()
	s.mu.Lock()
	s.history = append(s.history,
		circleai.CompanionTurn{Role: "user", Content: message, Timestamp: now},
		circleai.CompanionTurn{Role: "assistant", Content: reply, Timestamp: now},
	)
	s.mu.Unlock()
	return reply, nil
}

func (s *SimpleCompanionSession) Stream(ctx context.Context, message string) (<-chan string, <-chan error) {
	return s.generator.Stream(ctx, s.buildMessages(message), nil)
}

func (s *SimpleCompanionSession) Agent(ctx context.Context, instruction string) (string, error) {
	// Minimal agentic loop: single pass (no tool calls in mock).
	return s.Send(ctx, instruction)
}

func (s *SimpleCompanionSession) RefreshContext(_ context.Context) error {
	s.mu.Lock()
	s.compCtx.ContextBuiltAt = time.Now().UTC()
	s.mu.Unlock()
	return nil
}

func (s *SimpleCompanionSession) History() []circleai.CompanionTurn {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]circleai.CompanionTurn, len(s.history))
	copy(out, s.history)
	return out
}

func (s *SimpleCompanionSession) SignalFeedback(_ context.Context, _ bool, _ *string) error {
	// In production an Aether node would propagate this to AffectState via Aether sync.
	return nil
}

func (s *SimpleCompanionSession) ProactiveEvents() <-chan circleai.CompanionProactiveEvent {
	return s.proactive
}

func (s *SimpleCompanionSession) Close() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if !s.closed {
		s.closed = true
		close(s.proactive)
	}
	return nil
}

// EmitProactiveEvent injects a proactive event for test observation.
func (s *SimpleCompanionSession) EmitProactiveEvent(e circleai.CompanionProactiveEvent) {
	s.proactive <- e
}

// ---------------------------------------------------------------------------
// Helper: build a realistic CompanionContext from native Circle AI types
// ---------------------------------------------------------------------------

func buildTestContext() circleai.CompanionContext {
	lang := "en-ZA"

	affect := circleai.AffectState{
		UserID:         "aether-test-node",
		LastUpdatedUTC: time.Now().UTC(),
		Curiosity:      0.5,
		Engagement:     0.52, // after one positive signal
		Uncertainty:    0.18,
		Rapport:        0.01,
		Energy:         0.5,
	}

	return circleai.CompanionContext{
		IdentityID:        "550e8400-e29b-41d4-a716-446655440001",
		DisplayName:       "Thabo",
		PreferredLanguage: &lang,
		Interface:         circleai.InterfaceKindMobile,
		PersonaHints:      "[User preferences]\nVerbosity: brief\nFormality: casual",
		AffectSummary: fmt.Sprintf(
			"[Affect] engagement=%.2f rapport=%.2f uncertainty=%.2f",
			affect.Engagement, affect.Rapport, affect.Uncertainty,
		),
		RecentMemorySnippets: []string{
			"Mentioned interest in renewable energy.",
			"Prefers morning check-ins.",
		},
		ActiveGoals:    []string{"Learn Go", "Publish CircleAI SDK"},
		ContextBuiltAt: time.Now().UTC(),
	}
}

// findLang is a helper that searches KnownLanguagesAll — mirrors how an
// Aether node would locate a tag without a separate registry object.
func findLang(bcpTag string) *circleai.LanguageTag {
	for i := range circleai.KnownLanguagesAll {
		if circleai.KnownLanguagesAll[i].BcpTag == bcpTag {
			return &circleai.KnownLanguagesAll[i]
		}
	}
	return nil
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

// TestAetherNode_Send verifies the core request-reply path.
func TestAetherNode_Send(t *testing.T) {
	gen := &MockChatGenerator{Response: "Hello, Thabo! How can I help?"}
	session := NewSimpleCompanionSession("session-001", buildTestContext(), gen)
	defer session.Close()

	reply, err := session.Send(context.Background(), "Hi there!")
	if err != nil {
		t.Fatalf("Send failed: %v", err)
	}
	if reply != gen.Response {
		t.Errorf("want %q, got %q", gen.Response, reply)
	}
	if gen.CallCount() != 1 {
		t.Errorf("expected 1 generator call, got %d", gen.CallCount())
	}
}

// TestAetherNode_SystemPromptContainsContext verifies that identity, persona,
// affect, and goals are all injected into the system prompt.
func TestAetherNode_SystemPromptContainsContext(t *testing.T) {
	gen := &MockChatGenerator{Response: "Got it."}
	session := NewSimpleCompanionSession("session-002", buildTestContext(), gen)
	defer session.Close()

	_, err := session.Send(context.Background(), "What are my active goals?")
	if err != nil {
		t.Fatalf("Send failed: %v", err)
	}

	msgs := gen.LastMessages()
	if len(msgs) == 0 {
		t.Fatal("no messages captured")
	}
	sysMsg := msgs[0]
	if sysMsg.Role != "system" {
		t.Errorf("first message role: want system, got %s", sysMsg.Role)
	}

	for _, want := range []string{"Thabo", "brief", "casual", "Learn Go", "Publish CircleAI SDK"} {
		if !strings.Contains(sysMsg.Content, want) {
			t.Errorf("system prompt missing %q\nFull:\n%s", want, sysMsg.Content)
		}
	}
}

// TestAetherNode_ConversationHistory verifies multi-turn history accumulation.
func TestAetherNode_ConversationHistory(t *testing.T) {
	gen := &MockChatGenerator{Response: "Turn reply."}
	session := NewSimpleCompanionSession("session-003", buildTestContext(), gen)
	defer session.Close()

	for i := range 3 {
		if _, err := session.Send(context.Background(), fmt.Sprintf("Turn %d", i+1)); err != nil {
			t.Fatalf("turn %d: %v", i+1, err)
		}
	}

	history := session.History()
	// 3 turns × 2 entries (user + assistant) = 6.
	if len(history) != 6 {
		t.Errorf("expected 6 history entries, got %d", len(history))
	}
	if history[0].Role != "user" {
		t.Errorf("history[0].Role: want user, got %s", history[0].Role)
	}
	if history[1].Role != "assistant" {
		t.Errorf("history[1].Role: want assistant, got %s", history[1].Role)
	}
}

// TestAetherNode_Stream verifies word-by-word streaming reassembly.
func TestAetherNode_Stream(t *testing.T) {
	expected := "Streaming reply from Circle AI"
	gen := &MockChatGenerator{Response: expected}
	session := NewSimpleCompanionSession("session-004", buildTestContext(), gen)
	defer session.Close()

	tokens, errs := session.Stream(context.Background(), "Stream test")

	var sb strings.Builder
	for tok := range tokens {
		sb.WriteString(tok)
	}
	if err := <-errs; err != nil {
		t.Fatalf("stream error: %v", err)
	}
	if got := strings.TrimSpace(sb.String()); got != expected {
		t.Errorf("want %q, got %q", expected, got)
	}
}

// TestAetherNode_Agent verifies the agentic path (single-pass with mock).
func TestAetherNode_Agent(t *testing.T) {
	gen := &MockChatGenerator{Response: "Task completed."}
	session := NewSimpleCompanionSession("session-005", buildTestContext(), gen)
	defer session.Close()

	result, err := session.Agent(context.Background(), "Summarise my goals")
	if err != nil {
		t.Fatalf("Agent failed: %v", err)
	}
	if result != gen.Response {
		t.Errorf("want %q, got %q", gen.Response, result)
	}
}

// TestAetherNode_SignalFeedback verifies feedback signalling does not error.
func TestAetherNode_SignalFeedback(t *testing.T) {
	gen := &MockChatGenerator{Response: "Good."}
	session := NewSimpleCompanionSession("session-006", buildTestContext(), gen)
	defer session.Close()

	_, _ = session.Send(context.Background(), "Test message")
	note := "great response"
	if err := session.SignalFeedback(context.Background(), true, &note); err != nil {
		t.Fatalf("SignalFeedback(positive) failed: %v", err)
	}
	if err := session.SignalFeedback(context.Background(), false, nil); err != nil {
		t.Fatalf("SignalFeedback(negative) failed: %v", err)
	}
}

// TestAetherNode_ProactiveEvents verifies the proactive event channel lifecycle.
func TestAetherNode_ProactiveEvents(t *testing.T) {
	gen := &MockChatGenerator{Response: "OK"}
	ctx := buildTestContext()
	session := NewSimpleCompanionSession("session-007", ctx, gen)

	evt := circleai.CompanionProactiveEvent{
		SessionID:   session.SessionID(),
		IdentityID:  ctx.IdentityID,
		Interface:   circleai.InterfaceKindMobile,
		Message:     "You haven't checked in for a while.",
		TriggerName: "idle_too_long",
		GeneratedAt: time.Now().UTC(),
	}
	session.EmitProactiveEvent(evt)

	ch := session.ProactiveEvents()
	select {
	case got := <-ch:
		if got.TriggerName != evt.TriggerName {
			t.Errorf("trigger: want %q, got %q", evt.TriggerName, got.TriggerName)
		}
		if got.Message != evt.Message {
			t.Errorf("message: want %q, got %q", evt.Message, got.Message)
		}
	case <-time.After(200 * time.Millisecond):
		t.Fatal("timed out waiting for proactive event")
	}

	// Channel should close after session.Close().
	session.Close()
	_, open := <-ch
	if open {
		t.Error("proactive channel should be closed after session.Close()")
	}
}

// TestAetherNode_RefreshContext verifies context refresh updates the timestamp.
func TestAetherNode_RefreshContext(t *testing.T) {
	gen := &MockChatGenerator{Response: "OK"}
	ctx := buildTestContext()
	ctx.ContextBuiltAt = time.Now().UTC().Add(-10 * time.Minute) // stale
	session := NewSimpleCompanionSession("session-008", ctx, gen)
	defer session.Close()

	before := session.GetContext().ContextBuiltAt
	if err := session.RefreshContext(context.Background()); err != nil {
		t.Fatalf("RefreshContext failed: %v", err)
	}
	after := session.GetContext().ContextBuiltAt
	if !after.After(before) {
		t.Errorf("timestamp should advance: before=%v after=%v", before, after)
	}
}

// TestAetherNode_GeneratorClosed verifies graceful error when the generator
// is shut down before Send is called (node shutdown scenario).
func TestAetherNode_GeneratorClosed(t *testing.T) {
	gen := &MockChatGenerator{Response: "OK"}
	gen.Close() // pre-closed
	session := NewSimpleCompanionSession("session-009", buildTestContext(), gen)
	defer session.Close()

	_, err := session.Send(context.Background(), "Will this fail?")
	if err == nil {
		t.Error("expected error from closed generator, got nil")
	}
}

// TestAetherNode_AffectStateIntegration verifies that AffectState math
// produces the fixture-correct values and that the result can be embedded in
// a CompanionContext — proving the memory + companion modules compose at the
// Aether node level.
func TestAetherNode_AffectStateIntegration(t *testing.T) {
	const eps = float32(1e-5)

	state := circleai.AffectState{
		UserID:         "aether-node-001",
		LastUpdatedUTC: time.Now().UTC(),
		Curiosity:      0.5,
		Engagement:     0.5,
		Uncertainty:    0.2,
		Rapport:        0.0,
		Energy:         0.5,
	}
	state.ApplyPositiveSignal()

	checkF32 := func(name string, got, want float32) {
		t.Helper()
		if diff := got - want; diff > eps || diff < -eps {
			t.Errorf("%s: want %.5f, got %.5f", name, want, got)
		}
	}
	checkF32("engagement", state.Engagement, 0.52)
	checkF32("uncertainty", state.Uncertainty, 0.18)
	checkF32("rapport", state.Rapport, 0.01)
	checkF32("energy", state.Energy, 0.5)

	// Compose updated affect into a CompanionContext — the standard Aether pipeline.
	lang := "zu"
	compCtx := circleai.CompanionContext{
		IdentityID:        "aether-node-001",
		DisplayName:       "Circle Node",
		PreferredLanguage: &lang,
		Interface:         circleai.InterfaceKindIoT,
		AffectSummary: fmt.Sprintf(
			"engagement=%.2f rapport=%.2f uncertainty=%.2f",
			state.Engagement, state.Rapport, state.Uncertainty,
		),
		ContextBuiltAt: time.Now().UTC(),
	}

	if compCtx.IdentityID == "" {
		t.Error("context identity must not be empty")
	}
	if !strings.Contains(compCtx.AffectSummary, "0.52") {
		t.Errorf("affect summary should reflect updated engagement: %s", compCtx.AffectSummary)
	}
}

// TestAetherNode_LanguageRegistryIntegration verifies that LanguageTags from
// KnownLanguagesAll can be located and injected into a session — proving the
// locale-awareness pipeline that an Aether IoT node would use.
func TestAetherNode_LanguageRegistryIntegration(t *testing.T) {
	zu := findLang("zu")
	if zu == nil {
		t.Fatal("findLang('zu') returned nil")
	}
	// In the Go port Zulu is named "isiZulu" (the authentic Nguni name).
	if zu.EnglishName != "isiZulu" {
		t.Errorf("EnglishName: want isiZulu, got %s", zu.EnglishName)
	}
	if zu.IsRtl {
		t.Error("Zulu should not be RTL")
	}
	if zu.PrimaryRegion != "ZA" {
		t.Errorf("PrimaryRegion: want ZA, got %s", zu.PrimaryRegion)
	}

	// Aether IoT node uses Zulu locale.
	lang := zu.BcpTag
	compCtx := circleai.CompanionContext{
		IdentityID:        "aether-iot-zu",
		DisplayName:       "Nomvula",
		PreferredLanguage: &lang,
		Interface:         circleai.InterfaceKindIoT,
		ContextBuiltAt:    time.Now().UTC(),
	}

	gen := &MockChatGenerator{Response: "Sawubona, Nomvula!"}
	session := NewSimpleCompanionSession("session-zu-001", compCtx, gen)
	defer session.Close()

	reply, err := session.Send(context.Background(), "Sawubona")
	if err != nil {
		t.Fatalf("Send failed: %v", err)
	}
	if reply != gen.Response {
		t.Errorf("want %q, got %q", gen.Response, reply)
	}
}

// TestAetherNode_FullLifecycle is the end-to-end scenario:
// an Aether mesh node boots, builds context from Circle AI native types,
// exchanges three messages, receives a proactive event, then shuts down cleanly.
// No FFI, no runtime bridging — pure Go using the Circle AI package.
func TestAetherNode_FullLifecycle(t *testing.T) {
	// 1. Boot: node creates generator + session (no FFI, no bridging).
	gen := &MockChatGenerator{Response: "I understand. Let me help."}
	compCtx := buildTestContext()
	session := NewSimpleCompanionSession("aether-lifecycle-001", compCtx, gen)

	// 2. Active: three conversation turns.
	turns := []string{
		"Good morning",
		"What's on my agenda?",
		"Thanks, that's helpful.",
	}
	for _, msg := range turns {
		reply, err := session.Send(context.Background(), msg)
		if err != nil {
			t.Fatalf("turn %q failed: %v", msg, err)
		}
		if reply == "" {
			t.Errorf("empty reply for turn %q", msg)
		}
	}

	// 3. History depth: 3 turns × 2 = 6 entries.
	if got := len(session.History()); got != 6 {
		t.Errorf("expected 6 history entries, got %d", got)
	}

	// 4. Proactive event delivered from node.
	session.EmitProactiveEvent(circleai.CompanionProactiveEvent{
		SessionID:   session.SessionID(),
		IdentityID:  compCtx.IdentityID,
		Interface:   circleai.InterfaceKindMobile,
		Message:     "You have a goal deadline tomorrow.",
		TriggerName: "goal_deadline_approaching",
		GeneratedAt: time.Now().UTC(),
	})
	select {
	case evt := <-session.ProactiveEvents():
		if evt.TriggerName != "goal_deadline_approaching" {
			t.Errorf("unexpected trigger: %s", evt.TriggerName)
		}
	case <-time.After(200 * time.Millisecond):
		t.Fatal("proactive event not delivered")
	}

	// 5. Positive feedback.
	if err := session.SignalFeedback(context.Background(), true, nil); err != nil {
		t.Fatalf("SignalFeedback failed: %v", err)
	}

	// 6. Clean shutdown.
	if err := session.Close(); err != nil {
		t.Fatalf("Close failed: %v", err)
	}

	// 7. Generator called exactly once per turn.
	if gen.CallCount() != len(turns) {
		t.Errorf("expected %d generator calls, got %d", len(turns), gen.CallCount())
	}
}
