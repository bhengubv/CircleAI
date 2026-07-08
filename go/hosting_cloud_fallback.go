// hosting_cloud_fallback.go
//
// Ports CircleAI.Hosting.CloudFallback:
//   IConfigurableChatGenerator (CloudFallbackChain.cs)
//   CloudFallbackChain (CloudFallbackChain.cs)
//   BrainHealth, BrainStatus, BackupBrainPolicy, BackupBrainOrchestrator
//     (BackupBrainOrchestrator.cs)
//
// Cloud generators are injected behind IConfigurableChatGenerator; the concrete
// OpenAI/Anthropic/Gemini HTTP generators are host-supplied. For tests this file
// also ships a deterministic FakeConfigurableGenerator (no network) plus an
// OpenAI-compatible SSE generator driven by an injected HTTP doer so the wire
// path is exercised without a live endpoint.
//
// CloudFallbackChain = start-of-call ordering: first ready generator wins, the
// "[… not configured]" fail-soft frame is skipped. BackupBrainOrchestrator =
// between-turn failover with a degrade-after-N + cool-down half-open circuit.

package circleai

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
	"sync"
	"time"
)

// IConfigurableChatGenerator reports whether a generator can currently serve
// calls, plus a label + status. Ports
// CircleAI.Hosting.CloudFallback.IConfigurableChatGenerator.
type IConfigurableChatGenerator interface {
	IChatGenerator
	// IsConfigured is true when the generator can serve calls (e.g. key present).
	IsConfigured() bool
	// EngineLabel is a display name (e.g. "OpenAI · gpt-4o-mini").
	EngineLabel() string
	// StatusMessage is a human-readable explanation of the current state.
	StatusMessage() string
}

// ---------------------------------------------------------------------------
// CloudFallbackChain
// ---------------------------------------------------------------------------

// CloudFallbackChain tries an ordered list of IChatGenerators and streams from
// the first ready one. Ports CircleAI.Hosting.CloudFallback.CloudFallbackChain.
// A generator yielding a fail-soft "[… not configured]" / "[CloudFallbackChain…]"
// frame does not count as ready — the chain skips it. Generators that error are
// also skipped.
type CloudFallbackChain struct {
	generators []IChatGenerator
}

// NewCloudFallbackChain builds a chain. Order matters — put on-device first for
// sovereign-by-default.
func NewCloudFallbackChain(generators []IChatGenerator) *CloudFallbackChain {
	cp := make([]IChatGenerator, len(generators))
	copy(cp, generators)
	return &CloudFallbackChain{generators: cp}
}

// Generators returns the ordered generator list.
func (c *CloudFallbackChain) Generators() []IChatGenerator { return c.generators }

// Generate returns the first ready generator's full reply. Ports
// CloudFallbackChain.GenerateAsync.
func (c *CloudFallbackChain) Generate(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (string, error) {
	for _, g := range c.generators {
		if !cloudGeneratorReady(g) {
			continue
		}
		result, err := g.Generate(ctx, messages, opts)
		if err != nil {
			if ctx.Err() != nil {
				return "", err
			}
			continue // fall through to next generator
		}
		return result, nil
	}
	return "[CloudFallbackChain: no configured generator could serve the request]", nil
}

// Stream streams from the first ready generator that produces a real frame.
// Ports CloudFallbackChain.StreamAsync (fail-soft first-frame gating).
func (c *CloudFallbackChain) Stream(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)

	go func() {
		defer close(out)
		defer close(errc)

		for _, g := range c.generators {
			if !cloudGeneratorReady(g) {
				continue
			}
			chunks, cerrs := g.Stream(ctx, messages, opts)
			yielded := false
			faulted := false
			for chunk := range chunks {
				if !yielded && isFailSoftFrame(chunk) {
					// Generator declined the call — drain and move on.
					faulted = true
					break
				}
				yielded = true
				select {
				case out <- chunk:
				case <-ctx.Done():
					errc <- ctx.Err()
					drainStringChan(chunks)
					<-cerrs
					return
				}
			}
			if err := <-cerrs; err != nil {
				if ctx.Err() != nil {
					errc <- err
					return
				}
				// Faulted mid-stream: if we already yielded, stop; else next gen.
				if yielded {
					return
				}
				continue
			}
			if faulted {
				drainStringChan(chunks)
				continue
			}
			if yielded {
				return
			}
		}

		select {
		case out <- "[CloudFallbackChain: no configured generator could serve the request]":
		case <-ctx.Done():
			errc <- ctx.Err()
		}
	}()
	return out, errc
}

