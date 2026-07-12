// workflows_mcp.go
//
// Ports CircleAI.Workflows/PacaMcp.cs — the MCP server for paca workflows:
// registers built-in workflow tools plus plugin-registered tools, supports three
// transports (stdio/SSE/HTTP), and holds a per-agent MCP config so each agent
// has its own enabled-tool set.
//
//	McpTransportKind (enum)  -> int consts (Stdio=0, ServerSentEvents=1, Http=2)
//	AgentMcpConfig / PacaMcpTool (records) -> structs
//	PacaMcpHandler (delegate) -> func type
//	PacaMcpServer            -> PacaMcpServer
//	PacaCoreMcpTools (statics) -> package vars
//
// InvokeAsync enforces the agent's enabled-tool list (case-insensitive; an empty
// list means "all allowed"), then runs the handler, wrapping any error as the
// same {"error":{"message":…}} JSON envelope the C# emits. ToolsListJson embeds
// each tool's InputSchema as a live JSON object (re-parsed, matching the C#
// JsonDocument.Parse round-trip).

package circleai

import (
	"context"
	"encoding/json"
	"strconv"
	"strings"
	"sync"
)

// McpTransportKind is an MCP transport type. Ports McpTransportKind
// (Stdio=0, ServerSentEvents=1, Http=2).
type McpTransportKind int

const (
	// McpTransportStdio — stdio transport.
	McpTransportStdio McpTransportKind = 0
	// McpTransportServerSentEvents — SSE transport.
	McpTransportServerSentEvents McpTransportKind = 1
	// McpTransportHTTP — HTTP transport.
	McpTransportHTTP McpTransportKind = 2
)

// AgentMcpConfig is a per-agent MCP server config. Ports the AgentMcpConfig
// record.
type AgentMcpConfig struct {
	AgentMemberID string
	Transports    []McpTransportKind
	EnabledTools  []string
	ToolSettings  map[string]string
}

// PacaMcpTool is an MCP tool descriptor. Ports the PacaMcpTool record.
// InputSchema is a JSON-schema string.
type PacaMcpTool struct {
	Name        string
	Description string
	InputSchema string
}

// PacaMcpHandler is an MCP tool handler. Ports the PacaMcpHandler delegate:
// takes the arguments JSON, returns the result JSON.
type PacaMcpHandler func(ctx context.Context, argumentsJSON string) (string, error)

type mcpEntry struct {
	tool    PacaMcpTool
	handler PacaMcpHandler
}

// PacaMcpServer registers built-in workflow tools + plugin tools and dispatches
// per-agent invocations. Ports PacaMcpServer. Construct with NewPacaMcpServer.
type PacaMcpServer struct {
	mu           sync.Mutex
	tools        map[string]mcpEntry // key = lower(name)
	order        []string            // insertion order of lower(name)
	agentConfigs map[string]AgentMcpConfig
}

// NewPacaMcpServer constructs an empty MCP server.
func NewPacaMcpServer() *PacaMcpServer {
	return &PacaMcpServer{
		tools:        make(map[string]mcpEntry),
		agentConfigs: make(map[string]AgentMcpConfig),
	}
}

// Tools returns the registered tool descriptors (registration order). Ports the
// Tools property.
func (s *PacaMcpServer) Tools() []PacaMcpTool {
	s.mu.Lock()
	out := make([]PacaMcpTool, 0, len(s.order))
	for _, k := range s.order {
		out = append(out, s.tools[k].tool)
	}
	s.mu.Unlock()
	return out
}

// RegisterTool registers (or replaces by name, case-insensitively) a tool +
// handler. Ports RegisterTool.
func (s *PacaMcpServer) RegisterTool(tool PacaMcpTool, handler PacaMcpHandler) {
	key := strings.ToLower(tool.Name)
	s.mu.Lock()
	if _, exists := s.tools[key]; !exists {
		s.order = append(s.order, key)
	}
	s.tools[key] = mcpEntry{tool: tool, handler: handler}
	s.mu.Unlock()
}

// ConfigureAgent configures a per-agent toolset. Ports ConfigureAgent.
func (s *PacaMcpServer) ConfigureAgent(config AgentMcpConfig) {
	s.mu.Lock()
	s.agentConfigs[config.AgentMemberID] = config
	s.mu.Unlock()
}

// GetAgentConfig returns an agent's MCP config and true, or (zero, false).
// Ports GetAgentConfig.
func (s *PacaMcpServer) GetAgentConfig(agentMemberID string) (AgentMcpConfig, bool) {
	s.mu.Lock()
	c, ok := s.agentConfigs[agentMemberID]
	s.mu.Unlock()
	return c, ok
}

