// server_endpoints.go
//
// Ports the routing/handler logic of CircleAI.Inference.Server.Endpoints:
//   ChatCompletionsEndpoint.cs → HandleChatCompletion (+ BuildInferenceRequest, MapFinish)
//   EmbeddingsEndpoint.cs      → HandleEmbeddings (+ normaliseEmbeddingInput)
//   AdminEndpoints.cs          → AdminLoad / AdminUnload / AdminLifecycle
//                                (+ AdminLoadRequest / AdminLifecycleResponse)
//   CompanionEndpoint.cs       → HandleCompanionTurn (+ StreamCompanionReply)
//
// Per the port NOTE these are in-memory handlers behind the InferenceServer
// interface — no socket server is stood up. Each handler returns an
// EndpointResult (HTTP status + a JSON-serialisable body); streaming handlers
// return the ordered SSE frames as a slice + a terminator marker so the OpenAI
// SSE wire shape is fully reproduced and testable. Auth, admission gating,
// model resolution, finish-reason mapping, and error envelopes match the C#
// endpoints exactly.

package circleai

import (
	"context"
	"encoding/json"
	"strings"
	"time"

	"github.com/google/uuid"
)

// EndpointResult is a handler outcome: an HTTP status + a body to serialise as
// JSON. Body is nil for 204/empty responses. Mirrors ASP.NET's IResult.
type EndpointResult struct {
	StatusCode int
	Body       any
	// StreamFrames, when non-nil, is an ordered list of SSE data frames the
	// streaming endpoints would write (each already the JSON object for one
	// `data: {...}` line). DoneTerminator reports whether a trailing
	// `data: [DONE]` was written.
	StreamFrames   []any
	DoneTerminator bool
}

// HTTP status codes the endpoints emit (subset, named for clarity).
const (
	statusOK                   = 200
	statusNoContent            = 204
	statusBadRequest           = 400
	statusUnauthorized         = 401
	statusNotFound             = 404
	statusRequestTimeout       = 504
	statusServiceUnavailable   = 503
	statusInsufficientStorage  = 507
	statusInternalServerError  = 500
)

// InferenceServer is the in-memory handler surface for the inference server's
// endpoints. Ports the collective endpoint layer behind one interface so it can
// be exercised without a real HTTP listener.
type InferenceServer interface {
	HandleChatCompletion(ctx context.Context, auth AuthResult, body ChatCompletionRequest) EndpointResult
	HandleEmbeddings(ctx context.Context, auth AuthResult, body EmbeddingsRequest) EndpointResult
	HandleCompanionTurn(ctx context.Context, auth AuthResult, body CompanionTurnRequest) EndpointResult
	AdminLoad(ctx context.Context, auth AuthResult, body AdminLoadRequest) EndpointResult
	AdminUnload(ctx context.Context, auth AuthResult, modelID string) EndpointResult
	AdminLifecycle(ctx context.Context, auth AuthResult) EndpointResult
}

// AdminLoadRequest is the POST /v1/admin/models/load body. Ports AdminLoadRequest.
type AdminLoadRequest struct {
	ModelID           string `json:"modelId"`
	Backend           string `json:"backend"`
	Tier              string `json:"tier"`
	VramRequiredBytes int64  `json:"vramRequiredBytes"`
	RamRequiredBytes  int64  `json:"ramRequiredBytes"`
}

// AdminLifecycleResponse is the GET /v1/admin/lifecycle body. Ports AdminLifecycleResponse.
type AdminLifecycleResponse struct {
	TotalAllocatedVramBytes int64            `json:"totalAllocatedVramBytes"`
	TotalAllocatedRamBytes  int64            `json:"totalAllocatedRamBytes"`
	Loaded                  []ModelLoadState `json:"loaded"`
}

// InferenceServerHandlers wires the endpoint dependencies. Ports the DI graph
// the endpoints resolve (registry, admission, counters, lifecycle, factory,
// resolver, request-timeout).
type InferenceServerHandlers struct {
	Registry       IInferenceServerModelRegistry
	Admission      *AdmissionControl
	Counters       *ServerCounters
	Lifecycle      IModelLifecycleManager
	BridgeFactory  IBridgeFactory
	Resolver       ICompanionSessionResolver
	RequestTimeout time.Duration
}

