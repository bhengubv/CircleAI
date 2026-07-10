// telephony_tool_calling.go
//
// Ports CircleAI.Telephony/ToolCalling.cs — tool-calling for the voice loop:
//   TelephonyToolDefinition          -> TelephonyToolDefinition value struct
//   TelephonyToolInvocation          -> TelephonyToolInvocation value struct
//   TelephonyToolResult              -> TelephonyToolResult value struct
//   TelephonyLocalToolHandler        -> TelephonyLocalToolHandler func type
//   ITelephonyToolCallRegistry       -> ITelephonyToolCallRegistry interface
//   DefaultTelephonyToolCallRegistry -> DefaultTelephonyToolCallRegistry (thread-safe)
//
// The webhook dispatch path reaches an HTTPS endpoint via HttpClient in C#. Per
// the porting rules the transport is injected behind the package's existing
// HTTPDoer seam (from hosting_cloud_fallback.go) so the registry is deterministic
// in tests with no live endpoint. The posted body preserves the C# shape:
// {"call_id":..., "tool":..., "arguments": <parsed arguments JSON>}. A nil doer
// makes webhook tools fail-soft with a clear error rather than panicking.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/url"
	"strings"
	"sync"
)

// TelephonyToolDefinition is a tool definition surfaced to the LLM. Ports TelephonyToolDefinition.
type TelephonyToolDefinition struct {
	Name                string // function call name
	Description         string // human description used to pick the tool
	ArgumentsJSONSchema string // JSON Schema describing the arguments
}

// TelephonyToolInvocation is one invocation of a tool by the model. Ports TelephonyToolInvocation.
type TelephonyToolInvocation struct {
	CallID        string
	ToolName      string
	ArgumentsJSON string
}

// TelephonyToolResult is the result of a tool invocation. Ports TelephonyToolResult. Error is ""
// when Succeeded (C# null).
type TelephonyToolResult struct {
	CallID     string
	Succeeded  bool
	ResultJSON string
	Error      string
}

// TelephonyLocalToolHandler is an in-process tool handler. Ports the TelephonyLocalToolHandler
// delegate: given the arguments JSON, returns the result JSON (or an error).
type TelephonyLocalToolHandler func(ctx context.Context, argumentsJSON string) (string, error)

// ITelephonyToolCallRegistry registers local handlers OR HTTPS webhook URLs against a
// tool name; the orchestrator dispatches. Ports ITelephonyToolCallRegistry.
type ITelephonyToolCallRegistry interface {
	// Definitions returns all registered tool definitions.
	Definitions() []TelephonyToolDefinition
	// RegisterLocal registers a local handler for definition.
	RegisterLocal(definition TelephonyToolDefinition, handler TelephonyLocalToolHandler) error
	// RegisterWebhook registers a webhook URL; the orchestrator POSTs arguments JSON.
	RegisterWebhook(definition TelephonyToolDefinition, webhook string) error
	// Invoke invokes one tool call.
	Invoke(ctx context.Context, invocation TelephonyToolInvocation) (TelephonyToolResult, error)
}

// toolEntry is one registered tool: its definition plus exactly one of a local
// handler or a webhook URL.
type toolEntry struct {
	def     TelephonyToolDefinition
	local   TelephonyLocalToolHandler
	webhook string // "" when local
}

// DefaultTelephonyToolCallRegistry is the default in-memory registry. Thread-safe. Ports
// DefaultTelephonyToolCallRegistry. Tool names are matched case-insensitively (the C#
// ConcurrentDictionary uses StringComparer.OrdinalIgnoreCase).
type DefaultTelephonyToolCallRegistry struct {
	mu    sync.RWMutex
	tools map[string]toolEntry // keyed by lower-cased tool name (ordinal-ignore-case)
	order []string             // insertion/most-recent key order for Definitions()
	doer  HTTPDoer             // injected webhook transport; nil => webhook tools fail-soft
}

// NewDefaultTelephonyToolCallRegistry constructs a registry over an injected HTTP doer.
// Ports the DefaultTelephonyToolCallRegistry(HttpClient) constructor. doer may be nil for
// a local-only registry (webhook invocations then return a clear error instead
// of dereferencing a nil client — the C# constructor throws on a null HttpClient,
// but a nil doer here degrades gracefully because Go has no framework HttpClient
// to require).
func NewDefaultTelephonyToolCallRegistry(doer HTTPDoer) *DefaultTelephonyToolCallRegistry {
	return &DefaultTelephonyToolCallRegistry{
		tools: make(map[string]toolEntry),
		doer:  doer,
	}
}

// Definitions returns all registered tool definitions. Ports the Definitions
// getter (materialises a snapshot list).
func (r *DefaultTelephonyToolCallRegistry) Definitions() []TelephonyToolDefinition {
	r.mu.RLock()
	defer r.mu.RUnlock()
	list := make([]TelephonyToolDefinition, 0, len(r.tools))
	for _, k := range r.order {
		if e, ok := r.tools[k]; ok {
			list = append(list, e.def)
		}
	}
	return list
}

// RegisterLocal registers a local handler. Ports RegisterLocal (validates a
// non-empty name; handler required).
func (r *DefaultTelephonyToolCallRegistry) RegisterLocal(definition TelephonyToolDefinition, handler TelephonyLocalToolHandler) error {
	if handler == nil {
		return errors.New("handler is required")
	}
	if strings.TrimSpace(definition.Name) == "" {
		return errors.New("Tool name is required")
	}
	r.put(definition, handler, "")
	return nil
}

