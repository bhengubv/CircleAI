//! endpoints.rs
//!
//! Transport-agnostic surfaces that expose an [`IAIService`]. Ported from
//! `IAIEndpoint.cs`, `Endpoints/InProcessEndpoint.cs`,
//! `Endpoints/HttpLoopbackEndpoint.cs`, `Endpoints/AIHttpClient.cs`, and
//! `AIApiClient.cs`.
//!
//! The C# `HttpLoopbackEndpoint` binds a real `HttpListener` on `127.0.0.1`;
//! the C# clients own a real `HttpClient`. Per the in-memory porting brief, the
//! actual socket is the host's concern — this port models the deterministic,
//! load-bearing wire logic:
//!   * token auth (constant-time compare on `X-Butler-Token`)
//!   * route dispatch (`/butler/ask|chat|stream|tool`)
//!   * JSON request/response DTO shapes
//!   * SSE framing (`data: <json>` per chunk + a trailing `event: done` frame)
//!
//! [`HttpLoopbackEndpoint::handle`] turns a parsed [`HttpRequest`] into an
//! [`HttpResponse`] exactly as the C# route handlers do; a host wires that to
//! whatever listener it likes. The clients ([`AIHttpClient`], [`AIApiClient`])
//! talk over an injected [`IButlerTransport`] so the "network" is deterministic
//! in tests — an in-process transport routes straight into the endpoint.

use serde_json::{json, Value};

use crate::inference::{ChatMessage, GenerationOptions};
use crate::memory::{FeedbackPolarity, FeedbackSignal};
use crate::tools::{ToolInvocation, ToolResult};

pub use super::service::{HostingError, IAIService};
use super::HostAIOptions;

// ─────────────────────────────────────────────────────────────────────────────
// IAIEndpoint + InProcessEndpoint
// ─────────────────────────────────────────────────────────────────────────────

/// Transport-agnostic endpoint that exposes an [`IAIService`]. 1:1 with the C#
/// `IAIEndpoint` (minus `IAsyncDisposable` — Rust `Drop` covers teardown).
pub trait IAIEndpoint<'svc> {
    /// Begin serving requests against `service`. Idempotent.
    fn start(&mut self, service: &'svc dyn IAIService) -> Result<(), HostingError>;

    /// Stop accepting new requests.
    fn stop(&mut self) -> Result<(), HostingError>;
}

/// In-process endpoint. No transport — it just holds the service reference
/// behind [`Self::service_accessor`] so callers can invoke it as a regular
/// object. 1:1 with the C# `InProcessEndpoint`.
#[derive(Default)]
pub struct InProcessEndpoint<'svc> {
    service: Option<&'svc dyn IAIService>,
    started: bool,
}

impl<'svc> InProcessEndpoint<'svc> {
    pub fn new() -> Self {
        Self {
            service: None,
            started: false,
        }
    }

    /// The wrapped service. `None` until [`start`](IAIEndpoint::start) has run.
    pub fn service_accessor(&self) -> Option<&'svc dyn IAIService> {
        self.service
    }
}

impl<'svc> IAIEndpoint<'svc> for InProcessEndpoint<'svc> {
    fn start(&mut self, service: &'svc dyn IAIService) -> Result<(), HostingError> {
        if self.started {
            return Ok(());
        }
        self.service = Some(service);
        self.started = true;
        Ok(())
    }

    fn stop(&mut self) -> Result<(), HostingError> {
        self.started = false;
        self.service = None;
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HTTP request/response value types (transport-neutral)
// ─────────────────────────────────────────────────────────────────────────────

/// A parsed HTTP request handed to [`HttpLoopbackEndpoint::handle`]. The host's
/// listener fills this from the wire; the port never touches a socket.
#[derive(Debug, Clone)]
pub struct HttpRequest {
    /// HTTP method (e.g. `"POST"`).
    pub method: String,
    /// Absolute path (e.g. `"/butler/ask"`).
    pub path: String,
    /// Value of the `X-Butler-Token` header, if present.
    pub token: Option<String>,
    /// Raw request body (UTF-8 JSON).
    pub body: String,
}

impl HttpRequest {
    /// Convenience constructor for a `POST` with a bearer token.
    pub fn post(path: impl Into<String>, token: impl Into<String>, body: impl Into<String>) -> Self {
        Self {
            method: "POST".to_string(),
            path: path.into(),
            token: Some(token.into()),
            body: body.into(),
        }
    }
}

/// The response produced by [`HttpLoopbackEndpoint::handle`]. For streaming
/// (`/butler/stream`) the SSE frames are pre-materialised into [`Self::body`]
/// exactly as the C# `HandleStreamAsync` would write them to the wire.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HttpResponse {
    /// HTTP status code.
    pub status: u16,
    /// `Content-Type` header value.
    pub content_type: String,
    /// Response body.
    pub body: String,
}

