//! hosting_mcp — CircleAI.Hosting.Mcp (Rust port).
//!
//! MCP (Model Context Protocol) JSON-RPC 2.0 tool + resource surface. Ported
//! from `Contracts.cs` (`IMcpTool`, `IMcpResourceProvider`, `McpResource`,
//! `McpResourceContent`, `McpToolException`) and `McpEndpoints.cs`.
//!
//! The C# `McpEndpoints` maps ASP.NET routes (`POST /mcp`, `GET /mcp/manifest`);
//! that HTTP layer is a thin wrapper over the pure, DI-driven
//! `DispatchAsync(JsonNode, IServiceProvider, …)` dispatcher, which is what this
//! port reproduces. Tools + resource providers are held in an [`McpRegistry`]
//! (the analogue of `IServiceProvider.GetServices<T>()`). SYNC; tool execution
//! returns `Result<Value, McpToolError>`. Every JSON-RPC envelope, method route,
//! and error code is ported 1:1 — including the C# quirk of stringifying the
//! request id via `id?.ToJsonString()`.

use serde_json::{json, Value};

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/// A tool-level error raised from inside [`IMcpTool::execute`]. The dispatcher
/// returns it as `{content:[{type:"text",text:msg}], isError:true}`. 1:1 with
/// the C# `McpToolException`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpToolError(pub String);

impl std::fmt::Display for McpToolError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for McpToolError {}

/// (3.2.0) One MCP tool the host exposes. 1:1 with the C# `IMcpTool`.
pub trait IMcpTool: Send + Sync {
    /// Unique tool name (snake_case by convention).
    fn name(&self) -> &str;

    /// One-line description shown in tool listings.
    fn description(&self) -> &str;

    /// JSON Schema describing the tool's `arguments` object; included verbatim
    /// in `tools/list`.
    fn input_schema(&self) -> Value;

    /// Execute the tool. Return any JSON value; the dispatcher wraps it in the
    /// MCP `{content:[{type:"text",text:"..."}]}` envelope. Return
    /// `Err(McpToolError)` to signal a tool-level error (`isError:true`).
    fn execute(&self, arguments: &Value) -> Result<Value, McpToolError>;
}

/// (3.2.0) One MCP resource descriptor. 1:1 with the C# `McpResource` record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpResource {
    pub uri: String,
    pub name: String,
    pub description: Option<String>,
    pub mime_type: String,
}

/// (3.2.0) One MCP resource content (returned by `resources/read`). 1:1 with the
/// C# `McpResourceContent` record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct McpResourceContent {
    pub uri: String,
    pub mime_type: String,
    pub text: String,
}

/// (3.2.0) One MCP resource provider. `resources/list` walks every provider;
/// `resources/read` picks the first whose [`Self::uri_scheme`] prefixes the
/// requested uri. 1:1 with the C# `IMcpResourceProvider`.
pub trait IMcpResourceProvider: Send + Sync {
    /// e.g. `"vault://"`, `"models://"`.
    fn uri_scheme(&self) -> &str;

    /// List every resource this provider serves.
    fn list(&self) -> Vec<McpResource>;

    /// Read one resource by uri. Returns `None` on not-found.
    fn read(&self, uri: &str) -> Option<McpResourceContent>;
}

/// Server identity returned by `initialize` / the manifest. 1:1 with the C#
/// `McpEndpoints.McpServerInfo`.
#[derive(Debug, Clone)]
pub struct McpServerInfo {
    pub name: String,
    pub version: String,
    pub description: String,
}

