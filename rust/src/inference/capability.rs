//! capability.rs
//!
//! The capability axis the consumer DECLARES (`ChatCapability`), plus the
//! multimodal image container (`VisionInput`). Ported from
//! `CircleAI.Inference/ChatCapability.cs` and `VisionInput.cs`.
//!
//! Guiding principle: the consumer says WHAT they need (vision, tools, long
//! context); the SDK figures out WHICH model can do it on THIS device.

use bitflags::bitflags;
use serde::{Deserialize, Serialize};

bitflags! {
    /// Capability flags the consumer requests from a model selector. The
    /// selector finds the highest-quality model in the registry that satisfies
    /// every requested flag AND fits the device.
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
    pub struct ChatCapability: u32 {
        /// No requirement — selector picks the best-quality default-tier model that fits.
        const NONE = 0;
        /// Basic text chat. Every model in the registry satisfies this.
        const DEFAULT = 1 << 0;
        /// Model emits `<tool_call>{...}</tool_call>` blocks reliably (Qwen 3+ family).
        const TOOLS = 1 << 1;
        /// Model accepts image input via `VisionInput` (Kimi-VL family).
        const VISION = 1 << 2;
        /// Model supports a context window ≥ 32K tokens.
        const LONG_CONTEXT = 1 << 3;
        /// Model has an explicit "thinking" mode (Qwen3 reasoning variants).
        const REASONING = 1 << 4;
        /// (3.1.0) Model generates short videos from a text prompt.
        const VIDEO = 1 << 5;
    }
}

impl Default for ChatCapability {
    fn default() -> Self {
        ChatCapability::NONE
    }
}

/// Raw image data to be embedded by the vision encoder before text generation
/// begins. Passed to a vision-capable generator (Kimi-VL) when an image should
/// be embedded before the text prompt.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct VisionInput {
    /// Raw image bytes (JPEG, PNG, or any format the vision encoder accepts).
    #[serde(rename = "imageBytes")]
    pub image_bytes: Vec<u8>,

    /// Optional MIME type hint (e.g. "image/jpeg"). Useful for callers to track
    /// format; not passed to the native encoder directly.
    #[serde(rename = "mimeType", skip_serializing_if = "Option::is_none")]
    pub mime_type: Option<String>,
}

impl VisionInput {
    /// Constructs a vision input from raw bytes with no MIME hint.
    pub fn new(image_bytes: impl Into<Vec<u8>>) -> Self {
        Self {
            image_bytes: image_bytes.into(),
            mime_type: None,
        }
    }

    /// Attaches a MIME type hint.
    pub fn with_mime_type(mut self, mime_type: impl Into<String>) -> Self {
        self.mime_type = Some(mime_type.into());
        self
    }
}
