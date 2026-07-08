//! bridge.rs
//!
//! The inference-bridge contract shared by the server, ported from
//! `CircleAI.Hosting.InferenceBridge` (`IInferenceBridge`, `InferenceRequest`,
//! `InferenceResponse`, `ModelDescriptor`, `DeviceCapabilities`,
//! `LocalProcessInferenceBridge`) plus the `IBridgeFactory` contract from
//! `CircleAI.Inference.Server/Endpoints/AdminEndpoints.cs`, and the small
//! `BackendKind` / `CapabilityTier` enums the server routes on.
//!
//! `LocalProcessInferenceBridge` wraps any [`crate::inference::IChatGenerator`]
//! and exposes it through the bridge contract, exactly as the C# reference impl
//! does — including mapping the generator's structured response into an
//! [`InferenceResponse`] with a status classification and a reasoning channel.

use std::collections::BTreeMap;

use chrono::{DateTime, Utc};
use uuid::Uuid;

use crate::inference::chat_generator::DeterministicChatGenerator;
use crate::inference::{ChatFragmentKind, ChatMessage, GenerationOptions, IChatGenerator};

// ─────────────────────────────────────────────────────────────────────────────
// BackendKind + CapabilityTier (ported from CircleAI.Runtime.Backends)
// ─────────────────────────────────────────────────────────────────────────────

/// MNN execution backend. Values match the runtime-package layout.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum BackendKind {
    /// Pure-CPU SIMD backend. Always available.
    Cpu = 0,
    /// NVIDIA CUDA.
    Cuda = 1,
    /// Vulkan compute (AMD, Intel, Apple via MoltenVK).
    Vulkan = 2,
    /// OpenCL (older AMD/Intel Linux).
    OpenCL = 3,
    /// Apple Metal.
    Metal = 4,
    /// Huawei Ascend (CANN).
    Ascend = 5,
    /// Cambricon MLU.
    Cambricon = 6,
    /// Apple Core ML (ANE).
    CoreML = 7,
}

impl BackendKind {
    /// `true` for GPU-class backends (VRAM admission is enforced for these) —
    /// mirrors the C# `Cuda or Vulkan or Metal or OpenCL` check.
    pub fn is_gpu_class(self) -> bool {
        matches!(
            self,
            BackendKind::Cuda | BackendKind::Vulkan | BackendKind::Metal | BackendKind::OpenCL
        )
    }

    /// Parse a case-insensitive backend name (mirrors the admin request's
    /// `Backend` string → enum). Defaults to [`BackendKind::Cpu`].
    pub fn parse(s: &str) -> BackendKind {
        match s.trim().to_ascii_lowercase().as_str() {
            "cuda" => BackendKind::Cuda,
            "vulkan" => BackendKind::Vulkan,
            "opencl" => BackendKind::OpenCL,
            "metal" => BackendKind::Metal,
            "ascend" => BackendKind::Ascend,
            "cambricon" => BackendKind::Cambricon,
            "coreml" => BackendKind::CoreML,
            _ => BackendKind::Cpu,
        }
    }
}

/// Capability tier mapping to a Qwen/DeepSeek/GLM/Kimi model size band.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(i32)]
pub enum CapabilityTier {
    /// Qwen3-0.6B class. ≈600 MB. Always available.
    Tier0Tiny = 0,
    /// 1.7B–4B class. ≈2 GB.
    Tier1Small = 1,
    /// 7B–9B class Q4. ≈6 GB.
    Tier2Medium = 2,
    /// 14B–32B class Q4. ≈12 GB.
    Tier3Large = 3,
    /// 70B+ class Q4. ≈24 GB+.
    Tier4Frontier = 4,
}

