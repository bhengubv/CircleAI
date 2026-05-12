// tools.go
//
// ToolDefinition, ToolParameter, ToolInvocation, ToolResult, IToolBridge.
//
// Bridge between the local LLM and TheGeekNetwork APIs. Implementations
// route tool calls to the appropriate API client.

package circleai

import "context"

// ---------------------------------------------------------------------------
// ToolDefinition
// ---------------------------------------------------------------------------

// ToolDefinition describes a tool the model can call.
// Compatible with OpenAI/Qwen function-call schema.
type ToolDefinition struct {
	// Name is the unique name of the tool (e.g. "get_weather").
	Name string

	// Description explains what the tool does.
	Description string

	// Parameters maps parameter names to their schemas.
	Parameters map[string]ToolParameter

	// RequiredParameters lists the names of required parameters.
	RequiredParameters []string
}

// ---------------------------------------------------------------------------
// ToolParameter
// ---------------------------------------------------------------------------

// ToolParameter is the schema for a single tool parameter.
type ToolParameter struct {
	// Type is the JSON Schema type: "string", "number", "boolean", "object", "array".
	Type string

	// Description explains what this parameter does.
	Description string

	// Enum, if non-nil, restricts the value to one of the listed strings.
	Enum []string
}

// ---------------------------------------------------------------------------
// ToolInvocation
// ---------------------------------------------------------------------------

// ToolInvocation is a request from the model to call a specific tool.
type ToolInvocation struct {
	// ToolName is the name of the tool to invoke.
	ToolName string

	// Arguments maps argument names to their values (JSON-decoded).
	Arguments map[string]interface{}
}

// ---------------------------------------------------------------------------
// ToolResult
// ---------------------------------------------------------------------------

// ToolResult is the outcome of executing a ToolInvocation.
type ToolResult struct {
	// ToolName is the name of the tool that was invoked.
	ToolName string

	// Success indicates whether the invocation succeeded.
	Success bool

	// Result holds the tool's return value on success. May be nil.
	Result interface{}

	// Error holds the error message on failure. May be empty.
	Error string
}

// ToolResultFailure is a convenience constructor for a failed tool result.
func ToolResultFailure(toolName, errMsg string) ToolResult {
	return ToolResult{
		ToolName: toolName,
		Success:  false,
		Error:    errMsg,
	}
}

// ToolResultOK is a convenience constructor for a successful tool result.
func ToolResultOK(toolName string, result interface{}) ToolResult {
	return ToolResult{
		ToolName: toolName,
		Success:  true,
		Result:   result,
	}
}

// ---------------------------------------------------------------------------
// IToolBridge
// ---------------------------------------------------------------------------

// IToolBridge bridges the local LLM and the TheGeekNetwork APIs.
// Implementations route tool calls to the appropriate API client
// (HTTP, in-process service, etc.).
type IToolBridge interface {
	// AvailableTools returns the list of tools exposed by this bridge.
	AvailableTools() []ToolDefinition

	// Invoke dispatches a tool invocation and returns the result.
	Invoke(ctx context.Context, invocation ToolInvocation) (ToolResult, error)

	// GetAvailableTools queries the remote service for available tools.
	// Implementations that expose a static tool list may return the same
	// value as AvailableTools.
	GetAvailableTools(ctx context.Context) ([]ToolDefinition, error)
}
