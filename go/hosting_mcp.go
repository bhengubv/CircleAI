// hosting_mcp.go
//
// Ports CircleAI.Hosting.Mcp:
//   IMcpTool, IMcpResourceProvider, McpResource, McpResourceContent,
//   McpToolError (Contracts.cs)
//   McpServerInfo + the JSON-RPC 2.0 dispatcher (McpEndpoints.cs)
//
// The C# dispatcher resolves tools + resource providers from the DI container;
// the Go port takes them as explicit slices (registered via McpRegistry) so the
// pure-dispatch entry point stays testable without a host. The JSON-RPC method
// routing, error codes, and result envelopes match the C# exactly.

package circleai

import (
	"context"
	"encoding/json"
	"fmt"
	"strings"
)

// IMcpTool is one MCP tool the host exposes. Ports
// CircleAI.Hosting.Mcp.IMcpTool.
type IMcpTool interface {
	// Name is a unique tool name (snake_case by convention).
	Name() string
	// Description is a one-line description shown in tool listings.
	Description() string
	// InputSchema is a JSON Schema (any JSON-serialisable value) for arguments.
	InputSchema() interface{}
	// Execute runs the tool. Return any JSON-serialisable value; the dispatcher
	// wraps it in the MCP text-content envelope. Return an McpToolError to signal
	// a tool-level error (isError:true).
	Execute(ctx context.Context, arguments map[string]interface{}) (interface{}, error)
}

// IMcpResourceProvider is one MCP resource provider. Ports
// CircleAI.Hosting.Mcp.IMcpResourceProvider.
type IMcpResourceProvider interface {
	// URIScheme e.g. "vault://", "models://".
	URIScheme() string
	// List lists every resource this provider serves.
	List(ctx context.Context) ([]McpResource, error)
	// Read reads one resource by uri; returns nil on not-found.
	Read(ctx context.Context, uri string) (*McpResourceContent, error)
}

// McpResource is one MCP resource descriptor. Ports
// CircleAI.Hosting.Mcp.McpResource (record).
type McpResource struct {
	URI            string
	Name           string
	Description    string
	HasDescription bool // distinguishes "" from null Description for list output
	MimeType       string
}

// McpResourceContent is one MCP resource content (from resources/read). Ports
// CircleAI.Hosting.Mcp.McpResourceContent (record).
type McpResourceContent struct {
	URI      string
	MimeType string
	Text     string
}

// McpToolError signals a tool-level error (vs an MCP protocol error). Ports
// CircleAI.Hosting.Mcp.McpToolException. The dispatcher returns it as
// {content:[{type:"text",text:msg}], isError:true}.
type McpToolError struct {
	Message string
}

func (e *McpToolError) Error() string { return e.Message }

// NewMcpToolError builds an McpToolError.
func NewMcpToolError(message string) *McpToolError { return &McpToolError{Message: message} }

// McpServerInfo names the MCP server. Ports McpEndpoints.McpServerInfo (defaults).
type McpServerInfo struct {
	Name        string
	Version     string
	Description string
}

// DefaultMcpServerInfo returns the C# defaults.
func DefaultMcpServerInfo() McpServerInfo {
	return McpServerInfo{Name: "circleai-mcp", Version: "3.2.0", Description: "CircleAI MCP endpoint"}
}

// McpRegistry holds the tools + resource providers a host has registered. It
// stands in for the IServiceProvider the C# dispatcher resolves from.
type McpRegistry struct {
	Tools     []IMcpTool
	Resources []IMcpResourceProvider
}

