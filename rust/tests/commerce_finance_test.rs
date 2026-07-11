//! commerce_finance_test.rs
//!
//! Ports the behaviour of `CircleAI.Commerce.Finance`: invoice issue/get,
//! payment recording, tax-inclusive remaining balance, total outstanding,
//! mark-overdue (skips Paid), overdue listing, the static domain descriptor, and
//! the domain adapter.

use std::cell::RefCell;

use chrono::{NaiveDate, Utc};
use circle_ai::commerce_finance::{
    CommerceFinanceCompanionAdapter, CommerceFinanceDomainContext, FinancePayment, IInvoiceBoard,
    InMemoryInvoiceBoard, Invoice, InvoiceLine,
};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};

fn d(y: i32, m: u32, day: u32) -> NaiveDate {
    NaiveDate::from_ymd_opt(y, m, day).unwrap()
}

fn invoice(id: &str, due: NaiveDate, status: &str, lines: Vec<InvoiceLine>) -> Invoice {
    Invoice::new(id, "cust", d(2026, 1, 1), due, lines, "ZAR", status)
}

#[test]
fn remaining_applies_tax_then_subtracts_payments() {
    let board = InMemoryInvoiceBoard::new();
    // 100 @ 15% + 50 @ 0% = 115 + 50 = 165 billed.
    board.issue(invoice(
        "i1",
        d(2026, 2, 1),
        "Sent",
        vec![InvoiceLine::new("A", 100.0, 15.0), InvoiceLine::new("B", 50.0, 0.0)],
    ));
    assert!((board.remaining_on("i1") - 165.0).abs() < 1e-9);

    board.record_payment(FinancePayment::new("p1", "i1", 65.0, Utc::now()));
    assert!((board.remaining_on("i1") - 100.0).abs() < 1e-9);

    // Unknown invoice remaining is 0.
    assert_eq!(board.remaining_on("nope"), 0.0);
}

#[test]
fn total_outstanding_sums_all_invoices() {
    let board = InMemoryInvoiceBoard::new();
    board.issue(invoice("i1", d(2026, 2, 1), "Sent", vec![InvoiceLine::new("A", 100.0, 0.0)]));
    board.issue(invoice("i2", d(2026, 2, 1), "Sent", vec![InvoiceLine::new("B", 200.0, 0.0)]));
    board.record_payment(FinancePayment::new("p1", "i1", 40.0, Utc::now()));
    assert!((board.total_outstanding() - 260.0).abs() < 1e-9);
}

#[test]
fn mark_overdue_flips_unpaid_past_due_only() {
    let board = InMemoryInvoiceBoard::new();
    board.issue(invoice("past-unpaid", d(2026, 1, 1), "Sent", vec![]));
    board.issue(invoice("past-paid", d(2026, 1, 1), "Paid", vec![]));
    board.issue(invoice("future", d(2026, 12, 1), "Sent", vec![]));

    board.mark_overdue(d(2026, 6, 1));

    assert_eq!(board.get("past-unpaid").unwrap().status, "Overdue");
    assert_eq!(board.get("past-paid").unwrap().status, "Paid");
    assert_eq!(board.get("future").unwrap().status, "Sent");

    let overdue_ids: Vec<String> = board.overdue().into_iter().map(|i| i.invoice_id).collect();
    assert_eq!(overdue_ids, vec!["past-unpaid"]);
}

#[test]
fn mark_overdue_paid_check_is_case_insensitive() {
    let board = InMemoryInvoiceBoard::new();
    board.issue(invoice("i1", d(2026, 1, 1), "paid", vec![]));
    board.mark_overdue(d(2026, 6, 1));
    assert_eq!(board.get("i1").unwrap().status, "paid");
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(CommerceFinanceDomainContext::SYSTEM_PROMPT_SNIPPET
        .starts_with("[DOMAIN: Commerce.Finance]"));
    assert!(CommerceFinanceDomainContext::SYSTEM_PROMPT_SNIPPET.contains("cash conversion cycle"));
    assert_eq!(
        CommerceFinanceDomainContext::compliance_flags(),
        vec!["NCA_34_2005", "SARB_aware", "POPIA", "IFRS"]
    );
    assert_eq!(
        CommerceFinanceDomainContext::suggested_tools(),
        vec!["cash_flow_model", "spreadsheet", "web_search"]
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
        "sess-fin"
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
    let mut adapter = CommerceFinanceCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(
        seen,
        format!("{}\n\nhelp", CommerceFinanceDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = CommerceFinanceCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-fin");
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = CommerceFinanceCompanionAdapter::new(RecordingSession::new());

    adapter.forecast_cash_flow("balances", 12).unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(CommerceFinanceDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("Forecast cash flow for 12 weeks"));

    adapter.structure_debt("growth", 500000.0).unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("500000.00"));
}