// NewInferenceServerHandlers builds the handler set. RequestTimeout ≤ 0 defaults
// to 120s (mirrors InferenceServerOptions.RequestTimeoutSeconds default).
func NewInferenceServerHandlers(h InferenceServerHandlers) *InferenceServerHandlers {
	if h.RequestTimeout <= 0 {
		h.RequestTimeout = 120 * time.Second
	}
	if h.Counters == nil {
		h.Counters = NewServerCounters()
	}
	return &h
}

// requireAuth enforces the AuthenticatedPolicy: only AuthSuccess proceeds.
func requireAuth(auth AuthResult) *EndpointResult {
	if auth.Outcome == AuthSuccess {
		return nil
	}
	return &EndpointResult{
		StatusCode: statusUnauthorized,
		Body:       ErrorResponseOf("Unauthorized.", "invalid_request_error", "unauthorized"),
	}
}

// ── /v1/chat/completions ─────────────────────────────────────────────────────

// HandleChatCompletion ports ChatCompletionsEndpoint.HandleAsync.
func (h *InferenceServerHandlers) HandleChatCompletion(ctx context.Context, auth AuthResult, body ChatCompletionRequest) EndpointResult {
	if r := requireAuth(auth); r != nil {
		return *r
	}
	if strings.TrimSpace(body.Model) == "" {
		return EndpointResult{StatusCode: statusBadRequest,
			Body: ErrorResponseOf("Missing or empty 'model' field.", "invalid_request_error", "missing_model")}
	}
	if len(body.Messages) == 0 {
		return EndpointResult{StatusCode: statusBadRequest,
			Body: ErrorResponseOf("Missing 'messages' array.", "invalid_request_error", "missing_messages")}
	}

	bridge := h.Registry.Resolve(body.Model)
	if bridge == nil {
		return EndpointResult{StatusCode: statusNotFound,
			Body: ErrorResponseOf("Model '"+body.Model+"' is not loaded.", "invalid_request_error", "model_not_found")}
	}

	slot := h.Admission.TryEnter()
	if slot == nil {
		return EndpointResult{StatusCode: statusServiceUnavailable,
			Body: ErrorResponseOf(
				"Server is at concurrency cap. Retry after a brief delay.", "server_busy", "concurrency_cap")}
	}
	defer slot.Release()

	timeoutCtx, cancel := context.WithTimeout(ctx, h.RequestTimeout)
	defer cancel()

	request := buildInferenceRequest(body)

	if body.Stream {
		return h.streamChatResponse(timeoutCtx, bridge, request, body)
	}
	return h.nonStreamChatResponse(timeoutCtx, bridge, request, body)
}

func (h *InferenceServerHandlers) nonStreamChatResponse(ctx context.Context, bridge IInferenceBridge, request InferenceRequest, body ChatCompletionRequest) EndpointResult {
	resp, err := bridge.Complete(ctx, request)
	if err != nil {
		h.Counters.AccountFailed()
		if ctx.Err() != nil {
			return EndpointResult{StatusCode: statusRequestTimeout,
				Body: ErrorResponseOf("Request cancelled or timed out.", "timeout", "request_timeout")}
		}
		return EndpointResult{StatusCode: statusInternalServerError,
			Body: ErrorResponseOf(err.Error(), "internal_error", "bridge_failure")}
	}
	if resp.Status == InferenceStatusFailed {
		h.Counters.AccountFailed()
		msg := resp.FailureMessage
		if msg == "" {
			msg = "Inference failed."
		}
		return EndpointResult{StatusCode: statusInternalServerError,
			Body: ErrorResponseOf(msg, "internal_error", "inference_failed")}
	}

	msg := ChatCompletionMessage{Role: "assistant", Content: resp.OutputText}
	if resp.ReasoningText != "" {
		r := resp.ReasoningText
		msg.ReasoningContent = &r
	}
	response := ChatCompletionResponse{
		ID:      "chatcmpl-" + uuidN(),
		Object:  "chat.completion",
		Created: time.Now().UTC().Unix(),
		Model:   body.Model,
		Choices: []ChatCompletionChoice{{
			Index:        0,
			Message:      msg,
			FinishReason: mapFinish(resp.Status),
		}},
		Usage: UsageInfo{
			PromptTokens:     resp.PromptTokenCount,
			CompletionTokens: resp.OutputTokenCount,
			TotalTokens:      resp.PromptTokenCount + resp.OutputTokenCount,
		},
	}
	return EndpointResult{StatusCode: statusOK, Body: response}
}

