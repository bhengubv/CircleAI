//! personal_mental_companion_adapter.rs
//!
//! Rust port of
//! `src/CircleAI.Personal.Mental/PersonalMentalCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the mental-wellness domain
//! snippet onto free-form turns and adds wellness agent helpers. Domain helpers
//! call the inner agent with a raw instruction (no `E()` prefix).

use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::personal_mental_domain_context::PersonalMentalDomainContext;

/// (Personal.Mental) Domain decorator over an [`ICompanionSession`].
pub struct PersonalMentalCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> PersonalMentalCompanionAdapter<S> {
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
        format!("{}\n\n{m}", PersonalMentalDomainContext::SYSTEM_PROMPT_SNIPPET)
    }

    // ── Mental-wellness-domain agent helpers ────────────────────────────────

    /// Responds to a mood check-in.
    pub fn check_in(&mut self, mood: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "I am feeling: {mood}. Respond with empathy, validate my feeling, then gently offer one evidence-based coping tool relevant to my current state."
        ))
    }

    /// Guides a mindfulness/breathing exercise of a given duration.
    pub fn guide_mindfulness(&mut self, duration: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Guide me through a {duration} mindfulness or breathing exercise. Use a calm, grounding tone."
        ))
    }

    /// Helps reframe a distorted thought (CBT lens).
    pub fn reframe_thought(
        &mut self,
        distorted_thought: &str,
        context: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Help reframe this thought: {distorted_thought}. Context: {context}. Name the distortion (CBT lens), offer a balanced alternative."
        ))
    }

    /// Designs a daily mental check-in ritual.
    pub fn design_check_in_ritual(
        &mut self,
        life_stage: &str,
        available_minutes: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Design a {available_minutes}-minute daily mental check-in for someone in {life_stage}. Make it sustainable for low-energy days."
        ))
    }

    /// Prepares for a therapy session.
    pub fn prepare_therapy_session(
        &mut self,
        session_themes: &str,
        last_week_events: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Prepare for a therapy session on themes: {session_themes}. Recent events: {last_week_events}. List 3 top topics + one experiment to try."
        ))
    }

    /// Guides a grounding script during a panic episode.
    pub fn ground_during_panic(
        &mut self,
        trigger: &str,
        environment: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Guide a grounding script for panic triggered by: {trigger} in environment: {environment}. 5-4-3-2-1 sensory anchor + breath."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for PersonalMentalCompanionAdapter<S> {
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
