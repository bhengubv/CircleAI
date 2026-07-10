//! null_implementations.rs
//!
//! (2.6.0) Fail-closed defaults — Rust port of
//! `src/CircleAI.ContentPolicy/NullImplementations.cs`.
//!
//! When there is no real backend wired we treat content as **refused** (the
//! safest default): `NullContentFilter` and `NullPromptInjectionDetector` return
//! `Refuse`, and `NullRefusalPolicy` always refuses. `NullSafetyAuditLog` is an
//! inert sink.

use super::contracts::{
    IContentFilter, IPromptInjectionDetector, IRefusalPolicy, ISafetyAuditLog, SafetyAuditEntry,
    SafetyFinding, SafetyVerdict,
};

/// (2.6.0) Fail-closed content filter — always refuses.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullContentFilter;

impl NullContentFilter {
    /// The shared singleton (the C# `Instance`).
    pub const INSTANCE: NullContentFilter = NullContentFilter;
}

impl IContentFilter for NullContentFilter {
    fn backend_id(&self) -> &str {
        "null"
    }

    fn classify(&self, _text: &str) -> SafetyFinding {
        SafetyFinding::new(
            SafetyVerdict::Refuse,
            "no-filter-configured",
            "Fail-closed default — wire a real IContentFilter to relax.",
            1.0,
        )
    }
}

/// (2.6.0) Fail-closed refusal policy — always refuses.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullRefusalPolicy;

impl NullRefusalPolicy {
    /// The shared singleton (the C# `Instance`).
    pub const INSTANCE: NullRefusalPolicy = NullRefusalPolicy;
}

impl IRefusalPolicy for NullRefusalPolicy {
    fn backend_id(&self) -> &str {
        "null"
    }

    fn should_refuse(&self, _findings: &[SafetyFinding]) -> bool {
        true
    }
}

/// (2.6.0) Fail-closed prompt-injection detector — always refuses.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPromptInjectionDetector;

impl NullPromptInjectionDetector {
    /// The shared singleton (the C# `Instance`).
    pub const INSTANCE: NullPromptInjectionDetector = NullPromptInjectionDetector;
}

impl IPromptInjectionDetector for NullPromptInjectionDetector {
    fn backend_id(&self) -> &str {
        "null"
    }

    fn inspect(&self, _content: &str, _source: &str) -> SafetyFinding {
        SafetyFinding::new(
            SafetyVerdict::Refuse,
            "no-detector-configured",
            "Fail-closed default.",
            1.0,
        )
    }
}

/// (2.6.0) Inert safety audit log — logging is a no-op and reads return empty.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSafetyAuditLog;

impl NullSafetyAuditLog {
    /// The shared singleton (the C# `Instance`).
    pub const INSTANCE: NullSafetyAuditLog = NullSafetyAuditLog;
}

impl ISafetyAuditLog for NullSafetyAuditLog {
    fn backend_id(&self) -> &str {
        "null"
    }

    fn log(&self, _entry: SafetyAuditEntry) {}

    fn read(&self, _user_id: Option<&str>, _limit: usize) -> Vec<SafetyAuditEntry> {
        Vec::new()
    }
}