impl HttpResponse {
    fn plain(status: u16, text: impl Into<String>) -> Self {
        Self {
            status,
            content_type: "text/plain; charset=utf-8".to_string(),
            body: text.into(),
        }
    }

    fn json(status: u16, value: &Value) -> Self {
        Self {
            status,
            content_type: "application/json; charset=utf-8".to_string(),
            body: serde_json::to_string(value).unwrap_or_else(|_| "null".to_string()),
        }
    }

    fn event_stream(body: String) -> Self {
        Self {
            status: 200,
            content_type: "text/event-stream".to_string(),
            body,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// HttpLoopbackEndpoint
// ─────────────────────────────────────────────────────────────────────────────

/// Loopback HTTP transport for [`IAIService`]. The C# type binds a real
/// `HttpListener` on `127.0.0.1`; this port exposes [`Self::handle`], the pure
/// request→response function the C# route handlers implement. A host wires it to
/// any listener. 1:1 with the C# `HttpLoopbackEndpoint` route + auth semantics.
pub struct HttpLoopbackEndpoint<'svc> {
    token: String,
    bound_port: u16,
    service: Option<&'svc dyn IAIService>,
    started: bool,
}

impl<'svc> HttpLoopbackEndpoint<'svc> {
    /// Constructs the endpoint from [`HostAIOptions`] without binding. The token
    /// is taken from [`HostAIOptions::loopback_token`], or a random one when
    /// unset (mirrors the C# constructor deferring to `StartAsync`).
    pub fn new(options: &HostAIOptions) -> Self {
        let token = match &options.loopback_token {
            Some(t) if !t.is_empty() => t.clone(),
            _ => HostAIOptions::generate_random_token(),
        };
        Self {
            token,
            bound_port: options.loopback_port,
            service: None,
            started: false,
        }
    }

    /// The bound port (`0` when the host lets the OS assign one). Mirrors the C#
    /// `BoundPort`.
    pub fn bound_port(&self) -> u16 {
        self.bound_port
    }

    /// The effective shared-secret token. Mirrors the C# `Token`.
    pub fn token(&self) -> &str {
        &self.token
    }

    /// Handle one request against the bound service, returning the response the
    /// C# route handlers would write. Requires [`start`](IAIEndpoint::start) to
    /// have bound a service.
    pub fn handle(&self, request: &HttpRequest) -> HttpResponse {
        // 1. Auth — constant-time compare on X-Butler-Token.
        if !self.authorise(request) {
            return HttpResponse::plain(401, "unauthorised");
        }

        // 2. Method gate.
        if !request.method.eq_ignore_ascii_case("POST") {
            return HttpResponse::plain(405, "method not allowed");
        }

        let service = match self.service {
            Some(s) => s,
            None => return HttpResponse::plain(500, "internal error"),
        };

        match request.path.as_str() {
            "/butler/ask" => handle_ask(service, &request.body),
            "/butler/chat" => handle_chat(service, &request.body),
            "/butler/stream" => handle_stream(service, &request.body),
            "/butler/tool" => handle_tool(service, &request.body),
            _ => HttpResponse::plain(404, "not found"),
        }
    }

    fn authorise(&self, request: &HttpRequest) -> bool {
        if self.token.is_empty() {
            return false;
        }
        match &request.token {
            Some(supplied) if !supplied.is_empty() => cryptographic_equals(supplied, &self.token),
            _ => false,
        }
    }
}

impl<'svc> IAIEndpoint<'svc> for HttpLoopbackEndpoint<'svc> {
    fn start(&mut self, service: &'svc dyn IAIService) -> Result<(), HostingError> {
        if self.started {
            return Ok(());
        }
        self.service = Some(service);
        self.started = true;
        Ok(())
    }

    fn stop(&mut self) -> Result<(), HostingError> {
        self.started = false;
        self.service = None;
        Ok(())
    }
}

/// Constant-time string compare (mirrors the C# `CryptographicEquals`).
fn cryptographic_equals(a: &str, b: &str) -> bool {
    let (ab, bb) = (a.as_bytes(), b.as_bytes());
    if ab.len() != bb.len() {
        return false;
    }
    let mut diff = 0u8;
    for i in 0..ab.len() {
        diff |= ab[i] ^ bb[i];
    }
    diff == 0
}

// ── Route handlers (mirror HandleAsk/Chat/Stream/Tool) ──────────────────────

fn handle_ask(service: &dyn IAIService, body: &str) -> HttpResponse {
    let payload: Value = match serde_json::from_str(body) {
        Ok(v) => v,
        Err(_) => return HttpResponse::plain(400, "missing 'question'"),
    };
    let question = payload.get("question").and_then(|q| q.as_str()).unwrap_or("");
    if question.trim().is_empty() {
        return HttpResponse::plain(400, "missing 'question'");
    }
    match service.ask(question) {
        Ok(answer) => HttpResponse::plain(200, answer),
        Err(e) => HttpResponse::plain(500, e.to_string()),
    }
}

fn parse_chat_payload(body: &str) -> Option<(Vec<ChatMessage>, Option<GenerationOptions>)> {
    let payload: Value = serde_json::from_str(body).ok()?;
    let arr = payload.get("messages")?.as_array()?;
    if arr.is_empty() {
        return None;
    }
    let messages = arr
        .iter()
        .map(|m| {
            let role = m.get("role").and_then(|r| r.as_str()).unwrap_or("user");
            let content = m.get("content").and_then(|c| c.as_str()).unwrap_or("");
            ChatMessage::new(role, content)
        })
        .collect();
    let options = payload
        .get("options")
        .filter(|o| !o.is_null())
        .map(parse_generation_options);
    Some((messages, options))
}

/// Reads a partial `GenerationOptions` DTO, falling back to defaults for absent
/// fields (mirrors the C# `GenerationOptionsPayload.ToGenerationOptions`).
fn parse_generation_options(o: &Value) -> GenerationOptions {
    let d = GenerationOptions::default();
    GenerationOptions {
        max_tokens: o.get("maxTokens").and_then(|v| v.as_i64()).map(|v| v as i32).unwrap_or(d.max_tokens),
        temperature: o.get("temperature").and_then(|v| v.as_f64()).map(|v| v as f32).unwrap_or(d.temperature),
        top_p: o.get("topP").and_then(|v| v.as_f64()).map(|v| v as f32).unwrap_or(d.top_p),
        top_k: o.get("topK").and_then(|v| v.as_i64()).map(|v| v as i32).unwrap_or(d.top_k),
        seed: o.get("seed").and_then(|v| v.as_i64()).map(|v| v as i32),
        stop_sequences: o
            .get("stopSequences")
            .and_then(|v| v.as_array())
            .map(|a| a.iter().filter_map(|x| x.as_str().map(|s| s.to_string())).collect()),
        ..d
    }
}

fn handle_chat(service: &dyn IAIService, body: &str) -> HttpResponse {
    let Some((messages, options)) = parse_chat_payload(body) else {
        return HttpResponse::plain(400, "missing 'messages'");
    };
    match service.chat(&messages, options.as_ref()) {
        Ok(content) => HttpResponse::json(200, &json!({ "content": content })),
        Err(e) => HttpResponse::plain(500, e.to_string()),
    }
}

fn handle_stream(service: &dyn IAIService, body: &str) -> HttpResponse {
    let Some((messages, options)) = parse_chat_payload(body) else {
        return HttpResponse::plain(400, "missing 'messages'");
    };
    match service.stream(&messages, options.as_ref()) {
        Ok(chunks) => HttpResponse::event_stream(render_sse(&chunks)),
        Err(e) => HttpResponse::plain(500, e.to_string()),
    }
}

/// Renders SSE frames exactly as the C# `HandleStreamAsync` writes them: one
/// `data: <json-encoded-chunk>` frame per chunk (blank-line separated), then a
/// closing `event: done` / `data: {}` frame.
fn render_sse(chunks: &[String]) -> String {
    let mut out = String::new();
    for chunk in chunks {
        out.push_str("data: ");
        out.push_str(&serde_json::to_string(chunk).unwrap_or_else(|_| "\"\"".to_string()));
        out.push('\n');
        out.push('\n');
    }
    out.push_str("event: done\n");
    out.push_str("data: {}\n");
    out.push('\n');
    out
}

fn handle_tool(service: &dyn IAIService, body: &str) -> HttpResponse {
    let payload: Value = match serde_json::from_str(body) {
        Ok(v) => v,
        Err(_) => return HttpResponse::plain(400, "missing 'toolName'"),
    };
    let tool_name = payload.get("toolName").and_then(|t| t.as_str()).unwrap_or("");
    if tool_name.trim().is_empty() {
        return HttpResponse::plain(400, "missing 'toolName'");
    }
    let mut args = std::collections::HashMap::new();
    if let Some(obj) = payload.get("arguments").and_then(|a| a.as_object()) {
        for (k, v) in obj {
            args.insert(k.clone(), v.clone());
        }
    }
    let invocation = ToolInvocation::new(tool_name, args);
    match service.invoke_tool(&invocation) {
        Ok(result) => {
            let status = if result.success { 200 } else { 502 };
            HttpResponse::json(status, &tool_result_to_json(&result))
        }
        Err(e) => HttpResponse::plain(500, e.to_string()),
    }
}

fn tool_result_to_json(r: &ToolResult) -> Value {
    json!({
        "toolName": r.tool_name,
        "success": r.success,
        "result": r.result,
        "error": r.error,
    })
}

// ─────────────────────────────────────────────────────────────────────────────
// IButlerTransport — deterministic "network" for the HTTP clients
// ─────────────────────────────────────────────────────────────────────────────

/// The request/response boundary the HTTP clients ([`AIHttpClient`],
/// [`AIApiClient`]) talk over. Real hosts back this with a socket; tests back it
/// with an in-process transport that routes into an endpoint. Injecting it keeps
/// the client wire logic deterministic without touching the network.
pub trait IButlerTransport: Send + Sync {
    /// Send one request; return the response (or a transport error message).
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, String>;
}

/// A shared reference to a transport is itself a transport — lets a client
/// borrow a transport the caller keeps a handle to (e.g. to assert on it).
impl<T: IButlerTransport + ?Sized> IButlerTransport for &T {
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, String> {
        (**self).send(request)
    }
}

/// A transport that routes every request straight into a
/// [`HttpLoopbackEndpoint::handle`] — the in-process analogue of a loopback
/// socket. Deterministic; used by tests and single-process hosts.
pub struct InProcessLoopbackTransport<'a, 'svc> {
    endpoint: &'a HttpLoopbackEndpoint<'svc>,
}

impl<'a, 'svc> InProcessLoopbackTransport<'a, 'svc> {
    pub fn new(endpoint: &'a HttpLoopbackEndpoint<'svc>) -> Self {
        Self { endpoint }
    }
}

impl IButlerTransport for InProcessLoopbackTransport<'_, '_> {
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, String> {
        // Resolve the client's path against the loopback root, exactly as the
        // C# `HttpClient` resolves a relative request URI against its
        // `BaseAddress` of `http://127.0.0.1:{port}/`. The clients emit
        // relative paths (e.g. `butler/ask`); the endpoint routes on the
        // absolute wire path (`/butler/ask`). Without this the endpoint would
        // 404 every relative request.
        if request.path.starts_with('/') {
            Ok(self.endpoint.handle(request))
        } else {
            let mut resolved = request.clone();
            resolved.path = format!("/{}", request.path);
            Ok(self.endpoint.handle(&resolved))
        }
    }
}

/// Records every request it is asked to send and replays a queued response —
/// for tests that assert on client-side wire shaping.
#[derive(Default)]
pub struct RecordingButlerTransport {
    sent: std::sync::Mutex<Vec<HttpRequest>>,
    responses: std::sync::Mutex<std::collections::VecDeque<HttpResponse>>,
}

impl RecordingButlerTransport {
    pub fn new() -> Self {
        Self::default()
    }

