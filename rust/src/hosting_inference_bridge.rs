//! hosting_inference_bridge — CircleAI.Hosting.InferenceBridge (Rust port).
//!
//! Cross-OS contract for an inference daemon (the cross-platform analogue of
//! Apple Intelligence / Gemini Nano): one model loaded once per device, shared
//! by every app over an OS-specific IPC channel. Ported from `IInferenceBridge.cs`,
//! `LocalProcessInferenceBridge.cs`, `MockInferenceBridge.cs`,
//! `InferenceRequest.cs`, `InferenceResponse.cs`, `ModelDescriptor.cs`, and
//! `DeviceCapabilities.cs`.
//!
//! This module ships the contract + the in-process reference impl
//! ([`LocalProcessInferenceBridge`]) + a deterministic test double
//! ([`MockInferenceBridge`]). SYNC: streaming APIs return a materialised
//! `Vec<String>` / `Vec<InferenceFragment>`. The wrapped generator and the
//! device-capability probe are injected behind traits so nothing touches native
//! code. The status-classification + token-estimate heuristics are ported 1:1.

use chrono::{DateTime, Utc};
use uuid::Uuid;

// ─────────────────────────────────────────────────────────────────────────────
// ModelDescriptor + ModelFormat
// ─────────────────────────────────────────────────────────────────────────────

/// On-disk encoding format of a model weight artefact. 1:1 with the C#
/// `ModelFormat`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModelFormat {
    /// llama.cpp GGUF.
    Gguf,
    /// ONNX Runtime model file.
    Onnx,
    /// Apple Core ML model package.
    CoreMl,
    /// TensorFlow Lite flatbuffer.
    Tflite,
    /// Format not recognised or not yet classified.
    Unknown,
}

/// Canonical descriptor for a single loaded model. 1:1 with the C#
/// `ModelDescriptor` record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModelDescriptor {
    /// Canonical, human-readable model name; unique within a bridge instance.
    pub model_id: String,
    /// Semantic version or model-card checkpoint identifier.
    pub version: String,
    /// On-disk encoding of the weights.
    pub format: ModelFormat,
    /// Maximum context length the model was trained / fine-tuned for.
    pub context_window_tokens: i32,
    /// Tokeniser vocabulary size.
    pub vocab_size: i32,
    /// Total trainable parameter count.
    pub parameter_count: i64,
    /// Quantisation profile (`"Q4_K_M"`, `"INT8"`, …). `None` = full precision.
    pub quantisation_label: Option<String>,
    /// Approximate working-set bytes once loaded.
    pub approximate_memory_bytes: i64,
}

// ─────────────────────────────────────────────────────────────────────────────
// InferenceRequest / InferenceResponse / InferenceStatus / InferenceFragment
// ─────────────────────────────────────────────────────────────────────────────

/// One completion request submitted to an [`IInferenceBridge`]. 1:1 with the C#
/// `InferenceRequest` record. (`f32` fields preclude `Eq`.)
#[derive(Debug, Clone, PartialEq)]
pub struct InferenceRequest {
    /// Unique request identifier. Echoed back in the response.
    pub id: Uuid,
    /// Target model. Must be currently loaded in the bridge.
    pub model_id: String,
    /// The prompt text to complete.
    pub prompt: String,
    /// Hard upper bound on tokens to emit.
    pub max_output_tokens: i32,
    /// Sampling temperature. `0` = greedy.
    pub temperature: f32,
    /// Nucleus sampling cutoff. `1.0` disables.
    pub top_p: f32,
    /// Substrings that end generation immediately. May be empty.
    pub stop_sequences: Vec<String>,
    /// Free-form key/value bag for caller bookkeeping. Opaque to the bridge.
    pub metadata: std::collections::BTreeMap<String, String>,
    /// UTC timestamp the request was created at.
    pub requested_at: DateTime<Utc>,
}

impl InferenceRequest {
    /// Convenience factory that stamps a fresh id + `requested_at` and uses
    /// sensible defaults. 1:1 with the C# `InferenceRequest.Create`.
    pub fn create(model_id: impl Into<String>, prompt: impl Into<String>) -> Self {
        Self::create_with(model_id, prompt, 256, 0.7, 0.95)
    }

