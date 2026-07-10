//! content_policy — CircleAI safety guardrails.
//!
//! Full Rust port of `src/CircleAI.ContentPolicy/*.cs` (C# namespace
//! `CircleAI.ContentPolicy`, formerly `CircleAI.Guardrails`).
//!
//! Three cooperating contracts, each with a fail-closed `Null*` default and a
//! real keyword/regex implementation:
//!
//! - [`IContentFilter`] — per-message classification into
//!   [`SafetyVerdict::Allow`] / [`SafetyVerdict::Flag`] / [`SafetyVerdict::Refuse`].
//!   Real: [`KeywordContentFilter`]; fail-closed: [`NullContentFilter`].
//! - [`IRefusalPolicy`] — turns a set of [`SafetyFinding`]s into a refuse/allow
//!   decision. Real: [`ThresholdRefusalPolicy`]; fail-closed: [`NullRefusalPolicy`].
//! - [`IPromptInjectionDetector`] — inspects untrusted RAG/tool/web content for
//!   second-order attacks. Real: [`KeywordPromptInjectionDetector`]; fail-closed:
//!   [`NullPromptInjectionDetector`].
//!
//! Plus an append-only [`ISafetyAuditLog`] contract with an inert
//! [`NullSafetyAuditLog`] default.

pub mod contracts;
pub mod keyword_content_filter;
pub mod null_implementations;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use contracts::{
    IContentFilter, IPromptInjectionDetector, IRefusalPolicy, ISafetyAuditLog, SafetyAuditEntry,
    SafetyFinding, SafetyVerdict,
};
pub use keyword_content_filter::{
    CommonKeywordRules, KeywordContentFilter, KeywordPromptInjectionDetector, KeywordRule,
    ThresholdRefusalPolicy,
};
pub use null_implementations::{
    NullContentFilter, NullPromptInjectionDetector, NullRefusalPolicy, NullSafetyAuditLog,
};
