//! commerce_test.rs
//!
//! Ports the behaviour of `CircleAI.Commerce`: customers, orders (newest-first),
//! line items (insertion order), status update, lifetime value, the static
//! domain descriptor, and the domain adapter.

use std::cell::RefCell;

use chrono::{Duration, Utc};
use circle_ai::commerce::{
    CommerceCompanionAdapter, CommerceCustomer, CommerceDomainContext, CommerceLineItem,
    CommerceOrder, ICommerceBoard, InMemoryCommerceBoard,
};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};

#[test]
fn add_and_get_customer() {
    let board = InMemoryCommerceBoard::new();
    assert!(board.get_customer("c1").is_none());
    board.add_customer(CommerceCustomer::new("c1", "Ada", Some("ada@x.io".into()), Utc::now()));
    let c = board.get_customer("c1").unwrap();
    assert_eq!(c.name, "Ada");
    assert_eq!(c.email.as_deref(), Some("ada@x.io"));
}

#[test]
fn orders_newest_first_and_lifetime_value() {
    let board = InMemoryCommerceBoard::new();
    board.place(CommerceOrder::new("o-old", "c1", 100.0, "ZAR", "paid", Utc::now() - Duration::days(2)));
    board.place(CommerceOrder::new("o-new", "c1", 50.0, "ZAR", "paid", Utc::now()));
    board.place(CommerceOrder::new("o-other", "c2", 999.0, "ZAR", "paid", Utc::now()));

    let ids: Vec<String> = board.orders_for("c1").into_iter().map(|o| o.order_id).collect();
    assert_eq!(ids, vec!["o-new", "o-old"]);
    assert!((board.lifetime_value("c1") - 150.0).abs() < 1e-9);
    assert_eq!(board.lifetime_value("nobody"), 0.0);
}

#[test]
fn lines_preserve_insertion_order() {
    let board = InMemoryCommerceBoard::new();
    board.add_line(CommerceLineItem::new("l1", "o1", "SKU-A", 2, 10.0));
    board.add_line(CommerceLineItem::new("l2", "o1", "SKU-B", 1, 5.0));
    board.add_line(CommerceLineItem::new("l3", "o2", "SKU-C", 3, 1.0));

    let ids: Vec<String> = board.lines_for("o1").into_iter().map(|l| l.line_id).collect();
    assert_eq!(ids, vec!["l1", "l2"]);
}

#[test]
fn update_status_mutates_order() {
    let board = InMemoryCommerceBoard::new();
    board.place(CommerceOrder::new("o1", "c1", 10.0, "ZAR", "pending", Utc::now()));
    board.update_status("o1", "shipped");
    assert_eq!(board.orders_for("c1")[0].status, "shipped");
}

#[test]
#[should_panic(expected = "Unknown order")]
fn update_status_unknown_panics() {
    InMemoryCommerceBoard::new().update_status("nope", "x");
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(CommerceDomainContext::SYSTEM_PROMPT_SNIPPET.starts_with("[DOMAIN: Commerce]"));
    assert!(CommerceDomainContext::SYSTEM_PROMPT_SNIPPET.contains("margin-aware"));
    assert_eq!(
        CommerceDomainContext::compliance_flags(),
        vec!["POPIA", "Consumer_Protection_Act", "GDPR_aware"]
    );
    assert_eq!(
        CommerceDomainContext::suggested_tools(),
        vec!["inventory", "pricing_engine", "order_management", "analytics"]
    );
}

// ── Adapter fixture ──────────────────────────────────────────────────────────

#[derive(Debug)]
struct FakeError(String);
impl std::fmt::Display for FakeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}
impl std::error::Error for FakeError {}

struct RecordingSession {
    context: CompanionContext,
    history: Vec<CompanionTurn>,
    last_send: RefCell<Option<String>>,
    last_agent: RefCell<Option<String>>,
}

impl RecordingSession {
    fn new() -> Self {
        Self {
            context: CompanionContext::new(
                "id-1",
                "User",
                None,
                InterfaceKind::Mobile,
                "",
                "",
                Vec::new(),
                Vec::new(),
            ),
            history: vec![CompanionTurn::user("hi")],
            last_send: RefCell::new(None),
            last_agent: RefCell::new(None),
        }
    }
}

impl ICompanionSession for RecordingSession {
    type Error = FakeError;
    fn session_id(&self) -> &str {
        "sess-commerce"
    }
    fn identity_id(&self) -> &str {
        "id-1"
    }
    fn interface(&self) -> InterfaceKind {
        InterfaceKind::Mobile
    }
    fn send(&mut self, message: &str) -> Result<String, Self::Error> {
        *self.last_send.borrow_mut() = Some(message.to_string());
        Ok(format!("echo:{message}"))
    }
    fn stream(
        &mut self,
        message: &str,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        Ok(Box::new(std::iter::once(Ok(format!("chunk:{message}")))))
    }
    fn agent(&mut self, instruction: &str) -> Result<String, Self::Error> {
        *self.last_agent.borrow_mut() = Some(instruction.to_string());
        Ok(format!("agent:{instruction}"))
    }
    fn get_context(&self) -> &CompanionContext {
        &self.context
    }
    fn refresh_context(&mut self) -> Result<(), Self::Error> {
        Ok(())
    }
    fn history(&self) -> &[CompanionTurn] {
        &self.history
    }
    fn signal_feedback(&mut self, _p: bool, _n: Option<&str>) -> Result<(), Self::Error> {
        Ok(())
    }
}

#[test]
fn adapter_prefixes_domain_snippet_on_send() {
    let mut adapter = CommerceCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(seen, format!("{}\n\nhelp", CommerceDomainContext::SYSTEM_PROMPT_SNIPPET));
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = CommerceCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-commerce");
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = CommerceCompanionAdapter::new(RecordingSession::new());

    adapter.optimise_listing("Blue widget").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(CommerceDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("Optimise this product listing"));
    assert!(seen.contains("Blue widget"));

    // Money is rendered via the 2-decimal currency slot.
    adapter.analyse_pricing("Widget", 19.5).unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("at 19.50."));
}
