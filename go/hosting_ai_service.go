// hosting_ai_service.go
//
// Ports CircleAI.Hosting core service surface:
//   IAIService (IAIService.cs)
//   AIService (AIService.cs — generator-backed, deterministic; the deep native
//     model-loader / selector / registry machinery belongs to other work units
//     and is injected behind IChatGenerator + a SystemPromptEnricher hook)
//   FallbackAIService (FallbackAIService.cs)
//
// AIService owns a loaded IChatGenerator for its lifetime. Ask wraps a single
// user turn (system prompt injected automatically); Chat enriches the system
// message only when the caller did not supply one; the agentic loop parses
// <tool_call>…</tool_call>, dispatches to the tool bridge, and re-prompts. The
// <tool_call> extraction matches AIService.ParseToolCall byte-for-byte.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"sync"
	"time"

	"github.com/google/uuid"
)

// IAIService is the contract for a long-lived B! butler process. Ports
// CircleAI.Hosting.IAIService. Implementations are thread-safe.
type IAIService interface {
	// IsReady reports whether Start has completed and the model is loaded.
	IsReady() bool
	// Start resolves + loads the model and optionally warms it. Idempotent.
	Start(ctx context.Context) error
	// Stop releases the model handle and shuts the service down.
	Stop(ctx context.Context) error
	// Ask is a convenience wrapper for a single user question; the configured
	// system prompt (+ enrichment) is prepended automatically.
	Ask(ctx context.Context, question string) (string, error)
	// Chat generates a complete reply for the supplied conversation; the system
	// message is enriched automatically when the caller did not supply one.
	Chat(ctx context.Context, messages []ChatMessage, options *GenerationOptions) (string, error)
	// Stream streams the reply token-by-token. Enrichment matches Chat.
	Stream(ctx context.Context, messages []ChatMessage, options *GenerationOptions) (<-chan string, <-chan error)
	// InvokeTool routes a tool invocation to the configured tool bridge.
	InvokeTool(ctx context.Context, invocation ToolInvocation) (ToolResult, error)
	// AgenticChat generates, detects tool calls, executes them, and re-prompts
	// until a plain response is produced or the iteration cap is reached.
	AgenticChat(ctx context.Context, prompt string, options *GenerationOptions) (string, error)
	// SubmitFeedback records a feedback signal against a past response.
	SubmitFeedback(ctx context.Context, signal FeedbackSignal) error
	// CheckForUpgrades returns one UpgradeInfo per detected model upgrade.
	CheckForUpgrades(ctx context.Context) ([]UpgradeInfo, error)
	// Prewarm pre-warms the loaded generator (RT-07). Defaults to Start.
	Prewarm(ctx context.Context) error
}

// SystemPromptEnricher augments the base system prompt for a user query. It is
// the injection point for the persona/affect/device/RAG/skills enrichment the
// C# AIService.BuildEnrichedSystemPromptAsync performs against its stores. A nil
// enricher leaves the base prompt unchanged.
type SystemPromptEnricher func(ctx context.Context, base, userQuery string) string

// aiServiceToolCallOpen / Close mirror AIService.ToolCallOpen / ToolCallClose.
const (
	aiServiceToolCallOpen  = "<tool_call>"
	aiServiceToolCallClose = "</tool_call>"
)

// AIService is the generator-backed IAIService. Ports the contract behaviour of
// CircleAI.Hosting.AIService.
type AIService struct {
	generatorFactory func() (IChatGenerator, error)
	systemPrompt     string
	defaultOptions   GenerationOptions
	warmOnStart      bool
	agenticMaxIter   int

	toolBridge    IToolBridge
	feedbackStore IFeedbackStore
	observer      HostAIObserver
	enricher      SystemPromptEnricher

	mu        sync.Mutex
	generator IChatGenerator
	started   bool
	disposed  bool
}

// AIServiceOption configures an AIService.
type AIServiceOption func(*AIService)

// WithSystemPrompt sets the base system prompt.
func WithSystemPrompt(p string) AIServiceOption { return func(s *AIService) { s.systemPrompt = p } }

// WithDefaultGenerationOptions sets the default generation options.
func WithDefaultGenerationOptions(o GenerationOptions) AIServiceOption {
	return func(s *AIService) { s.defaultOptions = o }
}

// WithWarmOnStart toggles the warm-up generation on Start (default true).
func WithWarmOnStart(v bool) AIServiceOption { return func(s *AIService) { s.warmOnStart = v } }