impl CapabilityTier {
    /// Parse a tier from its C# enum name (e.g. `"Tier1_Small"`), defaulting to
    /// [`CapabilityTier::Tier1Small`].
    pub fn parse(s: &str) -> CapabilityTier {
        match s.trim() {
            "Tier0_Tiny" | "Tier0Tiny" => CapabilityTier::Tier0Tiny,
            "Tier2_Medium" | "Tier2Medium" => CapabilityTier::Tier2Medium,
            "Tier3_Large" | "Tier3Large" => CapabilityTier::Tier3Large,
            "Tier4_Frontier" | "Tier4Frontier" => CapabilityTier::Tier4Frontier,
            _ => CapabilityTier::Tier1Small,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ModelFormat + ModelDescriptor
// ─────────────────────────────────────────────────────────────────────────────

/// On-disk encoding format of a model weight artefact.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ModelFormat {
    /// llama.cpp GGUF.
    Gguf,
    /// ONNX Runtime.
    Onnx,
    /// Apple Core ML.
    CoreMl,
    /// TensorFlow Lite.
    Tflite,
    /// Not recognised.
    Unknown,
}

/// Canonical descriptor for a single loaded model. Mirrors `ModelDescriptor`.
#[derive(Debug, Clone, PartialEq)]
pub struct ModelDescriptor {
    pub model_id: String,
    pub version: String,
    pub format: ModelFormat,
    pub context_window_tokens: i32,
    pub vocab_size: i32,
    pub parameter_count: i64,
    pub quantisation_label: Option<String>,
    pub approximate_memory_bytes: i64,
}

// ─────────────────────────────────────────────────────────────────────────────
// DeviceCapabilities
// ─────────────────────────────────────────────────────────────────────────────

/// Static-ish capabilities report from the host. Mirrors `DeviceCapabilities`.
#[derive(Debug, Clone, PartialEq)]
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

// ─────────────────────────────────────────────────────────────────────────────
// InferenceRequest / InferenceResponse
// ─────────────────────────────────────────────────────────────────────────────

/// One completion request submitted to an [`IInferenceBridge`]. Mirrors
/// `InferenceRequest` (immutable; create new instances for retries).
#[derive(Debug, Clone, PartialEq)]
pub struct InferenceRequest {
    pub id: Uuid,
    pub model_id: String,
    pub prompt: String,
    pub max_output_tokens: i32,
    pub temperature: f32,
    pub top_p: f32,
    pub stop_sequences: Vec<String>,
    pub metadata: BTreeMap<String, String>,
    pub requested_at: DateTime<Utc>,
}

impl InferenceRequest {
    /// Convenience factory stamping a fresh id + timestamp with sensible
    /// defaults. Mirrors `InferenceRequest.Create`.
    pub fn create(model_id: impl Into<String>, prompt: impl Into<String>) -> InferenceRequest {
        Self::create_with(model_id, prompt, 256, 0.7, 0.95)
    }

    /// Full-knob factory.
    pub fn create_with(
        model_id: impl Into<String>,
        prompt: impl Into<String>,
        max_output_tokens: i32,
        temperature: f32,
        top_p: f32,
    ) -> InferenceRequest {
        InferenceRequest {
            id: Uuid::new_v4(),
            model_id: model_id.into(),
            prompt: prompt.into(),
            max_output_tokens,
            temperature,
            top_p,
            stop_sequences: Vec::new(),
            metadata: BTreeMap::new(),
            requested_at: Utc::now(),
        }
    }
}

/// Terminal state of a single inference call. Mirrors `InferenceStatus`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InferenceStatus {
    /// Finished cleanly (end-of-turn token).
    Completed,
    /// Halted because a stop sequence matched.
    StoppedByToken,
    /// Halted because max-output-tokens was reached.
    StoppedByLength,
    /// The bridge or model failed.
    Failed,
    /// The caller cancelled before completion.
    Cancelled,
}

/// Result of a single completion call. Mirrors `InferenceResponse`.
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
    pub reasoning_text: Option<String>,
}

/// Kind of fragment a streaming bridge emits. Mirrors `InferenceFragmentKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InferenceFragmentKind {
    /// Part of the user-facing answer (OpenAI `content`).
    Content,
    /// Part of the model's reasoning trace (OpenAI `reasoning_content`).
    Reasoning,
}

/// A single fragment emitted by [`IInferenceBridge::stream_fragments`].
#[derive(Debug, Clone, PartialEq)]
pub struct InferenceFragment {
    pub kind: InferenceFragmentKind,
    pub text: String,
}

// ─────────────────────────────────────────────────────────────────────────────
// IInferenceBridge
// ─────────────────────────────────────────────────────────────────────────────

/// Cross-OS contract for an inference daemon. Object-safe (no generics /
/// associated types) so it can be stored as `Arc<dyn IInferenceBridge>` in the
/// registry. Sync port of `IInferenceBridge`.
pub trait IInferenceBridge: Send + Sync {
    /// Descriptor for every model currently loaded by the bridge.
    fn list_loaded_models(&self) -> Vec<ModelDescriptor>;

    /// `true` when `model_id` is currently loaded.
    fn is_model_loaded(&self, model_id: &str) -> bool;

    /// Run a single completion and return the full response.
    fn complete(&self, request: &InferenceRequest) -> InferenceResponse;

    /// Stream tokens as the model decodes them. Content only.
    fn stream_completion(&self, request: &InferenceRequest) -> Vec<String>;

