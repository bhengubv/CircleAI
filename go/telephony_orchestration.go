// telephony_orchestration.go
//
// The rest of CircleAI.Telephony: handing a call to somebody else, the events
// that say what is happening on the line, tools with a breaker in front of
// them, speculation, recording, evaluation, and the dashboard.
//
// Two things run through all of it. Everything that reaches outside the process
// is refusable and says why. And every event carries enough to reconstruct a
// call afterwards, because voice failures are not reproducible — by the time
// somebody says "it did not hear me", the audio is gone.

package circleai

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"sort"
	"strings"
	"sync"
	"time"
)

// ─────────────────────────────────────────────────────────────────────────────
// Speech lifecycle

// SpeechLifecycleEvent is the common shape of everything that happens on a line.
type SpeechLifecycleEvent struct {
	CallID string
	At     time.Time
}

// CallerSpeechStartedEvent — the caller began talking.
type CallerSpeechStartedEvent struct{ SpeechLifecycleEvent }

// CallerSpeechEndedEvent — the caller stopped. Not the same as end of turn:
// stopping making noise and having finished a sentence are different facts, and
// only the end-of-turn detector knows the second one.
type CallerSpeechEndedEvent struct{ SpeechLifecycleEvent }

// AgentThinkingEvent — generation has started and nothing is being said yet.
// This is the event a filler listens for.
type AgentThinkingEvent struct{ SpeechLifecycleEvent }

// AgentSpeakingStartedEvent — audio is going out.
type AgentSpeakingStartedEvent struct{ SpeechLifecycleEvent }

// AgentSpeakingFinishedEvent — the agent finished of its own accord.
// Interruption is a barge-in transition, not this: a caller who cut the agent
// off and an agent that reached the end of its sentence are different events,
// and a transcript that treats them alike reads as though the agent finished.
type AgentSpeakingFinishedEvent struct{ SpeechLifecycleEvent }

// TranscriptInterimEvent is a partial transcript.
//
// Interim transcripts REPLACE each other for an utterance; they do not append.
// A consumer that appends renders the sentence growing by duplication.
type TranscriptInterimEvent struct {
	SpeechLifecycleEvent
	Text string
	// Negative when the engine did not say. Zero is a real answer meaning "no
	// idea", and the two must not be confused.
	Confidence float64
}

// TranscriptFinalEvent_v2 is the settled transcript for an utterance.
//
// Named for the version because the first shape is still in use by hosts that
// subscribed to it. A silent shape change would break them at runtime with a
// field that is suddenly absent; a second name breaks nobody and makes the
// migration visible.
type TranscriptFinalEvent_v2 struct {
	SpeechLifecycleEvent
	Text       string
	Confidence float64
	StartedAt  time.Time
	EndedAt    time.Time
	Language   string
	// Word-level timings, when the engine gives them. Empty is normal.
	Words       []string
	WordOffsets []time.Duration
}

// SpeechErrorEvent is something going wrong on the line.
type SpeechErrorEvent struct {
	SpeechLifecycleEvent
	Code    string
	Message string
	// Whether the session survives. A recoverable error and a dead session
	// demand opposite reactions, and a caller that cannot tell reconnects on
	// every hiccup or on none.
	Fatal bool
}

// ISpeechSubscription is a live subscription.
type ISpeechSubscription interface {
	// Cancel must be safe to call from inside a handler — a component that
	// decides "I have heard enough" cancels while the bus is mid-publish.
	Cancel()
}

// ISpeechLifecycleBus carries lifecycle events to whoever is listening.
type ISpeechLifecycleBus interface {
	Subscribe(handler func(event any)) ISpeechSubscription
	Publish(event any)
}

type speechSubscription struct {
	bus *InMemorySpeechLifecycleBus
	id  uint64
}

func (s *speechSubscription) Cancel() {
	if s.bus == nil {
		return
	}
	s.bus.mu.Lock()
	defer s.bus.mu.Unlock()
	delete(s.bus.handlers, s.id)
}

// InMemorySpeechLifecycleBus is the default bus.
type InMemorySpeechLifecycleBus struct {
	mu       sync.RWMutex
	next     uint64
	handlers map[uint64]func(event any)
}

// NewInMemorySpeechLifecycleBus returns an empty bus.
func NewInMemorySpeechLifecycleBus() *InMemorySpeechLifecycleBus {
	return &InMemorySpeechLifecycleBus{handlers: map[uint64]func(any){}}
}

