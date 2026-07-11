//! commerce_xero_domain_context.rs
//!
//! Rust port of
//! `src/CircleAI.Commerce.Integration.Xero/CommerceIntegrationXeroDomainContext.cs`.
//! The C# snippet is built from four concatenated string literals; reproduced
//! verbatim.

/// (Commerce.Integration.Xero) Static domain context for the Xero expert.
///
/// Mirrors `static class CommerceIntegrationXeroDomainContext`.
pub struct CommerceIntegrationXeroDomainContext;

impl CommerceIntegrationXeroDomainContext {
    /// The system-prompt snippet injected ahead of Xero-domain turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Commerce.Integration.Xero] You are a Xero accounting platform expert. Help with Xero chart of accounts, invoice creation, bank feeds, reconciliation workflows, Xero reporting, and API integration troubleshooting. Reference Xero HQ documentation for accuracy. Compliance: SARS, IFRS for SMEs, Xero data handling standards.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "SARS".to_string(),
            "IFRS".to_string(),
            "Xero_Data_Standards".to_string(),
            "POPIA".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "xero_api".to_string(),
            "spreadsheet".to_string(),
            "document_editor".to_string(),
        ]
    }
}
