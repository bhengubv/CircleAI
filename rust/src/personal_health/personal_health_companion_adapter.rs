//! personal_health_companion_adapter.rs
//!
//! Rust port of
//! `src/CircleAI.Personal.Health/PersonalHealthCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the personal-health domain
//! snippet onto free-form turns and adds health agent helpers. Domain helpers
//! call the inner agent with a raw instruction (no `E()` prefix).

use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::personal_health_domain_context::PersonalHealthDomainContext;

/// (Personal.Health) Domain decorator over an [`ICompanionSession`].
pub struct PersonalHealthCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> PersonalHealthCompanionAdapter<S> {
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
        format!("{}\n\n{m}", PersonalHealthDomainContext::SYSTEM_PROMPT_SNIPPET)
    }

    // ── Personal-health-domain agent helpers ────────────────────────────────

    /// Helps prepare for a doctor appointment.
    pub fn prepare_appointment(
        &mut self,
        symptoms: &str,
        med_history: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Help me prepare for a doctor appointment. Symptoms: {symptoms}. Relevant history: {med_history}. Draft a concise symptom summary and list of questions to ask the doctor."
        ))
    }

    /// Explains a medical term in plain language.
    pub fn explain_health_term(&mut self, term: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Explain the medical term or concept in plain language: {term}. Make it accessible to a non-medical person."
        ))
    }

    /// Interprets vitals for an age with baseline context.
    pub fn interpret_vitals(
        &mut self,
        vitals_json: &str,
        age: &str,
        baseline_notes: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Interpret vitals {vitals_json} for age {age}. Baseline: {baseline_notes}. Flag normal/borderline/concerning. Defer diagnosis to clinician."
        ))
    }

    /// Designs a sleep improvement plan.
    pub fn design_sleep_plan(
        &mut self,
        current_pattern: &str,
        target_wake_time: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Design a sleep improvement plan from {current_pattern} towards waking at {target_wake_time}. Cover light, caffeine, wind-down, environment."
        ))
    }

    /// Prepares for a specific appointment type about a concern.
    pub fn prepare_for_appointment(
        &mut self,
        concern: &str,
        appointment_type: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Prepare for a {appointment_type} about: {concern}. Pre-visit checklist: symptoms log, questions, medication list, decisions to make."
        ))
    }

    /// Analyses a habit's impact on vitals.
    pub fn track_habit_impact(
        &mut self,
        habit: &str,
        vitals_before_after: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Analyse impact of {habit} on vitals: {vitals_before_after}. Confounders, signal strength, what to keep measuring."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for PersonalHealthCompanionAdapter<S> {
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