// DispatchMcp is the pure JSON-RPC 2.0 dispatcher. Ports
// McpEndpoints.DispatchAsync. reqJSON is one request object's JSON. Returns the
// response object to serialise (nil for notifications like
// notifications/initialized, matching the C# null return).
func DispatchMcp(ctx context.Context, reqJSON json.RawMessage, reg McpRegistry, info McpServerInfo) (interface{}, error) {
	var req map[string]json.RawMessage
	if err := json.Unmarshal(reqJSON, &req); err != nil || req == nil {
		return mcpErrorObj(nil, -32600, "Invalid Request"), nil
	}

	id := req["id"] // raw JSON id (may be absent)

	jsonrpc := jsonString(req["jsonrpc"])
	method := ""
	if jsonrpc == "2.0" {
		method = jsonString(req["method"])
	}
	if method == "" {
		return mcpErrorObj(id, -32600, "Invalid Request: missing jsonrpc or method"), nil
	}

	params := req["params"]

	switch method {
	case "initialize":
		return mcpHandleInitialize(id, info), nil
	case "notifications/initialized":
		return nil, nil
	case "tools/list":
		return mcpHandleToolsList(id, reg), nil
	case "tools/call":
		return mcpHandleToolsCall(ctx, id, params, reg), nil
	case "resources/list":
		return mcpHandleResourcesList(ctx, id, reg), nil
	case "resources/read":
		return mcpHandleResourcesRead(ctx, id, params, reg), nil
	default:
		return mcpErrorObj(id, -32601, fmt.Sprintf("Method not found: %s", method)), nil
	}
}

// DispatchMcpBatch handles a single request or a JSON array batch. Ports the
// POST /mcp body handling in McpEndpoints.MapMcpApi. For a batch it returns the
// array of non-nil responses; for a single request it returns that response (or
// nil for a notification).
func DispatchMcpBatch(ctx context.Context, body []byte, reg McpRegistry, info McpServerInfo) (interface{}, error) {
	trimmed := strings.TrimSpace(string(body))
	if trimmed == "" {
		return mcpErrorObj(nil, -32600, "Invalid Request"), nil
	}
	if strings.HasPrefix(trimmed, "[") {
		var batch []json.RawMessage
		if err := json.Unmarshal(body, &batch); err != nil {
			return mcpErrorObj(nil, -32700, "Parse error"), nil
		}
		var responses []interface{}
		for _, item := range batch {
			resp, _ := DispatchMcp(ctx, item, reg, info)
			if resp != nil {
				responses = append(responses, resp)
			}
		}
		return responses, nil
	}
	// Validate parseability for the single-object path.
	var probe interface{}
	if err := json.Unmarshal(body, &probe); err != nil {
		return mcpErrorObj(nil, -32700, "Parse error"), nil
	}
	return DispatchMcp(ctx, body, reg, info)
}

func mcpHandleInitialize(id json.RawMessage, info McpServerInfo) interface{} {
	return mcpResult(id, map[string]interface{}{
		"protocolVersion": "2024-11-05",
		"serverInfo":      map[string]interface{}{"name": info.Name, "version": info.Version},
		"capabilities": map[string]interface{}{
			"tools":     map[string]interface{}{"listChanged": false},
			"resources": map[string]interface{}{"listChanged": false, "subscribe": false},
		},
	})
}

func mcpHandleToolsList(id json.RawMessage, reg McpRegistry) interface{} {
	tools := make([]map[string]interface{}, 0, len(reg.Tools))
	for _, t := range reg.Tools {
		tools = append(tools, map[string]interface{}{
			"name":        t.Name(),
			"description": t.Description(),
			"inputSchema": t.InputSchema(),
		})
	}
	return mcpResult(id, map[string]interface{}{"tools": tools})
}

func mcpHandleToolsCall(ctx context.Context, id, params json.RawMessage, reg McpRegistry) interface{} {
	p := parseObject(params)
	toolName := jsonString(p["name"])
	if isBlank(toolName) {
		return mcpErrorObj(id, -32602, "Invalid params: 'name' is required")
	}
	var tool IMcpTool
	for _, t := range reg.Tools {
		if t.Name() == toolName {
			tool = t
			break
		}
	}
	if tool == nil {
		return mcpErrorObj(id, -32602, fmt.Sprintf("Unknown tool: %s", toolName))
	}
	args := map[string]interface{}{}
	if raw, ok := p["arguments"]; ok {
		_ = json.Unmarshal(raw, &args)
	}
	result, err := tool.Execute(ctx, args)
	if err != nil {
		var toolErr *McpToolError
		if asMcpToolError(err, &toolErr) {
			return mcpToolError(id, toolErr.Message)
		}
		return mcpErrorObj(id, -32603, fmt.Sprintf("Internal error: %s", err.Error()))
	}
	return mcpToolResult(id, result)
}

