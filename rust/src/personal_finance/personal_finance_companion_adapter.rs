//! personal_finance_companion_adapter.rs
//!
//! Rust port of
//! `src/CircleAI.Personal.Finance/PersonalFinanceCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the personal-finance domain
//! snippet onto free-form turns and adds coaching agent helpers. Domain helpers
//! call the inner agent with a raw instruction (no `E()` prefix).

use crate::commerce::money;
use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::personal_finance_domain_context::PersonalFinanceDomainContext;

/// (Personal.Finance) Domain decorator over an [`ICompanionSession`].
pub struct PersonalFinanceCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> PersonalFinanceCompanionAdapter<S> {
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
            PersonalFinanceDomainContext::SYSTEM_PROMPT_SNIPPET
        )
    }

    // ── Personal-finance-domain agent helpers ───────────────────────────────

    /// Builds a monthly budget from income + expenses.
    pub fn build_budget(&mut self, income: &str, expenses: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Build a monthly budget. Income: {income}. Expenses: {expenses}. Apply the 50/30/20 rule, identify savings opportunities, and flag over-spending categories."
        ))
    }

    /// Creates a debt elimination plan (avalanche).
    pub fn create_debt_plan(&mut self, debts: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Create a debt elimination plan using the avalanche method (highest interest first):\n{debts}\nShow monthly payment schedule, total interest saved, and debt-free date."
        ))
    }

    /// Analyses spending against income.
    pub fn analyse_spending(
        &mut self,
        category_breakdown: &str,
        monthly_income: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Analyse spending {category_breakdown} against income {monthly_income}. Identify 2 leaks + a realistic redirect target."
        ))
    }

    /// Designs a savings-goal plan.
    pub fn design_savings_goal(
        &mut self,
        goal: &str,
        target_amount: f64,
        months_available: i32,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Plan to save {} for '{goal}' in {months_available} months. Monthly target + behavioural commitment device.",
            money::plain(target_amount)
        ))
    }

    /// Explains tax impact of a scenario.
    pub fn explain_tax_impact(
        &mut self,
        scenario: &str,
        jurisdiction: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Explain tax impact of: {scenario} in {jurisdiction}. Likely treatment, paperwork, optimisation lever. Not tax advice."
        ))
    }

    /// Reviews an investment mix.
    pub fn review_investment_mix(
        &mut self,
        portfolio: &str,
        risk_appetite: &str,
        horizon_years: i32,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Review investment mix: {portfolio} against {risk_appetite} appetite, {horizon_years}-year horizon. Coverage, concentration, fee drag."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for PersonalFinanceCompanionAdapter<S> {
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