// Invoke invokes a tool for an agent, enforcing the agent's enabled-tool list.
// Ports InvokeAsync. Errors (unknown tool, tool not enabled, handler failure)
// are returned as a {"error":{"message":…}} JSON string, never as a Go error.
func (s *PacaMcpServer) Invoke(ctx context.Context, agentMemberID, toolName, argumentsJSON string) string {
	s.mu.Lock()
	entry, ok := s.tools[strings.ToLower(toolName)]
	cfg, hasCfg := s.agentConfigs[agentMemberID]
	s.mu.Unlock()

	if !ok {
		return wrapMcpError("Unknown tool '" + toolName + "'.")
	}
	if hasCfg && len(cfg.EnabledTools) > 0 && !containsFold(cfg.EnabledTools, toolName) {
		return wrapMcpError("Tool '" + toolName + "' is not enabled for agent '" + agentMemberID + "'.")
	}
	result, err := entry.handler(ctx, argumentsJSON)
	if err != nil {
		return wrapMcpError(err.Error())
	}
	return result
}

// ToolsListJson renders the JSON-RPC tools/list response payload. Ports
// ToolsListJson — each tool's InputSchema is embedded as a live JSON object.
func (s *PacaMcpServer) ToolsListJson() string {
	type toolEntry struct {
		Name        string          `json:"name"`
		Description string          `json:"description"`
		InputSchema json.RawMessage `json:"inputSchema"`
	}
	s.mu.Lock()
	entries := make([]toolEntry, 0, len(s.order))
	for _, k := range s.order {
		t := s.tools[k].tool
		schema := json.RawMessage(strings.TrimSpace(t.InputSchema))
		if !json.Valid(schema) {
			schema = json.RawMessage("null")
		}
		entries = append(entries, toolEntry{Name: t.Name, Description: t.Description, InputSchema: schema})
	}
	s.mu.Unlock()
	payload, _ := json.Marshal(map[string]any{"tools": entries})
	return string(payload)
}

func wrapMcpError(message string) string {
	payload, _ := json.Marshal(map[string]any{"error": map[string]any{"message": message}})
	return string(payload)
}

// Built-in workflow tools. Port PacaCoreMcpTools (create_task, list_tasks,
// edit_task, create_doc, link_doc_to_task).
var (
	// PacaCoreMcpToolCreateTask ports PacaCoreMcpTools.CreateTask.
	PacaCoreMcpToolCreateTask = PacaMcpTool{
		Name:        "create_task",
		Description: "Create a new task in a project.",
		InputSchema: `{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"description":{"type":"string"}},"required":["project_id","title"]}`,
	}
	// PacaCoreMcpToolListTasks ports PacaCoreMcpTools.ListTasks.
	PacaCoreMcpToolListTasks = PacaMcpTool{
		Name:        "list_tasks",
		Description: "List live tasks in a project.",
		InputSchema: `{"type":"object","properties":{"project_id":{"type":"string"}},"required":["project_id"]}`,
	}
	// PacaCoreMcpToolEditTask ports PacaCoreMcpTools.EditTask.
	PacaCoreMcpToolEditTask = PacaMcpTool{
		Name:        "edit_task",
		Description: "Edit a task (title, description, status).",
		InputSchema: `{"type":"object","properties":{"project_id":{"type":"string"},"number":{"type":"integer"},"title":{"type":"string"},"description":{"type":"string"},"status":{"type":"string"}},"required":["project_id","number"]}`,
	}
	// PacaCoreMcpToolCreateDoc ports PacaCoreMcpTools.CreateDoc.
	PacaCoreMcpToolCreateDoc = PacaMcpTool{
		Name:        "create_doc",
		Description: "Create a doc in the project's doc tree.",
		InputSchema: `{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"parent_id":{"type":"string","nullable":true},"content_json":{"type":"string"}},"required":["project_id","title","content_json"]}`,
	}
	// PacaCoreMcpToolLinkDocToTask ports PacaCoreMcpTools.LinkDocToTask.
	PacaCoreMcpToolLinkDocToTask = PacaMcpTool{
		Name:        "link_doc_to_task",
		Description: "Link a doc section to a task.",
		InputSchema: `{"type":"object","properties":{"doc_id":{"type":"string"},"section_anchor":{"type":"string"},"project_id":{"type":"string"},"task_number":{"type":"integer"}},"required":["doc_id","section_anchor","project_id","task_number"]}`,
	}
)

// String renders an McpTransportKind as its C# enum name.
func (k McpTransportKind) String() string {
	switch k {
	case McpTransportStdio:
		return "Stdio"
	case McpTransportServerSentEvents:
		return "ServerSentEvents"
	case McpTransportHTTP:
		return "Http"
	default:
		return "McpTransportKind(" + strconv.Itoa(int(k)) + ")"
	}
}