    /// Factory with explicit sampling knobs.
    pub fn create_with(
        model_id: impl Into<String>,
        prompt: impl Into<String>,
        max_output_tokens: i32,
        temperature: f32,
        top_p: f32,
    ) -> Self {
        let model_id = model_id.into();
        assert!(!model_id.is_empty(), "modelId required");
        Self {
            id: Uuid::new_v4(),
            model_id,
            prompt: prompt.into(),
            max_output_tokens,
            temperature,
            top_p,
            stop_sequences: Vec::new(),
            metadata: std::collections::BTreeMap::new(),
            requested_at: Utc::now(),
        }
    }
}

/// Terminal state of a single inference call. 1:1 with the C# `InferenceStatus`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InferenceStatus {
    /// The model finished cleanly (end-of-turn).
    Completed,
    /// Halted because a stop sequence matched.
    StoppedByToken,
    /// Halted because `max_output_tokens` was reached.
    StoppedByLength,
    /// The bridge or model failed; see `failure_message`.
    Failed,
    /// The caller cancelled before generation could finish.
    Cancelled,
}

impl InferenceStatus {
    /// Lower-case name used in the `outcome` metric tag (`"completed"` etc.).
    pub fn as_metric(&self) -> &'static str {
        match self {
            InferenceStatus::Completed => "completed",
            InferenceStatus::StoppedByToken => "stoppedbytoken",
            InferenceStatus::StoppedByLength => "stoppedbylength",
            InferenceStatus::Failed => "failed",
            InferenceStatus::Cancelled => "cancelled",
        }
    }
}

/// Result of a single completion call. 1:1 with the C# `InferenceResponse`
/// record.
#[derive(Debug, Clone, PartialEq)]
pub struct InferenceResponse {
    pub request_id: Uuid,
    pub model_id: String,
    pub output_text: String,
    pub output_token_count: i32,
    pub prompt_token_count: i32,
    pub status: InferenceStatus,
    pub inference_millis: f64,
    pub failure_message: Option<String>,
    pub completed_at: DateTime<Utc>,
    /// Optional chain-of-thought (Qwen3/DeepSeek `<think>…</think>`).
    pub reasoning_text: Option<String>,
}

/// Kind of fragment a streaming bridge emits. 1:1 with the C#
/// `InferenceFragmentKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum InferenceFragmentKind {
    /// Part of the user-facing answer.
    Content = 0,
    /// Part of the model's reasoning trace.
    Reasoning = 1,
}

/// A single fragment emitted by [`IInferenceBridge::stream_fragments`]. 1:1 with
/// the C# `InferenceFragment` record struct.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct InferenceFragment {
    pub kind: InferenceFragmentKind,
    pub text: String,
}

