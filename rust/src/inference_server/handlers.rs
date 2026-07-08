//! handlers.rs
//!
//! In-memory ports of the server's HTTP handlers + their supporting gate/counter
//! types. Ported from `CircleAI.Inference.Server/`:
//!   - `Hosting/AdmissionControl.cs`  → [`AdmissionControl`]
//!   - `Models/ServerCounters.cs`     → [`ServerCounters`]
//!   - `Endpoints/ChatCompletionsEndpoint.cs` → [`ChatCompletionsHandler`]
//!   - `Endpoints/EmbeddingsEndpoint.cs`      → [`EmbeddingsHandler`]
//!   - `Endpoints/CompanionEndpoint.cs`       → [`CompanionHandler`]
//!   - `Endpoints/AdminEndpoints.cs`          → [`AdminHandler`]
//!
//! Per the porting brief, no real socket server is stood up. Each handler is an
//! in-process function behind a struct that returns a typed result carrying the
//! HTTP status + body, so the exact routing/validation/status-mapping logic is
//! preserved and testable.

use std::sync::Arc;

use chrono::Utc;
use uuid::Uuid;

use crate::inference_server::bridge::{
    BackendKind, CapabilityTier, InferenceFragmentKind, InferenceRequest, InferenceStatus,
};
use crate::inference_server::companion_resolver::ICompanionSessionResolver;
use crate::inference_server::lifecycle::{
    IModelLifecycleManager, LoadOutcome, ModelLoadDescriptor, UnloadOutcome,
};
use crate::inference_server::openai::{
    ChatCompletionChoice, ChatCompletionDelta, ChatCompletionMessage, ChatCompletionRequest,
    ChatCompletionResponse, ChatCompletionStreamChoice, ChatCompletionStreamChunk,
    EmbeddingDatum, EmbeddingsRequest, EmbeddingsResponse, ErrorResponse, UsageInfo,
};
use crate::inference_server::registry::IInferenceServerModelRegistry;

/// HTTP status codes used by the handlers (subset of what the endpoints return).
pub mod status {
    pub const OK: u16 = 200;
    pub const BAD_REQUEST: u16 = 400;
    pub const NOT_FOUND: u16 = 404;
    pub const INTERNAL_SERVER_ERROR: u16 = 500;
    pub const SERVICE_UNAVAILABLE: u16 = 503;
    pub const GATEWAY_TIMEOUT: u16 = 504;
}

/// A rendered handler outcome: the HTTP status plus the JSON body. `Empty` maps
/// the C# `Results.Empty` (headers/body written directly, as for streaming).
#[derive(Debug, Clone, PartialEq)]
pub enum HandlerResult<T> {
    /// A JSON body with a status code.
    Json(u16, T),
    /// An OpenAI error envelope with a status code.
    Error(u16, ErrorResponse),
    /// No body (streaming already emitted its frames).
    Empty,
}

// ─────────────────────────────────────────────────────────────────────────────
// ServerCounters
// ─────────────────────────────────────────────────────────────────────────────

/// Thread-safe server-wide counters. Sync port of `ServerCounters`.
#[derive(Debug, Default)]
pub struct ServerCounters {
    total: std::sync::atomic::AtomicI64,
    rejected: std::sync::atomic::AtomicI64,
    failed: std::sync::atomic::AtomicI64,
    active: std::sync::atomic::AtomicI64,
}

impl ServerCounters {
    /// Constructs zeroed counters.
    pub fn new() -> Self {
        Self::default()
    }

    /// Total requests accepted (including those that later failed).
    pub fn total_requests(&self) -> i64 {
        self.total.load(std::sync::atomic::Ordering::SeqCst)
    }
    /// Requests rejected at admission.
    pub fn rejected_requests(&self) -> i64 {
        self.rejected.load(std::sync::atomic::Ordering::SeqCst)
    }
    /// Requests that admitted but failed downstream.
    pub fn failed_requests(&self) -> i64 {
        self.failed.load(std::sync::atomic::Ordering::SeqCst)
    }
    /// Requests currently in flight.
    pub fn active_requests(&self) -> i64 {
        self.active.load(std::sync::atomic::Ordering::SeqCst)
    }

