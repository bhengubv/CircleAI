//! tools.rs
//!
//! ToolDefinition, ToolParameter, ToolInvocation, ToolResult, and IToolBridge trait.
//!
//! Compatible with OpenAI/Qwen function-call schema.

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;

// ─────────────────────────────────────────────────────────────────────────────
// ToolParameter
// ─────────────────────────────────────────────────────────────────────────────

/// Describes a single parameter for a tool.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolParameter {
    /// JSON schema type: `"string"`, `"number"`, `"boolean"`, `"object"`, `"array"`.
    pub r#type: String,
    pub description: String,
    /// Optional enumeration of allowed values.
    pub r#enum: Option<Vec<String>>,
}

impl ToolParameter {
    pub fn new(r#type: impl Into<String>, description: impl Into<String>) -> Self {
        Self {
            r#type: r#type.into(),
            description: description.into(),
            r#enum: None,
        }
    }

    pub fn with_enum(mut self, values: Vec<String>) -> Self {
        self.r#enum = Some(values);
        self
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ToolDefinition
// ─────────────────────────────────────────────────────────────────────────────

/// Describes a tool the model can call.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolDefinition {
    pub name: String,
    pub description: String,
    pub parameters: HashMap<String, ToolParameter>,
    pub required_parameters: Vec<String>,
}

impl ToolDefinition {
    pub fn new(
        name: impl Into<String>,
        description: impl Into<String>,
        parameters: HashMap<String, ToolParameter>,
        required_parameters: Vec<String>,
    ) -> Self {
        Self {
            name: name.into(),
            description: description.into(),
            parameters,
            required_parameters,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ToolInvocation
// ─────────────────────────────────────────────────────────────────────────────

/// A tool call emitted by the model.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolInvocation {
    pub tool_name: String,
    /// JSON-typed argument map. Uses `serde_json::Value` for flexibility.
    pub arguments: HashMap<String, Value>,
}

impl ToolInvocation {
    pub fn new(tool_name: impl Into<String>, arguments: HashMap<String, Value>) -> Self {
        Self {
            tool_name: tool_name.into(),
            arguments,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ToolResult
// ─────────────────────────────────────────────────────────────────────────────

/// The outcome of executing a tool invocation.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolResult {
    pub tool_name: String,
    pub success: bool,
    pub result: Option<Value>,
    pub error: Option<String>,
}

impl ToolResult {
    /// Convenience factory for a failed tool result.
    pub fn failure(tool_name: impl Into<String>, error: impl Into<String>) -> Self {
        Self {
            tool_name: tool_name.into(),
            success: false,
            result: None,
            error: Some(error.into()),
        }
    }

    /// Convenience factory for a successful tool result.
    pub fn ok(tool_name: impl Into<String>, result: Option<Value>) -> Self {
        Self {
            tool_name: tool_name.into(),
            success: true,
            result,
            error: None,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IToolBridge trait
// ─────────────────────────────────────────────────────────────────────────────

/// Bridge between the local LLM and the TheGeekNetwork APIs.
///
/// Implementations route tool calls to the appropriate API client
/// (HTTP, in-process service, etc.).
pub trait IToolBridge {
    type Error: std::error::Error;

    fn available_tools(&self) -> &[ToolDefinition];
    fn invoke(&mut self, invocation: &ToolInvocation) -> Result<ToolResult, Self::Error>;

    /// Returns the tools available through this bridge, optionally querying the
    /// remote service. Default implementation returns `available_tools()`.
    fn get_available_tools(&self) -> Result<Vec<ToolDefinition>, Self::Error> {
        Ok(self.available_tools().to_vec())
    }
}
