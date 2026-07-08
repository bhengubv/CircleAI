//! hosting_mcp_test.rs
//!
//! Verifies the MCP JSON-RPC 2.0 dispatcher: initialize, tools/list, tools/call
//! (success + tool error), resources/list, resources/read (found + not found +
//! no provider), method-not-found, notifications, and batch handling. Mirrors
//! the C# McpEndpoints.DispatchAsync.

use serde_json::json;

use circle_ai::hosting_mcp::{
    FnMcpTool, InMemoryResourceProvider, McpRegistry, McpServerInfo, McpToolError,
};

fn registry() -> McpRegistry {
    let mut r = McpRegistry::new();
    r.add_tool(Box::new(FnMcpTool::new(
        "add",
        "Adds two numbers",
        json!({ "type": "object", "properties": { "a": {"type":"number"}, "b": {"type":"number"} } }),
        |args| {
            let a = args.get("a").and_then(|v| v.as_i64()).unwrap_or(0);
            let b = args.get("b").and_then(|v| v.as_i64()).unwrap_or(0);
            Ok(json!({ "sum": a + b }))
        },
    )));
    r.add_tool(Box::new(FnMcpTool::new(
        "boom",
        "Always errors",
        json!({}),
        |_| Err(McpToolError("kaboom".to_string())),
    )));
    r.add_resource_provider(Box::new(
        InMemoryResourceProvider::new("vault://")
            .add("vault://note/1", "First note", "text/plain", "hello world"),
    ));
    r
}

fn rpc(method: &str, params: serde_json::Value, id: i64) -> serde_json::Value {
    json!({ "jsonrpc": "2.0", "id": id, "method": method, "params": params })
}

#[test]
fn initialize_returns_protocol_version() {
    let r = registry();
    let info = McpServerInfo::default();
    let resp = r.dispatch(&rpc("initialize", json!({}), 1), &info).unwrap();
    assert_eq!(resp["result"]["protocolVersion"], "2024-11-05");
    assert_eq!(resp["result"]["serverInfo"]["name"], "circleai-mcp");
    // The id is stringified per the C# `id?.ToJsonString()`.
    assert_eq!(resp["id"], "1");
}

#[test]
fn tools_list_returns_registered_tools() {
    let r = registry();
    let info = McpServerInfo::default();
    let resp = r.dispatch(&rpc("tools/list", json!({}), 2), &info).unwrap();
    let tools = resp["result"]["tools"].as_array().unwrap();
    assert_eq!(tools.len(), 2);
    assert_eq!(tools[0]["name"], "add");
}

#[test]
fn tools_call_success_wraps_content() {
    let r = registry();
    let info = McpServerInfo::default();
    let resp = r
        .dispatch(&rpc("tools/call", json!({ "name": "add", "arguments": { "a": 2, "b": 3 } }), 3), &info)
        .unwrap();
    assert_eq!(resp["result"]["isError"], false);
    let text = resp["result"]["content"][0]["text"].as_str().unwrap();
    // The tool result is JSON-serialised into the text field.
    let inner: serde_json::Value = serde_json::from_str(text).unwrap();
    assert_eq!(inner["sum"], 5);
}

#[test]
fn tools_call_tool_error_sets_is_error() {
    let r = registry();
    let info = McpServerInfo::default();
    let resp = r
        .dispatch(&rpc("tools/call", json!({ "name": "boom", "arguments": {} }), 4), &info)
        .unwrap();
    assert_eq!(resp["result"]["isError"], true);
    assert_eq!(resp["result"]["content"][0]["text"], "kaboom");
}

#[test]
fn tools_call_unknown_tool_errors() {
    let r = registry();
    let info = McpServerInfo::default();
    let resp = r
        .dispatch(&rpc("tools/call", json!({ "name": "nope" }), 5), &info)
        .unwrap();
    assert_eq!(resp["error"]["code"], -32602);
}

#[test]
fn tools_call_missing_name_errors() {
    let r = registry();
    let info = McpServerInfo::default();
    let resp = r
        .dispatch(&rpc("tools/call", json!({}), 6), &info)
        .unwrap();
    assert_eq!(resp["error"]["code"], -32602);
}

#[test]
fn resources_list_and_read() {
    let r = registry();
    let info = McpServerInfo::default();

    let list = r.dispatch(&rpc("resources/list", json!({}), 7), &info).unwrap();
    let resources = list["result"]["resources"].as_array().unwrap();
    assert_eq!(resources.len(), 1);
    assert_eq!(resources[0]["uri"], "vault://note/1");
    // Description defaults to the name when None.
    assert_eq!(resources[0]["description"], "First note");

    let read = r
        .dispatch(&rpc("resources/read", json!({ "uri": "vault://note/1" }), 8), &info)
        .unwrap();
    assert_eq!(read["result"]["contents"][0]["text"], "hello world");
}

#[test]
fn resources_read_no_provider_for_scheme() {
    let r = registry();
    let info = McpServerInfo::default();
    let resp = r
        .dispatch(&rpc("resources/read", json!({ "uri": "models://x" }), 9), &info)
        .unwrap();
    assert_eq!(resp["error"]["code"], -32602);
    assert!(resp["error"]["message"].as_str().unwrap().contains("No provider"));
}

#[test]
fn resources_read_not_found() {
    let r = registry();
    let info = McpServerInfo::default();
    let resp = r
        .dispatch(&rpc("resources/read", json!({ "uri": "vault://note/999" }), 10), &info)
        .unwrap();
    assert!(resp["error"]["message"].as_str().unwrap().contains("Resource not found"));
}

#[test]
fn unknown_method_returns_method_not_found() {
    let r = registry();
    let info = McpServerInfo::default();
    let resp = r.dispatch(&rpc("frobnicate", json!({}), 11), &info).unwrap();
    assert_eq!(resp["error"]["code"], -32601);
}

#[test]
fn notifications_initialized_returns_none() {
    let r = registry();
    let info = McpServerInfo::default();
    let req = json!({ "jsonrpc": "2.0", "method": "notifications/initialized" });
    assert!(r.dispatch(&req, &info).is_none());
}

#[test]
fn missing_jsonrpc_version_is_invalid_request() {
    let r = registry();
    let info = McpServerInfo::default();
    let req = json!({ "id": 1, "method": "tools/list" });
    let resp = r.dispatch(&req, &info).unwrap();
    assert_eq!(resp["error"]["code"], -32600);
}

#[test]
fn dispatch_body_handles_batch() {
    let r = registry();
    let info = McpServerInfo::default();
    let body = json!([
        rpc("tools/list", json!({}), 1),
        { "jsonrpc": "2.0", "method": "notifications/initialized" },
        rpc("initialize", json!({}), 2),
    ])
    .to_string();
    let out = r.dispatch_body(&body, &info);
    let arr = out.as_array().unwrap();
    // The notification produces no response → 2 entries.
    assert_eq!(arr.len(), 2);
}

#[test]
fn dispatch_body_parse_error() {
    let r = registry();
    let info = McpServerInfo::default();
    let out = r.dispatch_body("{not json", &info);
    assert_eq!(out["error"]["code"], -32700);
}
