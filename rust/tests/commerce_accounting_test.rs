//! commerce_accounting_test.rs
//!
//! Ports the behaviour of `CircleAI.Commerce.Accounting`: journal posting with
//! non-negative guard, tax-rate registry, account balances, period sums +
//! entries (oldest-first), net profit, the static domain descriptor, and the
//! domain adapter.

use std::cell::RefCell;

use chrono::{TimeZone, Utc};
use circle_ai::commerce_accounting::{
    AccountingEntry, CommerceAccountingCompanionAdapter, CommerceAccountingDomainContext,
    IAccountingBoard, InMemoryAccountingBoard, Period, TaxRate,
};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};

fn at(y: i32, m: u32, day: u32) -> chrono::DateTime<Utc> {
    Utc.with_ymd_and_hms(y, m, day, 12, 0, 0).unwrap()
}

#[test]
fn tax_registry() {
    let board = InMemoryAccountingBoard::new();
    assert!(board.get_tax("VAT").is_none());
    board.define_tax(TaxRate::new("VAT", 15.0));
    assert_eq!(board.get_tax("VAT").unwrap().percentage, 15.0);
}

#[test]
#[should_panic(expected = "amounts must be non-negative")]
fn post_negative_amount_panics() {
    let board = InMemoryAccountingBoard::new();
    board.post(AccountingEntry::new("e1", at(2026, 1, 1), "4000", -1.0, 0.0, "bad"));
}

#[test]
fn account_balance_is_debits_minus_credits() {
    let board = InMemoryAccountingBoard::new();
    board.post(AccountingEntry::new("e1", at(2026, 1, 5), "1000", 100.0, 0.0, "in"));
    board.post(AccountingEntry::new("e2", at(2026, 1, 6), "1000", 0.0, 30.0, "out"));
    board.post(AccountingEntry::new("e3", at(2026, 1, 7), "2000", 5.0, 0.0, "other"));
    assert!((board.account_balance("1000") - 70.0).abs() < 1e-9);
}

#[test]
fn period_sum_and_entries_oldest_first() {
    let board = InMemoryAccountingBoard::new();
    board.post(AccountingEntry::new("jan-b", at(2026, 1, 20), "4000", 40.0, 0.0, "late-jan"));
    board.post(AccountingEntry::new("jan-a", at(2026, 1, 3), "4000", 10.0, 0.0, "early-jan"));
    board.post(AccountingEntry::new("feb", at(2026, 2, 1), "4000", 999.0, 0.0, "feb"));

    let jan = Period::new(2026, 1);
    assert!((board.sum("4000", jan) - 50.0).abs() < 1e-9);

    let ids: Vec<String> = board.for_account("4000", jan).into_iter().map(|e| e.entry_id).collect();
    assert_eq!(ids, vec!["jan-a", "jan-b"]);
}

#[test]
fn net_profit_is_revenue_minus_expense() {
    let board = InMemoryAccountingBoard::new();
    let p = Period::new(2026, 3);
    board.post(AccountingEntry::new("rev", at(2026, 3, 1), "4000", 500.0, 0.0, "sales"));
    board.post(AccountingEntry::new("exp", at(2026, 3, 2), "5000", 200.0, 0.0, "costs"));
    assert!((board.net_profit(p, "4000", "5000") - 300.0).abs() < 1e-9);
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(CommerceAccountingDomainContext::SYSTEM_PROMPT_SNIPPET
        .starts_with("[DOMAIN: Commerce.Accounting]"));
    assert!(CommerceAccountingDomainContext::SYSTEM_PROMPT_SNIPPET.contains("15% standard rate"));
    assert_eq!(
        CommerceAccountingDomainContext::compliance_flags(),
        vec!["IFRS", "SARS", "Companies_Act_71_2008", "VAT_Act"]
    );
    assert_eq!(
        CommerceAccountingDomainContext::suggested_tools(),
        vec!["accounting_software", "spreadsheet", "document_editor"]
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
        "sess-acc"
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
    let mut adapter = CommerceAccountingCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(
        seen,
        format!("{}\n\nhelp", CommerceAccountingDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = CommerceAccountingCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-acc");
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = CommerceAccountingCompanionAdapter::new(RecordingSession::new());

    adapter.reconcile("stmt", "ledger").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(CommerceAccountingDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("Reconcile these records"));

    adapter.prepare_vat_return("2026-Q1", 1000.0, 250.5).unwrap();
    let vat = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(vat.contains("VAT201 return summary for 2026-Q1"));
    assert!(vat.contains("1000.00"));
    assert!(vat.contains("250.50"));
}
