//! commerce_payfast_companion_adapter.rs
//!
//! Rust port of
//! `src/CircleAI.Commerce.Integration.PayFast/CommerceIntegrationPayFastCompanionAdapter.cs`
//! — an [`ICompanionSession`] decorator that prefixes the PayFast domain snippet
//! onto free-form turns and adds PayFast agent helpers. Domain helpers call the
//! inner agent with a raw instruction (no `Enrich()` prefix).

use crate::commerce::money;
use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::commerce_payfast_domain_context::CommerceIntegrationPayFastDomainContext;

/// (Commerce.Integration.PayFast) Domain decorator over an [`ICompanionSession`].
pub struct CommerceIntegrationPayFastCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> CommerceIntegrationPayFastCompanionAdapter<S> {
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
            CommerceIntegrationPayFastDomainContext::SYSTEM_PROMPT_SNIPPET
        )
    }

    // ── PayFast-domain agent helpers ────────────────────────────────────────

    /// Diagnoses a PayFast ITN payload.
    pub fn diagnose_itn(&mut self, itn_payload: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Diagnose this PayFast ITN payload. Validate signature, check payment_status, and identify any issues:\n{itn_payload}"
        ))
    }

    /// Guides a refund for a transaction.
    pub fn guide_refund(
        &mut self,
        transaction_id: &str,
        reason: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Guide me through processing a PayFast refund for transaction {transaction_id}. Reason: {reason}. Include API call, required fields, and customer communication."
        ))
    }

    /// Reviews PayFast integration code.
    pub fn review_integration(&mut self, code_snippet: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Review this PayFast integration code for security, PCI-DSS compliance, and correctness:\n{code_snippet}"
        ))
    }

    /// Decodes an ITN payload and explains its status.
    pub fn explain_itn_status(&mut self, itn_payload: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Decode this PayFast ITN payload and explain its status: {itn_payload}. Cover payment_status, m_payment_id, signature validity."
        ))
    }

    /// Drafts a PayFast Buy Button form.
    pub fn draft_pay_fast_buy_button(
        &mut self,
        item_name: &str,
        amount: f64,
        return_url: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft a PayFast Buy Button form for '{item_name}' at {}, return to {return_url}. Include all required fields + signature placeholder.",
            money::plain(amount)
        ))
    }

    /// Troubleshoots a signature mismatch.
    pub fn troubleshoot_signature_mismatch(
        &mut self,
        request_params: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Troubleshoot a PayFast signature mismatch. Request params: {request_params}. List the 5 most common causes + how to verify each."
        ))
    }

    /// Reconciles a PayFast payout against expected vs actual amounts.
    pub fn reconcile_payout(
        &mut self,
        payout_id: &str,
        expected_amount: f64,
        actual_amount: f64,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Reconcile PayFast payout {payout_id}: expected {}, actual {}. List likely fee / refund / hold reasons.",
            money::plain(expected_amount),
            money::plain(actual_amount)
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for CommerceIntegrationPayFastCompanionAdapter<S> {
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
