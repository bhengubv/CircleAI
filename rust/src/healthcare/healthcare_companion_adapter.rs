//! healthcare_companion_adapter.rs
//!
//! Rust port of `src/CircleAI.Healthcare/HealthcareCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the healthcare domain snippet
//! onto free-form turns (`Send`/`Stream`/`Agent`) and adds healthcare-specific
//! agent helpers.
//!
//! The C# class wraps `ICompanionSession _i` and prefixes with
//! `E(m) = "{SystemPromptSnippet}\n\n{m}"`. The domain helpers call the inner
//! `AgentAsync` with a fully-formed instruction (NOT wrapped in `E`) — this is
//! reproduced exactly. Generic over the wrapped session `S`.

use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::healthcare_domain_context::HealthcareDomainContext;

/// (Healthcare) Domain decorator over an [`ICompanionSession`].
pub struct HealthcareCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> HealthcareCompanionAdapter<S> {
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
        format!("{}\n\n{m}", HealthcareDomainContext::SYSTEM_PROMPT_SNIPPET)
    }

    // ── Healthcare-domain agent helpers (raw instruction, no E() prefix) ──

    /// Formats a patient visit summary into a structured SOAP clinical note.
    pub fn document_clinical_note(
        &mut self,
        patient_visit_summary: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Format this patient visit summary into a structured SOAP clinical note:\n{patient_visit_summary}"
        ))
    }

    /// Suggests ICD-10-CM codes for a diagnosis.
    pub fn suggest_icd10_codes(&mut self, diagnosis: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Suggest relevant ICD-10-CM codes for the following diagnosis/condition: {diagnosis}. Include primary and secondary codes with descriptions."
        ))
    }

    /// Drafts a patient communication for a purpose.
    pub fn draft_patient_communication(
        &mut self,
        purpose: &str,
        patient_context: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft a clear, empathetic patient communication for: {purpose}. Patient context: {patient_context}. Keep language accessible (Grade 8 reading level)."
        ))
    }

    /// Triages symptoms for a patient.
    pub fn triage_symptoms(
        &mut self,
        patient_age: &str,
        symptoms: &str,
        duration: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Triage symptoms for {patient_age}-year-old: {symptoms}, duration {duration}. Output urgency (emergency/urgent/routine), red flags, next step. Defer diagnosis to clinician."
        ))
    }

    /// Explains a medication to a patient.
    pub fn explain_medication(
        &mut self,
        medication: &str,
        indication: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Explain {medication} prescribed for {indication} to a patient. Cover purpose, dose schedule, common side effects, when to call."
        ))
    }

    /// Drafts a referral letter.
    pub fn draft_referral_letter(
        &mut self,
        from_provider: &str,
        to_specialty: &str,
        clinical_summary: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft a referral letter from {from_provider} to {to_specialty}. Clinical summary: {clinical_summary}. Include reason, history, exam, ask."
        ))
    }

    /// Counsels on medication adherence.
    pub fn counsel_on_adherence(
        &mut self,
        medication: &str,
        patient_concerns: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Counsel on adherence to {medication}. Patient concerns: {patient_concerns}. Address each with evidence + practical strategies."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for HealthcareCompanionAdapter<S> {
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