// Close closes every generator, swallowing per-generator errors.
func (c *CloudFallbackChain) Close() error {
	for _, g := range c.generators {
		func() {
			defer func() { _ = recover() }()
			_ = g.Close()
		}()
	}
	return nil
}

func cloudGeneratorReady(g IChatGenerator) bool {
	if c, ok := g.(IConfigurableChatGenerator); ok {
		return c.IsConfigured()
	}
	return true
}

// isFailSoftFrame mirrors CloudFallbackChain.IsFailSoftFrame.
func isFailSoftFrame(chunk string) bool {
	if !strings.HasPrefix(chunk, "[") {
		return false
	}
	lower := strings.ToLower(chunk)
	return strings.Contains(lower, "not configured") || strings.Contains(lower, "cloudfallbackchain")
}

func drainStringChan(ch <-chan string) {
	for range ch {
	}
}

var _ IChatGenerator = (*CloudFallbackChain)(nil)

// ---------------------------------------------------------------------------
// BackupBrainOrchestrator
// ---------------------------------------------------------------------------

// BrainHealth is the health state of one brain in the chain. Ports
// CircleAI.Hosting.CloudFallback.BrainHealth (stable ordinals).
type BrainHealth int

const (
	// BrainHealthy — serving normally.
	BrainHealthy BrainHealth = iota
	// BrainDegraded — failed too many times; out of rotation until cool-down.
	BrainDegraded
	// BrainCoolingDown — half-open: ready for a retry attempt.
	BrainCoolingDown
)

// BrainStatus is a snapshot of brain health for monitoring. Ports
// CircleAI.Hosting.CloudFallback.BrainStatus.
type BrainStatus struct {
	Label               string
	Health              BrainHealth
	ConsecutiveFailures int
}

// BackupBrainPolicy holds the failover policy knobs. Ports
// CircleAI.Hosting.CloudFallback.BackupBrainPolicy.
type BackupBrainPolicy struct {
	// DegradedAfterFailures — consecutive failures that push a brain to degraded.
	DegradedAfterFailures int
	// CoolDownDuration — how long a degraded brain waits before a retry.
	CoolDownDuration time.Duration
	// MaxRetriesPerTurn — how many brains to try before giving up on one turn.
	MaxRetriesPerTurn int
}

// DefaultBackupBrainPolicy returns the C# record defaults (2, 30s, 3).
func DefaultBackupBrainPolicy() BackupBrainPolicy {
	return BackupBrainPolicy{DegradedAfterFailures: 2, CoolDownDuration: 30 * time.Second, MaxRetriesPerTurn: 3}
}

// coolDownOrDefault mirrors BackupBrainPolicy.CoolDownDurationOrDefault.
func (p BackupBrainPolicy) coolDownOrDefault() time.Duration {
	if p.CoolDownDuration <= 0 {
		return 30 * time.Second
	}
	return p.CoolDownDuration
}

// BackupBrainOrchestrator wraps an ordered set of brains; it switches on failure
// and retries the primary after a cool-down. Ports
// CircleAI.Hosting.CloudFallback.BackupBrainOrchestrator. Unlike CloudFallbackChain
// (start-of-call ordering) this is between-turn failover.
type BackupBrainOrchestrator struct {
	brains []*brainEntry
	policy BackupBrainPolicy
	clock  func() time.Time
}

