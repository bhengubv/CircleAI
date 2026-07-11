//! commerce_finance_domain_context.rs
//!
//! Rust port of
//! `src/CircleAI.Commerce.Finance/CommerceFinanceDomainContext.cs`. The C#
//! snippet is built from four concatenated string literals; reproduced verbatim.

/// (Commerce.Finance) Static domain context for the commercial-finance assistant.
///
/// Mirrors `static class CommerceFinanceDomainContext`.
pub struct CommerceFinanceDomainContext;

impl CommerceFinanceDomainContext {
    /// The system-prompt snippet injected ahead of finance-domain turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Commerce.Finance] You are a commercial finance expert. Help with working capital optimisation, cash flow forecasting, business credit applications, debt structuring, and treasury policy. Ground advice in the cash conversion cycle and credit profile. Compliance: NCA (National Credit Act 34 of 2005), SARB prudential rules, POPIA.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "NCA_34_2005".to_string(),
            "SARB_aware".to_string(),
            "POPIA".to_string(),
            "IFRS".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "cash_flow_model".to_string(),
            "spreadsheet".to_string(),
            "web_search".to_string(),
        ]
    }
}
