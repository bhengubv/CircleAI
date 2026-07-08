// server_openai_models.go
//
// Ports the CircleAI.Inference.Server wire DTOs + support types:
//   ChatCompletion.cs        → ChatCompletionRequest/Message/Response/Choice/
//                              UsageInfo/StreamChunk/StreamChoice/Delta
//   Embeddings.cs            → EmbeddingsRequest/Response/Datum
//   ErrorResponse.cs         → ErrorResponse/ErrorBody (+ ErrorResponseOf)
//   ServerCounters.cs        → ServerCounters
//   AdmissionControl.cs      → AdmissionControl (+ AdmissionSlot)
//   Companion/CompanionDtos.cs → CompanionTurnRequest/Response
//
// JSON tags match the C# [JsonPropertyName] attributes so the on-wire shape is
// byte-compatible with OpenAI-targeting SDKs (only a base-URL change needed).

package circleai

import (
	"encoding/json"
	"sync/atomic"
	"time"
)

// ── /v1/chat/completions ─────────────────────────────────────────────────────

// ChatCompletionRequest is the OpenAI-shaped chat-completion request body.
type ChatCompletionRequest struct {
	Model       string                  `json:"model"`
	Messages    []ChatCompletionMessage `json:"messages"`
	Temperature *float32                `json:"temperature,omitempty"`
	TopP        *float32                `json:"top_p,omitempty"`
	MaxTokens   *int                    `json:"max_tokens,omitempty"`
	Stream      bool                    `json:"stream"`
	Stop        []string                `json:"stop,omitempty"`
	User        *string                 `json:"user,omitempty"`
}

// ChatCompletionMessage is one message in the conversation. ReasoningContent is
// omitted from JSON when empty so non-reasoning models stay byte-stable.
type ChatCompletionMessage struct {
	Role             string  `json:"role"`
	Content          string  `json:"content"`
	Name             *string `json:"name,omitempty"`
	ReasoningContent *string `json:"reasoning_content,omitempty"`
}

// ChatCompletionResponse is the OpenAI-shaped successful response.
type ChatCompletionResponse struct {
	ID      string                 `json:"id"`
	Object  string                 `json:"object"`
	Created int64                  `json:"created"`
	Model   string                 `json:"model"`
	Choices []ChatCompletionChoice `json:"choices"`
	Usage   UsageInfo              `json:"usage"`
}

// ChatCompletionChoice is one choice in a non-streaming response.
type ChatCompletionChoice struct {
	Index        int                   `json:"index"`
	Message      ChatCompletionMessage `json:"message"`
	FinishReason string                `json:"finish_reason"`
}

// UsageInfo is the token-usage block.
type UsageInfo struct {
	PromptTokens     int `json:"prompt_tokens"`
	CompletionTokens int `json:"completion_tokens"`
	TotalTokens      int `json:"total_tokens"`
}

// ChatCompletionStreamChunk is one SSE delta frame in a streamed completion.
type ChatCompletionStreamChunk struct {
	ID      string                       `json:"id"`
	Object  string                       `json:"object"`
	Created int64                        `json:"created"`
	Model   string                       `json:"model"`
	Choices []ChatCompletionStreamChoice `json:"choices"`
}

// ChatCompletionStreamChoice is one delta in a streamed chunk.
type ChatCompletionStreamChoice struct {
	Index        int                 `json:"index"`
	Delta        ChatCompletionDelta `json:"delta"`
	FinishReason *string             `json:"finish_reason,omitempty"`
}

// ChatCompletionDelta is the delta payload — only non-empty fields are emitted.
type ChatCompletionDelta struct {
	Role             *string `json:"role,omitempty"`
	Content          *string `json:"content,omitempty"`
	ReasoningContent *string `json:"reasoning_content,omitempty"`
}

// NewChatCompletionResponseObject returns a response with the default Object tag.
func NewChatCompletionResponseObject() string { return "chat.completion" }

// ── /v1/embeddings ───────────────────────────────────────────────────────────

// EmbeddingsRequest is the OpenAI-shaped embeddings request. Input is either a
// single string or an array of strings (raw JSON, normalised at the endpoint).
type EmbeddingsRequest struct {
	Model string          `json:"model"`
	Input json.RawMessage `json:"input"`
	User  *string         `json:"user,omitempty"`
}

// EmbeddingsResponse is the OpenAI-shaped embeddings response.
type EmbeddingsResponse struct {
	Object string          `json:"object"`
	Data   []EmbeddingDatum `json:"data"`
	Model  string          `json:"model"`
	Usage  UsageInfo       `json:"usage"`
}

// EmbeddingDatum is one embedding row in the response.
type EmbeddingDatum struct {
	Object    string    `json:"object"`
	Index     int       `json:"index"`
	Embedding []float32 `json:"embedding"`
}

// ── error envelope ───────────────────────────────────────────────────────────

// ErrorResponse is the OpenAI-shaped error envelope: {"error": {...}}.
type ErrorResponse struct {
	Error ErrorBody `json:"error"`
}

