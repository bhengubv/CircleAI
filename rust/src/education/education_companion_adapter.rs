//! education_companion_adapter.rs
//!
//! Rust port of `src/CircleAI.Education/EducationCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the education domain snippet
//! onto free-form turns and adds education agent helpers. Domain helpers call
//! the inner agent with a raw instruction (no `E()` prefix).

use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::education_domain_context::EducationDomainContext;

/// (Education) Domain decorator over an [`ICompanionSession`].
pub struct EducationCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> EducationCompanionAdapter<S> {
    /// Wraps `inner`.
    pub fn new(inner: S) -> Self {
        Self { inner }
    }

    /// Borrows the wrapped session.
    pub fn inner(&self) -> &S {
        &self.inner
    }

    /// Mutably borrows the wrapped session.
    pub fn inner_mut(&mut self) -> &mut S {
        &mut self.inner
    }

    /// Consumes the adapter, returning the wrapped session.
    pub fn into_inner(self) -> S {
        self.inner
    }

    fn enrich(m: &str) -> String {
        format!("{}\n\n{m}", EducationDomainContext::SYSTEM_PROMPT_SNIPPET)
    }

    // ── Education-domain agent helpers ──────────────────────────────────────

    /// Creates a CAPS-aligned lesson plan.
    pub fn create_lesson_plan(
        &mut self,
        subject: &str,
        grade: &str,
        topic: &str,
        duration: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Create a CAPS-aligned lesson plan for Grade {grade} {subject}: {topic}. Duration: {duration}. Include LTSM, activities, differentiation strategies, and assessment criteria."
        ))
    }

    /// Generates an assessment rubric.
    pub fn generate_rubric(
        &mut self,
        assessment_task: &str,
        grade: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Generate an assessment rubric for Grade {grade}: {assessment_task}. Include criteria, descriptors for 4 performance levels, and weighting."
        ))
    }

    /// Designs a timed lesson plan for a grade band.
    pub fn design_lesson_plan(
        &mut self,
        topic: &str,
        grade_band: &str,
        minutes: i32,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Design a {minutes}-minute lesson plan on '{topic}' for {grade_band}. Include objectives, hook, instruction, practice, exit ticket."
        ))
    }

    /// Generates assessment items at a Bloom's level.
    pub fn generate_assessment(
        &mut self,
        topic: &str,
        blooms_level: &str,
        item_count: i32,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Generate {item_count} assessment items on '{topic}' at Bloom's {blooms_level} level. Mix MCQ + short-answer + one performance task."
        ))
    }

    /// Diagnoses the misconception behind a student response.
    pub fn diagnose_misconception(
        &mut self,
        topic: &str,
        student_response: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Diagnose the misconception in this student response on '{topic}': {student_response}. Identify the rule the student is following + a corrective move."
        ))
    }

    /// Drafts a parent update.
    pub fn draft_parent_update(
        &mut self,
        student_name: &str,
        period: &str,
        progress_notes: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft a parent update for {student_name} covering {period}. Notes: {progress_notes}. Warm, specific, actionable."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for EducationCompanionAdapter<S> {
    type Error = S::Error;

    fn session_id(&self) -> &str {
        self.inner.session_id()
    }

    fn identity_id(&self) -> &str {
        self.inner.identity_id()
    }

    fn interface(&self) -> InterfaceKind {
        self.inner.interface()
    }

    fn send(&mut self, message: &str) -> Result<String, Self::Error> {
        let enriched = Self::enrich(message);
        self.inner.send(&enriched)
    }

    fn stream(
        &mut self,
        message: &str,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        let enriched = Self::enrich(message);
        self.inner.stream(&enriched)
    }

    fn agent(&mut self, instruction: &str) -> Result<String, Self::Error> {
        let enriched = Self::enrich(instruction);
        self.inner.agent(&enriched)
    }

    fn get_context(&self) -> &CompanionContext {
        self.inner.get_context()
    }

    fn refresh_context(&mut self) -> Result<(), Self::Error> {
        self.inner.refresh_context()
    }

    fn history(&self) -> &[CompanionTurn] {
        self.inner.history()
    }

    fn signal_feedback(&mut self, positive: bool, note: Option<&str>) -> Result<(), Self::Error> {
        self.inner.signal_feedback(positive, note)
    }
}
