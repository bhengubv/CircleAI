//! chat_runtime.rs
//!
//! Host-neutral chat runtime seam — port of `CircleAI.Hosting.Chat.IChatRuntime`.
//! Lets a UI / harness drive the on-device engine without touching inference
//! types. `NeuronNode` implements these over an `IAIService` brain. Sync port:
//! `stream` returns a materialised chunk list (as the hosting `IAIService` does).

use super::service::HostingError;

/// Host-neutral chat turn. Mirrors `ChatTurn` (role / content).
#[derive(Debug, Clone)]
pub struct ChatTurn {
    pub role: String,
    pub content: String,
}

impl ChatTurn {
    /// Builds a turn from any string-likes.
    pub fn new(role: impl Into<String>, content: impl Into<String>) -> Self {
        Self { role: role.into(), content: content.into() }
    }
}

/// Host-neutral chat surface. Mirrors `IChatRuntime`.
pub trait IChatRuntime: Send + Sync {
    fn id(&self) -> String;
    fn engine_label(&self) -> String;
    fn is_ready(&self) -> bool;
    fn status_message(&self) -> String;
    /// Streams the reply as a materialised chunk list.
    fn stream(&self, messages: &[ChatTurn]) -> Result<Vec<String>, HostingError>;
}

/// Optional KV-snapshot capability. Mirrors `IPersistableChatRuntime`.
pub trait IPersistableChatRuntime: Send + Sync {
    fn session_snapshot_path(&self) -> Option<String>;
    fn save_session(&self, path: &str) -> Result<bool, HostingError>;
    fn load_session(&self, path: &str) -> Result<bool, HostingError>;
}

const NULL_STATUS: &str = "No chat engine is wired. Add a NeuronNode (or another IChatRuntime adapter) to enable conversations.";

/// Honest "engine offline" runtime. Mirrors `NullChatRuntime`.
pub struct NullChatRuntime;

impl IChatRuntime for NullChatRuntime {
    fn id(&self) -> String {
        "null".to_string()
    }
    fn engine_label(&self) -> String {
        "No engine wired".to_string()
    }
    fn is_ready(&self) -> bool {
        false
    }
    fn status_message(&self) -> String {
        NULL_STATUS.to_string()
    }
    fn stream(&self, _messages: &[ChatTurn]) -> Result<Vec<String>, HostingError> {
        Ok(vec![NULL_STATUS.to_string()])
    }
}