func (h *InferenceServerHandlers) streamChatResponse(ctx context.Context, bridge IInferenceBridge, request InferenceRequest, body ChatCompletionRequest) EndpointResult {
	id := "chatcmpl-" + uuidN()
	created := time.Now().UTC().Unix()
	frames := make([]any, 0, 8)

	// First frame: role announcement.
	roleStr := "assistant"
	frames = append(frames, ChatCompletionStreamChunk{
		ID: id, Object: "chat.completion.chunk", Created: created, Model: body.Model,
		Choices: []ChatCompletionStreamChoice{{Index: 0, Delta: ChatCompletionDelta{Role: &roleStr}}},
	})

	fragCh, errCh := bridge.StreamFragments(ctx, request)
	for f := range fragCh {
		if f.Text == "" {
			continue
		}
		var delta ChatCompletionDelta
		if f.Kind == InferenceFragmentReasoning {
			t := f.Text
			delta.ReasoningContent = &t
		} else {
			t := f.Text
			delta.Content = &t
		}
		frames = append(frames, ChatCompletionStreamChunk{
			ID: id, Object: "chat.completion.chunk", Created: created, Model: body.Model,
			Choices: []ChatCompletionStreamChoice{{Index: 0, Delta: delta}},
		})
	}
	if err := <-errCh; err != nil {
		h.Counters.AccountFailed()
		if ctx.Err() == nil {
			errStr := "error"
			content := "[error: " + err.Error() + "]"
			frames = append(frames, ChatCompletionStreamChunk{
				ID: id, Object: "chat.completion.chunk", Created: created, Model: body.Model,
				Choices: []ChatCompletionStreamChoice{{Index: 0, Delta: ChatCompletionDelta{Content: &content}, FinishReason: &errStr}},
			})
		}
	}

	// Final frame: stop reason + [DONE].
	stop := "stop"
	frames = append(frames, ChatCompletionStreamChunk{
		ID: id, Object: "chat.completion.chunk", Created: created, Model: body.Model,
		Choices: []ChatCompletionStreamChoice{{Index: 0, Delta: ChatCompletionDelta{}, FinishReason: &stop}},
	})
	return EndpointResult{StatusCode: statusOK, StreamFrames: frames, DoneTerminator: true}
}

// buildInferenceRequest joins the OpenAI messages into a single prompt with
// role markers, exactly like ChatCompletionsEndpoint.BuildInferenceRequest.
func buildInferenceRequest(body ChatCompletionRequest) InferenceRequest {
	parts := make([]string, 0, len(body.Messages))
	for _, m := range body.Messages {
		parts = append(parts, "<|"+m.Role+"|>\n"+m.Content+"\n<|end|>")
	}
	prompt := strings.Join(parts, "\n")

	metadata := map[string]string{}
	if body.User != nil && *body.User != "" {
		metadata["user"] = *body.User
	}
	maxTokens := 512
	if body.MaxTokens != nil {
		maxTokens = *body.MaxTokens
	}
	temperature := float32(0.7)
	if body.Temperature != nil {
		temperature = *body.Temperature
	}
	topP := float32(0.9)
	if body.TopP != nil {
		topP = *body.TopP
	}
	stops := body.Stop
	if stops == nil {
		stops = []string{}
	}
	return InferenceRequest{
		ID:              uuid.New(),
		ModelID:         body.Model,
		Prompt:          prompt,
		MaxOutputTokens: maxTokens,
		Temperature:     temperature,
		TopP:            topP,
		StopSequences:   stops,
		Metadata:        metadata,
		RequestedAt:     time.Now().UTC(),
	}
}

