//! safety_child_companion_adapter.rs
//!
//! Rust port of `src/CircleAI.Safety.Child/SafetyChildCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the child-safety domain snippet
//! onto free-form turns and adds safeguarding-specific agent helpers.
//!
//! As with the safety adapter, `Send`/`Stream`/`Agent` are prefixed with
//! `E(m) = "{SystemPromptSnippet}\n\n{m}"`, while the domain helpers call the
//! inner `AgentAsync` with a fully-formed instruction (NOT wrapped in `E`).
//! Generic over the wrapped session `S`.

use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::safety_child_domain_context::SafetyChildDomainContext;

/// (Safety.Child) Domain decorator over an [`ICompanionSession`].
pub struct SafetyChildCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> SafetyChildCompanionAdapter<S> {
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

    /// The domain-prefix helper `E(m)`.
    fn enrich(m: &str) -> String {
        format!("{}\n\n{m}", SafetyChildDomainContext::SYSTEM_PROMPT_SNIPPET)
    }

    // ── Child-safety agent helpers (call inner agent with a raw instruction) ──

    /// Creates age-appropriate digital safety rules.
    pub fn set_digital_rules(&mut self, child_age: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Create age-appropriate digital safety rules for a {child_age}-year-old. Include \
             screen time limits, app/platform permissions, online communication rules, and how \
             to report concerning content."
        ))
    }

    /// Explains online-safety concepts appropriate for a child's age.
    pub fn educate_online_risks(&mut self, child_age: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Explain online safety concepts appropriate for a {child_age}-year-old. Cover: \
             stranger danger online, personal information sharing, cyberbullying, and who to tell \
             if something feels wrong. Use simple, non-scary language."
        ))
    }

    /// Designs an age-appropriate safety conversation on a topic.
    pub fn design_safety_conversation(
        &mut self,
        child_age: &str,
        topic: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Design an age-appropriate safety conversation for {child_age} on: {topic}. Concrete \
             examples, scripts they can use, role-play prompt."
        ))
    }

    /// Assesses online risk on a platform for a child showing a behaviour.
    pub fn assess_online_risk(
        &mut self,
        platform: &str,
        child_age: &str,
        behaviour: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Assess online risk on {platform} for {child_age}-year-old showing {behaviour}. \
             Specific risks + parent-action checklist."
        ))
    }

    /// Helps vet a trusted-adult ring from a contact list.
    pub fn verify_trusted_adults(&mut self, contact_list: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Help vet trusted-adult ring from: {contact_list}. Criteria to apply, questions to \
             ask the child."
        ))
    }

    /// Drafts a school notification about a concern.
    pub fn draft_school_notification(
        &mut self,
        concern: &str,
        evidence: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft a school notification about: {concern}. Evidence: {evidence}. Calm, factual, \
             requesting specific action."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for SafetyChildCompanionAdapter<S> {
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