// ErrorBody is the inner error body.
type ErrorBody struct {
	Message string  `json:"message"`
	Type    string  `json:"type"`
	Param   *string `json:"param,omitempty"`
	Code    *string `json:"code,omitempty"`
}

// ErrorResponseOf builds an ErrorResponse. Ports ErrorResponse.Of. An empty code
// is emitted as null (omitted).
func ErrorResponseOf(message, typ, code string) ErrorResponse {
	body := ErrorBody{Message: message, Type: typ}
	if code != "" {
		c := code
		body.Code = &c
	}
	return ErrorResponse{Error: body}
}

// ── companion DTOs ───────────────────────────────────────────────────────────

// CompanionTurnRequest is the POST /v1/companion/turn request body.
type CompanionTurnRequest struct {
	SessionID  string `json:"session_id"`
	IdentityID string `json:"identity_id"`
	Message    string `json:"message"`
	Stream     bool   `json:"stream"`
	Agentic    bool   `json:"agentic"`
}

// CompanionTurnResponse is the POST /v1/companion/turn response body.
type CompanionTurnResponse struct {
	SessionID string `json:"session_id"`
	Reply     string `json:"reply"`
	Agentic   bool   `json:"agentic"`
	TurnIndex int    `json:"turn_index"`
}

// ── server counters ──────────────────────────────────────────────────────────

// ServerCounters holds thread-safe server-wide counters for diagnostics. Ports
// CircleAI.Inference.Server.Models.ServerCounters.
type ServerCounters struct {
	total     int64
	rejected  int64
	failed    int64
	active    int64
	startedAt time.Time
}

// NewServerCounters builds a counters instance stamped with the current UTC time.
func NewServerCounters() *ServerCounters {
	return &ServerCounters{startedAt: time.Now().UTC()}
}

// StartedAt is the UTC time the counters (server) started.
func (c *ServerCounters) StartedAt() time.Time { return c.startedAt }

// TotalRequests is the total accepted (including those that later failed).
func (c *ServerCounters) TotalRequests() int64 { return atomic.LoadInt64(&c.total) }

// RejectedRequests is the count rejected at admission.
func (c *ServerCounters) RejectedRequests() int64 { return atomic.LoadInt64(&c.rejected) }

// FailedRequests is the count that admitted but failed downstream.
func (c *ServerCounters) FailedRequests() int64 { return atomic.LoadInt64(&c.failed) }

// ActiveRequests is the count currently in flight.
func (c *ServerCounters) ActiveRequests() int64 { return atomic.LoadInt64(&c.active) }

// AccountAdmitted marks a request accepted (admission passed).
func (c *ServerCounters) AccountAdmitted() {
	atomic.AddInt64(&c.total, 1)
	atomic.AddInt64(&c.active, 1)
}

// AccountCompleted marks a request completed.
func (c *ServerCounters) AccountCompleted() { atomic.AddInt64(&c.active, -1) }

// AccountRejected marks a request rejected at admission (not counted in total).
func (c *ServerCounters) AccountRejected() { atomic.AddInt64(&c.rejected, 1) }

// AccountFailed marks a request failed downstream.
func (c *ServerCounters) AccountFailed() { atomic.AddInt64(&c.failed, 1) }

// ── admission control ────────────────────────────────────────────────────────

// AdmissionControl is a bounded admission gate — at most MaxConcurrentRequests
// in flight, excess rejected immediately (no queueing). Ports
// CircleAI.Inference.Server.Hosting.AdmissionControl.
type AdmissionControl struct {
	max      int
	counters *ServerCounters
	sem      chan struct{}
}

// NewAdmissionControl builds a gate with the given cap (floored at 1) and counters.
func NewAdmissionControl(maxConcurrentRequests int, counters *ServerCounters) *AdmissionControl {
	if maxConcurrentRequests < 1 {
		maxConcurrentRequests = 1
	}
	return &AdmissionControl{
		max:      maxConcurrentRequests,
		counters: counters,
		sem:      make(chan struct{}, maxConcurrentRequests),
	}
}

// MaxConcurrentRequests is the admitted-at-once cap.
func (a *AdmissionControl) MaxConcurrentRequests() int { return a.max }

// AdmissionSlot is a held admission slot. Release exactly once (idempotent).
type AdmissionSlot struct {
	parent   *AdmissionControl
	released int32
}

// TryEnter attempts to acquire one slot. Returns the slot on success (the caller
// MUST Release it), or nil when saturated (the endpoint responds 503). Ports TryEnter.
func (a *AdmissionControl) TryEnter() *AdmissionSlot {
	select {
	case a.sem <- struct{}{}:
		if a.counters != nil {
			a.counters.AccountAdmitted()
		}
		return &AdmissionSlot{parent: a}
	default:
		if a.counters != nil {
			a.counters.AccountRejected()
		}
		return nil
	}
}

// Release frees the slot. Idempotent.
func (s *AdmissionSlot) Release() {
	if s == nil {
		return
	}
	if atomic.CompareAndSwapInt32(&s.released, 0, 1) {
		<-s.parent.sem
		if s.parent.counters != nil {
			s.parent.counters.AccountCompleted()
		}
	}
}
