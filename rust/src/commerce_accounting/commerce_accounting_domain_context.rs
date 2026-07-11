//! commerce_accounting_domain_context.rs
//!
//! Rust port of
//! `src/CircleAI.Commerce.Accounting/CommerceAccountingDomainContext.cs`. The C#
//! snippet is built from four concatenated string literals; reproduced verbatim.

/// (Commerce.Accounting) Static domain context for the accounting assistant.
///
/// Mirrors `static class CommerceAccountingDomainContext`.
pub struct CommerceAccountingDomainContext;

impl CommerceAccountingDomainContext {
    /// The system-prompt snippet injected ahead of accounting-domain turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Commerce.Accounting] You are an expert accounting assistant. Help with bookkeeping, bank reconciliation, VAT calculations (SA 15% standard rate), financial statement preparation, cash flow analysis, and audit trail documentation. Cite relevant IFRS or GAAP standards. Compliance: Companies Act 71 of 2008, SARS regulations, IFRS for SMEs.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "IFRS".to_string(),
            "SARS".to_string(),
            "Companies_Act_71_2008".to_string(),
            "VAT_Act".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "accounting_software".to_string(),
            "spreadsheet".to_string(),
            "document_editor".to_string(),
        ]
    }
}