    /// Stream tokens tagged with their kind (content vs reasoning). Default
    /// wraps [`Self::stream_completion`] and tags every chunk as content.
    fn stream_fragments(&self, request: &InferenceRequest) -> Vec<InferenceFragment> {
        self.stream_completion(request)
            .into_iter()
            .map(|text| InferenceFragment {
                kind: InferenceFragmentKind::Content,
                text,
            })
            .collect()
    }

    /// The bridge's view of the hardware it is running on.
    fn device_capabilities(&self) -> DeviceCapabilities;
}

// ─────────────────────────────────────────────────────────────────────────────
// LocalProcessInferenceBridge
// ─────────────────────────────────────────────────────────────────────────────

/// In-process [`IInferenceBridge`] wrapping any [`IChatGenerator`]. Transport
/// encryption is reported `true` (no cross-process channel). Mirrors
/// `LocalProcessInferenceBridge`, including the status classification and
/// reasoning-channel mapping.
pub struct LocalProcessInferenceBridge<G: IChatGenerator + Send + Sync> {
    generator: G,
    descriptor: ModelDescriptor,
    capabilities: DeviceCapabilities,
}

impl<G: IChatGenerator + Send + Sync> LocalProcessInferenceBridge<G>
where
    G::Error: 'static,
{
    /// Constructs a bridge with a synthetic default device-capability report.
    pub fn new(generator: G, descriptor: ModelDescriptor) -> Self {
        Self::with_capabilities(generator, descriptor, default_device_capabilities())
    }

    /// Constructs a bridge with an explicit capability report.
    pub fn with_capabilities(
        generator: G,
        descriptor: ModelDescriptor,
        capabilities: DeviceCapabilities,
    ) -> Self {
        Self {
            generator,
            descriptor,
            capabilities,
        }
    }

    fn build_options(&self, request: &InferenceRequest) -> GenerationOptions {
        GenerationOptions {
            max_tokens: request.max_output_tokens,
            temperature: request.temperature,
            top_p: request.top_p,
            stop_sequences: if request.stop_sequences.is_empty() {
                None
            } else {
                Some(request.stop_sequences.clone())
            },
            ..GenerationOptions::default()
        }
    }

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
}

impl<G: IChatGenerator + Send + Sync> IInferenceBridge for LocalProcessInferenceBridge<G>
where
    G: 'static,
    G::Error: 'static,
{
    fn list_loaded_models(&self) -> Vec<ModelDescriptor> {
        vec![self.descriptor.clone()]
    }

    fn is_model_loaded(&self, model_id: &str) -> bool {
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

        let messages = [ChatMessage::user(request.prompt.clone())];
        let options = self.build_options(request);

        match self.generator.generate(&messages, Some(&options)) {
            Ok(output) => {
                let status = Self::determine_status(&output, request);
                // Split reasoning by re-running the structured path if the
                // generator is the deterministic one — for the generic path we
                // rely on generate() being content-only (reasoning is separate).
                InferenceResponse {
                    request_id: request.id,
                    model_id: request.model_id.clone(),
                    output_token_count: estimate_token_count(&output),
                    prompt_token_count: estimate_token_count(&request.prompt),
                    output_text: output,
                    status,
                    inference_millis: 0.0,
                    failure_message: None,
                    completed_at: Utc::now(),
                    reasoning_text: None,
                }
            }
            Err(ex) => InferenceResponse {
                request_id: request.id,
                model_id: request.model_id.clone(),
                output_text: String::new(),
                output_token_count: 0,
                prompt_token_count: estimate_token_count(&request.prompt),
                status: InferenceStatus::Failed,
                inference_millis: 0.0,
                failure_message: Some(ex.to_string()),
                completed_at: Utc::now(),
                reasoning_text: None,
            },
        }
    }

    fn stream_completion(&self, request: &InferenceRequest) -> Vec<String> {
        if self.descriptor.model_id != request.model_id {
            return Vec::new();
        }
        let messages = [ChatMessage::user(request.prompt.clone())];
        let options = self.build_options(request);

        let mut out = Vec::new();
        if let Ok(iter) = self.generator.stream(&messages, Some(&options)) {
            for chunk in iter.flatten() {
                out.push(chunk);
            }
        }
        if out.is_empty() {
            // Fall back to the full completion in a single chunk so callers
            // always see ≥ 1 token (mirrors the C# fallback).
            if let Ok(full) = self.generator.generate(&messages, Some(&options)) {
                out.push(full);
            }
        }
        out
    }

    fn stream_fragments(&self, request: &InferenceRequest) -> Vec<InferenceFragment> {
        if self.descriptor.model_id != request.model_id {
            return Vec::new();
        }
        let messages = [ChatMessage::user(request.prompt.clone())];
        let options = self.build_options(request);

        let mut out = Vec::new();
        if let Ok(iter) = self.generator.stream_fragments(&messages, Some(&options)) {
            for f in iter.flatten() {
                let kind = match f.kind {
                    ChatFragmentKind::Reasoning => InferenceFragmentKind::Reasoning,
                    ChatFragmentKind::Content => InferenceFragmentKind::Content,
                };
                out.push(InferenceFragment { kind, text: f.text });
            }
        }
        out
    }

    fn device_capabilities(&self) -> DeviceCapabilities {
        self.capabilities.clone()
    }
}

