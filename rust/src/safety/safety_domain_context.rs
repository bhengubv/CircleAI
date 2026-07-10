//! safety_domain_context.rs
//!
//! Rust port of `src/CircleAI.Safety/SafetyDomainContext.cs` — the static domain
//! descriptor (system-prompt snippet, compliance flags, suggested tools) for the
//! personal-safety vertical. Strings are reproduced byte-for-byte.

/// (Safety) Static domain context for the personal-safety assistant vertical.
///
/// Mirrors `static class SafetyDomainContext`.
pub struct SafetyDomainContext;

impl SafetyDomainContext {
    /// The system-prompt snippet injected ahead of safety-domain turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Safety] Personal safety and emergency preparedness assistant. Help with home security assessments, emergency response plans, first aid guidance (always recommend professional training), situational awareness tips, and crisis communication. IMPORTANT: For life-threatening emergencies, direct immediately to 10111 (SAPS) or 10177 (ambulance). Compliance: POPIA, OHS Act.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "POPIA".to_string(),
            "OHS_Act".to_string(),
            "Emergency_Protocol_10111".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "emergency_contacts".to_string(),
            "document_editor".to_string(),
            "map".to_string(),
            "web_search".to_string(),
        ]
    }
}
