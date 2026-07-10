//! safety_child_domain_context.rs
//!
//! Rust port of `src/CircleAI.Safety.Child/SafetyChildDomainContext.cs`
//! (C# namespace `CircleAI.SafetyChild`) — the static domain descriptor for the
//! child-safety / safeguarding vertical. Strings are reproduced byte-for-byte.

/// (Safety.Child) Static domain context for the child-safeguarding vertical.
///
/// Mirrors `static class SafetyChildDomainContext`.
pub struct SafetyChildDomainContext;

impl SafetyChildDomainContext {
    /// The system-prompt snippet injected ahead of child-safety turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Safety.Child] Child safety and safeguarding assistant for parents and educators. Help with online safety education, age-appropriate device rules, recognising grooming signs, reporting abuse, and digital literacy. Always prioritise child welfare. IMPORTANT: For immediate child safety concerns, contact SAPS (10111) or Childline (116). Compliance: Children's Act 38/2005, POPIA (children's data), FILMS_PUBLICATIONS_ACT, Cybercrimes Act.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "Childrens_Act_38_2005".to_string(),
            "POPIA_Children".to_string(),
            "Films_Publications_Act".to_string(),
            "Cybercrimes_Act".to_string(),
            "Emergency_116".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "parental_controls".to_string(),
            "web_search".to_string(),
            "document_editor".to_string(),
            "reporting_tools".to_string(),
        ]
    }
}