impl Default for McpServerInfo {
    fn default() -> Self {
        Self {
            name: "circleai-mcp".to_string(),
            version: "3.2.0".to_string(),
            description: "CircleAI MCP endpoint".to_string(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// McpRegistry — the analogue of IServiceProvider.GetServices<T>()
// ─────────────────────────────────────────────────────────────────────────────

/// Holds the registered tools + resource providers the dispatcher routes over.
/// Mirrors the C# DI collections resolved via `GetServices<IMcpTool>()` /
/// `GetServices<IMcpResourceProvider>()`.
#[derive(Default)]
pub struct McpRegistry {
    tools: Vec<Box<dyn IMcpTool>>,
    providers: Vec<Box<dyn IMcpResourceProvider>>,
}

impl McpRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Register a tool (registration order is preserved, mirroring DI order).
    pub fn add_tool(&mut self, tool: Box<dyn IMcpTool>) -> &mut Self {
        self.tools.push(tool);
        self
    }

    /// Register a resource provider.
    pub fn add_resource_provider(&mut self, provider: Box<dyn IMcpResourceProvider>) -> &mut Self {
        self.providers.push(provider);
        self
    }

    /// Every registered tool.
    pub fn tools(&self) -> &[Box<dyn IMcpTool>] {
        &self.tools
    }

    // ── Dispatcher ─────────────────────────────────────────────────────────

    /// (3.2.0) JSON-RPC 2.0 dispatch — the pure, HTTP-free entry point (1:1 with
    /// the C# `DispatchAsync`). Returns `None` for notifications (e.g.
    /// `notifications/initialized`), otherwise the response object.
    pub fn dispatch(&self, req: &Value, info: &McpServerInfo) -> Option<Value> {
        if req.is_null() {
            return Some(mcp_error_obj(None, -32600, "Invalid Request"));
        }

        let id = req.get("id");
        let is_rpc_2 = req.get("jsonrpc").and_then(|v| v.as_str()) == Some("2.0");
        let method = if is_rpc_2 {
            req.get("method").and_then(|v| v.as_str())
        } else {
            None
        };
        let Some(method) = method else {
            return Some(mcp_error_obj(
                id,
                -32600,
                "Invalid Request: missing jsonrpc or method",
            ));
        };

        let params = req.get("params");
        match method {
            "initialize" => Some(handle_initialize(id, info)),
            "notifications/initialized" => None,
            "tools/list" => Some(self.handle_tools_list(id)),
            "tools/call" => Some(self.handle_tools_call(id, params)),
            "resources/list" => Some(self.handle_resources_list(id)),
            "resources/read" => Some(self.handle_resources_read(id, params)),
            other => Some(mcp_error_obj(id, -32601, &format!("Method not found: {other}"))),
        }
    }

    /// Handle a single request or a JSON-RPC batch (array). 1:1 with the C#
    /// `POST /mcp` body handling: a batch returns the array of non-null
    /// responses; a parse failure yields the `-32700` error.
    pub fn dispatch_body(&self, body: &str, info: &McpServerInfo) -> Value {
        let parsed: Result<Value, _> = serde_json::from_str(body);
        let root = match parsed {
            Ok(v) => v,
            Err(_) => return mcp_error_obj(None, -32700, "Parse error"),
        };

        if let Some(batch) = root.as_array() {
            let responses: Vec<Value> = batch
                .iter()
                .filter_map(|item| self.dispatch(item, info))
                .collect();
            return Value::Array(responses);
        }

        self.dispatch(&root, info).unwrap_or(Value::Null)
    }

    /// The legacy `GET /mcp/manifest` payload. 1:1 with the C# manifest shape.
    pub fn manifest(&self, info: &McpServerInfo) -> Value {
        let tools: Vec<Value> = self
            .tools
            .iter()
            .map(|t| {
                json!({
                    "name": t.name(),
                    "description": t.description(),
                    "inputSchema": t.input_schema(),
                })
            })
            .collect();
        json!({
            "name": info.name,
            "version": info.version,
            "description": info.description,
            "deprecated": true,
            "deprecationNotice": "Use POST /mcp with JSON-RPC 2.0 instead.",
            "tools": tools,
        })
    }

    fn handle_tools_list(&self, id: Option<&Value>) -> Value {
        let tools: Vec<Value> = self
            .tools
            .iter()
            .map(|t| {
                json!({
                    "name": t.name(),
                    "description": t.description(),
                    "inputSchema": t.input_schema(),
                })
            })
            .collect();
        mcp_result(id, json!({ "tools": tools }))
    }

    fn handle_tools_call(&self, id: Option<&Value>, params: Option<&Value>) -> Value {
        let tool_name = params.and_then(|p| p.get("name")).and_then(|n| n.as_str());
        let tool_name = match tool_name {
            Some(n) if !n.trim().is_empty() => n,
            _ => return mcp_error_obj(id, -32602, "Invalid params: 'name' is required"),
        };

        let tool = self.tools.iter().find(|t| t.name() == tool_name);
        let Some(tool) = tool else {
            return mcp_error_obj(id, -32602, &format!("Unknown tool: {tool_name}"));
        };

        let args = params
            .and_then(|p| p.get("arguments"))
            .filter(|a| a.is_object())
            .cloned()
            .unwrap_or_else(|| json!({}));

        match tool.execute(&args) {
            Ok(result) => mcp_tool_result(id, &result),
            Err(e) => mcp_tool_error(id, &e.0),
        }
    }

    fn handle_resources_list(&self, id: Option<&Value>) -> Value {
        let mut resources = Vec::new();
        for p in &self.providers {
            for r in p.list() {
                resources.push(json!({
                    "uri": r.uri,
                    "name": r.name,
                    "description": r.description.clone().unwrap_or_else(|| r.name.clone()),
                    "mimeType": r.mime_type,
                }));
            }
        }
        mcp_result(id, json!({ "resources": resources }))
    }

    fn handle_resources_read(&self, id: Option<&Value>, params: Option<&Value>) -> Value {
        let uri = params.and_then(|p| p.get("uri")).and_then(|u| u.as_str());
        let uri = match uri {
            Some(u) if !u.trim().is_empty() => u,
            _ => return mcp_error_obj(id, -32602, "Invalid params: 'uri' is required"),
        };

        let provider = self.providers.iter().find(|p| {
            uri.to_lowercase().starts_with(&p.uri_scheme().to_lowercase())
        });
        let Some(provider) = provider else {
            return mcp_error_obj(id, -32602, &format!("No provider for URI scheme: {uri}"));
        };

        match provider.read(uri) {
            Some(content) => mcp_result(
                id,
                json!({
                    "contents": [
                        { "uri": content.uri, "mimeType": content.mime_type, "text": content.text }
                    ]
                }),
            ),
            None => mcp_error_obj(id, -32602, &format!("Resource not found: {uri}")),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Envelope helpers (mirror McpResult / McpToolResult / McpToolError / McpErrorObj)
// ─────────────────────────────────────────────────────────────────────────────

fn handle_initialize(id: Option<&Value>, info: &McpServerInfo) -> Value {
    mcp_result(
        id,
        json!({
            "protocolVersion": "2024-11-05",
            "serverInfo": { "name": info.name, "version": info.version },
            "capabilities": {
                "tools": { "listChanged": false },
                "resources": { "listChanged": false, "subscribe": false },
            },
        }),
    )
}

/// The C# serialises the id via `id?.ToJsonString()` — i.e. the id field of the
/// envelope is a *string* holding the JSON text of the request id (or `null`).
fn id_to_json_string(id: Option<&Value>) -> Value {
    match id {
        Some(v) if !v.is_null() => Value::String(v.to_string()),
        _ => Value::Null,
    }
}

fn mcp_result(id: Option<&Value>, result: Value) -> Value {
    json!({ "jsonrpc": "2.0", "id": id_to_json_string(id), "result": result })
}

fn mcp_tool_result(id: Option<&Value>, data: &Value) -> Value {
    mcp_result(
        id,
        json!({
            "content": [ { "type": "text", "text": serde_json::to_string(data).unwrap_or_else(|_| "null".to_string()) } ],
            "isError": false,
        }),
    )
}

fn mcp_tool_error(id: Option<&Value>, message: &str) -> Value {
    mcp_result(
        id,
        json!({
            "content": [ { "type": "text", "text": message } ],
            "isError": true,
        }),
    )
}

fn mcp_error_obj(id: Option<&Value>, code: i32, message: &str) -> Value {
    json!({ "jsonrpc": "2.0", "id": id_to_json_string(id), "error": { "code": code, "message": message } })
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory tool + provider (deterministic defaults / test doubles)
// ─────────────────────────────────────────────────────────────────────────────

/// A closure-backed [`IMcpTool`] — the simplest way to register a deterministic
/// tool in tests or lightweight hosts.
pub struct FnMcpTool {
    name: String,
    description: String,
    input_schema: Value,
    handler: Box<dyn Fn(&Value) -> Result<Value, McpToolError> + Send + Sync>,
}

impl FnMcpTool {
    pub fn new(
        name: impl Into<String>,
        description: impl Into<String>,
        input_schema: Value,
        handler: impl Fn(&Value) -> Result<Value, McpToolError> + Send + Sync + 'static,
    ) -> Self {
        Self {
            name: name.into(),
            description: description.into(),
            input_schema,
            handler: Box::new(handler),
        }
    }
}

impl IMcpTool for FnMcpTool {
    fn name(&self) -> &str {
        &self.name
    }
    fn description(&self) -> &str {
        &self.description
    }
    fn input_schema(&self) -> Value {
        self.input_schema.clone()
    }
    fn execute(&self, arguments: &Value) -> Result<Value, McpToolError> {
        (self.handler)(arguments)
    }
}

/// An in-memory [`IMcpResourceProvider`] backed by a fixed map of
/// uri → [`McpResourceContent`], all under one scheme.
pub struct InMemoryResourceProvider {
    scheme: String,
    resources: Vec<(McpResource, McpResourceContent)>,
}

impl InMemoryResourceProvider {
    pub fn new(scheme: impl Into<String>) -> Self {
        Self {
            scheme: scheme.into(),
            resources: Vec::new(),
        }
    }

    /// Add a resource + its content.
    pub fn add(
        mut self,
        uri: impl Into<String>,
        name: impl Into<String>,
        mime_type: impl Into<String>,
        text: impl Into<String>,
    ) -> Self {
        let uri = uri.into();
        let mime_type = mime_type.into();
        let text = text.into();
        let name = name.into();
        self.resources.push((
            McpResource {
                uri: uri.clone(),
                name,
                description: None,
                mime_type: mime_type.clone(),
            },
            McpResourceContent {
                uri,
                mime_type,
                text,
            },
        ));
        self
    }
}

impl IMcpResourceProvider for InMemoryResourceProvider {
    fn uri_scheme(&self) -> &str {
        &self.scheme
    }

    fn list(&self) -> Vec<McpResource> {
        self.resources.iter().map(|(r, _)| r.clone()).collect()
    }

    fn read(&self, uri: &str) -> Option<McpResourceContent> {
        self.resources
            .iter()
            .find(|(r, _)| r.uri == uri)
            .map(|(_, c)| c.clone())
    }
}