// RegisterWebhook registers a webhook URL. Ports RegisterWebhook (validates an
// absolute URL and a non-empty name).
func (r *DefaultTelephonyToolCallRegistry) RegisterWebhook(definition TelephonyToolDefinition, webhook string) error {
	if strings.TrimSpace(webhook) == "" || !isAbsoluteURL(webhook) {
		return errors.New("Webhook URL must be absolute.")
	}
	if strings.TrimSpace(definition.Name) == "" {
		return errors.New("Tool name is required")
	}
	r.put(definition, nil, webhook)
	return nil
}

// put upserts an entry keyed by the ordinal-ignore-case name.
func (r *DefaultTelephonyToolCallRegistry) put(def TelephonyToolDefinition, local TelephonyLocalToolHandler, webhook string) {
	key := strings.ToLower(def.Name)
	r.mu.Lock()
	defer r.mu.Unlock()
	if _, exists := r.tools[key]; !exists {
		r.order = append(r.order, key)
	}
	r.tools[key] = toolEntry{def: def, local: local, webhook: webhook}
}

// Invoke invokes one tool call. Ports InvokeAsync: unknown tool → failed result;
// local handler → its JSON (empty → "{}"); webhook → POST the arguments,
// non-2xx → failed with a truncated body, else the response body ("{}" if blank);
// registered-without-either → failed; any error → failed with the message.
//
// The method itself returns error only for a nil invocation-shaped violation; a
// tool failure is carried in TelephonyToolResult.Succeeded=false (matching the C# which
// never throws out of InvokeAsync).
func (r *DefaultTelephonyToolCallRegistry) Invoke(ctx context.Context, invocation TelephonyToolInvocation) (TelephonyToolResult, error) {
	r.mu.RLock()
	entry, ok := r.tools[strings.ToLower(invocation.ToolName)]
	r.mu.RUnlock()
	if !ok {
		return TelephonyToolResult{
			CallID:     invocation.CallID,
			Succeeded:  false,
			ResultJSON: "{}",
			Error:      fmt.Sprintf("Tool '%s' is not registered.", invocation.ToolName),
		}, nil
	}

	if entry.local != nil {
		resultJSON, err := entry.local(ctx, invocation.ArgumentsJSON)
		if err != nil {
			return TelephonyToolResult{CallID: invocation.CallID, Succeeded: false, ResultJSON: "{}", Error: err.Error()}, nil
		}
		if resultJSON == "" {
			resultJSON = "{}"
		}
		return TelephonyToolResult{CallID: invocation.CallID, Succeeded: true, ResultJSON: resultJSON}, nil
	}

	if entry.webhook != "" {
		if r.doer == nil {
			return TelephonyToolResult{
				CallID:     invocation.CallID,
				Succeeded:  false,
				ResultJSON: "{}",
				Error:      "No HTTP transport configured for webhook tools.",
			}, nil
		}
		// Build {"call_id":..,"tool":..,"arguments": <parsed>} — arguments is the
		// parsed JSON element, matching JsonDocument.Parse(...).RootElement.
		var argsElem json.RawMessage
		if strings.TrimSpace(invocation.ArgumentsJSON) == "" {
			argsElem = json.RawMessage("null")
		} else {
			// Validate + normalise; invalid JSON surfaces as a failed result,
			// mirroring the C# JsonDocument.Parse throwing into the catch block.
			if !json.Valid([]byte(invocation.ArgumentsJSON)) {
				return TelephonyToolResult{CallID: invocation.CallID, Succeeded: false, ResultJSON: "{}", Error: "invalid arguments JSON"}, nil
			}
			argsElem = json.RawMessage(invocation.ArgumentsJSON)
		}
		payload := map[string]interface{}{
			"call_id":   invocation.CallID,
			"tool":      invocation.ToolName,
			"arguments": argsElem,
		}
		body, _ := json.Marshal(payload)

		resp, err := r.doer.Do(&OutboundHTTPRequest{
			URL:     entry.webhook,
			Body:    body,
			Headers: map[string]string{"Content-Type": "application/json"},
		})
		if err != nil {
			return TelephonyToolResult{CallID: invocation.CallID, Succeeded: false, ResultJSON: "{}", Error: err.Error()}, nil
		}
		if resp.StatusCode < 200 || resp.StatusCode >= 300 {
			return TelephonyToolResult{
				CallID:     invocation.CallID,
				Succeeded:  false,
				ResultJSON: "{}",
				Error:      fmt.Sprintf("Webhook %d: %s", resp.StatusCode, truncateToolBody(string(resp.Body), 240)),
			}, nil
		}
		bodyStr := string(resp.Body)
		if strings.TrimSpace(bodyStr) == "" {
			bodyStr = "{}"
		}
		return TelephonyToolResult{CallID: invocation.CallID, Succeeded: true, ResultJSON: bodyStr}, nil
	}

	return TelephonyToolResult{
		CallID:     invocation.CallID,
		Succeeded:  false,
		ResultJSON: "{}",
		Error:      fmt.Sprintf("Tool '%s' is registered without a local handler or webhook.", invocation.ToolName),
	}, nil
}

// truncateToolBody ports the private Truncate(s, max): append '…' when longer.
// (The package's truncateEllipsis has the same behaviour; kept a thin wrapper so
// the intent is explicit against the C# call site.)
func truncateToolBody(s string, max int) string {
	if len(s) <= max {
		return s
	}
	return s[:max] + "…"
}

// isAbsoluteURL reports whether s is an absolute URL (scheme + host, parseable),
// mirroring Uri.IsAbsoluteUri as used by RegisterWebhook. The C# webhook POST is
// implicit in HttpClient.PostAsync; the shared HTTPDoer.Do is POST-shaped, so no
// method is carried on the request.
func isAbsoluteURL(s string) bool {
	u, err := url.Parse(s)
	return err == nil && u.IsAbs() && u.Scheme != "" && u.Host != ""
}

var _ ITelephonyToolCallRegistry = (*DefaultTelephonyToolCallRegistry)(nil)