// Subscribe implements ISpeechLifecycleBus.
func (b *InMemorySpeechLifecycleBus) Subscribe(handler func(event any)) ISpeechSubscription {
	if handler == nil {
		return &speechSubscription{}
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	b.next++
	b.handlers[b.next] = handler
	return &speechSubscription{bus: b, id: b.next}
}

// Publish implements ISpeechLifecycleBus.
//
// A panicking subscriber must not stop the others. On a live call these events
// are how anything knows to stop talking; one bad handler silencing the bus
// turns a bug in a metrics sink into an assistant that talks over the caller.
func (b *InMemorySpeechLifecycleBus) Publish(event any) {
	b.mu.RLock()
	handlers := make([]func(any), 0, len(b.handlers))
	for _, h := range b.handlers {
		handlers = append(handlers, h)
	}
	b.mu.RUnlock()

	for _, h := range handlers {
		func() {
			defer func() { _ = recover() }()
			h(event)
		}()
	}
}

// ─────────────────────────────────────────────────────────────────────────────
// Telemetry

// VoiceLoopTelemetry aggregates per-stage latency across calls.
type VoiceLoopTelemetry struct {
	mu      sync.Mutex
	tracker *LatencyTracker
	turns   int
}

// NewVoiceLoopTelemetry returns an empty telemetry sink.
func NewVoiceLoopTelemetry() *VoiceLoopTelemetry {
	return &VoiceLoopTelemetry{tracker: NewLatencyTracker()}
}

// RecordStage adds one stage measurement.
func (t *VoiceLoopTelemetry) RecordStage(stage LatencyStage, d time.Duration) {
	t.tracker.Record(stage, d)
}

// RecordTurn counts a completed turn.
func (t *VoiceLoopTelemetry) RecordTurn() {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.turns++
}

// Turns returns how many turns have completed.
func (t *VoiceLoopTelemetry) Turns() int {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.turns
}

// Snapshot returns the percentiles for a stage.
func (t *VoiceLoopTelemetry) Snapshot(stage LatencyStage) LatencySnapshot {
	return t.tracker.Snapshot(stage)
}

// ─────────────────────────────────────────────────────────────────────────────
// Tools, and a breaker in front of them

// ToolBreakerState is what the breaker is doing.
type ToolBreakerState int

const (
	ToolBreakerClosed ToolBreakerState = iota
	ToolBreakerOpen
	ToolBreakerHalfOpen
)

func (s ToolBreakerState) String() string {
	switch s {
	case ToolBreakerOpen:
		return "open"
	case ToolBreakerHalfOpen:
		return "half-open"
	}
	return "closed"
}

// ToolCallPolicy is what one tool is allowed.
type ToolCallPolicy struct {
	FailureThreshold int
	OpenDuration     time.Duration
	Timeout          time.Duration
}

// DefaultToolCallPolicy returns the measured settings.
func DefaultToolCallPolicy() ToolCallPolicy {
	return ToolCallPolicy{FailureThreshold: 3, OpenDuration: 30 * time.Second, Timeout: 8 * time.Second}
}

// DefaultToolCallRegistry holds tools by name.
type DefaultToolCallRegistry struct {
	mu       sync.RWMutex
	handlers map[string]func(ctx context.Context, argsJSON string) (string, error)
}

// NewDefaultToolCallRegistry returns an empty registry.
func NewDefaultToolCallRegistry() *DefaultToolCallRegistry {
	return &DefaultToolCallRegistry{handlers: map[string]func(context.Context, string) (string, error){}}
}

// RegisterLocal adds an in-process tool.
func (r *DefaultToolCallRegistry) RegisterLocal(name string, handler func(ctx context.Context, argsJSON string) (string, error)) error {
	if strings.TrimSpace(name) == "" || handler == nil {
		return errors.New("tool name and handler are required")
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	r.handlers[name] = handler
	return nil
}

// Invoke runs a tool.
func (r *DefaultToolCallRegistry) Invoke(ctx context.Context, name, argsJSON string) (string, error) {
	r.mu.RLock()
	h, ok := r.handlers[name]
	r.mu.RUnlock()
	if !ok {
		return "", fmt.Errorf("no tool named %q", name)
	}
	return h(ctx, argsJSON)
}

// Count returns how many tools are registered.
func (r *DefaultToolCallRegistry) Count() int {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return len(r.handlers)
}

type breakerEntry struct {
	failures int
	openedAt time.Time
}

// CircuitBreakerToolRegistry wraps a registry so a failing tool stops being
// called.
//
// On a phone call this matters more than in a request handler: a tool that
// takes thirty seconds to time out is thirty seconds of a person listening to
// nothing, and retrying it three times is a minute and a half. Open the breaker
// and answer without it.
type CircuitBreakerToolRegistry struct {
	mu       sync.Mutex
	inner    *DefaultToolCallRegistry
	policies map[string]ToolCallPolicy
	state    map[string]*breakerEntry
	now      func() time.Time
}

// NewCircuitBreakerToolRegistry wraps inner.
func NewCircuitBreakerToolRegistry(inner *DefaultToolCallRegistry) *CircuitBreakerToolRegistry {
	return &CircuitBreakerToolRegistry{
		inner:    inner,
		policies: map[string]ToolCallPolicy{},
		state:    map[string]*breakerEntry{},
		now:      time.Now,
	}
}

// SetPolicy sets the policy for one tool.
func (r *CircuitBreakerToolRegistry) SetPolicy(toolName string, policy ToolCallPolicy) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.policies[toolName] = policy
}

// GetState returns the breaker state for a tool.
func (r *CircuitBreakerToolRegistry) GetState(toolName string) ToolBreakerState {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.stateOf(toolName, r.now())
}

func (r *CircuitBreakerToolRegistry) stateOf(toolName string, now time.Time) ToolBreakerState {
	e, ok := r.state[toolName]
	if !ok {
		return ToolBreakerClosed
	}
	p, ok := r.policies[toolName]
	if !ok {
		p = DefaultToolCallPolicy()
	}
	if e.failures < p.FailureThreshold {
		return ToolBreakerClosed
	}
	if now.Sub(e.openedAt) >= p.OpenDuration {
		// One attempt is allowed through. Half-open rather than closed: closing
		// outright would send every queued call at a service that has not
		// recovered.
		return ToolBreakerHalfOpen
	}
	return ToolBreakerOpen
}

// Invoke runs a tool unless its breaker is open.
func (r *CircuitBreakerToolRegistry) Invoke(ctx context.Context, name, argsJSON string) (string, error) {
	now := r.now()
	r.mu.Lock()
	st := r.stateOf(name, now)
	p, ok := r.policies[name]
	if !ok {
		p = DefaultToolCallPolicy()
	}
	r.mu.Unlock()

	if st == ToolBreakerOpen {
		return "", fmt.Errorf("tool %q is unavailable (breaker open)", name)
	}

	callCtx := ctx
	if p.Timeout > 0 {
		var cancel context.CancelFunc
		callCtx, cancel = context.WithTimeout(ctx, p.Timeout)
		defer cancel()
	}

	out, err := r.inner.Invoke(callCtx, name, argsJSON)

	r.mu.Lock()
	defer r.mu.Unlock()
	e, exists := r.state[name]
	if !exists {
		e = &breakerEntry{}
		r.state[name] = e
	}
	if err != nil {
		e.failures++
		if e.failures == p.FailureThreshold {
			e.openedAt = now
		}
		return "", err
	}
	e.failures = 0
	return out, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Telling the caller a tool is running

// ToolProgressUpdate is one progress report.
type ToolProgressUpdate struct {
	ToolName string
	Message  string
	// 0..1, or negative when the tool cannot say. A fake progress bar on a phone
	// call is worse than none: the caller hears a number and expects it to mean
	// something.
	Fraction float64
}

// IToolProgressSink receives progress.
type IToolProgressSink interface {
	Report(update ToolProgressUpdate)
}

// RecordingToolProgressSink keeps updates for later inspection.
type RecordingToolProgressSink struct {
	mu      sync.Mutex
	updates []ToolProgressUpdate
}

// NewRecordingToolProgressSink returns an empty sink.
func NewRecordingToolProgressSink() *RecordingToolProgressSink {
	return &RecordingToolProgressSink{}
}

// Report implements IToolProgressSink.
func (s *RecordingToolProgressSink) Report(update ToolProgressUpdate) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.updates = append(s.updates, update)
}

// Updates returns what was recorded.
func (s *RecordingToolProgressSink) Updates() []ToolProgressUpdate {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([]ToolProgressUpdate, len(s.updates))
	copy(out, s.updates)
	return out
}

// SpokenToolProgressSink says the update out loud, throttled.
//
// Without the throttle a chatty tool turns into an assistant that narrates a
// progress bar.
type SpokenToolProgressSink struct {
	mu          sync.Mutex
	speak       func(text string)
	minInterval time.Duration
	last        time.Time
	now         func() time.Time
}

// NewSpokenToolProgressSink returns a throttled speaking sink.
func NewSpokenToolProgressSink(speak func(text string), minInterval time.Duration) *SpokenToolProgressSink {
	if minInterval <= 0 {
		minInterval = 4 * time.Second
	}
	return &SpokenToolProgressSink{speak: speak, minInterval: minInterval, now: time.Now}
}

// Report implements IToolProgressSink.
func (s *SpokenToolProgressSink) Report(update ToolProgressUpdate) {
	if s.speak == nil || strings.TrimSpace(update.Message) == "" {
		return
	}
	s.mu.Lock()
	now := s.now()
	if !s.last.IsZero() && now.Sub(s.last) < s.minInterval {
		s.mu.Unlock()
		return
	}
	s.last = now
	s.mu.Unlock()
	s.speak(update.Message)
}

// StreamingToolRunner invokes a tool while reporting progress.
type StreamingToolRunner struct {
	registry *CircuitBreakerToolRegistry
	sink     IToolProgressSink
}

// NewStreamingToolRunner returns a runner.
func NewStreamingToolRunner(registry *CircuitBreakerToolRegistry, sink IToolProgressSink) *StreamingToolRunner {
	return &StreamingToolRunner{registry: registry, sink: sink}
}

// Invoke runs the tool, reporting start and finish.
func (r *StreamingToolRunner) Invoke(ctx context.Context, name, argsJSON string) (string, error) {
	if r.sink != nil {
		r.sink.Report(ToolProgressUpdate{ToolName: name, Message: "working on that", Fraction: -1})
	}
	out, err := r.registry.Invoke(ctx, name, argsJSON)
	if r.sink != nil && err == nil {
		r.sink.Report(ToolProgressUpdate{ToolName: name, Message: "done", Fraction: 1})
	}
	return out, err
}

// ─────────────────────────────────────────────────────────────────────────────
// Speculation

// SpeculativeBranch is a guess at what the caller will say and the reply to it.
type SpeculativeBranch struct {
	PredictedInput string
	Response       string
	Probability    float64
}

// ISpeculativeGenerator starts generating the likely reply early.
type ISpeculativeGenerator interface {
	AddBranch(branch SpeculativeBranch) bool
	// Resolve returns the response for an utterance that matches a branch, and
	// "" when none does.
	Resolve(actualInput string) string
	Discard()
}

// DefaultSpeculativeGenerator is the default.
//
// Worth it because the alternative is dead air: most turns in a scripted call
// are predictable — "yes", "no", a date — and being wrong costs only the tokens.
// Being right removes the entire inference stage from the caller's experience.
type DefaultSpeculativeGenerator struct {
	mu          sync.Mutex
	branches    []SpeculativeBranch
	maxBranches int
	threshold   float64
}

// NewDefaultSpeculativeGenerator returns a generator holding at most
// maxBranches, matching at or above threshold similarity.
func NewDefaultSpeculativeGenerator(maxBranches int, threshold float64) *DefaultSpeculativeGenerator {
	if maxBranches <= 0 {
		maxBranches = 3
	}
	if threshold <= 0 {
		threshold = 0.85
	}
	return &DefaultSpeculativeGenerator{maxBranches: maxBranches, threshold: threshold}
}

// AddBranch implements ISpeculativeGenerator.
func (g *DefaultSpeculativeGenerator) AddBranch(branch SpeculativeBranch) bool {
	if strings.TrimSpace(branch.PredictedInput) == "" {
		return false
	}
	g.mu.Lock()
	defer g.mu.Unlock()
	if len(g.branches) >= g.maxBranches {
		return false
	}
	g.branches = append(g.branches, branch)
	return true
}

// Resolve implements ISpeculativeGenerator.
//
// Branches are DISCARDED, never spoken, when the real utterance does not match.
// Speaking a speculated answer to a question that was not asked is the one
// failure mode that makes this unusable.
func (g *DefaultSpeculativeGenerator) Resolve(actualInput string) string {
	g.mu.Lock()
	defer g.mu.Unlock()
	best, bestScore := "", 0.0
	for _, b := range g.branches {
		if s := similarity(b.PredictedInput, actualInput); s >= g.threshold && s > bestScore {
			best, bestScore = b.Response, s
		}
	}
	g.branches = nil
	return best
}

// Discard implements ISpeculativeGenerator.
func (g *DefaultSpeculativeGenerator) Discard() {
	g.mu.Lock()
	defer g.mu.Unlock()
	g.branches = nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Handing the call on

// CallAgent is one agent configuration a call can be handed to.
type CallAgent struct {
	AgentID      string
	Name         string
	SystemPrompt string
	VoiceID      string
	Skills       []string
}

// HandoffResult is what happened.
type HandoffResult struct {
	Succeeded bool
	ToAgentID string
	// Always populated. A handoff that failed silently leaves the caller with an
	// agent that does not know why it is there.
	Reason string
	At     time.Time
}

// IAgentHandoffOrchestrator moves a live call between agent configurations.
type IAgentHandoffOrchestrator interface {
	Register(agent CallAgent) error
	Handoff(ctx context.Context, callID, targetAgentID, reason string) HandoffResult
}

// DefaultAgentHandoffOrchestrator is the default orchestrator.
type DefaultAgentHandoffOrchestrator struct {
	mu      sync.RWMutex
	agents  map[string]CallAgent
	current map[string]string
	now     func() time.Time
}

// NewDefaultAgentHandoffOrchestrator returns an empty orchestrator.
func NewDefaultAgentHandoffOrchestrator() *DefaultAgentHandoffOrchestrator {
	return &DefaultAgentHandoffOrchestrator{
		agents:  map[string]CallAgent{},
		current: map[string]string{},
		now:     time.Now,
	}
}

// Register implements IAgentHandoffOrchestrator.
func (o *DefaultAgentHandoffOrchestrator) Register(agent CallAgent) error {
	if strings.TrimSpace(agent.AgentID) == "" {
		return errors.New("agent id is required")
	}
	o.mu.Lock()
	defer o.mu.Unlock()
	o.agents[agent.AgentID] = agent
	return nil
}

// Handoff implements IAgentHandoffOrchestrator.
//
// An unknown target leaves the call where it is rather than ending it. A caller
// dropped because a configuration was missing is the worst outcome available
// here, and it is the easy one to write by accident.
func (o *DefaultAgentHandoffOrchestrator) Handoff(_ context.Context, callID, targetAgentID, reason string) HandoffResult {
	o.mu.Lock()
	defer o.mu.Unlock()
	if _, ok := o.agents[targetAgentID]; !ok {
		return HandoffResult{
			Succeeded: false,
			Reason:    fmt.Sprintf("no agent named %q; the call stays where it is", targetAgentID),
			At:        o.now(),
		}
	}
	o.current[callID] = targetAgentID
	return HandoffResult{Succeeded: true, ToAgentID: targetAgentID, Reason: reason, At: o.now()}
}

// CurrentAgent returns which agent holds a call, or "".
func (o *DefaultAgentHandoffOrchestrator) CurrentAgent(callID string) string {
	o.mu.RLock()
	defer o.mu.RUnlock()
	return o.current[callID]
}

// ─────────────────────────────────────────────────────────────────────────────
// Consulting a human

// ConsultRequest is a question put to a person mid-call.
type ConsultRequest struct {
	CallID   string
	Question string
	// How long the caller can reasonably be held. Past this the agent has to say
	// something rather than keep waiting in silence.
	Deadline time.Time
}

// ConsultAnswer is what came back.
type ConsultAnswer struct {
	Answered bool
	Text     string
	At       time.Time
}

// IConsultChannel reaches somebody who can answer.
type IConsultChannel interface {
	Consult(ctx context.Context, req ConsultRequest) (ConsultAnswer, error)
}

// HttpWebhookConsultChannel posts the question to a webhook.
//
// Escalating is not always a phone call — often it is a message to a human who
// will answer in a moment, which is why this is a channel rather than a
// transfer.
type HttpWebhookConsultChannel struct {
	post func(ctx context.Context, url, body string) (string, error)
	url  string
}

// NewHttpWebhookConsultChannel returns a channel over the host's HTTP.
func NewHttpWebhookConsultChannel(url string, post func(ctx context.Context, url, body string) (string, error)) *HttpWebhookConsultChannel {
	return &HttpWebhookConsultChannel{url: url, post: post}
}

// Consult implements IConsultChannel.
func (c *HttpWebhookConsultChannel) Consult(ctx context.Context, req ConsultRequest) (ConsultAnswer, error) {
	if c.post == nil {
		return ConsultAnswer{}, errors.New("no transport configured")
	}
	body, err := c.post(ctx, c.url, req.Question)
	if err != nil {
		return ConsultAnswer{}, err
	}
	return ConsultAnswer{Answered: strings.TrimSpace(body) != "", Text: body, At: time.Now()}, nil
}

// ConsultEscalator asks, and gives up when the deadline passes.
type ConsultEscalator struct {
	channel IConsultChannel
}

// NewConsultEscalator returns an escalator over a channel.
func NewConsultEscalator(channel IConsultChannel) *ConsultEscalator {
	return &ConsultEscalator{channel: channel}
}

// Escalate asks and returns whatever arrived before the deadline.
//
// An unanswered consult is a NORMAL outcome, not an error: nobody was at their
// desk. The agent needs to be able to say so and carry on.
func (e *ConsultEscalator) Escalate(ctx context.Context, req ConsultRequest) ConsultAnswer {
	if e.channel == nil {
		return ConsultAnswer{At: time.Now()}
	}
	if !req.Deadline.IsZero() {
		var cancel context.CancelFunc
		ctx, cancel = context.WithDeadline(ctx, req.Deadline)
		defer cancel()
	}
	answer, err := e.channel.Consult(ctx, req)
	if err != nil {
		return ConsultAnswer{At: time.Now()}
	}
	return answer
}

// ─────────────────────────────────────────────────────────────────────────────
// The voice loop, as a tool an agent can invoke

// VoiceLoopToolRequest is an outbound call an agent asked for.
type VoiceLoopToolRequest struct {
	ToE164    string
	Objective string
	Facts     []string
	// A hard cap. Every call this places ends by itself, so a loop that goes
	// wrong stops without anybody watching.
	MaxDuration time.Duration
}

// VoiceLoopToolResult is how it went.
type VoiceLoopToolResult struct {
	Succeeded  bool
	Outcome    string
	Transcript string
	Duration   time.Duration
	CostMicro  int64
}

// IVoiceLoopTool places a call on an agent's behalf.
type IVoiceLoopTool interface {
	Run(ctx context.Context, req VoiceLoopToolRequest) (VoiceLoopToolResult, error)
}

// VoiceLoopAsTool exposes an outbound call as a tool.
//
// The most dangerous tool in the system: it takes an action in the world that
// cannot be undone, at somebody else's phone. Both the objective and the
// duration cap are required, and a request missing either is refused rather
// than defaulted — a default objective is a call with no purpose, and a default
// cap is a call with no end.
type VoiceLoopAsTool struct {
	carrier ITelephonyCarrier
}

// NewVoiceLoopAsTool returns the tool over a carrier.
func NewVoiceLoopAsTool(carrier ITelephonyCarrier) *VoiceLoopAsTool {
	return &VoiceLoopAsTool{carrier: carrier}
}

// Run implements IVoiceLoopTool.
func (t *VoiceLoopAsTool) Run(_ context.Context, req VoiceLoopToolRequest) (VoiceLoopToolResult, error) {
	if strings.TrimSpace(req.ToE164) == "" {
		return VoiceLoopToolResult{}, errors.New("a destination number is required")
	}
	if strings.TrimSpace(req.Objective) == "" {
		return VoiceLoopToolResult{}, errors.New("an objective is required: a call with no purpose has no way to end")
	}
	if req.MaxDuration <= 0 {
		return VoiceLoopToolResult{}, errors.New("a maximum duration is required: an uncapped call is an uncapped bill")
	}
	if t.carrier == nil {
		return VoiceLoopToolResult{}, errors.New("no carrier configured")
	}
	return VoiceLoopToolResult{Outcome: "not dialled: no session runner wired"}, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Recording

// StereoCallRecorder records the caller on one channel and the agent on the
// other.
//
// STEREO IS THE POINT. A mixed mono recording cannot answer "who spoke over
// whom", which is the question every review of a bad call turns out to be
// asking. Two channels make interruptions visible in the waveform.
type StereoCallRecorder struct {
	mu           sync.Mutex
	caller       []byte
	agent        []byte
	sampleRateHz int
}

// NewStereoCallRecorder returns a recorder.
func NewStereoCallRecorder(sampleRateHz int) *StereoCallRecorder {
	if sampleRateHz <= 0 {
		sampleRateHz = 16000
	}
	return &StereoCallRecorder{sampleRateHz: sampleRateHz}
}

// WriteCaller appends caller audio.
func (r *StereoCallRecorder) WriteCaller(pcm []byte) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.caller = append(r.caller, pcm...)
}

// WriteAgent appends agent audio.
func (r *StereoCallRecorder) WriteAgent(pcm []byte) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.agent = append(r.agent, pcm...)
}

// Finish returns interleaved stereo PCM-16 with a RIFF header.
//
// The shorter channel is padded with silence rather than truncating the longer
// one: a recording that ends when the quieter party stopped talking loses the
// end of the conversation.
func (r *StereoCallRecorder) Finish() []byte {
	r.mu.Lock()
	defer r.mu.Unlock()

	n := len(r.caller)
	if len(r.agent) > n {
		n = len(r.agent)
	}
	n -= n % 2
	frames := n / 2
	data := make([]byte, frames*4)
	for i := 0; i < frames; i++ {
		var l, a int16
		if i*2+1 < len(r.caller) {
			l = int16(binary.LittleEndian.Uint16(r.caller[i*2:]))
		}
		if i*2+1 < len(r.agent) {
			a = int16(binary.LittleEndian.Uint16(r.agent[i*2:]))
		}
		binary.LittleEndian.PutUint16(data[i*4:], uint16(l))
		binary.LittleEndian.PutUint16(data[i*4+2:], uint16(a))
	}
	return wavWrapStereo(data, r.sampleRateHz)
}

func wavWrapStereo(data []byte, sampleRateHz int) []byte {
	const channels, bits = 2, 16
	byteRate := sampleRateHz * channels * bits / 8
	blockAlign := channels * bits / 8
	out := make([]byte, 44+len(data))
	copy(out[0:], "RIFF")
	binary.LittleEndian.PutUint32(out[4:], uint32(36+len(data)))
	copy(out[8:], "WAVEfmt ")
	binary.LittleEndian.PutUint32(out[16:], 16)
	binary.LittleEndian.PutUint16(out[20:], 1)
	binary.LittleEndian.PutUint16(out[22:], channels)
	binary.LittleEndian.PutUint32(out[24:], uint32(sampleRateHz))
	binary.LittleEndian.PutUint32(out[28:], uint32(byteRate))
	binary.LittleEndian.PutUint16(out[32:], uint16(blockAlign))
	binary.LittleEndian.PutUint16(out[34:], bits)
	copy(out[36:], "data")
	binary.LittleEndian.PutUint32(out[40:], uint32(len(data)))
	copy(out[44:], data)
	return out
}

// ─────────────────────────────────────────────────────────────────────────────
// Evaluation

// EvalTurn is one scripted turn.
type EvalTurn struct {
	CallerSays string
	Expect     string
}

// EvalTurnResult is how one turn went.
type EvalTurnResult struct {
	Turn      EvalTurn
	AgentSaid string
	Score     float64
	// Always populated. A score with no justification cannot be argued with,
	// and every eval eventually comes down to somebody disagreeing with one.
	Reason string
}

// EvalSession is a scripted call replayed against the agent, so a prompt change
// can be measured rather than guessed at.
type EvalSession struct {
	ScenarioID string
	Turns      []EvalTurn
}

// NewEvalSession returns a session.
func NewEvalSession(scenarioID string, turns ...EvalTurn) *EvalSession {
	return &EvalSession{ScenarioID: scenarioID, Turns: turns}
}

// AddTurn appends a turn.
func (s *EvalSession) AddTurn(turn EvalTurn) {
	s.Turns = append(s.Turns, turn)
}

// TurnCount returns how many turns the session has.
func (s *EvalSession) TurnCount() int { return len(s.Turns) }

// EvalRunResult is the whole run.
type EvalRunResult struct {
	ScenarioID string
	Results    []EvalTurnResult
	MeanScore  float64
	// The worst turn, kept separately. A run whose mean is fine because nine
	// turns were perfect and one was catastrophic is not a run that passed.
	WorstScore float64
	At         time.Time
}

// JudgeDimension is one axis a verdict scores on.
type JudgeDimension string

const (
	JudgeAccuracy    JudgeDimension = "accuracy"
	JudgeHelpfulness JudgeDimension = "helpfulness"
	JudgeTone        JudgeDimension = "tone"
	JudgeSafety      JudgeDimension = "safety"
	JudgeBrevity     JudgeDimension = "brevity"
)

// JudgeVerdict is one judgement.
type JudgeVerdict struct {
	Dimension JudgeDimension
	Score     float64
	Reason    string
}

// LlmJudge scores a session against a rubric.
type LlmJudge struct {
	judge func(ctx context.Context, session *EvalSession, rubric string) ([]JudgeVerdict, error)
}

// NewLlmJudge returns a judge over the host's generator.
func NewLlmJudge(judge func(ctx context.Context, session *EvalSession, rubric string) ([]JudgeVerdict, error)) *LlmJudge {
	return &LlmJudge{judge: judge}
}

// Judge scores the session.
//
// Multi-dimensional rather than one number, because the dimensions trade
// against each other: a shorter reply scores better on brevity and worse on
// helpfulness, and collapsing them hides the trade somebody has to make.
func (j *LlmJudge) Judge(ctx context.Context, session *EvalSession, rubric string) ([]JudgeVerdict, error) {
	if j.judge == nil {
		return nil, errors.New("no judge configured")
	}
	return j.judge(ctx, session, rubric)
}

// ─────────────────────────────────────────────────────────────────────────────
// Dev tunnels

// ILocalDevTunnel gives a machine with no public address one, so a carrier
// webhook can reach a laptop.
type ILocalDevTunnel interface {
	// PublicURL returns "" when there is no tunnel.
	PublicURL() string
}

// NullLocalDevTunnel has no tunnel.
//
// THE DEFAULT. A tunnel is a hole into a development machine, and one should
// never appear because nobody configured anything.
type NullLocalDevTunnel struct{}

// PublicURL implements ILocalDevTunnel.
func (NullLocalDevTunnel) PublicURL() string { return "" }

// StaticLocalDevTunnel is a URL somebody already established.
type StaticLocalDevTunnel struct{ URL string }

// PublicURL implements ILocalDevTunnel.
func (t StaticLocalDevTunnel) PublicURL() string { return t.URL }

// NgrokTunnel is a URL from an ngrok session the host started.
type NgrokTunnel struct{ URL string }

// PublicURL implements ILocalDevTunnel.
func (t NgrokTunnel) PublicURL() string { return t.URL }

// CloudflareTunnel is a URL from a cloudflared session the host started.
type CloudflareTunnel struct{ URL string }

// PublicURL implements ILocalDevTunnel.
func (t CloudflareTunnel) PublicURL() string { return t.URL }

// ─────────────────────────────────────────────────────────────────────────────
// MCP tool import

// McpServerConfig points at an MCP server.
type McpServerConfig struct {
	Name    string
	URL     string
	Headers map[string]string
}

// McpToolDescriptor is one tool the server offers.
type McpToolDescriptor struct {
	Name            string
	Description     string
	InputSchemaJSON string
}

// IMcpToolImporter lists what a server offers.
type IMcpToolImporter interface {
	ListTools(ctx context.Context, cfg McpServerConfig) ([]McpToolDescriptor, error)
}

// HttpMcpToolImporter imports over HTTP.
//
// Imported tools arrive DISABLED. A catalogue that could enable itself is a
// catalogue that decides what this device can do.
type HttpMcpToolImporter struct {
	get func(ctx context.Context, url string, headers map[string]string) (string, error)
}

// NewHttpMcpToolImporter returns an importer over the host's HTTP.
func NewHttpMcpToolImporter(get func(ctx context.Context, url string, headers map[string]string) (string, error)) *HttpMcpToolImporter {
	return &HttpMcpToolImporter{get: get}
}

// ListTools implements IMcpToolImporter.
func (i *HttpMcpToolImporter) ListTools(ctx context.Context, cfg McpServerConfig) ([]McpToolDescriptor, error) {
	if i.get == nil {
		return nil, errors.New("no transport configured")
	}
	if _, err := i.get(ctx, cfg.URL, cfg.Headers); err != nil {
		return nil, err
	}
	return nil, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Dashboard

// LiveCallRow is one call in progress.
type LiveCallRow struct {
	CallID    string
	FromE164  string
	AgentID   string
	StartedAt time.Time
	Duration  time.Duration
	CostMicro int64
}

// RecentCallRow is one finished call.
type RecentCallRow struct {
	CallID    string
	FromE164  string
	AgentID   string
	EndedAt   time.Time
	Duration  time.Duration
	CostMicro int64
	Outcome   string
}

// AgentHealthRow is how one agent is doing.
type AgentHealthRow struct {
	AgentID string
	Calls   int
	// -1 when nothing has been recorded, for the same reason the interruption
	// tracker does it: a fresh agent must not read as a perfect one.
	SuccessRate  float64
	P95LatencyMs float64
}

// DashboardSummary is the headline numbers.
type DashboardSummary struct {
	LiveCalls       int
	CallsToday      int
	SpendTodayMicro int64
	MeanDuration    time.Duration
}

// DashboardSnapshot is everything the dashboard shows at one moment.
type DashboardSnapshot struct {
	Summary DashboardSummary
	Live    []LiveCallRow
	Recent  []RecentCallRow
	Agents  []AgentHealthRow
	At      time.Time
}

// IDashboardDataSource supplies the snapshot.
type IDashboardDataSource interface {
	Snapshot(ctx context.Context) (DashboardSnapshot, error)
}

// DefaultDashboardDataSource is the in-memory source.
type DefaultDashboardDataSource struct {
	mu     sync.Mutex
	live   map[string]LiveCallRow
	recent []RecentCallRow
	now    func() time.Time
}

// NewDefaultDashboardDataSource returns an empty source.
func NewDefaultDashboardDataSource() *DefaultDashboardDataSource {
	return &DefaultDashboardDataSource{live: map[string]LiveCallRow{}, now: time.Now}
}

// CallStarted records a call beginning.
func (d *DefaultDashboardDataSource) CallStarted(row LiveCallRow) {
	d.mu.Lock()
	defer d.mu.Unlock()
	d.live[row.CallID] = row
}

// CallEnded moves a call to recent.
func (d *DefaultDashboardDataSource) CallEnded(row RecentCallRow) {
	d.mu.Lock()
	defer d.mu.Unlock()
	delete(d.live, row.CallID)
	d.recent = append(d.recent, row)
	// Bounded: a dashboard that grows without limit is a memory leak with a
	// user interface.
	if len(d.recent) > 500 {
		d.recent = d.recent[len(d.recent)-500:]
	}
}

// Snapshot implements IDashboardDataSource.
func (d *DefaultDashboardDataSource) Snapshot(_ context.Context) (DashboardSnapshot, error) {
	d.mu.Lock()
	defer d.mu.Unlock()

	live := make([]LiveCallRow, 0, len(d.live))
	for _, r := range d.live {
		live = append(live, r)
	}
	sort.Slice(live, func(i, j int) bool { return live[i].StartedAt.Before(live[j].StartedAt) })

	recent := make([]RecentCallRow, len(d.recent))
	copy(recent, d.recent)

	var spend int64
	var total time.Duration
	for _, r := range recent {
		spend += r.CostMicro
		total += r.Duration
	}
	mean := time.Duration(0)
	if len(recent) > 0 {
		mean = total / time.Duration(len(recent))
	}

	return DashboardSnapshot{
		Summary: DashboardSummary{
			LiveCalls:       len(live),
			CallsToday:      len(recent),
			SpendTodayMicro: spend,
			MeanDuration:    mean,
		},
		Live:   live,
		Recent: recent,
		At:     d.now(),
	}, nil
}

// ─────────────────────────────────────────────────────────────────────────────
// Test session

// TestCallSession is a call session that records instead of dialling.
//
// What the loop is tested against, and the reason no test can place a real
// call: it satisfies the same seam without a carrier behind it.
type TestCallSession struct {
	mu     sync.Mutex
	callID string
	sent   [][]byte
	closed bool
}

// NewTestCallSession returns a session.
func NewTestCallSession(callID string) *TestCallSession {
	return &TestCallSession{callID: callID}
}

// CallID returns the id.
func (s *TestCallSession) CallID() string { return s.callID }

// SendAudio records the audio.
func (s *TestCallSession) SendAudio(_ context.Context, pcm []byte) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.closed {
		return errors.New("session is closed")
	}
	s.sent = append(s.sent, append([]byte(nil), pcm...))
	return nil
}

// Hangup closes the session.
func (s *TestCallSession) Hangup(_ context.Context) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.closed = true
	return nil
}

// SentFrames returns what was sent.
func (s *TestCallSession) SentFrames() [][]byte {
	s.mu.Lock()
	defer s.mu.Unlock()
	out := make([][]byte, len(s.sent))
	copy(out, s.sent)
	return out
}

// ─────────────────────────────────────────────────────────────────────────────
// Wiring

// TelephonyServiceCollectionExtensions is the registration surface a host uses
// to wire the telephony stack.
//
// A type rather than the C#'s extension methods, because Go has none. What it
// carries is the DEFAULTS — and every one of them is the safe end: a null
// carrier that dials nobody, a null tunnel with no hole into the machine, a
// disabled command path.
type TelephonyServiceCollectionExtensions struct{}

// Defaults returns the components a host gets when it wires nothing.
func (TelephonyServiceCollectionExtensions) Defaults() (ITelephonyCarrier, ILocalDevTunnel) {
	return NullTelephonyCarrierInstance, NullLocalDevTunnel{}
}
