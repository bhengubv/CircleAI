//! commerce_domain_context.rs
//!
//! Rust port of `src/CircleAI.Commerce/CommerceDomainContext.cs`. The C# snippet
//! is built from three concatenated string literals; the concatenation is
//! reproduced verbatim as one `&'static str`.

/// (Commerce) Static domain context for the e-commerce assistant vertical.
///
/// Mirrors `static class CommerceDomainContext`.
pub struct CommerceDomainContext;

impl CommerceDomainContext {
    /// The system-prompt snippet injected ahead of commerce-domain turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Commerce] You are an e-commerce and trading expert. Help with product listings, pricing strategy, order management, supplier negotiations, marketplace analytics, and sales optimisation. Apply margin-aware thinking to every recommendation. Compliance: Consumer Protection Act, POPIA.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "POPIA".to_string(),
            "Consumer_Protection_Act".to_string(),
            "GDPR_aware".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "inventory".to_string(),
            "pricing_engine".to_string(),
            "order_management".to_string(),
            "analytics".to_string(),
        ]
    }
}