    /// Mark a request admitted.
    pub fn account_admitted(&self) {
        self.total.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        self.active.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    }
    /// Mark a request completed.
    pub fn account_completed(&self) {
        self.active.fetch_sub(1, std::sync::atomic::Ordering::SeqCst);
    }
    /// Mark a request rejected at admission.
    pub fn account_rejected(&self) {
        self.rejected.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    }
    /// Mark a request failed downstream.
    pub fn account_failed(&self) {
        self.failed.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AdmissionControl
// ─────────────────────────────────────────────────────────────────────────────

/// Bounded admission gate — at most `max_concurrent_requests` in flight. Sync
/// port of `AdmissionControl` (non-blocking `try_enter`; excess → 503 at the
/// endpoint layer). A returned [`AdmissionSlot`] releases on drop.
pub struct AdmissionControl {
    max_concurrent_requests: usize,
    in_flight: Arc<std::sync::atomic::AtomicUsize>,
    counters: Arc<ServerCounters>,
}

impl AdmissionControl {
    /// Constructs the gate. `max_concurrent_requests` is clamped to ≥ 1.
    pub fn new(max_concurrent_requests: usize, counters: Arc<ServerCounters>) -> Self {
        Self {
            max_concurrent_requests: max_concurrent_requests.max(1),
            in_flight: Arc::new(std::sync::atomic::AtomicUsize::new(0)),
            counters,
        }
    }

    /// Maximum admitted-at-once requests.
    pub fn max_concurrent_requests(&self) -> usize {
        self.max_concurrent_requests
    }

    /// Attempt to acquire one slot. Returns `Some(slot)` (release on drop) or
    /// `None` when saturated (→ endpoint responds 503).
    pub fn try_enter(&self) -> Option<AdmissionSlot> {
        // CAS loop to atomically bump in-flight while under the cap.
        loop {
            let cur = self.in_flight.load(std::sync::atomic::Ordering::SeqCst);
            if cur >= self.max_concurrent_requests {
                self.counters.account_rejected();
                return None;
            }
            if self
                .in_flight
                .compare_exchange(
                    cur,
                    cur + 1,
                    std::sync::atomic::Ordering::SeqCst,
                    std::sync::atomic::Ordering::SeqCst,
                )
                .is_ok()
            {
                self.counters.account_admitted();
                return Some(AdmissionSlot {
                    in_flight: self.in_flight.clone(),
                    counters: self.counters.clone(),
                    released: false,
                });
            }
        }
    }
}

/// A held admission slot. Releases the gate + accounts completion on drop.
pub struct AdmissionSlot {
    in_flight: Arc<std::sync::atomic::AtomicUsize>,
    counters: Arc<ServerCounters>,
    released: bool,
}

impl Drop for AdmissionSlot {
    fn drop(&mut self) {
        if !self.released {
            self.released = true;
            self.in_flight.fetch_sub(1, std::sync::atomic::Ordering::SeqCst);
            self.counters.account_completed();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ChatCompletionsHandler  (POST /v1/chat/completions)
// ─────────────────────────────────────────────────────────────────────────────

/// A single streamed frame produced by the streaming chat branch.
pub type StreamFrame = ChatCompletionStreamChunk;

/// The streaming chat-completion outcome: the ordered SSE frames plus a trailing
/// `[DONE]` marker flag (the C# `WriteTerminatorAsync`).
#[derive(Debug, Clone, PartialEq)]
pub struct ChatStreamResult {
    pub frames: Vec<StreamFrame>,
    pub done: bool,
}

/// In-memory handler for `POST /v1/chat/completions`. Sync port of
/// `ChatCompletionsEndpoint.HandleAsync` routing/validation/status-mapping.
pub struct ChatCompletionsHandler {
    registry: Arc<dyn IInferenceServerModelRegistry>,
    admission: Arc<AdmissionControl>,
    counters: Arc<ServerCounters>,
}

impl ChatCompletionsHandler {
    /// Constructs the handler over the registry + admission gate + counters.
    pub fn new(
        registry: Arc<dyn IInferenceServerModelRegistry>,
        admission: Arc<AdmissionControl>,
        counters: Arc<ServerCounters>,
    ) -> Self {
        Self {
            registry,
            admission,
            counters,
        }
    }

    /// Handle a non-streaming request. Mirrors the validation → resolve →
    /// admission → `NonStreamResponseAsync` path.
    pub fn handle(&self, body: &ChatCompletionRequest) -> HandlerResult<ChatCompletionResponse> {
        if body.model.trim().is_empty() {
            return HandlerResult::Error(
                status::BAD_REQUEST,
                ErrorResponse::of(
                    "Missing or empty 'model' field.",
                    "invalid_request_error",
                    Some("missing_model"),
                ),
            );
        }
        if body.messages.is_empty() {
            return HandlerResult::Error(
                status::BAD_REQUEST,
                ErrorResponse::of(
                    "Missing 'messages' array.",
                    "invalid_request_error",
                    Some("missing_messages"),
                ),
            );
        }

        let bridge = match self.registry.resolve(&body.model) {
            Some(b) => b,
            None => {
                return HandlerResult::Error(
                    status::NOT_FOUND,
                    ErrorResponse::of(
                        format!("Model '{}' is not loaded.", body.model),
                        "invalid_request_error",
                        Some("model_not_found"),
                    ),
                );
            }
        };

        let _slot = match self.admission.try_enter() {
            Some(s) => s,
            None => {
                return HandlerResult::Error(
                    status::SERVICE_UNAVAILABLE,
                    ErrorResponse::of(
                        format!(
                            "Server is at concurrency cap ({}). Retry after a brief delay.",
                            self.admission.max_concurrent_requests()
                        ),
                        "server_busy",
                        Some("concurrency_cap"),
                    ),
                );
            }
        };

        let request = build_inference_request(body);
        let resp = bridge.complete(&request);

        if resp.status == InferenceStatus::Failed {
            self.counters.account_failed();
            return HandlerResult::Error(
                status::INTERNAL_SERVER_ERROR,
                ErrorResponse::of(
                    resp.failure_message.unwrap_or_else(|| "Inference failed.".to_string()),
                    "internal_error",
                    Some("inference_failed"),
                ),
            );
        }

        let response = ChatCompletionResponse {
            id: format!("chatcmpl-{}", Uuid::new_v4().simple()),
            created: Utc::now().timestamp(),
            model: body.model.clone(),
            choices: vec![ChatCompletionChoice {
                index: 0,
                message: ChatCompletionMessage {
                    role: "assistant".to_string(),
                    content: resp.output_text,
                    name: None,
                    reasoning_content: resp.reasoning_text,
                },
                finish_reason: map_finish(resp.status),
            }],
            usage: UsageInfo {
                prompt_tokens: resp.prompt_token_count,
                completion_tokens: resp.output_token_count,
                total_tokens: resp.prompt_token_count + resp.output_token_count,
            },
            ..ChatCompletionResponse::default()
        };
        HandlerResult::Json(status::OK, response)
    }

    /// Handle a streaming request, producing the ordered SSE frames. Mirrors
    /// `StreamResponseAsync`: a leading role frame, one delta per fragment
    /// (content vs reasoning), a trailing stop frame, then `[DONE]`.
    pub fn handle_stream(
        &self,
        body: &ChatCompletionRequest,
    ) -> Result<ChatStreamResult, HandlerResult<ChatCompletionResponse>> {
        if body.model.trim().is_empty() {
            return Err(HandlerResult::Error(
                status::BAD_REQUEST,
                ErrorResponse::of(
                    "Missing or empty 'model' field.",
                    "invalid_request_error",
                    Some("missing_model"),
                ),
            ));
        }
        if body.messages.is_empty() {
            return Err(HandlerResult::Error(
                status::BAD_REQUEST,
                ErrorResponse::of(
                    "Missing 'messages' array.",
                    "invalid_request_error",
                    Some("missing_messages"),
                ),
            ));
        }
        let bridge = match self.registry.resolve(&body.model) {
            Some(b) => b,
            None => {
                return Err(HandlerResult::Error(
                    status::NOT_FOUND,
                    ErrorResponse::of(
                        format!("Model '{}' is not loaded.", body.model),
                        "invalid_request_error",
                        Some("model_not_found"),
                    ),
                ))
            }
        };
        let _slot = match self.admission.try_enter() {
            Some(s) => s,
            None => {
                return Err(HandlerResult::Error(
                    status::SERVICE_UNAVAILABLE,
                    ErrorResponse::of(
                        format!(
                            "Server is at concurrency cap ({}). Retry after a brief delay.",
                            self.admission.max_concurrent_requests()
                        ),
                        "server_busy",
                        Some("concurrency_cap"),
                    ),
                ))
            }
        };

        let request = build_inference_request(body);
        let id = format!("chatcmpl-{}", Uuid::new_v4().simple());
        let created = Utc::now().timestamp();
        let model = body.model.clone();

        let mut frames: Vec<StreamFrame> = Vec::new();

        // First frame: role announcement.
        frames.push(ChatCompletionStreamChunk {
            id: id.clone(),
            created,
            model: model.clone(),
            choices: vec![ChatCompletionStreamChoice {
                index: 0,
                delta: ChatCompletionDelta {
                    role: Some("assistant".to_string()),
                    ..ChatCompletionDelta::default()
                },
                finish_reason: None,
            }],
            ..ChatCompletionStreamChunk::default()
        });

        for f in bridge.stream_fragments(&request) {
            if f.text.is_empty() {
                continue;
            }
            let delta = match f.kind {
                InferenceFragmentKind::Reasoning => ChatCompletionDelta {
                    reasoning_content: Some(f.text),
                    ..ChatCompletionDelta::default()
                },
                InferenceFragmentKind::Content => ChatCompletionDelta {
                    content: Some(f.text),
                    ..ChatCompletionDelta::default()
                },
            };
            frames.push(ChatCompletionStreamChunk {
                id: id.clone(),
                created,
                model: model.clone(),
                choices: vec![ChatCompletionStreamChoice {
                    index: 0,
                    delta,
                    finish_reason: None,
                }],
                ..ChatCompletionStreamChunk::default()
            });
        }

        // Final frame: stop reason + [DONE].
        frames.push(ChatCompletionStreamChunk {
            id,
            created,
            model,
            choices: vec![ChatCompletionStreamChoice {
                index: 0,
                delta: ChatCompletionDelta::default(),
                finish_reason: Some("stop".to_string()),
            }],
            ..ChatCompletionStreamChunk::default()
        });

        Ok(ChatStreamResult { frames, done: true })
    }
}

/// Builds an [`InferenceRequest`] from an OpenAI body — concatenates messages
/// into a single prompt joined with role markers. Mirrors `BuildInferenceRequest`.
pub fn build_inference_request(body: &ChatCompletionRequest) -> InferenceRequest {
    let prompt = body
        .messages
        .iter()
        .map(|m| format!("<|{}|>\n{}\n<|end|>", m.role, m.content))
        .collect::<Vec<_>>()
        .join("\n");

    let mut metadata = std::collections::BTreeMap::new();
    if let Some(u) = &body.user {
        if !u.is_empty() {
            metadata.insert("user".to_string(), u.clone());
        }
    }

    InferenceRequest {
        id: Uuid::new_v4(),
        model_id: body.model.clone(),
        prompt,
        max_output_tokens: body.max_tokens.unwrap_or(512),
        temperature: body.temperature.unwrap_or(0.7),
        top_p: body.top_p.unwrap_or(0.9),
        stop_sequences: body.stop.clone().unwrap_or_default(),
        metadata,
        requested_at: Utc::now(),
    }
}

/// Maps an [`InferenceStatus`] to the OpenAI `finish_reason` string. Mirrors
/// `MapFinish`.
pub fn map_finish(status: InferenceStatus) -> String {
    match status {
        InferenceStatus::Completed => "stop",
        InferenceStatus::StoppedByToken => "stop",
        InferenceStatus::StoppedByLength => "length",
        InferenceStatus::Cancelled => "cancelled",
        InferenceStatus::Failed => "error",
    }
    .to_string()
}

// ─────────────────────────────────────────────────────────────────────────────
// EmbeddingsHandler  (POST /v1/embeddings)
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory handler for `POST /v1/embeddings`. Resolves the embedder from the
/// registry and produces one [`EmbeddingDatum`] per input string.
pub struct EmbeddingsHandler {
    registry: Arc<dyn IInferenceServerModelRegistry>,
}

impl EmbeddingsHandler {
    /// Constructs the handler over the registry.
    pub fn new(registry: Arc<dyn IInferenceServerModelRegistry>) -> Self {
        Self { registry }
    }

    /// Handle an embeddings request. Validates the model + input, resolves the
    /// embedder, and embeds each input. `prompt_tokens`/`total_tokens` are the
    /// summed char/4 estimate over the inputs (matches the server's estimate).
    pub fn handle(&self, body: &EmbeddingsRequest) -> HandlerResult<EmbeddingsResponse> {
        if body.model.trim().is_empty() {
            return HandlerResult::Error(
                status::BAD_REQUEST,
                ErrorResponse::of(
                    "Missing or empty 'model' field.",
                    "invalid_request_error",
                    Some("missing_model"),
                ),
            );
        }
        let inputs = body.inputs();
        if inputs.is_empty() {
            return HandlerResult::Error(
                status::BAD_REQUEST,
                ErrorResponse::of(
                    "Missing or empty 'input' field.",
                    "invalid_request_error",
                    Some("missing_input"),
                ),
            );
        }
        let embedder = match self.registry.resolve_embedder(&body.model) {
            Some(e) => e,
            None => {
                return HandlerResult::Error(
                    status::NOT_FOUND,
                    ErrorResponse::of(
                        format!("Embedding model '{}' is not loaded.", body.model),
                        "invalid_request_error",
                        Some("model_not_found"),
                    ),
                )
            }
        };

        let mut data = Vec::with_capacity(inputs.len());
        let mut prompt_tokens = 0i32;
        for (index, text) in inputs.iter().enumerate() {
            prompt_tokens += (text.len() as i32 / 4).max(if text.is_empty() { 0 } else { 1 });
            let embedding = match embedder.generate(text) {
                Ok(v) => v,
                Err(ex) => {
                    return HandlerResult::Error(
                        status::INTERNAL_SERVER_ERROR,
                        ErrorResponse::of(ex.to_string(), "internal_error", Some("embed_failure")),
                    )
                }
            };
            data.push(EmbeddingDatum {
                object: "embedding".to_string(),
                index: index as i32,
                embedding,
            });
        }

        HandlerResult::Json(
            status::OK,
            EmbeddingsResponse {
                object: "list".to_string(),
                data,
                model: body.model.clone(),
                usage: UsageInfo {
                    prompt_tokens,
                    completion_tokens: 0,
                    total_tokens: prompt_tokens,
                },
            },
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionHandler  (POST /v1/companion/turn)
// ─────────────────────────────────────────────────────────────────────────────

/// Request body for `POST /v1/companion/turn`. Mirrors `CompanionTurnRequest`.
#[derive(Debug, Clone, Default, serde::Serialize, serde::Deserialize)]
pub struct CompanionTurnRequest {
    #[serde(default, rename = "session_id")]
    pub session_id: String,
    #[serde(default, rename = "identity_id")]
    pub identity_id: String,
    #[serde(default)]
    pub message: String,
    #[serde(default)]
    pub stream: bool,
    #[serde(default)]
    pub agentic: bool,
}

/// Response body for `POST /v1/companion/turn`. Mirrors `CompanionTurnResponse`.
#[derive(Debug, Clone, Default, PartialEq, serde::Serialize, serde::Deserialize)]
pub struct CompanionTurnResponse {
    #[serde(rename = "session_id")]
    pub session_id: String,
    pub reply: String,
    pub agentic: bool,
    #[serde(rename = "turn_index")]
    pub turn_index: i32,
}

/// In-memory handler for `POST /v1/companion/turn`. Sync port of
/// `CompanionEndpoint.HandleTurnAsync`.
pub struct CompanionHandler {
    resolver: Arc<dyn ICompanionSessionResolver>,
    admission: Arc<AdmissionControl>,
}

impl CompanionHandler {
    /// Constructs the handler over the resolver + admission gate.
    pub fn new(
        resolver: Arc<dyn ICompanionSessionResolver>,
        admission: Arc<AdmissionControl>,
    ) -> Self {
        Self {
            resolver,
            admission,
        }
    }

    /// Handle a non-streaming turn. Mirrors the validation → resolve →
    /// admission → send/agent path.
    pub fn handle(&self, body: &CompanionTurnRequest) -> HandlerResult<CompanionTurnResponse> {
        if body.session_id.trim().is_empty()
            || body.identity_id.trim().is_empty()
            || body.message.trim().is_empty()
        {
            return HandlerResult::Error(
                status::BAD_REQUEST,
                ErrorResponse::of(
                    "session_id, identity_id, and message are all required.",
                    "invalid_request_error",
                    Some("missing_field"),
                ),
            );
        }

        let session = match self.resolver.resolve(&body.session_id, &body.identity_id) {
            Some(s) => s,
            None => {
                return HandlerResult::Error(
                    status::NOT_FOUND,
                    ErrorResponse::of(
                        format!(
                            "No Companion session for session_id='{}', identity_id='{}'.",
                            body.session_id, body.identity_id
                        ),
                        "invalid_request_error",
                        Some("session_not_found"),
                    ),
                )
            }
        };

        let _slot = match self.admission.try_enter() {
            Some(s) => s,
            None => {
                return HandlerResult::Error(
                    status::SERVICE_UNAVAILABLE,
                    ErrorResponse::of(
                        "Server is at concurrency cap. Retry shortly.",
                        "server_busy",
                        Some("concurrency_cap"),
                    ),
                )
            }
        };

        let reply = if body.agentic {
            session.agent(&body.message)
        } else {
            session.send(&body.message)
        };

        HandlerResult::Json(
            status::OK,
            CompanionTurnResponse {
                session_id: body.session_id.clone(),
                reply,
                agentic: body.agentic,
                turn_index: session.history_len() as i32,
            },
        )
    }

    /// Handle a streaming turn, returning the ordered delta chunks (the C#
    /// writes `{session_id, delta}` SSE frames). Returns an error result on the
    /// same validation/resolve/admission failures as [`Self::handle`].
    pub fn handle_stream(
        &self,
        body: &CompanionTurnRequest,
    ) -> Result<Vec<String>, HandlerResult<CompanionTurnResponse>> {
        if body.session_id.trim().is_empty()
            || body.identity_id.trim().is_empty()
            || body.message.trim().is_empty()
        {
            return Err(HandlerResult::Error(
                status::BAD_REQUEST,
                ErrorResponse::of(
                    "session_id, identity_id, and message are all required.",
                    "invalid_request_error",
                    Some("missing_field"),
                ),
            ));
        }
        let session = match self.resolver.resolve(&body.session_id, &body.identity_id) {
            Some(s) => s,
            None => {
                return Err(HandlerResult::Error(
                    status::NOT_FOUND,
                    ErrorResponse::of(
                        format!(
                            "No Companion session for session_id='{}', identity_id='{}'.",
                            body.session_id, body.identity_id
                        ),
                        "invalid_request_error",
                        Some("session_not_found"),
                    ),
                ))
            }
        };
        let _slot = match self.admission.try_enter() {
            Some(s) => s,
            None => {
                return Err(HandlerResult::Error(
                    status::SERVICE_UNAVAILABLE,
                    ErrorResponse::of(
                        "Server is at concurrency cap. Retry shortly.",
                        "server_busy",
                        Some("concurrency_cap"),
                    ),
                ))
            }
        };
        Ok(session.stream(&body.message))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AdminHandler  (POST /v1/admin/models/load, DELETE /v1/admin/models/{id}, GET /v1/admin/lifecycle)
// ─────────────────────────────────────────────────────────────────────────────

/// Request body for `POST /v1/admin/models/load`. Mirrors `AdminLoadRequest`.
#[derive(Debug, Clone, Default, serde::Serialize, serde::Deserialize)]
pub struct AdminLoadRequest {
    #[serde(default, rename = "modelId")]
    pub model_id: String,
    #[serde(default = "default_backend", rename = "backend")]
    pub backend: String,
    #[serde(default = "default_tier", rename = "tier")]
    pub tier: String,
    #[serde(default, rename = "vramRequiredBytes")]
    pub vram_required_bytes: i64,
    #[serde(default, rename = "ramRequiredBytes")]
    pub ram_required_bytes: i64,
}

fn default_backend() -> String {
    "Cpu".to_string()
}
fn default_tier() -> String {
    "Tier1_Small".to_string()
}

/// Response body for `GET /v1/admin/lifecycle`. Mirrors `AdminLifecycleResponse`
/// (rendered as a small owned view — the `ModelLoadState` list is exposed
/// through the manager).
#[derive(Debug, Clone, PartialEq)]
pub struct AdminLifecycleResponse {
    pub total_allocated_vram_bytes: i64,
    pub total_allocated_ram_bytes: i64,
    pub loaded_model_ids: Vec<String>,
}

/// In-memory handler for the admin lifecycle endpoints. Sync port of
/// `AdminEndpoints`.
pub struct AdminHandler {
    manager: Arc<dyn IModelLifecycleManager>,
    factory: Arc<dyn crate::inference_server::bridge::IBridgeFactory>,
}

impl AdminHandler {
    /// Constructs the handler over the lifecycle manager + bridge factory.
    pub fn new(
        manager: Arc<dyn IModelLifecycleManager>,
        factory: Arc<dyn crate::inference_server::bridge::IBridgeFactory>,
    ) -> Self {
        Self { manager, factory }
    }

    /// `POST /v1/admin/models/load` — validate + delegate to the lifecycle
    /// manager, mapping the outcome to a status code.
    pub fn load(&self, body: &AdminLoadRequest) -> (u16, LoadOutcome, String) {
        if body.model_id.trim().is_empty() {
            return (
                status::BAD_REQUEST,
                LoadOutcome::FactoryFailed,
                "modelId required".to_string(),
            );
        }
        let backend = BackendKind::parse(&body.backend);
        let tier = CapabilityTier::parse(&body.tier);
        let descriptor = ModelLoadDescriptor::new(
            body.model_id.clone(),
            backend,
            tier,
            body.vram_required_bytes,
            body.ram_required_bytes,
            self.factory.clone(),
        );
        let result = self.manager.load(descriptor);
        let code = match result.outcome {
            LoadOutcome::Loaded | LoadOutcome::AlreadyLoaded => status::OK,
            LoadOutcome::InsufficientVram | LoadOutcome::InsufficientRam => {
                status::SERVICE_UNAVAILABLE
            }
            LoadOutcome::FactoryFailed => status::INTERNAL_SERVER_ERROR,
        };
        (code, result.outcome, result.rationale)
    }

    /// `DELETE /v1/admin/models/{id}` — unload a model.
    pub fn unload(&self, model_id: &str) -> (u16, UnloadOutcome) {
        let outcome = self.manager.unload(model_id);
        let code = match outcome {
            UnloadOutcome::Unloaded => status::OK,
            UnloadOutcome::NotLoaded => status::NOT_FOUND,
        };
        (code, outcome)
    }

    /// `GET /v1/admin/lifecycle` — current footprint.
    pub fn lifecycle(&self) -> AdminLifecycleResponse {
        AdminLifecycleResponse {
            total_allocated_vram_bytes: self.manager.total_allocated_vram_bytes(),
            total_allocated_ram_bytes: self.manager.total_allocated_ram_bytes(),
            loaded_model_ids: self.manager.list().into_iter().map(|s| s.model_id).collect(),
        }
    }
}
