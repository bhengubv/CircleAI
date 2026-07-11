//! personal_health_domain_context.rs
//!
//! Rust port of `src/CircleAI.Personal.Health/PersonalHealthDomainContext.cs`.
//! Strings reproduced byte-for-byte.

/// (Personal.Health) Static domain context for the personal-health assistant.
///
/// Mirrors `static class PersonalHealthDomainContext`.
pub struct PersonalHealthDomainContext;

impl PersonalHealthDomainContext {
    /// The system-prompt snippet injected ahead of personal-health turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Personal.Health] Personal health and wellness assistant. Help with symptom tracking, appointment preparation, medication reminders, health goal setting, nutrition basics, and health literacy. IMPORTANT: Always recommend consulting a qualified healthcare professional for medical decisions. This is not medical advice. Compliance: POPIA, Health Professions Act.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "POPIA".to_string(),
            "Health_Professions_Act".to_string(),
            "Not_Medical_Advice".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "health_tracker".to_string(),
            "symptom_checker_ref".to_string(),
            "calendar".to_string(),
            "document_editor".to_string(),
        ]
    }
}