// WithAgenticMaxIterations sets the agentic loop cap (default 4).
func WithAgenticMaxIterations(n int) AIServiceOption {
	return func(s *AIService) { s.agenticMaxIter = n }
}

// WithToolBridge wires the tool bridge used by InvokeTool + the agentic loop.
func WithToolBridge(b IToolBridge) AIServiceOption { return func(s *AIService) { s.toolBridge = b } }

// WithFeedbackStore wires the feedback store used by SubmitFeedback.
func WithFeedbackStore(f IFeedbackStore) AIServiceOption {
	return func(s *AIService) { s.feedbackStore = f }
}

// WithHostObserver wires the event-based observer.
func WithHostObserver(o HostAIObserver) AIServiceOption { return func(s *AIService) { s.observer = o } }

// WithSystemPromptEnricher wires the enrichment hook.
func WithSystemPromptEnricher(e SystemPromptEnricher) AIServiceOption {
	return func(s *AIService) { s.enricher = e }
}

// NewAIService builds an AIService that loads its generator via factory. The
// factory is invoked once on Start (mirroring the C# generatorFactory(modelPath)).
func NewAIService(factory func() (IChatGenerator, error), opts ...AIServiceOption) *AIService {
	s := &AIService{
		generatorFactory: factory,
		systemPrompt:     "You are B!, a helpful on-device assistant.",
		defaultOptions:   DefaultGenerationOptions(),
		warmOnStart:      true,
		agenticMaxIter:   4,
	}
	for _, o := range opts {
		o(s)
	}
	return s
}

// IsReady reports whether Start completed and the generator is loaded.
func (s *AIService) IsReady() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.started && s.generator != nil && !s.disposed
}

// Start loads the generator (once) and optionally warms it. Ports
// AIService.StartAsync (control flow: load → warm-up → started → OnStarted).
func (s *AIService) Start(ctx context.Context) error {
	s.mu.Lock()
	if s.disposed {
		s.mu.Unlock()
		return errors.New("AIService is disposed")
	}
	if s.started {
		s.mu.Unlock()
		return nil
	}
	gen, err := s.generatorFactory()
	if err != nil {
		s.mu.Unlock()
		return err
	}
	if gen == nil {
		s.mu.Unlock()
		return errors.New("generator factory returned nil")
	}
	s.generator = gen
	warm := s.warmOnStart
	s.started = true
	obs := s.observer
	s.mu.Unlock()

	if warm {
		// Warm-up generation is best-effort — failures are non-fatal.
		_, _ = gen.Generate(ctx, []ChatMessage{{Role: "user", Content: "Hello"}}, s.warmupOptions())
	}
	if obs != nil {
		_ = fireHostObserver(func() error { return obs.OnStarted(ctx) })
	}
	return nil
}

// Stop releases the generator and shuts the service down. Ports
// AIService.StopAsync.
func (s *AIService) Stop(ctx context.Context) error {
	s.mu.Lock()
	if s.disposed {
		s.mu.Unlock()
		return nil
	}
	gen := s.generator
	s.generator = nil
	s.started = false
	obs := s.observer
	s.mu.Unlock()

	if gen != nil {
		_ = gen.Close()
	}
	if obs != nil {
		_ = fireHostObserver(func() error { return obs.OnStopped(context.Background()) })
	}
	return nil
}

// Ask wraps a single user turn. Ports AIService.AskAsync.
func (s *AIService) Ask(ctx context.Context, question string) (string, error) {
	if question == "" {
		return "", errArg("question must not be null or empty")
	}
	messages := []ChatMessage{{Role: "user", Content: question}}
	return s.Chat(ctx, messages, &s.defaultOptions)
}

// Chat generates a complete reply, injecting an enriched system prompt when the
// caller did not supply one. Ports AIService.ChatAsync + PrepareMessagesAsync.
func (s *AIService) Chat(ctx context.Context, messages []ChatMessage, options *GenerationOptions) (string, error) {
	if messages == nil {
		return "", errNilArg("messages")
	}
	if err := s.ensureStarted(ctx); err != nil {
		return "", err
	}
	gen := s.currentGenerator()
	if gen == nil {
		return "", errors.New("Butler is not ready")
	}

	userQuery := lastUserContent(messages)
	prepared := s.prepareMessages(ctx, messages, userQuery)
	effective := s.effectiveOptions(options)

	correlationID := uuid.New()
	start := time.Now()
	response, err := gen.Generate(ctx, prepared, effective)
	if err != nil {
		return "", err
	}
	elapsed := time.Since(start)

	if obs := s.observer; obs != nil {
		_ = fireHostObserver(func() error {
			return obs.OnChatCompleted(ctx, AIChatEvent{
				CorrelationID: correlationID,
				Messages:      prepared,
				Response:      response,
				Elapsed:       elapsed,
				Timestamp:     time.Now().UTC(),
			})
		})
	}
	return response, nil
}