// mapFinish ports ChatCompletionsEndpoint.MapFinish.
func mapFinish(status InferenceStatus) string {
	switch status {
	case InferenceStatusCompleted, InferenceStatusStoppedByToken:
		return "stop"
	case InferenceStatusStoppedByLength:
		return "length"
	case InferenceStatusCancelled:
		return "cancelled"
	default:
		return "error"
	}
}

// ── /v1/embeddings ───────────────────────────────────────────────────────────

// HandleEmbeddings ports EmbeddingsEndpoint.HandleAsync.
func (h *InferenceServerHandlers) HandleEmbeddings(ctx context.Context, auth AuthResult, body EmbeddingsRequest) EndpointResult {
	if r := requireAuth(auth); r != nil {
		return *r
	}
	if strings.TrimSpace(body.Model) == "" {
		return EndpointResult{StatusCode: statusBadRequest,
			Body: ErrorResponseOf("Missing or empty 'model' field.", "invalid_request_error", "missing_model")}
	}

	embedder := h.Registry.ResolveEmbedder(body.Model)
	if embedder == nil {
		return EndpointResult{StatusCode: statusNotFound,
			Body: ErrorResponseOf("Embedding model '"+body.Model+"' is not loaded.", "invalid_request_error", "model_not_found")}
	}

	inputs, errResp := normaliseEmbeddingInput(body.Input)
	if errResp != nil {
		return EndpointResult{StatusCode: statusBadRequest, Body: *errResp}
	}

	slot := h.Admission.TryEnter()
	if slot == nil {
		return EndpointResult{StatusCode: statusServiceUnavailable,
			Body: ErrorResponseOf("Server is at concurrency cap. Retry shortly.", "server_busy", "concurrency_cap")}
	}
	defer slot.Release()

	data := make([]EmbeddingDatum, 0, len(inputs))
	totalChars := 0
	for i, in := range inputs {
		vec, err := embedder.Generate(ctx, in)
		if err != nil {
			h.Counters.AccountFailed()
			if ctx.Err() != nil {
				return EndpointResult{StatusCode: statusRequestTimeout,
					Body: ErrorResponseOf("Request cancelled or timed out.", "timeout", "request_timeout")}
			}
			return EndpointResult{StatusCode: statusInternalServerError,
				Body: ErrorResponseOf(err.Error(), "internal_error", "embedding_failure")}
		}
		data = append(data, EmbeddingDatum{Object: "embedding", Index: i, Embedding: vec})
		totalChars += len(in)
	}

	estTokens := totalChars / 4
	if estTokens < 1 {
		estTokens = 1
	}
	return EndpointResult{StatusCode: statusOK, Body: EmbeddingsResponse{
		Object: "list",
		Data:   data,
		Model:  body.Model,
		Usage:  UsageInfo{PromptTokens: estTokens, CompletionTokens: 0, TotalTokens: estTokens},
	}}
}

// normaliseEmbeddingInput coerces the OpenAI input (string | []string) into a
// list. Ports EmbeddingsEndpoint.TryNormaliseInput.
func normaliseEmbeddingInput(raw json.RawMessage) ([]string, *ErrorResponse) {
	if len(raw) == 0 {
		e := ErrorResponseOf("'input' must be a string or array of strings.", "invalid_request_error", "invalid_input")
		return nil, &e
	}
	// Single string?
	var s string
	if err := json.Unmarshal(raw, &s); err == nil {
		return []string{s}, nil
	}
	// Array of strings?
	var arr []string
	if err := json.Unmarshal(raw, &arr); err == nil {
		if len(arr) == 0 {
			e := ErrorResponseOf("'input' array must not be empty.", "invalid_request_error", "invalid_input")
			return nil, &e
		}
		return arr, nil
	}
	// Array with a non-string element, or some other shape.
	var anyArr []json.RawMessage
	if err := json.Unmarshal(raw, &anyArr); err == nil {
		e := ErrorResponseOf("Every 'input' array element must be a string.", "invalid_request_error", "invalid_input")
		return nil, &e
	}
	e := ErrorResponseOf("'input' must be a string or array of strings.", "invalid_request_error", "invalid_input")
	return nil, &e
}

// ── /v1/companion/turn ───────────────────────────────────────────────────────

