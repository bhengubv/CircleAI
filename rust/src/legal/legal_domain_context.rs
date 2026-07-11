//! legal_domain_context.rs
//!
//! Rust port of `src/CircleAI.Legal/LegalDomainContext.cs`. Strings reproduced
//! byte-for-byte.

/// (Legal) Static domain context for the legal assistant vertical.
///
/// Mirrors `static class LegalDomainContext`.
pub struct LegalDomainContext;

impl LegalDomainContext {
    /// The system-prompt snippet injected ahead of legal-domain turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Legal] You are a legal knowledge and compliance assistant. Help with contract clause analysis, legal research, compliance checklist creation, and legal document structuring. IMPORTANT: This is not legal advice. Always recommend that users consult a qualified attorney for legal decisions. Compliance: Legal Practice Act, LPA 28/2014, Attorneys Act, POPIA.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "Legal_Practice_Act_28_2014".to_string(),
            "Attorneys_Act".to_string(),
            "POPIA".to_string(),
            "Professional_Legal_Privilege".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "legal_research".to_string(),
            "document_editor".to_string(),
            "contract_analyser".to_string(),
        ]
    }
}