// Stream streams the reply token-by-token with the same enrichment as Chat.
// Ports AIService.StreamAsync.
func (s *AIService) Stream(ctx context.Context, messages []ChatMessage, options *GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)

	if messages == nil {
		errc <- errNilArg("messages")
		close(out)
		close(errc)
		return out, errc
	}
	if err := s.ensureStarted(ctx); err != nil {
		errc <- err
		close(out)
		close(errc)
		return out, errc
	}
	gen := s.currentGenerator()
	if gen == nil {
		errc <- errors.New("Butler is not ready")
		close(out)
		close(errc)
		return out, errc
	}

	userQuery := lastUserContent(messages)
	prepared := s.prepareMessages(ctx, messages, userQuery)
	effective := s.effectiveOptions(options)
	obs := s.observer

	go func() {
		defer close(out)
		defer close(errc)
		correlationID := uuid.New()
		start := time.Now()
		tokenCount := 0
		firstToken := true

		chunks, cerrs := gen.Stream(ctx, prepared, effective)
		for piece := range chunks {
			if firstToken {
				firstToken = false
				if obs != nil {
					_ = fireHostObserver(func() error {
						return obs.OnStreamStarted(ctx, AIStreamEvent{
							CorrelationID: correlationID,
							Messages:      prepared,
							Elapsed:       time.Since(start),
							TokenCount:    0,
							Timestamp:     time.Now().UTC(),
						})
					})
				}
			}
			tokenCount++
			select {
			case out <- piece:
			case <-ctx.Done():
				errc <- ctx.Err()
				return
			}
		}
		if err := <-cerrs; err != nil {
			errc <- err
			return
		}
		if obs != nil {
			_ = fireHostObserver(func() error {
				return obs.OnStreamCompleted(ctx, AIStreamEvent{
					CorrelationID: correlationID,
					Messages:      prepared,
					Elapsed:       time.Since(start),
					TokenCount:    tokenCount,
					Timestamp:     time.Now().UTC(),
				})
			})
		}
	}()
	return out, errc
}

// InvokeTool routes a tool invocation to the configured bridge. Ports
// AIService.InvokeToolAsync. Returns a failure result when no bridge is wired.
func (s *AIService) InvokeTool(ctx context.Context, invocation ToolInvocation) (ToolResult, error) {
	s.mu.Lock()
	if s.disposed {
		s.mu.Unlock()
		return ToolResult{}, errors.New("AIService is disposed")
	}
	bridge := s.toolBridge
	obs := s.observer
	s.mu.Unlock()

	if bridge == nil {
		fail := ToolResult{ToolName: invocation.ToolName, Success: false, Error: "No tool bridge configured."}
		if obs != nil {
			_ = fireHostObserver(func() error {
				return obs.OnToolInvoked(ctx, AIToolEvent{
					CorrelationID: uuid.New(), Invocation: invocation, Result: fail,
					Elapsed: 0, Timestamp: time.Now().UTC(),
				})
			})
		}
		return fail, nil
	}

	correlationID := uuid.New()
	start := time.Now()
	result, err := bridge.Invoke(ctx, invocation)
	if err != nil {
		return ToolResult{}, err
	}
	elapsed := time.Since(start)
	if obs != nil {
		_ = fireHostObserver(func() error {
			return obs.OnToolInvoked(ctx, AIToolEvent{
				CorrelationID: correlationID, Invocation: invocation, Result: result,
				Elapsed: elapsed, Timestamp: time.Now().UTC(),
			})
		})
	}
	return result, nil
}