    /// Queue a response to be returned by the next [`send`](IButlerTransport::send).
    pub fn push_response(&self, response: HttpResponse) {
        self.responses.lock().unwrap().push_back(response);
    }

    /// Snapshot of every request sent so far.
    pub fn sent(&self) -> Vec<HttpRequest> {
        self.sent.lock().unwrap().clone()
    }
}

impl IButlerTransport for RecordingButlerTransport {
    fn send(&self, request: &HttpRequest) -> Result<HttpResponse, String> {
        self.sent.lock().unwrap().push(request.clone());
        self.responses
            .lock()
            .unwrap()
            .pop_front()
            .ok_or_else(|| "no queued response".to_string())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AIHttpClient — talks to a HttpLoopbackEndpoint
// ─────────────────────────────────────────────────────────────────────────────

/// Client for a [`HttpLoopbackEndpoint`]. Methods mirror [`IAIService`] so the
/// same call sites work in-process or out-of-process. 1:1 with the C#
/// `AIHttpClient` (request shaping + SSE parsing).
pub struct AIHttpClient<T: IButlerTransport> {
    transport: T,
    token: String,
}

impl<T: IButlerTransport> AIHttpClient<T> {
    /// Constructs the client over a transport + shared-secret token.
    pub fn new(transport: T, token: impl Into<String>) -> Self {
        Self {
            transport,
            token: token.into(),
        }
    }

    /// Mirrors [`IAIService::ask`].
    pub fn ask(&self, question: &str) -> Result<String, String> {
        assert!(!question.is_empty(), "question required");
        let body = json!({ "question": question }).to_string();
        let resp = self
            .transport
            .send(&HttpRequest::post("butler/ask", &self.token, body))?;
        ensure_success(&resp)?;
        Ok(resp.body)
    }

    /// Mirrors [`IAIService::chat`].
    pub fn chat(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<String, String> {
        let body = chat_request_json(messages, options).to_string();
        let resp = self
            .transport
            .send(&HttpRequest::post("butler/chat", &self.token, body))?;
        ensure_success(&resp)?;
        let parsed: Value = serde_json::from_str(&resp.body).map_err(|e| e.to_string())?;
        Ok(parsed
            .get("content")
            .and_then(|c| c.as_str())
            .unwrap_or("")
            .to_string())
    }

    /// Mirrors [`IAIService::stream`] — parses the SSE frames back into chunks.
    /// Stops at `event: done`, skips blank separators, JSON-decodes each
    /// `data:` payload (1:1 with the C# `StreamAsync` parser).
    pub fn stream(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<Vec<String>, String> {
        let body = chat_request_json(messages, options).to_string();
        let resp = self
            .transport
            .send(&HttpRequest::post("butler/stream", &self.token, body))?;
        ensure_success(&resp)?;
        Ok(parse_sse(&resp.body))
    }

    /// Mirrors [`IAIService::invoke_tool`].
    pub fn invoke_tool(&self, invocation: &ToolInvocation) -> Result<ToolResult, String> {
        let body = json!({
            "toolName": invocation.tool_name,
            "arguments": invocation.arguments,
        })
        .to_string();
        let resp = self
            .transport
            .send(&HttpRequest::post("butler/tool", &self.token, body))?;
        // Accept 200 (success) and 502 (tool failure) — both carry a ToolResult.
        if resp.status != 200 && resp.status != 502 {
            return Err(format!("transport error {}", resp.status));
        }
        json_to_tool_result(&resp.body).ok_or_else(|| "Empty response from Butler endpoint.".to_string())
    }
}

/// Parses SSE frames into the ordered list of content chunks, mirroring the C#
/// `AIHttpClient.StreamAsync` loop: skip blanks, terminate on `event: done`,
/// JSON-decode `data:` payloads.
fn parse_sse(body: &str) -> Vec<String> {
    let mut out = Vec::new();
    for line in body.lines() {
        if line.is_empty() {
            continue;
        }
        if let Some(rest) = line.strip_prefix("event:") {
            if rest.trim() == "done" {
                break;
            }
            continue;
        }
        let Some(rest) = line.strip_prefix("data:") else {
            continue;
        };
        let data = rest.trim_start();
        if data.is_empty() {
            continue;
        }
        // Server always sends JSON-encoded strings; tolerate plain text.
        let piece = serde_json::from_str::<String>(data).unwrap_or_else(|_| data.to_string());
        if !piece.is_empty() {
            out.push(piece);
        }
    }
    out
}

fn chat_request_json(messages: &[ChatMessage], options: Option<&GenerationOptions>) -> Value {
    let msgs: Vec<Value> = messages
        .iter()
        .map(|m| json!({ "role": m.role, "content": m.content }))
        .collect();
    let opts = options.map(|o| {
        json!({
            "maxTokens": o.max_tokens,
            "temperature": o.temperature,
            "topP": o.top_p,
            "topK": o.top_k,
            "seed": o.seed,
            "stopSequences": o.stop_sequences,
        })
    });
    json!({ "messages": msgs, "options": opts })
}

fn json_to_tool_result(body: &str) -> Option<ToolResult> {
    let v: Value = serde_json::from_str(body).ok()?;
    Some(ToolResult {
        tool_name: v.get("toolName").and_then(|t| t.as_str()).unwrap_or("").to_string(),
        success: v.get("success").and_then(|s| s.as_bool()).unwrap_or(false),
        result: v.get("result").cloned().filter(|r| !r.is_null()),
        error: v.get("error").and_then(|e| e.as_str()).map(|s| s.to_string()),
    })
}

fn ensure_success(resp: &HttpResponse) -> Result<(), String> {
    if (200..300).contains(&resp.status) {
        Ok(())
    } else {
        Err(format!("HTTP {}: {}", resp.status, resp.body))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AIApiClient — IAIService proxy to a remote ButlerAPI
// ─────────────────────────────────────────────────────────────────────────────

/// [`IAIService`] that proxies requests to a remote ButlerAPI over an injected
/// [`IButlerTransport`]. 1:1 with the C# `AIApiClient` (routes under
/// `api/butler/*`, JSON DTOs, SSE stream, `/health` readiness probe). The C#
/// owns a `HttpClient`; the port takes a transport so the "network" is
/// deterministic in tests.
pub struct AIApiClient<T: IButlerTransport> {
    transport: T,
    bearer_token: Option<String>,
    ready: std::sync::Mutex<bool>,
}

impl<T: IButlerTransport> AIApiClient<T> {
    /// Constructs the client. `bearer_token` is echoed on every request.
    pub fn new(transport: T, bearer_token: Option<String>) -> Self {
        Self {
            transport,
            bearer_token,
            ready: std::sync::Mutex::new(false),
        }
    }

    fn request(&self, path: &str, body: String) -> HttpRequest {
        HttpRequest {
            method: "POST".to_string(),
            path: path.to_string(),
            token: self.bearer_token.clone(),
            body,
        }
    }
}

impl<T: IButlerTransport> IAIService for AIApiClient<T> {
    fn is_ready(&self) -> bool {
        *self.ready.lock().unwrap()
    }

    fn start(&self) -> Result<(), HostingError> {
        // GET api/butler/health — confirm the remote is ready.
        let req = HttpRequest {
            method: "GET".to_string(),
            path: "api/butler/health".to_string(),
            token: self.bearer_token.clone(),
            body: String::new(),
        };
        let resp = self.transport.send(&req).map_err(HostingError::Failed)?;
        if !(200..300).contains(&resp.status) {
            return Err(HostingError::Failed(format!("health check failed: {}", resp.status)));
        }
        *self.ready.lock().unwrap() = true;
        Ok(())
    }

    fn stop(&self) -> Result<(), HostingError> {
        *self.ready.lock().unwrap() = false;
        Ok(())
    }

    fn ask(&self, question: &str) -> Result<String, HostingError> {
        let body = json!({ "question": question }).to_string();
        let resp = self
            .transport
            .send(&self.request("api/butler/ask", body))
            .map_err(HostingError::Failed)?;
        require_2xx(&resp)?;
        Ok(string_payload(&resp.body))
    }

    fn chat(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<String, HostingError> {
        let body = chat_request_json(messages, options).to_string();
        let resp = self
            .transport
            .send(&self.request("api/butler/chat", body))
            .map_err(HostingError::Failed)?;
        require_2xx(&resp)?;
        Ok(string_payload(&resp.body))
    }

    fn stream(
        &self,
        messages: &[ChatMessage],
        options: Option<&GenerationOptions>,
    ) -> Result<Vec<String>, HostingError> {
        let body = chat_request_json(messages, options).to_string();
        let resp = self
            .transport
            .send(&self.request("api/butler/stream", body))
            .map_err(HostingError::Failed)?;
        require_2xx(&resp)?;
        // ButlerAPI frames as `data: <token>` with a `[DONE]` sentinel.
        Ok(parse_sse_tokens(&resp.body))
    }

    fn invoke_tool(&self, invocation: &ToolInvocation) -> Result<ToolResult, HostingError> {
        let body = json!({
            "name": invocation.tool_name,
            "arguments": invocation.arguments,
        })
        .to_string();
        let resp = self
            .transport
            .send(&self.request("api/butler/tool", body))
            .map_err(HostingError::Failed)?;
        require_2xx(&resp)?;
        Ok(json_to_tool_result(&resp.body)
            .unwrap_or_else(|| ToolResult::failure(&invocation.tool_name, "Empty response from cloud")))
    }

    fn agentic_chat(
        &self,
        prompt: &str,
        options: Option<&GenerationOptions>,
    ) -> Result<String, HostingError> {
        let opts = options.map(|o| {
            json!({
                "maxTokens": o.max_tokens,
                "temperature": o.temperature,
                "topP": o.top_p,
                "topK": o.top_k,
                "seed": o.seed,
                "stopSequences": o.stop_sequences,
            })
        });
        let body = json!({ "prompt": prompt, "options": opts }).to_string();
        let resp = self
            .transport
            .send(&self.request("api/butler/agentic", body))
            .map_err(HostingError::Failed)?;
        require_2xx(&resp)?;
        Ok(string_payload(&resp.body))
    }

    fn submit_feedback(&self, signal: FeedbackSignal) -> Result<(), HostingError> {
        let polarity = match signal.polarity {
            FeedbackPolarity::Positive => "Positive",
            FeedbackPolarity::Negative => "Negative",
            FeedbackPolarity::Correction => "Correction",
        };
        let body = json!({
            "id": signal.id.to_string(),
            "polarity": polarity,
            "userText": signal.user_text,
            "assistantText": signal.assistant_text,
            "comment": signal.comment,
        })
        .to_string();
        let resp = self
            .transport
            .send(&self.request("api/butler/feedback", body))
            .map_err(HostingError::Failed)?;
        require_2xx(&resp)?;
        Ok(())
    }
}

/// Reads a `{ "text": ... }` payload, defaulting to empty (mirrors the C#
/// `StringPayload?.Text ?? string.Empty`).
fn string_payload(body: &str) -> String {
    serde_json::from_str::<Value>(body)
        .ok()
        .and_then(|v| v.get("text").and_then(|t| t.as_str()).map(|s| s.to_string()))
        .unwrap_or_default()
}

/// Parses ButlerAPI SSE tokens: `data: <token>` frames, terminating on the
/// `[DONE]` sentinel (mirrors the C# `AIApiClient.StreamAsync`).
fn parse_sse_tokens(body: &str) -> Vec<String> {
    let mut out = Vec::new();
    for line in body.lines() {
        let Some(rest) = line.strip_prefix("data:") else {
            continue;
        };
        let token = rest.trim();
        if token == "[DONE]" {
            break;
        }
        if !token.is_empty() {
            out.push(token.to_string());
        }
    }
    out
}

fn require_2xx(resp: &HttpResponse) -> Result<(), HostingError> {
    if (200..300).contains(&resp.status) {
        Ok(())
    } else {
        Err(HostingError::Failed(format!("HTTP {}: {}", resp.status, resp.body)))
    }
}
