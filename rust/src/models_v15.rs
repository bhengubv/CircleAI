//! models_v15.rs
//!
//! 1.5.0 portable surface extensions. Kept in a separate module so the
//! 1.0–1.4 `models.rs` ChatMessage stays byte-stable.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// Why a generation stopped.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum FinishReason {
    /// Hit the natural end-of-sequence token.
    Stop = 0,
    /// Reached the requested token budget.
    MaxTokens = 1,
    /// Matched one of the caller's stop sequences.
    StopSequence = 2,
    /// Cancelled by the caller before completing.
    Cancelled = 3,
    /// Generator surfaced an error mid-stream.
    Error = 4,
    /// Generator did not provide a reason.
    Unknown = 5,
}

/// Structured generation result.
///
/// `reasoning_content` is the chain-of-thought emitted by reasoning models
/// (Qwen3 / DeepSeek-R1 / o1) inside `<think>…</think>`. `None` when the
/// model emitted no reasoning or `GenerationOptions::include_reasoning`
/// was `false`. Tags themselves are stripped — only the text content.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatResponse {
    pub text: String,
    #[serde(rename = "finishReason")]
    pub finish_reason: FinishReason,
    /// Optional tokens-generated count (None if the generator can't report).
    #[serde(rename = "tokensGenerated", skip_serializing_if = "Option::is_none")]
    pub tokens_generated: Option<i32>,
    #[serde(rename = "reasoningContent", skip_serializing_if = "Option::is_none")]
    pub reasoning_content: Option<String>,
}

impl ChatResponse {
    pub fn new(text: impl Into<String>, finish_reason: FinishReason) -> Self {
        Self {
            text: text.into(),
            finish_reason,
            tokens_generated: None,
            reasoning_content: None,
        }
    }

    pub fn with_reasoning(mut self, reasoning: impl Into<String>) -> Self {
        self.reasoning_content = Some(reasoning.into());
        self
    }
}

/// Kind of fragment a streaming generator emits.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ChatFragmentKind {
    /// Part of the user-facing answer (goes into `content`).
    Content,
    /// Part of the model's reasoning trace (goes into `reasoning_content`).
    Reasoning,
}

/// A single fragment yielded by streaming generators.
/// Tagged so the caller can route the model's `<think>` block into a
/// separate `reasoning_content` field (o1 / DeepSeek style).
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ChatFragment {
    pub kind: ChatFragmentKind,
    pub text: String,
}

impl ChatFragment {
    pub fn content(text: impl Into<String>) -> Self {
        Self { kind: ChatFragmentKind::Content, text: text.into() }
    }

    pub fn reasoning(text: impl Into<String>) -> Self {
        Self { kind: ChatFragmentKind::Reasoning, text: text.into() }
    }
}

/// One file inside a model bundle, with its expected hash.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BundleFile {
    /// Relative path inside the model directory (e.g. "llm.mnn", "tokenizer.txt").
    pub name: String,
    /// Lowercase hex SHA-256 of the file's bytes.
    pub sha256: String,
    /// File size in bytes.
    #[serde(rename = "sizeBytes")]
    pub size_bytes: i64,
}

/// Manifest written to disk after a successful model install. Sits at
/// `<storage>/<modelId>/installed.json`.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct InstalledManifest {
    #[serde(rename = "modelId")]
    pub model_id: String,
    pub version: String,
    #[serde(skip_serializing_if = "Option::is_none")]
    pub repo: Option<String>,
    #[serde(rename = "totalBytes")]
    pub total_bytes: i64,
    pub files: Vec<BundleFile>,
    #[serde(rename = "installedAtUtc")]
    pub installed_at_utc: DateTime<Utc>,
}

/// Why an installed model is considered out of date.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum UpgradeReason {
    /// We can see a model dir on disk but no installed.json — can't tell what's there.
    Unknown = 0,
    /// Manifest version differs from catalog version.
    VersionChanged = 1,
    /// At least one bundle file's SHA differs from catalog.
    ShaChanged = 2,
    /// Both version and SHA differ.
    Both = 3,
}

/// A single upgrade detection result.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct UpgradeInfo {
    #[serde(rename = "modelId")]
    pub model_id: String,
    /// `None` when the installed manifest is missing (Unknown reason).
    #[serde(rename = "installedVersion", skip_serializing_if = "Option::is_none")]
    pub installed_version: Option<String>,
    #[serde(rename = "availableVersion")]
    pub available_version: String,
    pub reason: UpgradeReason,
    /// Sum of `BundleFile.size_bytes` for files that actually drifted.
    /// 0 for `VersionChanged` (no SHAs differ), total catalog bytes for `Unknown`.
    #[serde(rename = "estimatedDownloadBytes")]
    pub estimated_download_bytes: i64,
    #[serde(rename = "detectedAt")]
    pub detected_at: DateTime<Utc>,
}

/// Multimodal extension to `models::ChatMessage`. Kept separate so the
/// 1.0–1.4 shape stays unchanged for callers that only do text.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct VisionChatMessage {
    pub role: String,
    pub content: String,
    /// Optional raw image bytes (PNG/JPEG/etc.) for vision-capable models.
    #[serde(rename = "imageBytes", skip_serializing_if = "Option::is_none")]
    pub image_bytes: Option<Vec<u8>>,
}