// AgenticChat runs the generate→tool→re-prompt loop. Ports
// AIService.AgenticChatAsync.
func (s *AIService) AgenticChat(ctx context.Context, prompt string, options *GenerationOptions) (string, error) {
	if prompt == "" {
		return "", errArg("prompt must not be null or empty")
	}
	if err := s.ensureStarted(ctx); err != nil {
		return "", err
	}
	gen := s.currentGenerator()
	if gen == nil {
		return "", errors.New("Butler is not ready")
	}

	maxIter := s.agenticMaxIter
	if maxIter < 1 {
		maxIter = 1
	}
	effective := s.effectiveOptions(options)

	history := []ChatMessage{{Role: "user", Content: prompt}}
	lastResponse := ""

	for iteration := 0; iteration < maxIter; iteration++ {
		prepared := s.prepareMessages(ctx, history, prompt)

		start := time.Now()
		response, err := gen.Generate(ctx, prepared, effective)
		if err != nil {
			return "", err
		}
		elapsed := time.Since(start)
		lastResponse = response
		history = append(history, ChatMessage{Role: "assistant", Content: response})

		if obs := s.observer; obs != nil {
			_ = fireHostObserver(func() error {
				return obs.OnChatCompleted(ctx, AIChatEvent{
					CorrelationID: uuid.New(), Messages: prepared, Response: response,
					Elapsed: elapsed, Timestamp: time.Now().UTC(),
				})
			})
		}

		invocation, ok := parseAIServiceToolCall(response)
		if !ok {
			break // No tool call — done.
		}

		if s.toolBridge == nil {
			history = append(history, ChatMessage{Role: "tool",
				Content: `{"tool": "` + invocation.ToolName + `", "error": "No tool bridge configured."}`})
			continue
		}

		toolResult, err := s.InvokeTool(ctx, invocation)
		if err != nil {
			return "", err
		}
		var toolContent string
		if toolResult.Success {
			resJSON, _ := json.Marshal(toolResult.Result)
			toolContent = `{"tool": "` + toolResult.ToolName + `", "result": ` + string(resJSON) + `}`
		} else {
			errJSON, _ := json.Marshal(toolResult.Error)
			toolContent = `{"tool": "` + toolResult.ToolName + `", "error": ` + string(errJSON) + `}`
		}
		history = append(history, ChatMessage{Role: "tool", Content: toolContent})
	}
	return lastResponse, nil
}

// SubmitFeedback records a feedback signal in the configured store. Ports the
// storage portion of AIService.SubmitFeedbackAsync (persona evolution belongs to
// the memory work unit).
func (s *AIService) SubmitFeedback(ctx context.Context, signal FeedbackSignal) error {
	s.mu.Lock()
	if s.disposed {
		s.mu.Unlock()
		return errors.New("AIService is disposed")
	}
	store := s.feedbackStore
	s.mu.Unlock()
	if store == nil {
		return nil
	}
	return store.Add(ctx, signal)
}

// CheckForUpgrades returns an empty list by default (matches the IAIService
// default implementation; the registry-driven check belongs to another unit).
func (s *AIService) CheckForUpgrades(context.Context) ([]UpgradeInfo, error) {
	return nil, nil
}

// Prewarm pre-warms the loaded generator. Ports the IAIService.PrewarmAsync
// default (=> Start).
func (s *AIService) Prewarm(ctx context.Context) error {
	return s.Start(ctx)
}

// ------------------------------------------------------------------ helpers

func (s *AIService) ensureStarted(ctx context.Context) error {
	if s.IsReady() {
		return nil
	}
	return s.Start(ctx)
}

func (s *AIService) currentGenerator() IChatGenerator {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.generator
}

func (s *AIService) effectiveOptions(options *GenerationOptions) *GenerationOptions {
	if options != nil {
		return options
	}
	o := s.defaultOptions
	return &o
}

func (s *AIService) warmupOptions() *GenerationOptions {
	o := s.defaultOptions
	o.MaxTokens = 1
	return &o
}

// prepareMessages injects an enriched system prompt only when the caller did not
// supply their own system message. Ports AIService.PrepareMessagesAsync.
func (s *AIService) prepareMessages(ctx context.Context, messages []ChatMessage, userQuery string) []ChatMessage {
	systemContent := s.buildEnrichedSystemPrompt(ctx, userQuery)

	hasSystem := false
	for _, m := range messages {
		if strings.EqualFold(m.Role, "system") {
			hasSystem = true
			break
		}
	}

	if hasSystem {
		prepared := make([]ChatMessage, len(messages))
		copy(prepared, messages)
		return prepared
	}

	prepared := make([]ChatMessage, 0, len(messages)+1)
	if !isBlank(systemContent) {
		prepared = append(prepared, ChatMessage{Role: "system", Content: systemContent})
	}
	prepared = append(prepared, messages...)
	return prepared
}

