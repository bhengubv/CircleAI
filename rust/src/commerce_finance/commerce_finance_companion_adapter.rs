//! commerce_finance_companion_adapter.rs
//!
//! Rust port of
//! `src/CircleAI.Commerce.Finance/CommerceFinanceCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the finance domain snippet onto
//! free-form turns and adds commercial-finance agent helpers.
//!
//! The C# class exposes two `ForecastCashFlowAsync` overloads. Rust has no
//! method overloading, so they are named distinctly here:
//! [`forecast_cash_flow`](CommerceFinanceCompanionAdapter::forecast_cash_flow)
//! is the `(financials, weeksAhead)` overload and
//! [`forecast_cash_flow_horizon`](CommerceFinanceCompanionAdapter::forecast_cash_flow_horizon)
//! is the `(outstandingInvoices, upcomingExpenses, horizonDays)` overload —
//! both produce byte-identical instruction strings to their C# counterparts.

use crate::commerce::money;
use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::commerce_finance_domain_context::CommerceFinanceDomainContext;

/// (Commerce.Finance) Domain decorator over an [`ICompanionSession`].
pub struct CommerceFinanceCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> CommerceFinanceCompanionAdapter<S> {
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
            CommerceFinanceDomainContext::SYSTEM_PROMPT_SNIPPET
        )
    }

    // ── Finance-domain agent helpers ────────────────────────────────────────

    /// Forecasts cash flow for a number of weeks from a financials blob (the
    /// `(financials, weeksAhead)` C# overload).
    pub fn forecast_cash_flow(
        &mut self,
        financials: &str,
        weeks_ahead: i32,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Forecast cash flow for {weeks_ahead} weeks based on:\n{financials}\nIdentify liquidity risks and recommend mitigation actions."
        ))
    }

    /// Recommends a debt structure for a required amount.
    pub fn structure_debt(&mut self, context: &str, amount: f64) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Recommend a debt structure for a business needing {}. Context:\n{context}\nCompare term loans, revolving credit, and invoice financing.",
            money::currency(amount)
        ))
    }

    /// Reviews a credit application.
    pub fn review_credit_application(
        &mut self,
        application_data: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Review this credit application and identify strengths, weaknesses, and risk factors:\n{application_data}"
        ))
    }

    /// Generates an aging report from outstanding invoices.
    pub fn generate_aging_report(
        &mut self,
        outstanding_invoices: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Generate an aging report from: {outstanding_invoices}. Bucket 0-30/31-60/61-90/90+, name the worst offenders, suggest collection actions."
        ))
    }

    /// Prepares an invoice follow-up message.
    pub fn prepare_invoice_follow_up(
        &mut self,
        customer_name: &str,
        amount: f64,
        days_overdue: i32,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft a follow-up message to {customer_name} for {} due {days_overdue} days. Tone: firm but relationship-preserving.",
            money::plain(amount)
        ))
    }

    /// Evaluates credit-worthiness for a proposed limit.
    pub fn evaluate_credit(
        &mut self,
        customer_summary: &str,
        proposed_limit: f64,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Evaluate credit-worthiness of {customer_summary} for a {} limit. Recommend approve/decline + conditions.",
            money::plain(proposed_limit)
        ))
    }

    /// Forecasts cash flow over a day horizon from invoices + expenses (the
    /// `(outstandingInvoices, upcomingExpenses, horizonDays)` C# overload).
    pub fn forecast_cash_flow_horizon(
        &mut self,
        outstanding_invoices: &str,
        upcoming_expenses: &str,
        horizon_days: i32,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Forecast cash flow for next {horizon_days} days from invoices: {outstanding_invoices} and expenses: {upcoming_expenses}. Flag squeeze points."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for CommerceFinanceCompanionAdapter<S> {
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
