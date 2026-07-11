//! education_domain_context.rs
//!
//! Rust port of `src/CircleAI.Education/EducationDomainContext.cs`. Strings
//! reproduced byte-for-byte.

/// (Education) Static domain context for the education assistant vertical.
///
/// Mirrors `static class EducationDomainContext`.
pub struct EducationDomainContext;

impl EducationDomainContext {
    /// The system-prompt snippet injected ahead of education-domain turns.
    pub const SYSTEM_PROMPT_SNIPPET: &'static str = "[DOMAIN: Education] Expert education assistant. Help with lesson plan design, curriculum alignment (CAPS/NCS), assessment rubric creation, differentiated instruction strategies, and learner progress tracking. Adapt communication to the relevant grade level and learning style. Compliance: SASA, DBE curriculum frameworks, POPIA for learner data.";

    /// Compliance flags applicable to this vertical.
    pub fn compliance_flags() -> Vec<String> {
        vec![
            "SASA".to_string(),
            "CAPS_NCS".to_string(),
            "POPIA".to_string(),
            "PAIA".to_string(),
        ]
    }

    /// Tools suggested for this vertical.
    pub fn suggested_tools() -> Vec<String> {
        vec![
            "learning_management".to_string(),
            "document_editor".to_string(),
            "assessment_tools".to_string(),
            "web_search".to_string(),
        ]
    }
}