/// A [`LocalProcessInferenceBridge`] over a [`DeterministicChatGenerator`], with
/// the reasoning channel populated from the generator's structured response.
/// This is the concrete bridge the in-memory factory produces.
pub struct DeterministicBridge {
    generator: DeterministicChatGenerator,
    descriptor: ModelDescriptor,
    capabilities: DeviceCapabilities,
}

impl DeterministicBridge {
    /// Constructs a deterministic bridge for `descriptor`, wrapping a generator
    /// keyed to the descriptor's model id. Set `reasoning` to emit a `<think>`
    /// reasoning channel.
    pub fn new(descriptor: ModelDescriptor, reasoning: bool) -> Self {
        let generator = if reasoning {
            DeterministicChatGenerator::reasoning(descriptor.model_id.clone())
        } else {
            DeterministicChatGenerator::new(descriptor.model_id.clone())
        };
        Self {
            generator,
            descriptor,
            capabilities: default_device_capabilities(),
        }
    }

    fn build_options(&self, request: &InferenceRequest) -> GenerationOptions {
        GenerationOptions {
            max_tokens: request.max_output_tokens,
            temperature: request.temperature,
            top_p: request.top_p,
            stop_sequences: if request.stop_sequences.is_empty() {
                None
            } else {
                Some(request.stop_sequences.clone())
            },
            ..GenerationOptions::default()
        }
    }
}

impl IInferenceBridge for DeterministicBridge {
    fn list_loaded_models(&self) -> Vec<ModelDescriptor> {
        vec![self.descriptor.clone()]
    }

    fn is_model_loaded(&self, model_id: &str) -> bool {
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

        let messages = [ChatMessage::user(request.prompt.clone())];
        let options = self.build_options(request);
        // Structured path so a reasoning trace populates reasoning_text
        // separately (mirrors the C# GenerateResponseAsync route).
        let resp = self.generator.generate_response(&messages, Some(&options));
        let status = {
            // classify against stop sequences + length, like the C# helper.
            if !request.stop_sequences.is_empty()
                && request
                    .stop_sequences
                    .iter()
                    .any(|s| !s.is_empty() && resp.text.contains(s.as_str()))
            {
                InferenceStatus::StoppedByToken
            } else if estimate_token_count(&resp.text) >= request.max_output_tokens {
                InferenceStatus::StoppedByLength
            } else {
                InferenceStatus::Completed
            }
        };

        InferenceResponse {
            request_id: request.id,
            model_id: request.model_id.clone(),
            output_token_count: estimate_token_count(&resp.text),
            prompt_token_count: estimate_token_count(&request.prompt),
            output_text: resp.text,
            status,
            inference_millis: 0.0,
            failure_message: None,
            completed_at: Utc::now(),
            reasoning_text: resp.reasoning_content,
        }
    }

    fn stream_completion(&self, request: &InferenceRequest) -> Vec<String> {
        if self.descriptor.model_id != request.model_id {
            return Vec::new();
        }
        let messages = [ChatMessage::user(request.prompt.clone())];
        let options = self.build_options(request);
        match self.generator.stream(&messages, Some(&options)) {
            Ok(iter) => iter.flatten().collect(),
            Err(_) => Vec::new(),
        }
    }

    fn stream_fragments(&self, request: &InferenceRequest) -> Vec<InferenceFragment> {
        if self.descriptor.model_id != request.model_id {
            return Vec::new();
        }
        let messages = [ChatMessage::user(request.prompt.clone())];
        let options = self.build_options(request);
        match self.generator.stream_fragments(&messages, Some(&options)) {
            Ok(iter) => iter
                .flatten()
                .map(|f| InferenceFragment {
                    kind: match f.kind {
                        ChatFragmentKind::Reasoning => InferenceFragmentKind::Reasoning,
                        ChatFragmentKind::Content => InferenceFragmentKind::Content,
                    },
                    text: f.text,
                })
                .collect(),
            Err(_) => Vec::new(),
        }
    }