// NewBackupBrainOrchestrator builds the orchestrator. Returns an error when no
// brains are supplied. A nil clock defaults to time.Now().UTC; a zero policy
// uses DefaultBackupBrainPolicy.
func NewBackupBrainOrchestrator(brains []IChatGenerator, policy *BackupBrainPolicy, clock func() time.Time) (*BackupBrainOrchestrator, error) {
	if len(brains) == 0 {
		return nil, errArg("at least one brain is required")
	}
	entries := make([]*brainEntry, 0, len(brains))
	for _, b := range brains {
		entries = append(entries, &brainEntry{brain: b})
	}
	p := DefaultBackupBrainPolicy()
	if policy != nil {
		p = *policy
		if p.DegradedAfterFailures == 0 {
			p.DegradedAfterFailures = 2
		}
		if p.MaxRetriesPerTurn == 0 {
			p.MaxRetriesPerTurn = 3
		}
	}
	if clock == nil {
		clock = func() time.Time { return time.Now().UTC() }
	}
	return &BackupBrainOrchestrator{brains: entries, policy: p, clock: clock}, nil
}

// Statuses returns a health snapshot of every brain. Ports
// BackupBrainOrchestrator.Statuses.
func (o *BackupBrainOrchestrator) Statuses() []BrainStatus {
	now := o.clock()
	out := make([]BrainStatus, 0, len(o.brains))
	for _, e := range o.brains {
		e.mu.Lock()
		h := e.healthAt(now, o.policy.coolDownOrDefault())
		label := brainLabel(e.brain)
		out = append(out, BrainStatus{Label: label, Health: h, ConsecutiveFailures: e.consecutive})
		e.mu.Unlock()
	}
	return out
}

// Generate tries brains in rotation until one succeeds. Ports
// BackupBrainOrchestrator.GenerateAsync.
func (o *BackupBrainOrchestrator) Generate(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (string, error) {
	maxRetries := o.policy.MaxRetriesPerTurn
	if len(o.brains) < maxRetries {
		maxRetries = len(o.brains)
	}
	tried := map[*brainEntry]bool{}
	for attempt := 0; attempt < maxRetries; attempt++ {
		pick := o.pickAvailable(tried)
		if pick == nil {
			break
		}
		tried[pick] = true
		result, err := pick.brain.Generate(ctx, messages, opts)
		if err == nil {
			pick.recordSuccess()
			return result, nil
		}
		pick.recordFailure(o.policy.DegradedAfterFailures, o.clock())
	}
	return "[All brains failed.]", nil
}

// Stream tries brains in rotation, committing to the first that streams a frame.
// Ports BackupBrainOrchestrator.StreamAsync.
func (o *BackupBrainOrchestrator) Stream(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)

	go func() {
		defer close(out)
		defer close(errc)

		maxRetries := o.policy.MaxRetriesPerTurn
		if len(o.brains) < maxRetries {
			maxRetries = len(o.brains)
		}
		tried := map[*brainEntry]bool{}
		for attempt := 0; attempt < maxRetries; attempt++ {
			pick := o.pickAvailable(tried)
			if pick == nil {
				break
			}
			tried[pick] = true

			chunks, cerrs := pick.brain.Stream(ctx, messages, opts)
			streamedAny := false
			for chunk := range chunks {
				streamedAny = true
				select {
				case out <- chunk:
				case <-ctx.Done():
					errc <- ctx.Err()
					drainStringChan(chunks)
					<-cerrs
					return
				}
			}
			streamErr := <-cerrs
			if streamErr != nil {
				pick.recordFailure(o.policy.DegradedAfterFailures, o.clock())
				if !streamedAny {
					continue // try the backup
				}
				return // already streamed content; stop
			}
			if streamedAny {
				pick.recordSuccess()
				return
			}
		}
		select {
		case out <- "[All brains failed.]":
		case <-ctx.Done():
			errc <- ctx.Err()
		}
	}()
	return out, errc
}

// Close is a no-op (brains are owned by the caller). Ports the C# Dispose.
func (o *BackupBrainOrchestrator) Close() error { return nil }

