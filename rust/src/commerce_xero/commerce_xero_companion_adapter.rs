//! commerce_xero_companion_adapter.rs
//!
//! Rust port of
//! `src/CircleAI.Commerce.Integration.Xero/CommerceIntegrationXeroCompanionAdapter.cs`
//! — an [`ICompanionSession`] decorator that prefixes the Xero domain snippet
//! onto free-form turns and adds Xero agent helpers. Domain helpers call the
//! inner agent with a raw instruction (no `Enrich()` prefix).

use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::commerce_xero_domain_context::CommerceIntegrationXeroDomainContext;

/// (Commerce.Integration.Xero) Domain decorator over an [`ICompanionSession`].
pub struct CommerceIntegrationXeroCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> CommerceIntegrationXeroCompanionAdapter<S> {
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
        format!(
            "{}\n\n{m}",
            CommerceIntegrationXeroDomainContext::SYSTEM_PROMPT_SNIPPET
        )
    }

    // ── Xero-domain agent helpers ───────────────────────────────────────────

    /// Explains a Xero transaction code and maps it to an account code.
    pub fn explain_xero_code(&mut self, transaction_code: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Explain Xero transaction code '{transaction_code}' and suggest the correct account code mapping under South African chart of accounts."
        ))
    }

    /// Troubleshoots a Xero bank feed error.
    pub fn troubleshoot_bank_feed(&mut self, feed_error: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Troubleshoot this Xero bank feed error and provide resolution steps:\n{feed_error}"
        ))
    }

    /// Generates a Xero reporting guide for a business type.
    pub fn generate_xero_reporting_guide(
        &mut self,
        business_type: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Generate a Xero reporting guide for a {business_type}. Include recommended reports, frequency, and key metrics to track."
        ))
    }

    /// Maps a transaction to a Xero entry.
    pub fn map_transaction_to_xero(
        &mut self,
        transaction_description: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Map this transaction to a Xero entry: {transaction_description}. Pick contact, account code, tax rate; output the API payload outline."
        ))
    }

    /// Resolves a Xero API error.
    pub fn resolve_xero_error(&mut self, xero_error_json: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Resolve this Xero API error: {xero_error_json}. Explain the root cause + the exact fix (header, scope, validation, etc.)."
        ))
    }

    /// Generates a Xero report request prompt.
    pub fn generate_xero_report_prompt(
        &mut self,
        report_type: &str,
        period: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Generate the Xero report request for a {report_type} for {period}. Include endpoint, query params, response fields to surface."
        ))
    }

    /// Maps a VAT context to a Xero tax-rate code.
    pub fn map_vat_to_xero_tax_rate(
        &mut self,
        country_iso: &str,
        supply_type: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Map this VAT context to the correct Xero tax-rate code: country {country_iso}, supply {supply_type}. Show the code + a one-line justification."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for CommerceIntegrationXeroCompanionAdapter<S> {
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