// HandleCompanionTurn ports CompanionEndpoint.HandleTurnAsync.
func (h *InferenceServerHandlers) HandleCompanionTurn(ctx context.Context, auth AuthResult, body CompanionTurnRequest) EndpointResult {
	if r := requireAuth(auth); r != nil {
		return *r
	}
	if strings.TrimSpace(body.SessionID) == "" ||
		strings.TrimSpace(body.IdentityID) == "" ||
		strings.TrimSpace(body.Message) == "" {
		return EndpointResult{StatusCode: statusBadRequest,
			Body: ErrorResponseOf("session_id, identity_id, and message are all required.", "invalid_request_error", "missing_field")}
	}

	session, err := h.Resolver.Resolve(ctx, body.SessionID, body.IdentityID)
	if err != nil {
		return EndpointResult{StatusCode: statusInternalServerError,
			Body: ErrorResponseOf(err.Error(), "internal_error", "companion_failure")}
	}
	if session == nil {
		return EndpointResult{StatusCode: statusNotFound,
			Body: ErrorResponseOf(
				"No Companion session for session_id='"+body.SessionID+"', identity_id='"+body.IdentityID+"'.",
				"invalid_request_error", "session_not_found")}
	}

	slot := h.Admission.TryEnter()
	if slot == nil {
		return EndpointResult{StatusCode: statusServiceUnavailable,
			Body: ErrorResponseOf("Server is at concurrency cap. Retry shortly.", "server_busy", "concurrency_cap")}
	}
	defer slot.Release()

	if body.Stream {
		return h.streamCompanionReply(ctx, session, body)
	}

	var reply string
	var rerr error
	if body.Agentic {
		reply, rerr = session.Agent(ctx, body.Message)
	} else {
		reply, rerr = session.Send(ctx, body.Message)
	}
	if rerr != nil {
		h.Counters.AccountFailed()
		if ctx.Err() != nil {
			return EndpointResult{StatusCode: statusRequestTimeout,
				Body: ErrorResponseOf("Request cancelled or timed out.", "timeout", "request_timeout")}
		}
		return EndpointResult{StatusCode: statusInternalServerError,
			Body: ErrorResponseOf(rerr.Error(), "internal_error", "companion_failure")}
	}
	return EndpointResult{StatusCode: statusOK, Body: CompanionTurnResponse{
		SessionID: body.SessionID,
		Reply:     reply,
		Agentic:   body.Agentic,
		TurnIndex: len(session.History()),
	}}
}

func (h *InferenceServerHandlers) streamCompanionReply(ctx context.Context, session ICompanionSession, body CompanionTurnRequest) EndpointResult {
	frames := make([]any, 0, 8)
	chunks, errc := session.Stream(ctx, body.Message)
	for chunk := range chunks {
		if chunk == "" {
			continue
		}
		frames = append(frames, map[string]any{"session_id": body.SessionID, "delta": chunk})
	}
	if err := <-errc; err != nil {
		h.Counters.AccountFailed()
		frames = append(frames, map[string]any{"session_id": body.SessionID, "error": err.Error()})
	}
	return EndpointResult{StatusCode: statusOK, StreamFrames: frames, DoneTerminator: true}
}

// ── /v1/admin ────────────────────────────────────────────────────────────────

// AdminLifecycle ports the GET /v1/admin/lifecycle handler.
func (h *InferenceServerHandlers) AdminLifecycle(_ context.Context, auth AuthResult) EndpointResult {
	if r := requireAuth(auth); r != nil {
		return *r
	}
	return EndpointResult{StatusCode: statusOK, Body: AdminLifecycleResponse{
		TotalAllocatedVramBytes: h.Lifecycle.TotalAllocatedVramBytes(),
		TotalAllocatedRamBytes:  h.Lifecycle.TotalAllocatedRamBytes(),
		Loaded:                  h.Lifecycle.List(),
	}}
}