func (o *BackupBrainOrchestrator) pickAvailable(skip map[*brainEntry]bool) *brainEntry {
	now := o.clock()
	for _, e := range o.brains {
		if skip[e] {
			continue
		}
		e.mu.Lock()
		h := e.healthAt(now, o.policy.coolDownOrDefault())
		e.mu.Unlock()
		if h == BrainHealthy || h == BrainCoolingDown {
			return e
		}
	}
	// None healthy — pick first untried brain anyway (degraded might recover).
	for _, e := range o.brains {
		if !skip[e] {
			return e
		}
	}
	return nil
}

func brainLabel(b IChatGenerator) string {
	if c, ok := b.(IConfigurableChatGenerator); ok {
		return c.EngineLabel()
	}
	return errorTypeName(fmt.Errorf("%T", b))
}

type brainEntry struct {
	brain         IChatGenerator
	mu            sync.Mutex
	consecutive   int
	degradedSince time.Time
	isDegraded    bool
}

func (e *brainEntry) healthAt(now time.Time, coolDown time.Duration) BrainHealth {
	if !e.isDegraded {
		return BrainHealthy
	}
	if now.Sub(e.degradedSince) >= coolDown {
		return BrainCoolingDown // half-open
	}
	return BrainDegraded
}

func (e *brainEntry) recordSuccess() {
	e.mu.Lock()
	e.consecutive = 0
	e.isDegraded = false
	e.mu.Unlock()
}

func (e *brainEntry) recordFailure(threshold int, now time.Time) {
	e.mu.Lock()
	e.consecutive++
	if e.consecutive >= threshold {
		e.isDegraded = true
		e.degradedSince = now
	}
	e.mu.Unlock()
}

var _ IChatGenerator = (*BackupBrainOrchestrator)(nil)

// ---------------------------------------------------------------------------
// FakeConfigurableGenerator — deterministic local fake for tests
// ---------------------------------------------------------------------------

// FakeConfigurableGenerator is a deterministic IConfigurableChatGenerator with
// no network. It stands in for the injected cloud generators in tests: when
// configured it echoes a scripted reply (or a stable transform of the last user
// turn); when not configured it emits the fail-soft "[<label> not configured]"
// frame the chain skips.
type FakeConfigurableGenerator struct {
	label      string
	configured bool
	// reply, when non-empty, is returned verbatim; otherwise a stable
	// "<label>: <lastUser>" transform is used.
	reply string
	// failEveryCall, when true, makes Generate/Stream return an error — used to
	// exercise BackupBrainOrchestrator failover.
	failEveryCall bool
	// chunks, when set, are streamed one at a time (else the whole reply is one).
	chunks []string
}

// NewFakeConfigurableGenerator builds a fake with the given label + configured
// state.
func NewFakeConfigurableGenerator(label string, configured bool) *FakeConfigurableGenerator {
	return &FakeConfigurableGenerator{label: label, configured: configured}
}

// WithReply sets the scripted reply.
func (g *FakeConfigurableGenerator) WithReply(reply string) *FakeConfigurableGenerator {
	g.reply = reply
	return g
}

// WithChunks sets the streamed chunks.
func (g *FakeConfigurableGenerator) WithChunks(chunks ...string) *FakeConfigurableGenerator {
	g.chunks = chunks
	return g
}

// WithFailure makes every call fail (for failover tests).
func (g *FakeConfigurableGenerator) WithFailure() *FakeConfigurableGenerator {
	g.failEveryCall = true
	return g
}

// IsConfigured implements IConfigurableChatGenerator.
func (g *FakeConfigurableGenerator) IsConfigured() bool { return g.configured }

// EngineLabel implements IConfigurableChatGenerator.
func (g *FakeConfigurableGenerator) EngineLabel() string { return g.label }

// StatusMessage implements IConfigurableChatGenerator.
func (g *FakeConfigurableGenerator) StatusMessage() string {
	if g.configured {
		return "Ready · " + g.label
	}
	return g.label + " not configured."
}

// Generate implements IChatGenerator.
func (g *FakeConfigurableGenerator) Generate(_ context.Context, messages []ChatMessage, _ *GenerationOptions) (string, error) {
	if g.failEveryCall {
		return "", fmt.Errorf("%s: simulated failure", g.label)
	}
	if !g.configured {
		return "[" + g.StatusMessage() + "]", nil
	}
	return g.body(messages), nil
}

