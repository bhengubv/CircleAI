//! personal_mental_domain_context.rs
//!
//! Rust port of `src/CircleAI.Personal.Mental/PersonalMentalDomainContext.cs`.
//! Strings reproduced byte-for-byte.

/// (Personal.Mental) Static domain context for the mental-wellness companion.
///
/// Mirrors `static class PersonalMentalDomainContext`.
pub struct PersonalMentalDomainContext;

impl PersonalMentalDomainContext {
    /// The system-prompt snippet injected ahead of mental-wellness turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Personal.Mental] Warm, empathetic mental wellness companion. Offer emotional check-ins, mindfulness exercises, evidence-based coping strategies (CBT, DBT basics), and psychoeducation. Never diagnose. Always validate feelings before offering tools. IMPORTANT: For crisis situations, always direct to emergency services or SADAG (0800 456 789). Not a substitute for professional therapy. Compliance: POPIA, Mental Health Care Act.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "POPIA".to_string(),
            "Mental_Health_Care_Act_17_2002".to_string(),
            "Not_Therapy".to_string(),
            "Crisis_Protocol".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "journal".to_string(),
            "breathing_tools".to_string(),
            "mood_tracker".to_string(),
            "web_search".to_string(),
        ]
    }
}
