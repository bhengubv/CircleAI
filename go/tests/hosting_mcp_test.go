// hosting_mcp_test.go
//
// Verifies CircleAI.Hosting.Mcp ports: the JSON-RPC 2.0 dispatcher (initialize,
// tools/list, tools/call, resources/list, resources/read), tool-level error
// envelopes, unknown-method / unknown-tool errors, and batch handling.

package circleai_test

import (
	"context"
	"encoding/json"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

// echoMcpTool concatenates a fixed prefix with an argument.
type echoMcpTool struct{}

func (echoMcpTool) Name() string        { return "echo" }
func (echoMcpTool) Description() string { return "Echoes text" }
func (echoMcpTool) InputSchema() interface{} {
	return map[string]interface{}{"type": "object"}
}
func (echoMcpTool) Execute(_ context.Context, args map[string]interface{}) (interface{}, error) {
	return map[string]interface{}{"echoed": args["text"]}, nil
}

// erroringMcpTool always signals a tool-level error.
type erroringMcpTool struct{}

func (erroringMcpTool) Name() string             { return "boom" }
func (erroringMcpTool) Description() string      { return "Always fails" }
func (erroringMcpTool) InputSchema() interface{} { return map[string]interface{}{} }
func (erroringMcpTool) Execute(context.Context, map[string]interface{}) (interface{}, error) {
	return nil, circleai.NewMcpToolError("nope")
}

// memResourceProvider serves one vault:// resource.
type memResourceProvider struct{}

func (memResourceProvider) URIScheme() string { return "vault://" }
func (memResourceProvider) List(context.Context) ([]circleai.McpResource, error) {
	return []circleai.McpResource{{URI: "vault://note/1", Name: "Note 1", MimeType: "text/plain"}}, nil
}
func (memResourceProvider) Read(_ context.Context, uri string) (*circleai.McpResourceContent, error) {
	if uri == "vault://note/1" {
		return &circleai.McpResourceContent{URI: uri, MimeType: "text/plain", Text: "hello"}, nil
	}
	return nil, nil
}

func dispatch(t *testing.T, reg circleai.McpRegistry, req string) map[string]interface{} {
	t.Helper()
	resp, err := circleai.DispatchMcp(context.Background(), json.RawMessage(req), reg, circleai.DefaultMcpServerInfo())
	if err != nil {
		t.Fatalf("dispatch err: %v", err)
	}
	if resp == nil {
		return nil
	}
	// Round-trip through JSON to inspect as a generic map.
	b, _ := json.Marshal(resp)
	var m map[string]interface{}
	_ = json.Unmarshal(b, &m)
	return m
}

func TestMcp_Initialize(t *testing.T) {
	reg := circleai.McpRegistry{}
	m := dispatch(t, reg, `{"jsonrpc":"2.0","id":1,"method":"initialize"}`)
	result, ok := m["result"].(map[string]interface{})
	if !ok {
		t.Fatalf("no result: %v", m)
	}
	if result["protocolVersion"] != "2024-11-05" {
		t.Errorf("protocolVersion = %v", result["protocolVersion"])
	}
	// id is echoed as its JSON text form ("1").
	if m["id"] != "1" {
		t.Errorf("id = %v, want \"1\"", m["id"])
	}
}

func TestMcp_ToolsListAndCall(t *testing.T) {
	reg := circleai.McpRegistry{Tools: []circleai.IMcpTool{echoMcpTool{}}}

	list := dispatch(t, reg, `{"jsonrpc":"2.0","id":2,"method":"tools/list"}`)
	result := list["result"].(map[string]interface{})
	tools := result["tools"].([]interface{})
	if len(tools) != 1 || tools[0].(map[string]interface{})["name"] != "echo" {
		t.Fatalf("tools/list wrong: %v", tools)
	}

	call := dispatch(t, reg, `{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}`)
	callResult := call["result"].(map[string]interface{})
	if callResult["isError"] != false {
		t.Errorf("isError = %v, want false", callResult["isError"])
	}
	content := callResult["content"].([]interface{})
	text := content[0].(map[string]interface{})["text"].(string)
	if text != `{"echoed":"hi"}` {
		t.Errorf("tool text = %q", text)
	}
}

func TestMcp_ToolError(t *testing.T) {
	reg := circleai.McpRegistry{Tools: []circleai.IMcpTool{erroringMcpTool{}}}
	m := dispatch(t, reg, `{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"boom"}}`)
	result := m["result"].(map[string]interface{})
	if result["isError"] != true {
		t.Errorf("isError = %v, want true", result["isError"])
	}
	text := result["content"].([]interface{})[0].(map[string]interface{})["text"]
	if text != "nope" {
		t.Errorf("error text = %v", text)
	}
}

func TestMcp_UnknownTool(t *testing.T) {
	reg := circleai.McpRegistry{Tools: []circleai.IMcpTool{echoMcpTool{}}}
	m := dispatch(t, reg, `{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"ghost"}}`)
	errObj := m["error"].(map[string]interface{})
	if errObj["code"].(float64) != -32602 {
		t.Errorf("code = %v, want -32602", errObj["code"])
	}
}

func TestMcp_UnknownMethod(t *testing.T) {
	m := dispatch(t, circleai.McpRegistry{}, `{"jsonrpc":"2.0","id":6,"method":"does/not/exist"}`)
	errObj := m["error"].(map[string]interface{})
	if errObj["code"].(float64) != -32601 {
		t.Errorf("code = %v, want -32601", errObj["code"])
	}
}

func TestMcp_Notification_ReturnsNil(t *testing.T) {
	resp, err := circleai.DispatchMcp(context.Background(),
		json.RawMessage(`{"jsonrpc":"2.0","method":"notifications/initialized"}`),
		circleai.McpRegistry{}, circleai.DefaultMcpServerInfo())
	if err != nil {
		t.Fatalf("dispatch: %v", err)
	}
	if resp != nil {
		t.Errorf("notification should return nil, got %v", resp)
	}
}

func TestMcp_ResourcesListAndRead(t *testing.T) {
	reg := circleai.McpRegistry{Resources: []circleai.IMcpResourceProvider{memResourceProvider{}}}

	list := dispatch(t, reg, `{"jsonrpc":"2.0","id":7,"method":"resources/list"}`)
	resources := list["result"].(map[string]interface{})["resources"].([]interface{})
	if len(resources) != 1 {
		t.Fatalf("resources/list = %v", resources)
	}
	first := resources[0].(map[string]interface{})
	if first["uri"] != "vault://note/1" || first["description"] != "Note 1" {
		t.Errorf("resource = %v (description should default to Name)", first)
	}

	read := dispatch(t, reg, `{"jsonrpc":"2.0","id":8,"method":"resources/read","params":{"uri":"vault://note/1"}}`)
	contents := read["result"].(map[string]interface{})["contents"].([]interface{})
	text := contents[0].(map[string]interface{})["text"]
	if text != "hello" {
		t.Errorf("read text = %v", text)
	}

	// Unknown scheme → error.
	noProv := dispatch(t, reg, `{"jsonrpc":"2.0","id":9,"method":"resources/read","params":{"uri":"models://x"}}`)
	if _, ok := noProv["error"]; !ok {
		t.Error("expected error for unknown scheme")
	}
}

func TestMcp_Batch(t *testing.T) {
	reg := circleai.McpRegistry{Tools: []circleai.IMcpTool{echoMcpTool{}}}
	body := `[{"jsonrpc":"2.0","id":1,"method":"initialize"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":2,"method":"tools/list"}]`
	resp, err := circleai.DispatchMcpBatch(context.Background(), []byte(body), reg, circleai.DefaultMcpServerInfo())
	if err != nil {
		t.Fatalf("batch: %v", err)
	}
	arr, ok := resp.([]interface{})
	if !ok {
		t.Fatalf("batch result not an array: %T", resp)
	}
	// Notification produces no response → 2 responses, not 3.
	if len(arr) != 2 {
		t.Errorf("batch responses = %d, want 2 (notification omitted)", len(arr))
	}
}