impl InferenceFragment {
    pub fn content(text: impl Into<String>) -> Self {
        Self {
            kind: InferenceFragmentKind::Content,
            text: text.into(),
        }
    }
    pub fn reasoning(text: impl Into<String>) -> Self {
        Self {
            kind: InferenceFragmentKind::Reasoning,
            text: text.into(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DeviceCapabilities + probe
// ─────────────────────────────────────────────────────────────────────────────

/// Static-ish capabilities report from the device hosting the bridge. 1:1 with
/// the C# `DeviceCapabilities` record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeviceCapabilities {
    pub os_name: String,
    pub os_version: String,
    pub physical_memory_bytes: i64,
    pub cpu_core_count: i32,
    pub has_gpu: bool,
    pub gpu_name: Option<String>,
    pub gpu_memory_bytes: Option<i64>,
    pub has_npu: bool,
    pub npu_name: Option<String>,
    pub has_transport_layer_encryption: bool,
}

/// Injected device-capability probe. Real hosts read the OS; a fixed probe ships
/// for tests. Mirrors `CircleAI.Runtime.ICapabilityProbe` (projected to the
/// bridge's [`DeviceCapabilities`] shape).
pub trait ICapabilityProbe: Send + Sync {
    fn probe(&self) -> DeviceCapabilities;
}

/// A fixed [`DeviceCapabilities`] probe for tests / headless scenarios.
pub struct FixedCapabilityProbe(pub DeviceCapabilities);

impl ICapabilityProbe for FixedCapabilityProbe {
    fn probe(&self) -> DeviceCapabilities {
        self.0.clone()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IBridgeChatGenerator — object-safe generator wrapped by the local bridge
// ─────────────────────────────────────────────────────────────────────────────

/// Structured reply the bridge reads from its wrapped generator — mirrors the
/// C# `IChatGenerator.GenerateResponseAsync` return (`Text` + `ReasoningContent`).
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct BridgeChatResponse {
    pub text: String,
    pub reasoning_content: Option<String>,
}

/// Object-safe chat generator wrapped by [`LocalProcessInferenceBridge`]. A
/// deterministic implementation ([`FakeBridgeGenerator`]) ships for tests.
pub trait IBridgeChatGenerator: Send + Sync {
    /// Structured completion (content + optional reasoning). `Err` → failed.
    fn generate_response(&self, prompt: &str, options: &BridgeGenerationOptions)
        -> Result<BridgeChatResponse, String>;

    /// Streamed content chunks. Empty → the bridge falls back to a single
    /// full-completion chunk (mirrors the C# hasYielded fallback).
    fn stream(&self, prompt: &str, options: &BridgeGenerationOptions) -> Vec<String>;

    /// Fragment-aware stream (content vs reasoning). Default: every chunk from
    /// [`Self::stream`] tagged as content.
    fn stream_fragments(
        &self,
        prompt: &str,
        options: &BridgeGenerationOptions,
    ) -> Vec<InferenceFragment> {
        self.stream(prompt, options)
            .into_iter()
            .map(InferenceFragment::content)
            .collect()
    }
}

/// The generation knobs the bridge derives from an [`InferenceRequest`].
#[derive(Debug, Clone, PartialEq)]
pub struct BridgeGenerationOptions {
    pub max_tokens: i32,
    pub temperature: f32,
    pub top_p: f32,
    pub stop_sequences: Vec<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// IInferenceBridge
// ─────────────────────────────────────────────────────────────────────────────

/// Cross-OS contract for an inference daemon. 1:1 with the C# `IInferenceBridge`
/// (SYNC; streaming APIs materialised).
pub trait IInferenceBridge {
    /// Descriptors for every model currently loaded. May be empty while warming.
    fn list_loaded_models(&self) -> Vec<ModelDescriptor>;

    /// `true` when `model_id` is loaded and ready.
    fn is_model_loaded(&self, model_id: &str) -> bool;

    /// Run a single completion and return the full response.
    fn complete(&self, request: &InferenceRequest) -> InferenceResponse;

    /// Stream content chunks (reasoning filtered out).
    fn stream_completion(&self, request: &InferenceRequest) -> Vec<String>;

    /// Stream tokens tagged with their kind. Default wraps
    /// [`Self::stream_completion`] tagging everything as content. 1:1 with the
    /// C# default `StreamFragmentsAsync`.
    fn stream_fragments(&self, request: &InferenceRequest) -> Vec<InferenceFragment> {
        self.stream_completion(request)
            .into_iter()
            .map(InferenceFragment::content)
            .collect()
    }

    /// The bridge's view of the hardware it runs on.
    fn get_device_capabilities(&self) -> DeviceCapabilities;
}

// ─────────────────────────────────────────────────────────────────────────────
// LocalProcessInferenceBridge
// ─────────────────────────────────────────────────────────────────────────────

/// In-process [`IInferenceBridge`] wrapping a single generator. Transport-layer
/// encryption reports `true` (no cross-process channel). 1:1 with the C#
/// `LocalProcessInferenceBridge`.
pub struct LocalProcessInferenceBridge {
    generator: Box<dyn IBridgeChatGenerator>,
    descriptor: ModelDescriptor,
    probe: Box<dyn ICapabilityProbe>,
}

impl LocalProcessInferenceBridge {
    /// Constructs a bridge over the generator + descriptor + a fixed probe.
    pub fn new(
        generator: Box<dyn IBridgeChatGenerator>,
        descriptor: ModelDescriptor,
        probe: Box<dyn ICapabilityProbe>,
    ) -> Self {
        Self {
            generator,
            descriptor,
            probe,
        }
    }

    fn options_for(request: &InferenceRequest) -> BridgeGenerationOptions {
        BridgeGenerationOptions {
            max_tokens: request.max_output_tokens,
            temperature: request.temperature,
            top_p: request.top_p,
            stop_sequences: request.stop_sequences.clone(),
        }
    }
}

impl IInferenceBridge for LocalProcessInferenceBridge {
    fn list_loaded_models(&self) -> Vec<ModelDescriptor> {
        vec![self.descriptor.clone()]
    }

    fn is_model_loaded(&self, model_id: &str) -> bool {
        assert!(!model_id.is_empty(), "modelId required");
        self.descriptor.model_id == model_id
    }

    fn complete(&self, request: &InferenceRequest) -> InferenceResponse {
        if self.descriptor.model_id != request.model_id {
            return InferenceResponse {
                request_id: request.id,
                model_id: request.model_id.clone(),
                output_text: String::new(),
                output_token_count: 0,
                prompt_token_count: 0,
                status: InferenceStatus::Failed,
                inference_millis: 0.0,
                failure_message: Some(format!(
                    "Model '{}' is not loaded by this bridge (have '{}').",
                    request.model_id, self.descriptor.model_id
                )),
                completed_at: Utc::now(),
                reasoning_text: None,
            };
        }

        let options = Self::options_for(request);
        match self.generator.generate_response(&request.prompt, &options) {
            Ok(resp) => {
                let status = determine_status(&resp.text, request);
                InferenceResponse {
                    request_id: request.id,
                    model_id: request.model_id.clone(),
                    output_text: resp.text.clone(),
                    output_token_count: estimate_token_count(&resp.text),
                    prompt_token_count: estimate_token_count(&request.prompt),
                    status,
                    inference_millis: 0.0,
                    failure_message: None,
                    completed_at: Utc::now(),
                    reasoning_text: resp.reasoning_content,
                }
            }
            Err(msg) => InferenceResponse {
                request_id: request.id,
                model_id: request.model_id.clone(),
                output_text: String::new(),
                output_token_count: 0,
                prompt_token_count: estimate_token_count(&request.prompt),
                status: InferenceStatus::Failed,
                inference_millis: 0.0,
                failure_message: Some(msg),
                completed_at: Utc::now(),
                reasoning_text: None,
            },
        }
    }

    fn stream_completion(&self, request: &InferenceRequest) -> Vec<String> {
        if self.descriptor.model_id != request.model_id {
            return Vec::new();
        }
        let options = Self::options_for(request);
        let chunks = self.generator.stream(&request.prompt, &options);
        if !chunks.is_empty() {
            return chunks;
        }
        // Fallback: generator streamed nothing — return the full completion in
        // one chunk (mirrors the C# hasYielded fallback).
        match self.generator.generate_response(&request.prompt, &options) {
            Ok(resp) => vec![resp.text],
            Err(_) => vec![String::new()],
        }
    }

    fn stream_fragments(&self, request: &InferenceRequest) -> Vec<InferenceFragment> {
        if self.descriptor.model_id != request.model_id {
            return Vec::new();
        }
        let options = Self::options_for(request);
        self.generator.stream_fragments(&request.prompt, &options)
    }

    fn get_device_capabilities(&self) -> DeviceCapabilities {
        // The C# projects HostProfile → DeviceCapabilities, always reporting
        // transport-layer encryption true (no cross-process channel).
        let mut caps = self.probe.probe();
        caps.has_transport_layer_encryption = true;
        caps
    }
}

/// Classifies a completion's terminal status. 1:1 with the C#
/// `DetermineStatus`: stop-sequence match → `StoppedByToken`; produced tokens
/// ≥ cap → `StoppedByLength`; else `Completed`.
fn determine_status(output: &str, request: &InferenceRequest) -> InferenceStatus {
    if !request.stop_sequences.is_empty() {
        for s in &request.stop_sequences {
            if !s.is_empty() && output.contains(s.as_str()) {
                return InferenceStatus::StoppedByToken;
            }
        }
    }
    let produced = estimate_token_count(output);
    if produced >= request.max_output_tokens {
        InferenceStatus::StoppedByLength
    } else {
        InferenceStatus::Completed
    }
}

/// ~4 chars/token heuristic, min 1 for non-empty text. 1:1 with the C#
/// `EstimateTokenCount`.
fn estimate_token_count(text: &str) -> i32 {
    if text.is_empty() {
        return 0;
    }
    std::cmp::max(1, (text.len() / 4) as i32)
}

// ─────────────────────────────────────────────────────────────────────────────
// MockInferenceBridge — deterministic test double
// ─────────────────────────────────────────────────────────────────────────────

/// Deterministic [`IInferenceBridge`] for tests. Returns the same canned output
/// for every call and reports one fixed model as loaded. 1:1 with the C#
/// `MockInferenceBridge`.
pub struct MockInferenceBridge {
    canned_output: String,
    descriptor: ModelDescriptor,
}

impl MockInferenceBridge {
    /// Constructs a mock that always returns `canned_output`. `model_id`
    /// defaults to `"mock-model"`.
    pub fn new(canned_output: impl Into<String>, model_id: Option<&str>) -> Self {
        Self {
            canned_output: canned_output.into(),
            descriptor: ModelDescriptor {
                model_id: model_id.unwrap_or("mock-model").to_string(),
                version: "mock-1.0.0".to_string(),
                format: ModelFormat::Unknown,
                context_window_tokens: 4096,
                vocab_size: 32000,
                parameter_count: 0,
                quantisation_label: None,
                approximate_memory_bytes: 0,
            },
        }
    }

    /// The model descriptor this mock reports as loaded.
    pub fn descriptor(&self) -> &ModelDescriptor {
        &self.descriptor
    }
}

impl IInferenceBridge for MockInferenceBridge {
    fn list_loaded_models(&self) -> Vec<ModelDescriptor> {
        vec![self.descriptor.clone()]
    }

    fn is_model_loaded(&self, model_id: &str) -> bool {
        assert!(!model_id.is_empty(), "modelId required");
        self.descriptor.model_id == model_id
    }

    fn complete(&self, request: &InferenceRequest) -> InferenceResponse {
        InferenceResponse {
            request_id: request.id,
            model_id: self.descriptor.model_id.clone(),
            output_text: self.canned_output.clone(),
            output_token_count: std::cmp::max(0, (self.canned_output.len() / 4) as i32),
            prompt_token_count: std::cmp::max(0, (request.prompt.len() / 4) as i32),
            status: InferenceStatus::Completed,
            inference_millis: 0.0,
            failure_message: None,
            completed_at: Utc::now(),
            reasoning_text: None,
        }
    }

    fn stream_completion(&self, _request: &InferenceRequest) -> Vec<String> {
        vec![self.canned_output.clone()]
    }

    fn get_device_capabilities(&self) -> DeviceCapabilities {
        DeviceCapabilities {
            os_name: "Mock".to_string(),
            os_version: "1.0".to_string(),
            physical_memory_bytes: 4i64 * 1024 * 1024 * 1024,
            cpu_core_count: 1,
            has_gpu: false,
            gpu_name: None,
            gpu_memory_bytes: None,
            has_npu: false,
            npu_name: None,
            has_transport_layer_encryption: true,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FakeBridgeGenerator — deterministic generator for LocalProcessInferenceBridge
// ─────────────────────────────────────────────────────────────────────────────

/// Deterministic [`IBridgeChatGenerator`] for tests: echoes a fixed reply built
/// from the prompt, optionally carrying a reasoning trace and stream chunks.
pub struct FakeBridgeGenerator {
    reply: String,
    reasoning: Option<String>,
    chunks: Vec<String>,
}

impl FakeBridgeGenerator {
    /// A generator that replies with `reply` (non-streaming) and streams
    /// `chunks` (or one `reply` chunk when `chunks` is empty).
    pub fn new(reply: impl Into<String>) -> Self {
        Self {
            reply: reply.into(),
            reasoning: None,
            chunks: Vec::new(),
        }
    }

    pub fn with_reasoning(mut self, reasoning: impl Into<String>) -> Self {
        self.reasoning = Some(reasoning.into());
        self
    }

    pub fn with_chunks(mut self, chunks: Vec<String>) -> Self {
        self.chunks = chunks;
        self
    }
}

impl IBridgeChatGenerator for FakeBridgeGenerator {
    fn generate_response(
        &self,
        _prompt: &str,
        _options: &BridgeGenerationOptions,
    ) -> Result<BridgeChatResponse, String> {
        Ok(BridgeChatResponse {
            text: self.reply.clone(),
            reasoning_content: self.reasoning.clone(),
        })
    }

    fn stream(&self, _prompt: &str, _options: &BridgeGenerationOptions) -> Vec<String> {
        if self.chunks.is_empty() {
            Vec::new()
        } else {
            self.chunks.clone()
        }
    }
}