// Stream implements IChatGenerator.
func (g *FakeConfigurableGenerator) Stream(ctx context.Context, messages []ChatMessage, _ *GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)
	go func() {
		defer close(out)
		defer close(errc)
		if g.failEveryCall {
			errc <- fmt.Errorf("%s: simulated failure", g.label)
			return
		}
		var frames []string
		if !g.configured {
			frames = []string{"[" + g.StatusMessage() + "]"}
		} else if len(g.chunks) > 0 {
			frames = g.chunks
		} else {
			frames = []string{g.body(messages)}
		}
		for _, f := range frames {
			select {
			case out <- f:
			case <-ctx.Done():
				errc <- ctx.Err()
				return
			}
		}
	}()
	return out, errc
}

// Close implements IChatGenerator.
func (g *FakeConfigurableGenerator) Close() error { return nil }

func (g *FakeConfigurableGenerator) body(messages []ChatMessage) string {
	if g.reply != "" {
		return g.reply
	}
	return g.label + ": " + lastUserContent(messages)
}

var _ IConfigurableChatGenerator = (*FakeConfigurableGenerator)(nil)

// ---------------------------------------------------------------------------
// OpenAiCompatibleChatGenerator — real SSE wire path, injected transport
// ---------------------------------------------------------------------------

// HTTPDoer is the minimal HTTP surface the OpenAI-compatible generator needs.
// Ports the dependency the C# generator gets from HttpClient; injecting it keeps
// the generator deterministic in tests (no live endpoint).
type HTTPDoer interface {
	Do(req *OutboundHTTPRequest) (*InboundHTTPResponse, error)
}

// OutboundHTTPRequest / InboundHTTPResponse are transport-neutral value types so
// the generator carries no net/http dependency in its contract.
type OutboundHTTPRequest struct {
	URL     string
	Body    []byte
	Headers map[string]string
}

// InboundHTTPResponse is the doer's reply.
type InboundHTTPResponse struct {
	StatusCode int
	Body       []byte
}

// OpenAiChatConfig configures the OpenAI-compatible generator. Ports the shape
// of CircleAI.Hosting.CloudFallback.OpenAiChatOptions (base address, key, model,
// sampling defaults).
type OpenAiChatConfig struct {
	ProviderID          string
	BaseURL             string
	APIKey              string
	Model               string
	DefaultTemperature  float32
	DefaultMaxTokens    int
	ChatCompletionsPath string
}

// OpenAiCompatibleChatGenerator speaks the OpenAI Chat Completions streaming wire
// format. Ports CircleAI.Hosting.CloudFallback.OpenAiCompatibleChatGeneratorBase.
// Fail-soft: when unconfigured it yields one "[<status>]" frame and stops.
type OpenAiCompatibleChatGenerator struct {
	cfg  OpenAiChatConfig
	doer HTTPDoer
}

// NewOpenAiCompatibleChatGenerator builds the generator against an injected doer.
func NewOpenAiCompatibleChatGenerator(cfg OpenAiChatConfig, doer HTTPDoer) *OpenAiCompatibleChatGenerator {
	if cfg.ChatCompletionsPath == "" {
		cfg.ChatCompletionsPath = "/v1/chat/completions"
	}
	return &OpenAiCompatibleChatGenerator{cfg: cfg, doer: doer}
}

// IsConfigured implements IConfigurableChatGenerator.
func (g *OpenAiCompatibleChatGenerator) IsConfigured() bool { return !isBlank(g.cfg.APIKey) }

// EngineLabel implements IConfigurableChatGenerator.
func (g *OpenAiCompatibleChatGenerator) EngineLabel() string {
	return g.cfg.ProviderID + " · " + g.cfg.Model
}

// StatusMessage implements IConfigurableChatGenerator.
func (g *OpenAiCompatibleChatGenerator) StatusMessage() string {
	if g.IsConfigured() {
		return "Ready · " + g.cfg.Model
	}
	return g.cfg.ProviderID + " API key not configured."
}

