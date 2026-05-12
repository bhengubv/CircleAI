//! inference.rs
//!
//! ChatMessage (inference-layer), GenerationOptions, and IChatGenerator trait.
//!
//! Note: `models::ChatMessage` is the shared primitive; this module re-exports
//! it as `ChatMessage` for convenience and adds `GenerationOptions`.

use serde::{Deserialize, Serialize};

// Re-export the shared ChatMessage so callers can use `inference::ChatMessage`.
pub use crate::models::ChatMessage;

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
///
/// NOTE: Synchronous shape for portability. Platform implementations wrap
/// this with async execution. The `stream` method returns a boxed iterator
/// of string tokens (or chunks) in decode order.
pub trait IChatGenerator {
    type Error: std::error::Error;

    /// Generates a complete assistant reply for the given conversation.
    fn generate(
        &self,
        messages: &[ChatMessage],
        opts: Option<&GenerationOptions>,
    ) -> Result<String, Self::Error>;

    /// Streams the assistant reply chunk-by-chunk. Callers concatenate in order.
    fn stream(
        &self,
        messages: &[ChatMessage],
        opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error>;
}
