//! commerce_payfast_domain_context.rs
//!
//! Rust port of
//! `src/CircleAI.Commerce.Integration.PayFast/CommerceIntegrationPayFastDomainContext.cs`.
//! The C# snippet is built from four concatenated string literals; reproduced
//! verbatim.

/// (Commerce.Integration.PayFast) Static domain context for the PayFast expert.
///
/// Mirrors `static class CommerceIntegrationPayFastDomainContext`.
pub struct CommerceIntegrationPayFastDomainContext;

impl CommerceIntegrationPayFastDomainContext {
    /// The system-prompt snippet injected ahead of PayFast-domain turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Commerce.Integration.PayFast] You are a PayFast payment gateway integration expert. Help with PayFast ITN (Instant Transaction Notification) webhook handling, payment flow debugging, refund processing, subscription billing, split payments, and PCI-DSS compliance guidance. Compliance: PCI-DSS, POPIA, PASA, Consumer Protection Act.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "PCI_DSS".to_string(),
            "POPIA".to_string(),
            "PASA".to_string(),
            "Consumer_Protection_Act".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "payfast_api".to_string(),
            "webhook_debugger".to_string(),
            "document_editor".to_string(),
        ]
    }
}