func mcpHandleResourcesList(ctx context.Context, id json.RawMessage, reg McpRegistry) interface{} {
	var resources []map[string]interface{}
	for _, prov := range reg.Resources {
		page, err := prov.List(ctx)
		if err != nil {
			return mcpErrorObj(id, -32603, fmt.Sprintf("Internal error: %s", err.Error()))
		}
		for _, r := range page {
			desc := r.Description
			if !r.HasDescription || desc == "" {
				desc = r.Name // C#: Description ?? Name
			}
			resources = append(resources, map[string]interface{}{
				"uri":         r.URI,
				"name":        r.Name,
				"description": desc,
				"mimeType":    r.MimeType,
			})
		}
	}
	return mcpResult(id, map[string]interface{}{"resources": resources})
}

func mcpHandleResourcesRead(ctx context.Context, id, params json.RawMessage, reg McpRegistry) interface{} {
	p := parseObject(params)
	uri := jsonString(p["uri"])
	if isBlank(uri) {
		return mcpErrorObj(id, -32602, "Invalid params: 'uri' is required")
	}
	var provider IMcpResourceProvider
	for _, prov := range reg.Resources {
		if strings.HasPrefix(strings.ToLower(uri), strings.ToLower(prov.URIScheme())) {
			provider = prov
			break
		}
	}
	if provider == nil {
		return mcpErrorObj(id, -32602, fmt.Sprintf("No provider for URI scheme: %s", uri))
	}
	content, err := provider.Read(ctx, uri)
	if err != nil {
		return mcpErrorObj(id, -32603, fmt.Sprintf("Internal error: %s", err.Error()))
	}
	if content == nil {
		return mcpErrorObj(id, -32602, fmt.Sprintf("Resource not found: %s", uri))
	}
	return mcpResult(id, map[string]interface{}{
		"contents": []map[string]interface{}{
			{"uri": content.URI, "mimeType": content.MimeType, "text": content.Text},
		},
	})
}

// ── envelope helpers (mirror McpEndpoints.Mcp* helpers) ─────────────────────

// mcpResult wraps a result. The C# helper serialises id via id?.ToJsonString(),
// i.e. the id is emitted as its JSON string form; we replicate that so the wire
// shape matches ({"id":"1"} for a numeric id 1).
func mcpResult(id json.RawMessage, result interface{}) interface{} {
	return map[string]interface{}{"jsonrpc": "2.0", "id": mcpIDString(id), "result": result}
}

func mcpToolResult(id json.RawMessage, data interface{}) interface{} {
	dataJSON, _ := json.Marshal(data)
	return mcpResult(id, map[string]interface{}{
		"content": []map[string]interface{}{{"type": "text", "text": string(dataJSON)}},
		"isError": false,
	})
}

func mcpToolError(id json.RawMessage, message string) interface{} {
	return mcpResult(id, map[string]interface{}{
		"content": []map[string]interface{}{{"type": "text", "text": message}},
		"isError": true,
	})
}

func mcpErrorObj(id json.RawMessage, code int, message string) interface{} {
	return map[string]interface{}{
		"jsonrpc": "2.0",
		"id":      mcpIDString(id),
		"error":   map[string]interface{}{"code": code, "message": message},
	}
}

// mcpIDString mirrors id?.ToJsonString(): nil id → nil; otherwise the compact
// JSON text of the id node.
func mcpIDString(id json.RawMessage) interface{} {
	if len(id) == 0 {
		return nil
	}
	return string(id)
}

func parseObject(raw json.RawMessage) map[string]json.RawMessage {
	out := map[string]json.RawMessage{}
	if len(raw) == 0 {
		return out
	}
	_ = json.Unmarshal(raw, &out)
	return out
}

// (jsonString lives in memory_llm_extractor.go — reused here, not redeclared.)

func asMcpToolError(err error, target **McpToolError) bool {
	for err != nil {
		if te, ok := err.(*McpToolError); ok {
			*target = te
			return true
		}
		u, ok := err.(interface{ Unwrap() error })
		if !ok {
			break
		}
		err = u.Unwrap()
	}
	return false
}