    fn device_capabilities(&self) -> DeviceCapabilities {
        self.capabilities.clone()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IBridgeFactory
// ─────────────────────────────────────────────────────────────────────────────

/// Error returned by a bridge factory.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BridgeFactoryError(String);

impl BridgeFactoryError {
    pub fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
    pub fn message(&self) -> &str {
        &self.0
    }
}

impl std::fmt::Display for BridgeFactoryError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for BridgeFactoryError {}

/// Factory that materialises an [`IInferenceBridge`] for a (modelId, backend,
/// tier). Sync port of `IBridgeFactory`.
pub trait IBridgeFactory: Send + Sync {
    /// Create a bridge for the requested model + backend + tier.
    fn create(
        &self,
        model_id: &str,
        backend: BackendKind,
        tier: CapabilityTier,
    ) -> Result<std::sync::Arc<dyn IInferenceBridge>, BridgeFactoryError>;
}

/// Default factory — refuses every load with a clear error. Mirrors
/// `UnconfiguredBridgeFactory`.
#[derive(Debug, Default, Clone)]
pub struct UnconfiguredBridgeFactory;

impl IBridgeFactory for UnconfiguredBridgeFactory {
    fn create(
        &self,
        _model_id: &str,
        _backend: BackendKind,
        _tier: CapabilityTier,
    ) -> Result<std::sync::Arc<dyn IInferenceBridge>, BridgeFactoryError> {
        Err(BridgeFactoryError::new(
            "No IBridgeFactory is configured. Register one before calling /v1/admin/models/load.",
        ))
    }
}

/// A working in-memory [`IBridgeFactory`] that materialises a
/// [`DeterministicBridge`] for any model id. The memory footprint attributed to
/// the descriptor scales with the tier (mirrors `MnnInferenceBridgeFactory`'s
/// `ApproxMemoryFromTier`).
#[derive(Debug, Default, Clone)]
pub struct DeterministicBridgeFactory {
    reasoning: bool,
}

impl DeterministicBridgeFactory {
    /// A factory producing plain-text bridges.
    pub fn new() -> Self {
        Self { reasoning: false }
    }

    /// A factory producing reasoning-capable bridges.
    pub fn reasoning() -> Self {
        Self { reasoning: true }
    }

    /// Approximate working-set bytes for a tier — mirrors the C# tier→bytes map.
    pub fn approx_memory_from_tier(tier: CapabilityTier) -> i64 {
        let gib = 1024i64 * 1024 * 1024;
        match tier {
            CapabilityTier::Tier0Tiny => gib,
            CapabilityTier::Tier1Small => 2 * gib,
            CapabilityTier::Tier2Medium => 6 * gib,
            CapabilityTier::Tier3Large => 12 * gib,
            CapabilityTier::Tier4Frontier => 24 * gib,
        }
    }
}

impl IBridgeFactory for DeterministicBridgeFactory {
    fn create(
        &self,
        model_id: &str,
        _backend: BackendKind,
        tier: CapabilityTier,
    ) -> Result<std::sync::Arc<dyn IInferenceBridge>, BridgeFactoryError> {
        if model_id.trim().is_empty() {
            return Err(BridgeFactoryError::new("modelId required"));
        }
        let descriptor = ModelDescriptor {
            model_id: model_id.to_string(),
            version: "1.0.0".to_string(),
            format: ModelFormat::Gguf,
            context_window_tokens: 4096,
            vocab_size: 151_936, // Qwen 3 family default
            parameter_count: 0,
            quantisation_label: Some("Q4_K_M".to_string()),
            approximate_memory_bytes: Self::approx_memory_from_tier(tier),
        };
        Ok(std::sync::Arc::new(DeterministicBridge::new(
            descriptor,
            self.reasoning,
        )))
    }
}

// ── helpers ──────────────────────────────────────────────────────────────────

/// Rough token estimate: `max(1, len/4)` for non-empty text. Mirrors the C#
/// `EstimateTokenCount`.
pub fn estimate_token_count(text: &str) -> i32 {
    if text.is_empty() {
        0
    } else {
        (text.len() as i32 / 4).max(1)
    }
}

/// A synthetic device-capability report used by the in-process bridges.
pub fn default_device_capabilities() -> DeviceCapabilities {
    DeviceCapabilities {
        os_name: std::env::consts::OS.to_string(),
        os_version: String::new(),
        physical_memory_bytes: 8 * 1024 * 1024 * 1024,
        cpu_core_count: 8,
        has_gpu: false,
        gpu_name: None,
        gpu_memory_bytes: None,
        has_npu: false,
        npu_name: None,
        has_transport_layer_encryption: true,
    }
}