// AdminLoad ports the POST /v1/admin/models/load handler.
func (h *InferenceServerHandlers) AdminLoad(ctx context.Context, auth AuthResult, body AdminLoadRequest) EndpointResult {
	if r := requireAuth(auth); r != nil {
		return *r
	}
	if strings.TrimSpace(body.ModelID) == "" {
		return EndpointResult{StatusCode: statusBadRequest,
			Body: ErrorResponseOf("Missing 'modelId'.", "invalid_request_error", "missing_model")}
	}
	backend, ok := ParseBackendKind(defaultStr(body.Backend, "Cpu"))
	if !ok {
		return EndpointResult{StatusCode: statusBadRequest,
			Body: ErrorResponseOf(
				"Unknown backend '"+body.Backend+"'. Valid: Cpu, Cuda, Vulkan, OpenCL, Metal, Ascend, Cambricon, CoreML.",
				"invalid_request_error", "invalid_backend")}
	}
	tier, ok := ParseCapabilityTier(defaultStr(body.Tier, "Tier1_Small"))
	if !ok {
		return EndpointResult{StatusCode: statusBadRequest,
			Body: ErrorResponseOf(
				"Unknown tier '"+body.Tier+"'. Valid: Tier0_Tiny..Tier4_Frontier.",
				"invalid_request_error", "invalid_tier")}
	}

	descriptor := ModelLoadDescriptor{
		ModelID:           body.ModelID,
		Backend:           backend,
		RequestedTier:     tier,
		VramRequiredBytes: maxI64(0, body.VramRequiredBytes),
		RamRequiredBytes:  maxI64(0, body.RamRequiredBytes),
		BridgeFactory: func(c context.Context) (IInferenceBridge, error) {
			return h.BridgeFactory.Create(c, body.ModelID, backend, tier)
		},
	}
	result, err := h.Lifecycle.Load(ctx, descriptor)
	if err != nil {
		return EndpointResult{StatusCode: statusInternalServerError,
			Body: ErrorResponseOf(err.Error(), "internal_error", "unknown")}
	}
	switch result.Outcome {
	case LoadOutcomeLoaded, LoadOutcomeAlreadyLoaded:
		return EndpointResult{StatusCode: statusOK, Body: map[string]any{
			"outcome":   result.Outcome.String(),
			"state":     result.State,
			"rationale": result.Rationale,
		}}
	case LoadOutcomeInsufficientVram, LoadOutcomeInsufficientRam:
		return EndpointResult{StatusCode: statusInsufficientStorage,
			Body: ErrorResponseOf(result.Rationale, "resource_exhausted", result.Outcome.String())}
	case LoadOutcomeFactoryFailed:
		return EndpointResult{StatusCode: statusInternalServerError,
			Body: ErrorResponseOf(result.Rationale, "internal_error", "factory_failed")}
	default:
		return EndpointResult{StatusCode: statusInternalServerError,
			Body: ErrorResponseOf(result.Rationale, "internal_error", "unknown")}
	}
}

// AdminUnload ports the DELETE /v1/admin/models/{id} handler.
func (h *InferenceServerHandlers) AdminUnload(ctx context.Context, auth AuthResult, modelID string) EndpointResult {
	if r := requireAuth(auth); r != nil {
		return *r
	}
	outcome, err := h.Lifecycle.Unload(ctx, modelID)
	if err != nil {
		return EndpointResult{StatusCode: statusInternalServerError,
			Body: ErrorResponseOf(err.Error(), "internal_error", "unknown")}
	}
	switch outcome {
	case UnloadOutcomeUnloaded:
		return EndpointResult{StatusCode: statusOK, Body: map[string]any{"outcome": "Unloaded", "modelId": modelID}}
	case UnloadOutcomeNotLoaded:
		return EndpointResult{StatusCode: statusNotFound,
			Body: ErrorResponseOf("Model '"+modelID+"' is not loaded.", "invalid_request_error", "not_loaded")}
	default:
		return EndpointResult{StatusCode: statusInternalServerError,
			Body: ErrorResponseOf("Unknown unload outcome.", "internal_error", "unknown")}
	}
}

// ── helpers ──────────────────────────────────────────────────────────────────

func defaultStr(v, def string) string {
	if strings.TrimSpace(v) == "" {
		return def
	}
	return v
}

// uuidN returns a UUID with no dashes (mirrors Guid.NewGuid():N).
func uuidN() string {
	return strings.ReplaceAll(uuid.New().String(), "-", "")
}

var _ InferenceServer = (*InferenceServerHandlers)(nil)
