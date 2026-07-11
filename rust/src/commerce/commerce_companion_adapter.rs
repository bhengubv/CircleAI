//! commerce_companion_adapter.rs
//!
//! Rust port of `src/CircleAI.Commerce/CommerceCompanionAdapter.cs` — an
//! [`ICompanionSession`] decorator that prefixes the commerce domain snippet
//! onto free-form turns and adds commerce agent helpers. Domain helpers call the
//! inner agent with a raw instruction (no `Enrich()` prefix).
//!
//! Money-typed helper arguments render via [`super::money`]: `{price:C}` slots
//! use [`super::money::currency`], plain `{total}` slots use
//! [`super::money::plain`].

use crate::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::commerce_domain_context::CommerceDomainContext;
use super::money;

/// (Commerce) Domain decorator over an [`ICompanionSession`].
pub struct CommerceCompanionAdapter<S: ICompanionSession> {
    inner: S,
}

impl<S: ICompanionSession> CommerceCompanionAdapter<S> {
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
        format!("{}\n\n{m}", CommerceDomainContext::SYSTEM_PROMPT_SNIPPET)
    }

    // ── Commerce-domain agent helpers ───────────────────────────────────────

    /// Optimises a product listing.
    pub fn optimise_listing(&mut self, product_details: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Optimise this product listing for search discovery and conversions:\n{product_details}"
        ))
    }

    /// Analyses pricing for a product at its current price.
    pub fn analyse_pricing(
        &mut self,
        product: &str,
        current_price: f64,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Analyse pricing for: {product} at {}. Recommend optimal pricing considering margins, competition, and demand.",
            money::currency(current_price)
        ))
    }

    /// Writes a supplier brief.
    pub fn generate_supplier_brief(
        &mut self,
        product_requirements: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Write a supplier brief for: {product_requirements}. Include quantity, specs, quality standards, delivery terms, and pricing expectations."
        ))
    }

    /// Writes a product description aimed at a target customer.
    pub fn write_product_description(
        &mut self,
        product_name: &str,
        features: &str,
        target_customer: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Write a product description for {product_name} aimed at {target_customer}. Features: {features}. Use the 'feature → benefit' pattern, end with a CTA."
        ))
    }

    /// Analyses a conversion funnel.
    pub fn analyse_conversion_funnel(&mut self, funnel_metrics: &str) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Analyse this funnel: {funnel_metrics}. Identify the biggest drop-off, the likely cause, and the test to validate."
        ))
    }

    /// Suggests upsells for a cart.
    pub fn suggest_upsell(
        &mut self,
        cart_contents: &str,
        cart_total: f64,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Suggest 1-2 upsells for this cart: {cart_contents} (total {}). Justify each with attach rate intuition + margin notes.",
            money::plain(cart_total)
        ))
    }

    /// Drafts a return policy for a category and region.
    pub fn draft_return_policy(
        &mut self,
        category: &str,
        region: &str,
    ) -> Result<String, S::Error> {
        self.inner.agent(&format!(
            "Draft a return policy for {category} sold in {region}. Comply with local consumer law, balance customer trust with fraud prevention."
        ))
    }
}

impl<S: ICompanionSession> ICompanionSession for CommerceCompanionAdapter<S> {
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
