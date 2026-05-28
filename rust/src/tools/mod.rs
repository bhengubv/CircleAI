//! tools — ToolDefinition, ToolParameter, ToolInvocation, ToolResult,
//! IToolBridge trait, and facial_metric types.

pub mod facial_metric;

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::collections::HashMap;

// Re-export facial metric types
pub use facial_metric::{FaceBoundingBox, FaceExpressionClassification, FacialMetricMatrix};

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
    /// JSON-typed argument map.
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
    pub fn failure(tool_name: impl Into<String>, error: impl Into<String>) -> Self {
        Self {
            tool_name: tool_name.into(),
            success: false,
            result: None,
            error: Some(error.into()),
        }
    }

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
pub trait IToolBridge {
    type Error: std::error::Error;

    fn available_tools(&self) -> &[ToolDefinition];
    fn invoke(&mut self, invocation: &ToolInvocation) -> Result<ToolResult, Self::Error>;

    fn get_available_tools(&self) -> Result<Vec<ToolDefinition>, Self::Error> {
        Ok(self.available_tools().to_vec())
    }
}
