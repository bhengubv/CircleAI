//! inference.rs
//!
//! ChatMessage (inference-layer), GenerationOptions, and IChatGenerator trait.
//!
//! Note: `models::ChatMessage` is the shared primitive; this module re-exports
//! it as `ChatMessage` for convenience and adds `GenerationOptions`.

use serde::{Deserialize, Serialize};

// Re-export the shared ChatMessage so callers can use `inference::ChatMessage`.
pub use crate::models::ChatMessage;
pub use crate::models_v15::{ChatFragment, ChatFragmentKind};

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
    ///
    /// When `true` the generator separates reasoning from the final answer:
    /// `ChatResponse.reasoning_content` gets the reasoning, `ChatResponse.text`
    /// gets the answer. Streaming callers see fragments tagged with
    /// `ChatFragmentKind::Reasoning`.
    ///
    /// When `false` the generator still RUNS reasoning (this is per-call
    /// output gating, NOT a thinking disable) but the reasoning text is
    /// dropped — only the final answer reaches the caller.
    #[serde(default = "default_include_reasoning")]
    pub include_reasoning: bool,
}

fn default_include_reasoning() -> bool { true }

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
        Ok(Box::new(
            inner.map(|item| item.map(ChatFragment::content)),
        ))
    }
}
