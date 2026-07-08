//! openai.rs
//!
//! OpenAI-compatible request/response DTOs ported from
//! `CircleAI.Inference.Server/Models/OpenAI/` (`ChatCompletion.cs`,
//! `Embeddings.cs`, `ErrorResponse.cs`).
//!
//! Field names and JSON shape match the public OpenAI Chat Completions /
//! Embeddings API (v1) so SDKs targeting OpenAI work against CircleAI with only
//! a base-URL change. `serde` attributes reproduce the C# `[JsonPropertyName]` +
//! `[JsonIgnore(WhenWritingNull)]` behaviour exactly.

use serde::{Deserialize, Serialize};

// ─────────────────────────────────────────────────────────────────────────────
// Chat completions
// ─────────────────────────────────────────────────────────────────────────────

/// OpenAI-shaped chat-completion request body.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ChatCompletionRequest {
    #[serde(default)]
    pub model: String,
    #[serde(default)]
    pub messages: Vec<ChatCompletionMessage>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub temperature: Option<f32>,
    #[serde(default, rename = "top_p", skip_serializing_if = "Option::is_none")]
    pub top_p: Option<f32>,
    #[serde(default, rename = "max_tokens", skip_serializing_if = "Option::is_none")]
    pub max_tokens: Option<i32>,
    #[serde(default)]
    pub stream: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stop: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub user: Option<String>,
}

/// One message in the chat-completion conversation.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ChatCompletionMessage {
    #[serde(default = "default_role")]
    pub role: String,
    #[serde(default)]
    pub content: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub name: Option<String>,
    /// Chain-of-thought trace (Qwen3 `<think>`, DeepSeek-R1, o1). Omitted from
    /// JSON when `None` (matches `JsonIgnore(WhenWritingNull)`).
    #[serde(
        default,
        rename = "reasoning_content",
        skip_serializing_if = "Option::is_none"
    )]
    pub reasoning_content: Option<String>,
}

fn default_role() -> String {
    "user".to_string()
}

impl Default for ChatCompletionMessage {
    fn default() -> Self {
        Self {
            role: default_role(),
            content: String::new(),
            name: None,
            reasoning_content: None,
        }
    }
}

/// OpenAI-shaped successful chat-completion response.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ChatCompletionResponse {
    #[serde(default)]
    pub id: String,
    #[serde(default = "chat_completion_object")]
    pub object: String,
    #[serde(default)]
    pub created: i64,
    #[serde(default)]
    pub model: String,
    #[serde(default)]
    pub choices: Vec<ChatCompletionChoice>,
    #[serde(default)]
    pub usage: UsageInfo,
}

fn chat_completion_object() -> String {
    "chat.completion".to_string()
}

impl Default for ChatCompletionResponse {
    fn default() -> Self {
        Self {
            id: String::new(),
            object: chat_completion_object(),
            created: 0,
            model: String::new(),
            choices: Vec::new(),
            usage: UsageInfo::default(),
        }
    }
}

/// One choice in a non-streaming chat-completion response.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ChatCompletionChoice {
    #[serde(default)]
    pub index: i32,
    #[serde(default)]
    pub message: ChatCompletionMessage,
    #[serde(default = "finish_stop", rename = "finish_reason")]
    pub finish_reason: String,
}

fn finish_stop() -> String {
    "stop".to_string()
}

impl Default for ChatCompletionChoice {
    fn default() -> Self {
        Self {
            index: 0,
            message: ChatCompletionMessage::default(),
            finish_reason: finish_stop(),
        }
    }
}

/// Token-usage block.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct UsageInfo {
    #[serde(default, rename = "prompt_tokens")]
    pub prompt_tokens: i32,
    #[serde(default, rename = "completion_tokens")]
    pub completion_tokens: i32,
    #[serde(default, rename = "total_tokens")]
    pub total_tokens: i32,
}

/// One SSE delta frame in a streamed chat completion.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ChatCompletionStreamChunk {
    #[serde(default)]
    pub id: String,
    #[serde(default = "chat_chunk_object")]
    pub object: String,
    #[serde(default)]
    pub created: i64,
    #[serde(default)]
    pub model: String,
    #[serde(default)]
    pub choices: Vec<ChatCompletionStreamChoice>,
}

fn chat_chunk_object() -> String {
    "chat.completion.chunk".to_string()
}

