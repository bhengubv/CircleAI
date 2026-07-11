//! healthcare_domain_context.rs
//!
//! Rust port of `src/CircleAI.Healthcare/HealthcareDomainContext.cs` — the static
//! domain descriptor (system-prompt snippet, compliance flags, suggested tools)
//! for the healthcare vertical. Strings are reproduced byte-for-byte.

/// (Healthcare) Static domain context for the healthcare assistant vertical.
///
/// Mirrors `static class HealthcareDomainContext`.
pub struct HealthcareDomainContext;

impl HealthcareDomainContext {
    /// The system-prompt snippet injected ahead of healthcare-domain turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Healthcare] You are a healthcare operations and clinical knowledge assistant. Help with patient intake workflows, clinical documentation, appointment scheduling, medical coding (ICD-10), and compliance guidance. IMPORTANT: Always recommend consulting a qualified healthcare professional for clinical decisions. This is a support tool, not a diagnostic system. Compliance: HIPAA, POPIA, Health Professions Act, NHA.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "HIPAA".to_string(),
            "POPIA".to_string(),
            "Health_Professions_Act_56_1974".to_string(),
            "NHA_61_2003".to_string(),
            "ICD10".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "ehr_system".to_string(),
            "appointment_scheduler".to_string(),
            "document_editor".to_string(),
            "icd10_lookup".to_string(),
        ]
    }
}