// buildEnrichedSystemPrompt applies the injected enricher hook to the base
// prompt. Ports the extensibility of AIService.BuildEnrichedSystemPromptAsync
// (the concrete store lookups live in the memory/personality work units).
func (s *AIService) buildEnrichedSystemPrompt(ctx context.Context, userQuery string) string {
	base := s.systemPrompt
	if s.enricher != nil {
		return s.enricher(ctx, base, userQuery)
	}
	return base
}

// parseAIServiceToolCall extracts a ToolInvocation from a <tool_call>…</tool_call>
// block. Ports AIService.ParseToolCall exactly: supports {"name":…} and
// {"tool_name":…}; string args stay strings, non-string args become raw JSON text.
func parseAIServiceToolCall(response string) (ToolInvocation, bool) {
	if isBlank(response) {
		return ToolInvocation{}, false
	}
	start := strings.Index(response, aiServiceToolCallOpen)
	if start < 0 {
		return ToolInvocation{}, false
	}
	contentStart := start + len(aiServiceToolCallOpen)
	rel := strings.Index(response[contentStart:], aiServiceToolCallClose)
	if rel < 0 {
		return ToolInvocation{}, false
	}
	jsonText := strings.TrimSpace(response[contentStart : contentStart+rel])
	if isBlank(jsonText) {
		return ToolInvocation{}, false
	}

	dec := json.NewDecoder(strings.NewReader(jsonText))
	dec.UseNumber()
	var rootAny interface{}
	if err := dec.Decode(&rootAny); err != nil {
		return ToolInvocation{}, false
	}
	root, ok := rootAny.(map[string]interface{})
	if !ok {
		return ToolInvocation{}, false
	}

	toolName := ""
	if v, ok := root["name"].(string); ok {
		toolName = v
	} else if v, ok := root["tool_name"].(string); ok {
		toolName = v
	}
	if isBlank(toolName) {
		return ToolInvocation{}, false
	}

	args := map[string]interface{}{}
	if rawArgs, ok := root["arguments"]; ok {
		if argsObj, ok := rawArgs.(map[string]interface{}); ok {
			for name, v := range argsObj {
				if sv, ok := v.(string); ok {
					args[name] = sv
				} else {
					args[name] = rawJSONText(v)
				}
			}
		}
	}
	return ToolInvocation{ToolName: toolName, Arguments: args}, true
}

// rawJSONText re-serialises a decoded JSON value to its compact text form,
// approximating JsonElement.GetRawText() for the non-string argument case.
func rawJSONText(v interface{}) string {
	b, err := json.Marshal(v)
	if err != nil {
		return ""
	}
	return string(b)
}

func lastUserContent(messages []ChatMessage) string {
	for i := len(messages) - 1; i >= 0; i-- {
		if strings.EqualFold(messages[i].Role, "user") {
			return messages[i].Content
		}
	}
	return ""
}

// fireHostObserver runs an observer action with panic isolation; observer errors
// are non-fatal (mirrors AIService.FireObserverAsync).
func fireHostObserver(action func() error) (err error) {
	defer func() {
		if recover() != nil {
			err = nil
		}
	}()
	_ = action()
	return nil
}

var _ IAIService = (*AIService)(nil)

// ---------------------------------------------------------------------------
// FallbackAIService
// ---------------------------------------------------------------------------

// defaultFallbackRamThresholdBytes mirrors the FallbackAIService default (2 GB).
const defaultFallbackRamThresholdBytes int64 = 2 * 1024 * 1024 * 1024

// FallbackAIService wraps a local IAIService with a cloud IAIService fallback.
// Local inference is preferred; cloud is used transparently when local is
// unavailable (available RAM below threshold, or local Start fails). Ports
// CircleAI.Hosting.FallbackAIService.
//
// The C# implementation reads GC memory info; Go injects the available-RAM probe
// so the decision is deterministic and testable.
type FallbackAIService struct {
	local             IAIService
	cloud             IAIService
	ramThresholdBytes int64
	availableRAM      func() int64

	mu       sync.Mutex
	active   IAIService
	disposed bool
}

