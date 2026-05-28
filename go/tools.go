// tools.go
//
// ToolDefinition, ToolParameter, ToolInvocation, ToolResult, IToolBridge.
// FaceExpressionClassification, FaceBoundingBox, FacialMetricMatrix.
//
// Bridge between the local LLM and TheGeekNetwork APIs. Implementations
// route tool calls to the appropriate API client.

package circleai

import (
	"context"
	"time"
)

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
// FaceExpressionClassification
// ---------------------------------------------------------------------------

// FaceExpressionClassification is the detected facial expression.
type FaceExpressionClassification int

const (
	// FaceExpressionNeutral is the baseline, no-emotion expression.
	FaceExpressionNeutral FaceExpressionClassification = iota

	// FaceExpressionHappy: smiling or positive affect.
	FaceExpressionHappy

	// FaceExpressionSad: downturned mouth, sad eyes.
	FaceExpressionSad

	// FaceExpressionSurprised: raised eyebrows, open mouth.
	FaceExpressionSurprised

	// FaceExpressionConfused: furrowed brow, tilted head.
	FaceExpressionConfused

	// FaceExpressionStressed: tense brow, tight mouth.
	FaceExpressionStressed

	// FaceExpressionAngry: narrowed eyes, lowered brow.
	FaceExpressionAngry

	// FaceExpressionUnknown is returned when classification fails.
	FaceExpressionUnknown
)

// ---------------------------------------------------------------------------
// FaceBoundingBox
// ---------------------------------------------------------------------------

// FaceBoundingBox is a normalised (0.0–1.0) face bounding box within the
// source image frame.
type FaceBoundingBox struct {
	// X is the left edge, normalised to [0, 1].
	X float32

	// Y is the top edge, normalised to [0, 1].
	Y float32

	// Width is the width, normalised to [0, 1].
	Width float32

	// Height is the height, normalised to [0, 1].
	Height float32
}

// ---------------------------------------------------------------------------
// FacialMetricMatrix
// ---------------------------------------------------------------------------

// FacialMetricMatrix holds all facial analytics produced by the on-device
// face processing pipeline for a single detected face.
type FacialMetricMatrix struct {
	// Landmarks holds 68 (x, y) landmark coordinate pairs normalised to
	// [0.0, 1.0] relative to the face bounding box, stored as a flat
	// array of length 136 (index 2*i = x, 2*i+1 = y for landmark i).
	Landmarks [136]float32

	// BoundingBox is the normalised face bounding box in the source frame.
	BoundingBox FaceBoundingBox

	// Expression is the classified facial expression.
	Expression FaceExpressionClassification

	// ConfidenceScore is the detector's confidence in the expression
	// classification, in [0, 1].
	ConfidenceScore float32

	// CapturedAt is the UTC time when the frame was captured.
	CapturedAt time.Time
}

// GetLandmark returns the (x, y) coordinates of landmark i (0-indexed).
// Panics when i is out of range.
func (m *FacialMetricMatrix) GetLandmark(i int) (x, y float32) {
	return m.Landmarks[2*i], m.Landmarks[2*i+1]
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
