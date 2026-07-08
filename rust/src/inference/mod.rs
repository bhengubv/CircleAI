//! inference — CircleAI.Inference runtime (Rust port).
//!
//! Core chat-generation surface plus the runtime gaps ported from
//! `CircleAI.Inference`:
//!   - [`ChatMessage`] / [`GenerationOptions`] / [`PowerBudget`] / [`IChatGenerator`]
//!   - [`chat_generator`]: inference-specific [`chat_generator::ChatResponse`] +
//!     [`chat_generator::FinishReason`] + [`chat_generator::DeterministicChatGenerator`]
//!     (a deterministic local generator standing in for QwenTextGenerator /
//!     KimiVlGenerator)
//!   - [`capability`]: [`capability::ChatCapability`] + [`capability::VisionInput`]
//!   - [`context_budget`]: [`context_budget::ContextWindowBudgetManager`]
//!   - [`prefix_cache`]: [`prefix_cache::PrefixCacheService`]
//!   - [`download_service`]: [`download_service::IModelDownloadService`] +
//!     [`download_service::ModelDownloadService`]
//!   - [`feedback_queue`]: [`feedback_queue::IFeedbackTrainingQueue`] + queue
//!   - [`nightly_trainer`]: [`nightly_trainer::NightlyAdapterTrainer`]
//!   - [`layer_streaming`]: [`layer_streaming::ILayerStreamingRunner`] +
//!     orchestrator
//!   - [`kv_compression`]: [`kv_compression::KvCompressionMode`] + apply +
//!     [`kv_compression::PowerBudgetPolicy`]
//!
//! External/native/disk dependencies are injected behind traits with
//! deterministic in-memory defaults, per the no-real-IO porting brief.

use serde::{Deserialize, Serialize};

// Re-export the shared ChatMessage so callers can use `inference::ChatMessage`.
pub use crate::models::ChatMessage;
pub use crate::models_v15::{ChatFragment, ChatFragmentKind};

pub mod capability;
pub mod chat_generator;
pub mod context_budget;
pub mod download_service;
pub mod feedback_queue;
pub mod kv_compression;
pub mod layer_streaming;
pub mod nightly_trainer;
pub mod prefix_cache;

// ─────────────────────────────────────────────────────────────────────────────
// GenerationOptions
// ─────────────────────────────────────────────────────────────────────────────

/// Knobs for a single generation call.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GenerationOptions {
    /// Maximum number of new tokens to produce.
    pub max_tokens: i32,

    /// Sampling temperature. 0 = greedy; higher = more random.
    pub temperature: f32,

    /// Nucleus sampling cutoff (top-p). 1.0 disables.
    pub top_p: f32,

    /// Top-k cutoff. 0 disables.
    pub top_k: i32,

    /// Optional RNG seed. `None` means non-deterministic.
    pub seed: Option<i32>,

    /// Optional substrings that end generation when matched in emitted output.
    pub stop_sequences: Option<Vec<String>>,

    /// Whether to surface the model's reasoning trace (Qwen3
    /// `<think>…</think>`) on the call. Default `true`.
    #[serde(default = "default_include_reasoning")]
    pub include_reasoning: bool,

    /// (RT-11) Declarative per-call power budget. The runtime maps the
    /// budget to a max-tokens cap and (eventually) model size.
    #[serde(default)]
    pub budget: PowerBudget,

    /// (RT-06) Whether the runtime should consult the cross-session prefix
    /// cache for a warm `(model_id, system_prompt)` snapshot. Default `false`.
    #[serde(default)]
    pub use_prefix_cache: bool,
}

/// Per-call power budget. Mirrors CircleAI.Inference.PowerBudget.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, Default)]
#[serde(rename_all = "lowercase")]
pub enum PowerBudget {
    /// Opt out — honour `max_tokens` literally.
    None,
    /// ~64 token cap; prefers TQ4 KV; smaller model in chain when configured.
    Low,
    /// Default. ~512 token cap. Auto-downgrades to Low below 15% battery.
    #[default]
    Normal,
    /// ~2048 token cap; full FP16 KV. Auto-throttles on thermal warnings.
    High,
}

fn default_include_reasoning() -> bool {
    true
}

impl Default for GenerationOptions {
    fn default() -> Self {
        Self {
            max_tokens: 512,
            temperature: 0.7,
            top_p: 0.9,
            top_k: 40,
            seed: None,
            stop_sequences: None,
            include_reasoning: true,
            budget: PowerBudget::Normal,
            use_prefix_cache: false,
        }
    }
}

impl GenerationOptions {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn with_max_tokens(mut self, max_tokens: i32) -> Self {
        self.max_tokens = max_tokens;
        self
    }

    pub fn with_temperature(mut self, temperature: f32) -> Self {
        self.temperature = temperature;
        self
    }

    pub fn with_seed(mut self, seed: i32) -> Self {
        self.seed = Some(seed);
        self
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IChatGenerator trait
// ─────────────────────────────────────────────────────────────────────────────

/// Contract for an on-device chat-style text generator.
pub trait IChatGenerator {
    type Error: std::error::Error;

    fn generate(
        &self,
        messages: &[ChatMessage],
        opts: Option<&GenerationOptions>,
    ) -> Result<String, Self::Error>;

    /// Streams the assistant reply as decoded chunks. Content only — any
    /// reasoning inside `<think>…</think>` is filtered out. Use
    /// `stream_fragments` when you also need the reasoning stream.
    fn stream(
        &self,
        messages: &[ChatMessage],
        opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error>;

    /// Fragment-aware streaming variant. Yields each piece tagged as either
    /// [`ChatFragmentKind::Content`] or [`ChatFragmentKind::Reasoning`] so the
    /// caller can route the model's `<think>` block into a separate
    /// `reasoning_content` field (o1 / DeepSeek style).
    ///
    /// Default implementation wraps [`Self::stream`] and tags every chunk as
    /// `Content`; generators that surface reasoning override this method.
    fn stream_fragments(
        &self,
        messages: &[ChatMessage],
        opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<ChatFragment, Self::Error>>>, Self::Error>
    where
        Self::Error: 'static,
    {
        let inner = self.stream(messages, opts)?;
        Ok(Box::new(inner.map(|item| item.map(ChatFragment::content))))
    }

    /// (RT-02) Save the current model session to `path`. Returns `Ok(true)`
    /// on success. Default returns `Ok(false)`; native generators override.
    fn save_session(&self, _path: &str) -> Result<bool, Self::Error> {
        Ok(false)
    }

    /// (RT-02) Load a previously-saved session from `path`. Returns
    /// `Ok(true)` on success. Default returns `Ok(false)`.
    fn load_session(&self, _path: &str) -> Result<bool, Self::Error> {
        Ok(false)
    }
}
