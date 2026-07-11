//! personal_finance_domain_context.rs
//!
//! Rust port of
//! `src/CircleAI.Personal.Finance/PersonalFinanceDomainContext.cs`. Strings
//! reproduced byte-for-byte.

/// (Personal.Finance) Static domain context for the personal-finance coach.
///
/// Mirrors `static class PersonalFinanceDomainContext`.
pub struct PersonalFinanceDomainContext;

impl PersonalFinanceDomainContext {
    /// The system-prompt snippet injected ahead of personal-finance turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Personal.Finance] Personal finance coach. Help with monthly budgeting, emergency fund planning, debt snowball/avalanche strategy, savings goals, retirement planning basics, and investment options education. IMPORTANT: This is financial education, not advice. Recommend a registered financial planner for personalised investment advice. Compliance: FAIS Act, NCA, POPIA.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "FAIS_Act_37_2002".to_string(),
            "NCA".to_string(),
            "POPIA".to_string(),
            "Not_Financial_Advice".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "budget_tracker".to_string(),
            "spreadsheet".to_string(),
            "calculator".to_string(),
            "web_search".to_string(),
        ]
    }
}
