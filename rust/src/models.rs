//! models.rs
//!
//! Shared primitive types used across multiple Circle AI modules.
//! `ChatMessage` lives here alongside `DownloadProgress` so that modules that
//! only need the message type don't have to import the full inference module.

use serde::{Deserialize, Serialize};

/// A single message in a chat history.
///
/// `role` is one of `"system"`, `"user"`, or `"assistant"`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ChatMessage {
    pub role: String,
    pub content: String,
}

impl ChatMessage {
    pub fn new(role: impl Into<String>, content: impl Into<String>) -> Self {
        Self {
            role: role.into(),
            content: content.into(),
        }
    }

    pub fn system(content: impl Into<String>) -> Self {
        Self::new("system", content)
    }

    pub fn user(content: impl Into<String>) -> Self {
        Self::new("user", content)
    }

    pub fn assistant(content: impl Into<String>) -> Self {
        Self::new("assistant", content)
    }
}

/// Progress report for a model or asset download.
#[derive(Debug, Clone)]
pub struct DownloadProgress {
    pub bytes_received: u64,
    /// `None` when content-length is unknown.
    pub total_bytes: Option<u64>,
}

impl DownloadProgress {
    pub fn new(bytes_received: u64, total_bytes: Option<u64>) -> Self {
        Self {
            bytes_received,
            total_bytes,
        }
    }

    /// 0.0–1.0 fraction complete, or `None` when total is unknown.
    pub fn fraction(&self) -> Option<f64> {
        match self.total_bytes {
            Some(total) if total > 0 => Some(self.bytes_received as f64 / total as f64),
            _ => None,
        }
    }
}
