//! safety_companion_adapter.rs
//!
//! Rust port of `src/CircleAI.Safety/SafetyCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the personal-safety domain
//! snippet onto free-form turns and adds safety-specific agent helpers.
//!
//! The C# class wraps `ICompanionSession _i` and prefixes `Send`/`Stream`/`Agent`
//! with `E(m) = "{SystemPromptSnippet}\n\n{m}"`. The five domain helpers call the
//! inner `AgentAsync` with a fully-formed instruction (NOT wrapped in `E`) — this
//! is reproduced exactly. Generic over the wrapped session `S`.

use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::safety_domain_context::SafetyDomainContext;

/// (Safety) Domain decorator over an [`ICompanionSession`].
pub struct SafetyCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> SafetyCompanionAdapter<S> {
    /// Wraps `inner`. (The C# constructor throws on null; a Rust value is always
    /// present, so there is nothing to guard.)
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

    /// The domain-prefix helper `E(m)` — prepends the safety system-prompt
    /// snippet to a free-form message.
    fn enrich(m: &str) -> String {
        format!("{}\n\n{m}", SafetyDomainContext::SYSTEM_PROMPT_SNIPPET)
    }

    // ── Safety-domain agent helpers (call inner agent with a raw instruction) ──

    /// Creates a personalised emergency preparedness plan.
    pub fn create_emergency_plan(
        &mut self,
        household_size: &str,
        location: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Create a personalised emergency preparedness plan for a {household_size}-person \
             household in {location}. Include evacuation routes, emergency contacts, go-bag \
             checklist, and 72-hour supply list."
        ))
    }

    /// Assesses home security for a property.
    pub fn assess_security(
        &mut self,
        property_type: &str,
        concerns: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Assess home security for a {property_type}. Concerns: {concerns}. Identify \
             vulnerabilities and recommend physical, electronic, and procedural improvements."
        ))
    }

    /// Conducts a risk assessment for an activity in an environment.
    pub fn conduct_risk_assessment(
        &mut self,
        activity: &str,
        environment: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Conduct a risk assessment for {activity} in {environment}. Hazard, likelihood, \
             severity, controls."
        ))
    }

    /// Drafts emergency response steps for an incident type at a site.
    pub fn draft_emergency_response(
        &mut self,
        incident_type: &str,
        site_context: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft emergency response steps for {incident_type} at {site_context}. Roles, \
             escalation, comms, debrief."
        ))
    }

    /// Briefs a 5-minute toolbox talk for a task.
    pub fn brief_safety_toolbox(
        &mut self,
        task: &str,
        top_hazards: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Brief a 5-min toolbox talk for task: {task}. Top hazards: {top_hazards}. Controls, \
             PPE, sign-off."
        ))
    }

    /// Reviews an incident narrative for root cause and corrective actions.
    pub fn review_incident_report(
        &mut self,
        incident_narrative: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Review this incident narrative: {incident_narrative}. Identify root cause, \
             contributing factors, corrective + preventive actions."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for SafetyCompanionAdapter<S> {
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