// Generate concatenates the stream. Ports the base GenerateAsync.
func (g *OpenAiCompatibleChatGenerator) Generate(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (string, error) {
	chunks, errc := g.Stream(ctx, messages, opts)
	var sb strings.Builder
	for c := range chunks {
		sb.WriteString(c)
	}
	if err := <-errc; err != nil {
		return "", err
	}
	return sb.String(), nil
}

// Stream posts the request and yields each delta.content frame. Ports the base
// StreamAsync (fail-soft when unconfigured; SSE frame → choices[0].delta.content).
func (g *OpenAiCompatibleChatGenerator) Stream(ctx context.Context, messages []ChatMessage, opts *GenerationOptions) (<-chan string, <-chan error) {
	out := make(chan string)
	errc := make(chan error, 1)

	go func() {
		defer close(out)
		defer close(errc)

		if !g.IsConfigured() {
			out <- "[" + g.StatusMessage() + "]"
			return
		}

		temperature := g.cfg.DefaultTemperature
		maxTokens := g.cfg.DefaultMaxTokens
		if opts != nil {
			temperature = opts.Temperature
			maxTokens = opts.MaxTokens
		}
		reqBody := map[string]interface{}{
			"model":       g.cfg.Model,
			"stream":      true,
			"temperature": temperature,
			"max_tokens":  maxTokens,
			"messages":    openAiMessages(messages),
		}
		body, _ := json.Marshal(reqBody)

		resp, err := g.doer.Do(&OutboundHTTPRequest{
			URL:  strings.TrimRight(g.cfg.BaseURL, "/") + g.cfg.ChatCompletionsPath,
			Body: body,
			Headers: map[string]string{
				"Authorization": "Bearer " + g.cfg.APIKey,
				"Content-Type":  "application/json",
			},
		})
		if err != nil {
			errc <- err
			return
		}
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			out <- fmt.Sprintf("[%s error %d: %s]", g.cfg.ProviderID, resp.StatusCode,
				truncateEllipsis(string(resp.Body), 240))
			return
		}

		for _, frame := range parseSSEFrames(resp.Body) {
			delta := extractOpenAiDelta(frame)
			if delta != "" {
				select {
				case out <- delta:
				case <-ctx.Done():
					errc <- ctx.Err()
					return
				}
			}
		}
	}()
	return out, errc
}

// Close implements IChatGenerator.
func (g *OpenAiCompatibleChatGenerator) Close() error { return nil }

func openAiMessages(messages []ChatMessage) []map[string]string {
	out := make([]map[string]string, len(messages))
	for i, m := range messages {
		out[i] = map[string]string{"role": m.Role, "content": m.Content}
	}
	return out
}

// parseSSEFrames extracts the payload of every `data:` frame from an SSE body,
// stopping at the `[DONE]` sentinel. Ports
// CircleAI.Hosting.CloudFallback.ServerSentEventsReader.ReadFramesAsync.
func parseSSEFrames(body []byte) []string {
	var frames []string
	for _, line := range strings.Split(string(body), "\n") {
		line = strings.TrimRight(line, "\r")
		if !strings.HasPrefix(line, "data:") {
			continue
		}
		payload := strings.TrimSpace(line[len("data:"):])
		if payload == "[DONE]" {
			break
		}
		frames = append(frames, payload)
	}
	return frames
}

// extractOpenAiDelta parses one SSE JSON frame and returns choices[0].delta.content
// (empty when absent). Mirrors the base generator's per-frame parse.
func extractOpenAiDelta(frame string) string {
	var parsed struct {
		Choices []struct {
			Delta struct {
				Content *string `json:"content"`
			} `json:"delta"`
		} `json:"choices"`
	}
	if err := json.Unmarshal([]byte(frame), &parsed); err != nil {
		return ""
	}
	if len(parsed.Choices) > 0 && parsed.Choices[0].Delta.Content != nil {
		return *parsed.Choices[0].Delta.Content
	}
	return ""
}

var _ IConfigurableChatGenerator = (*OpenAiCompatibleChatGenerator)(nil)