// NewFallbackAIService builds the wrapper. ramThresholdBytes<=0 uses the 2 GB
// default. availableRAM may be nil, in which case it reports 0 (forcing cloud) —
// callers wanting local should inject a real probe.
func NewFallbackAIService(local, cloud IAIService, ramThresholdBytes int64, availableRAM func() int64) *FallbackAIService {
	if ramThresholdBytes <= 0 {
		ramThresholdBytes = defaultFallbackRamThresholdBytes
	}
	if availableRAM == nil {
		availableRAM = func() int64 { return 0 }
	}
	return &FallbackAIService{
		local:             local,
		cloud:             cloud,
		ramThresholdBytes: ramThresholdBytes,
		availableRAM:      availableRAM,
	}
}

// IsReady reports whether the active backend is ready.
func (f *FallbackAIService) IsReady() bool {
	f.mu.Lock()
	active := f.active
	f.mu.Unlock()
	return active != nil && active.IsReady()
}

// Start tries local first (when RAM is sufficient), else falls back to cloud.
// Ports FallbackAIService.StartAsync.
func (f *FallbackAIService) Start(ctx context.Context) error {
	if f.availableRAM() >= f.ramThresholdBytes {
		if err := f.local.Start(ctx); err == nil {
			f.setActive(f.local)
			return nil
		}
		// Local start failed — fall back to cloud (silently, as in C#).
	}
	if err := f.cloud.Start(ctx); err != nil {
		return err
	}
	f.setActive(f.cloud)
	return nil
}

// Stop stops the active backend. Ports FallbackAIService.StopAsync.
func (f *FallbackAIService) Stop(ctx context.Context) error {
	f.mu.Lock()
	active := f.active
	f.mu.Unlock()
	if active != nil {
		return active.Stop(ctx)
	}
	return nil
}

// Ask delegates to the active backend.
func (f *FallbackAIService) Ask(ctx context.Context, question string) (string, error) {
	a, err := f.requireActive()
	if err != nil {
		return "", err
	}
	return a.Ask(ctx, question)
}

// Chat delegates to the active backend.
func (f *FallbackAIService) Chat(ctx context.Context, messages []ChatMessage, options *GenerationOptions) (string, error) {
	a, err := f.requireActive()
	if err != nil {
		return "", err
	}
	return a.Chat(ctx, messages, options)
}

// Stream delegates to the active backend.
func (f *FallbackAIService) Stream(ctx context.Context, messages []ChatMessage, options *GenerationOptions) (<-chan string, <-chan error) {
	a, err := f.requireActive()
	if err != nil {
		out := make(chan string)
		errc := make(chan error, 1)
		errc <- err
		close(out)
		close(errc)
		return out, errc
	}
	return a.Stream(ctx, messages, options)
}

// InvokeTool delegates to the active backend.
func (f *FallbackAIService) InvokeTool(ctx context.Context, invocation ToolInvocation) (ToolResult, error) {
	a, err := f.requireActive()
	if err != nil {
		return ToolResult{}, err
	}
	return a.InvokeTool(ctx, invocation)
}

// AgenticChat delegates to the active backend.
func (f *FallbackAIService) AgenticChat(ctx context.Context, prompt string, options *GenerationOptions) (string, error) {
	a, err := f.requireActive()
	if err != nil {
		return "", err
	}
	return a.AgenticChat(ctx, prompt, options)
}

// SubmitFeedback delegates to the active backend.
func (f *FallbackAIService) SubmitFeedback(ctx context.Context, signal FeedbackSignal) error {
	a, err := f.requireActive()
	if err != nil {
		return err
	}
	return a.SubmitFeedback(ctx, signal)
}

// CheckForUpgrades delegates to the active backend.
func (f *FallbackAIService) CheckForUpgrades(ctx context.Context) ([]UpgradeInfo, error) {
	a, err := f.requireActive()
	if err != nil {
		return nil, err
	}
	return a.CheckForUpgrades(ctx)
}

// Prewarm delegates to the active backend.
func (f *FallbackAIService) Prewarm(ctx context.Context) error {
	a, err := f.requireActive()
	if err != nil {
		return err
	}
	return a.Prewarm(ctx)
}

// ActiveIsCloud reports whether the cloud backend is currently active (test aid).
func (f *FallbackAIService) ActiveIsCloud() bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.active == f.cloud
}

func (f *FallbackAIService) setActive(a IAIService) {
	f.mu.Lock()
	f.active = a
	f.mu.Unlock()
}

func (f *FallbackAIService) requireActive() (IAIService, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.active == nil {
		return nil, errors.New("FallbackAIService has not been started. Call Start first.")
	}
	return f.active, nil
}

var _ IAIService = (*FallbackAIService)(nil)
