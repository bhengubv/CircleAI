//! brain.rs
//!
//! Shared error type for the memory-brain concrete implementations (episodic
//! store, knowledge graph, HippoRAG, fused recall, extractors, belief store,
//! encoder, and session).
//!
//! The existing store/generator/session traits in this crate are generic over an
//! associated `Error: std::error::Error`. The memory-brain concretes all use a
//! single, simple string-backed error — matching the reference ports, whose
//! errors are all `errors.New("…")` (Go) / `throw new Error("…")` (TS) style
//! flat messages.

use std::fmt;

/// A flat, string-backed error for the memory-brain. Mirrors the reference
/// ports' `errors.New(msg)` / `Error(msg)` style.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BrainError {
    message: String,
}

impl BrainError {
    /// Creates a new error with the given message.
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }

    /// The error message.
    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for BrainError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for BrainError {}