impl Default for ChatCompletionStreamChunk {
    fn default() -> Self {
        Self {
            id: String::new(),
            object: chat_chunk_object(),
            created: 0,
            model: String::new(),
            choices: Vec::new(),
        }
    }
}

/// One delta in a streamed chat-completion chunk.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ChatCompletionStreamChoice {
    #[serde(default)]
    pub index: i32,
    #[serde(default)]
    pub delta: ChatCompletionDelta,
    #[serde(default, rename = "finish_reason", skip_serializing_if = "Option::is_none")]
    pub finish_reason: Option<String>,
}

/// Delta payload — only non-null fields are emitted between SSE frames.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ChatCompletionDelta {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub role: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub content: Option<String>,
    #[serde(
        default,
        rename = "reasoning_content",
        skip_serializing_if = "Option::is_none"
    )]
    pub reasoning_content: Option<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Embeddings
// ─────────────────────────────────────────────────────────────────────────────

/// OpenAI-shaped embeddings request. `input` is either a single string or an
/// array of strings — modelled as a `serde_json::Value` (matching the C#
/// `JsonElement`), with [`EmbeddingsRequest::inputs`] normalising both shapes.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct EmbeddingsRequest {
    #[serde(default)]
    pub model: String,
    #[serde(default)]
    pub input: serde_json::Value,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub user: Option<String>,
}

impl Default for EmbeddingsRequest {
    fn default() -> Self {
        Self {
            model: String::new(),
            input: serde_json::Value::Null,
            user: None,
        }
    }
}

impl EmbeddingsRequest {
    /// Normalises the polymorphic `input` field into a list of strings. A bare
    /// string becomes a one-element list; an array of strings is flattened;
    /// anything else yields an empty list.
    pub fn inputs(&self) -> Vec<String> {
        match &self.input {
            serde_json::Value::String(s) => vec![s.clone()],
            serde_json::Value::Array(items) => items
                .iter()
                .filter_map(|v| v.as_str().map(|s| s.to_string()))
                .collect(),
            _ => Vec::new(),
        }
    }
}

/// OpenAI-shaped embeddings response.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct EmbeddingsResponse {
    #[serde(default = "list_object")]
    pub object: String,
    #[serde(default)]
    pub data: Vec<EmbeddingDatum>,
    #[serde(default)]
    pub model: String,
    #[serde(default)]
    pub usage: UsageInfo,
}

fn list_object() -> String {
    "list".to_string()
}

impl Default for EmbeddingsResponse {
    fn default() -> Self {
        Self {
            object: list_object(),
            data: Vec::new(),
            model: String::new(),
            usage: UsageInfo::default(),
        }
    }
}

/// One embedding row in the response.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct EmbeddingDatum {
    #[serde(default = "embedding_object")]
    pub object: String,
    #[serde(default)]
    pub index: i32,
    #[serde(default)]
    pub embedding: Vec<f32>,
}

fn embedding_object() -> String {
    "embedding".to_string()
}

impl Default for EmbeddingDatum {
    fn default() -> Self {
        Self {
            object: embedding_object(),
            index: 0,
            embedding: Vec::new(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Error envelope
// ─────────────────────────────────────────────────────────────────────────────

/// OpenAI-shaped error envelope: `{"error": {...}}`.
#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct ErrorResponse {
    #[serde(default)]
    pub error: ErrorBody,
}

impl ErrorResponse {
    /// Convenience constructor mirroring `ErrorResponse.Of`.
    pub fn of(
        message: impl Into<String>,
        error_type: impl Into<String>,
        code: Option<&str>,
    ) -> Self {
        Self {
            error: ErrorBody {
                message: message.into(),
                error_type: error_type.into(),
                param: None,
                code: code.map(|c| c.to_string()),
            },
        }
    }
}

/// Inner error body.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct ErrorBody {
    #[serde(default)]
    pub message: String,
    #[serde(default = "invalid_request_error", rename = "type")]
    pub error_type: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub param: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub code: Option<String>,
}

fn invalid_request_error() -> String {
    "invalid_request_error".to_string()
}

impl Default for ErrorBody {
    fn default() -> Self {
        Self {
            message: String::new(),
            error_type: invalid_request_error(),
            param: None,
            code: None,
        }
    }
}
