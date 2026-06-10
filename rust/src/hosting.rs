//! hosting.rs
//!
//! IAIObserver + AIOptions for the hosting layer.

use crate::models_v15::{ChatResponse, UpgradeInfo};
use crate::selector::ChatCapability;

/// Stream of lifecycle events from the AI host. Implementations decide whether
/// to ignore individual callbacks.
pub trait IAIObserver: Send + Sync {
    fn on_started(&self) {}
    fn on_stopped(&self) {}
    fn on_chat_completed(&self, _response: &ChatResponse) {}
    fn on_stream_started(&self, _model_id: &str) {}
    fn on_stream_completed(&self, _model_id: &str, _token_count: u32) {}
    fn on_tool_invoked(&self, _tool_name: &str, _success: bool) {}
    fn on_model_fetching(&self, _model_id: &str, _auto_selected: bool) {}
    fn on_upgrade_available(&self, _upgrade: &UpgradeInfo) {}
}

/// No-op observer baseline.
pub struct NullAIObserver;
impl IAIObserver for NullAIObserver {}

/// Host options. All fields optional → reasonable defaults.
#[derive(Debug, Clone)]
pub struct AIOptions {
    pub model_id: Option<String>,
    pub model_path: Option<String>,
    pub system_prompt: String,
    pub context_size: Option<u32>,
    pub thread_count: Option<u32>,
    pub warm_on_start: bool,
    pub required_capabilities: ChatCapability,
    pub agentic_max_iterations: Option<u32>,
    pub check_for_upgrades_on_start: bool,
    pub model_storage_directory: Option<String>,
}

impl Default for AIOptions {
    fn default() -> Self {
        Self {
            model_id: None,
            model_path: None,
            system_prompt: "You are B!, a helpful on-device assistant.".to_string(),
            context_size: None,
            thread_count: None,
            warm_on_start: true,
            required_capabilities: ChatCapability::DEFAULT,
            agentic_max_iterations: None,
            check_for_upgrades_on_start: false,
            model_storage_directory: None,
        }
    }
}
