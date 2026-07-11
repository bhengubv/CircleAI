//! commerce_accounting_companion_adapter.rs
//!
//! Rust port of
//! `src/CircleAI.Commerce.Accounting/CommerceAccountingCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the accounting domain snippet
//! onto free-form turns and adds accounting agent helpers. Domain helpers call
//! the inner agent with a raw instruction (no `Enrich()` prefix).
//!
//! Currency-typed helper arguments (`{value:C}`) render via
//! [`crate::commerce::money::currency`]; plain money slots via
//! [`crate::commerce::money::plain`].

use crate::commerce::money;
use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::commerce_accounting_domain_context::CommerceAccountingDomainContext;

/// (Commerce.Accounting) Domain decorator over an [`ICompanionSession`].
pub struct CommerceAccountingCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> CommerceAccountingCompanionAdapter<S> {
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
            CommerceAccountingDomainContext::SYSTEM_PROMPT_SNIPPET
        )
    }

    // ── Accounting-domain agent helpers ─────────────────────────────────────

    /// Reconciles a bank statement against a ledger.
    pub fn reconcile(&mut self, bank_statement: &str, ledger: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Reconcile these records and identify discrepancies.\n\nBank statement:\n{bank_statement}\n\nLedger:\n{ledger}"
        ))
    }

    /// Prepares a VAT201 return summary.
    pub fn prepare_vat_return(
        &mut self,
        period: &str,
        sales_total: f64,
        purchases_total: f64,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Prepare a VAT201 return summary for {period}. Output VAT on sales {}, Input VAT on purchases {}. Show net payable/refundable and filing checklist.",
            money::currency(sales_total),
            money::currency(purchases_total)
        ))
    }

    /// Drafts management accounts for a period.
    pub fn draft_management_accounts(
        &mut self,
        financial_data: &str,
        period: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft management accounts for {period} from this data:\n{financial_data}\nInclude P&L, balance sheet summary, cash flow, and key ratio analysis."
        ))
    }

    /// Translates a transaction into double-entry journal lines.
    pub fn explain_journal_entry(
        &mut self,
        entry_description: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Translate this transaction into double-entry journal lines: {entry_description}. Show debits/credits, account codes, narrative."
        ))
    }

    /// Reconciles a variance between book and statement balances.
    pub fn reconcile_variance(
        &mut self,
        account_code: &str,
        book_balance: f64,
        statement_balance: f64,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Reconcile {account_code}: book {} vs statement {}. List likely variance causes + the journal to fix each.",
            money::plain(book_balance),
            money::plain(statement_balance)
        ))
    }

    /// Comments on a trial balance for a period.
    pub fn generate_trial_balance_commentary(
        &mut self,
        period: &str,
        top_movements: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Comment on the trial balance for {period}. Top movements: {top_movements}. Explain abnormal swings."
        ))
    }

    /// Drafts a VAT return narrative.
    pub fn draft_vat_return_narrative(
        &mut self,
        period: &str,
        output_vat: f64,
        input_vat: f64,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft VAT return narrative for {period}: output {}, input {}. Cover net payable, anomalies, supporting documents.",
            money::plain(output_vat),
            money::plain(input_vat)
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for CommerceAccountingCompanionAdapter<S> {
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
