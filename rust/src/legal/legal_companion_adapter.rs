//! legal_companion_adapter.rs
//!
//! Rust port of `src/CircleAI.Legal/LegalCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the legal domain snippet onto
//! free-form turns and adds legal agent helpers. Domain helpers call the inner
//! agent with a raw instruction (no `E()` prefix).

use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::legal_domain_context::LegalDomainContext;

/// (Legal) Domain decorator over an [`ICompanionSession`].
pub struct LegalCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> LegalCompanionAdapter<S> {
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
        format!("{}\n\n{m}", LegalDomainContext::SYSTEM_PROMPT_SNIPPET)
    }

    // ── Legal-domain agent helpers ──────────────────────────────────────────

    /// Reviews contract clauses for a focus area.
    pub fn review_contract_clauses(
        &mut self,
        contract_text: &str,
        focus_area: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Review the following contract for {focus_area} issues. Identify risky clauses, missing protections, and suggest improvements:\n{contract_text}"
        ))
    }

    /// Drafts a plain-language contract summary.
    pub fn draft_contract_summary(&mut self, contract_text: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Summarise this contract in plain language. Highlight key obligations, payment terms, IP ownership, termination, and dispute resolution:\n{contract_text}"
        ))
    }

    /// Generates a compliance checklist for a business in a jurisdiction.
    pub fn generate_compliance_checklist(
        &mut self,
        business_type: &str,
        jurisdiction: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Generate a compliance checklist for a {business_type} operating in {jurisdiction}. Cover company registration, tax, labour, data protection, and sector-specific regulations."
        ))
    }

    /// Summarises a contract from a client role's perspective.
    pub fn summarise_contract(
        &mut self,
        contract_text: &str,
        client_role: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Summarise this contract from the {client_role}'s perspective: {contract_text}. Highlight obligations, rights, risks, deadlines."
        ))
    }

    /// Drafts a clause favouring a position in a jurisdiction.
    pub fn draft_clause(
        &mut self,
        clause_type: &str,
        position: &str,
        jurisdiction: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft a {clause_type} clause favouring the {position} in {jurisdiction}. Plain-English notes alongside."
        ))
    }

    /// Assesses the merits of a matter.
    pub fn assess_matter_strength(&mut self, matter_summary: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Assess this matter's merits: {matter_summary}. Cover liability theory, likely defences, evidence gaps, settlement range. Not legal advice."
        ))
    }

    /// Identifies deadlines triggered by a key date for a matter type.
    pub fn track_deadline(
        &mut self,
        matter_type: &str,
        key_date: &str,
        jurisdiction: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Identify all deadlines triggered by {key_date} for a {matter_type} matter in {jurisdiction}. List date, action, statute reference."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for LegalCompanionAdapter<S> {
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
