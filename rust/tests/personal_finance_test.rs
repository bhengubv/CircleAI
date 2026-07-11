//! personal_finance_test.rs
//!
//! Ports the behaviour of `CircleAI.Personal.Finance`: accounts + balance
//! application on record, unknown-account guard, per-month listing,
//! case-insensitive budgets ordered by category, the month summary (in/out +
//! by-category), the static domain descriptor, and the domain adapter.

use std::cell::RefCell;

use chrono::{TimeZone, Utc};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};
use circle_ai::personal_finance::{
    Account, BudgetLine, FinanceTransaction, IPersonalFinanceBoard, InMemoryPersonalFinanceBoard,
    PersonalFinanceCompanionAdapter, PersonalFinanceDomainContext,
};

fn at(y: i32, m: u32, day: u32) -> chrono::DateTime<Utc> {
    Utc.with_ymd_and_hms(y, m, day, 9, 0, 0).unwrap()
}

#[test]
fn record_applies_balance() {
    let board = InMemoryPersonalFinanceBoard::new();
    board.upsert(Account::new("a1", "Cheque", 100.0, "ZAR"));
    board.record(FinanceTransaction::new("t1", "a1", -30.0, "Food", None, at(2026, 5, 1)));
    board.record(FinanceTransaction::new("t2", "a1", 50.0, "Salary", None, at(2026, 5, 2)));
    assert!((board.get_account("a1").unwrap().balance - 120.0).abs() < 1e-9);
}

#[test]
#[should_panic(expected = "Unknown account")]
fn record_unknown_account_panics() {
    let board = InMemoryPersonalFinanceBoard::new();
    board.record(FinanceTransaction::new("t1", "nope", 1.0, "X", None, at(2026, 5, 1)));
}

#[test]
fn list_for_month_filters_by_account_and_month() {
    let board = InMemoryPersonalFinanceBoard::new();
    board.upsert(Account::new("a1", "Cheque", 0.0, "ZAR"));
    board.record(FinanceTransaction::new("may1", "a1", 10.0, "X", None, at(2026, 5, 3)));
    board.record(FinanceTransaction::new("may2", "a1", 20.0, "Y", None, at(2026, 5, 20)));
    board.record(FinanceTransaction::new("jun", "a1", 99.0, "Z", None, at(2026, 6, 1)));

    let ids: Vec<String> = board
        .list_for_month("a1", 2026, 5)
        .into_iter()
        .map(|t| t.tx_id)
        .collect();
    assert_eq!(ids.len(), 2);
    assert!(ids.contains(&"may1".to_string()) && ids.contains(&"may2".to_string()));
}

#[test]
fn budgets_case_insensitive_key_ordered_by_category() {
    let board = InMemoryPersonalFinanceBoard::new();
    board.set_budget(BudgetLine::new("Food", 2000.0));
    board.set_budget(BudgetLine::new("food", 2500.0)); // overwrites (case-insensitive key)
    board.set_budget(BudgetLine::new("Airtime", 300.0));

    let budgets = board.budgets();
    let cats: Vec<String> = budgets.iter().map(|b| b.category.clone()).collect();
    assert_eq!(cats, vec!["Airtime", "food"]);
    // The overwrite kept the latest value + latest original casing.
    let food = budgets.iter().find(|b| b.category.eq_ignore_ascii_case("food")).unwrap();
    assert_eq!(food.monthly_limit, 2500.0);
}

#[test]
fn summarise_splits_in_out_and_by_category() {
    let board = InMemoryPersonalFinanceBoard::new();
    board.upsert(Account::new("a1", "Cheque", 0.0, "ZAR"));
    board.record(FinanceTransaction::new("t1", "a1", 100.0, "Salary", None, at(2026, 4, 1)));
    board.record(FinanceTransaction::new("t2", "a1", -30.0, "Food", None, at(2026, 4, 2)));
    board.record(FinanceTransaction::new("t3", "a1", -20.0, "Food", None, at(2026, 4, 3)));

    let s = board.summarise("a1", 2026, 4);
    assert_eq!(s.year, 2026);
    assert_eq!(s.month, 4);
    assert!((s.total_in - 100.0).abs() < 1e-9);
    assert!((s.total_out - 50.0).abs() < 1e-9);
    assert!((s.by_category["Food"] + 50.0).abs() < 1e-9);
    assert!((s.by_category["Salary"] - 100.0).abs() < 1e-9);
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(PersonalFinanceDomainContext::SYSTEM_PROMPT_SNIPPET
        .starts_with("[DOMAIN: Personal.Finance]"));
    assert!(PersonalFinanceDomainContext::SYSTEM_PROMPT_SNIPPET.contains("not advice"));
    assert_eq!(
        PersonalFinanceDomainContext::compliance_flags(),
        vec!["FAIS_Act_37_2002", "NCA", "POPIA", "Not_Financial_Advice"]
    );
    assert_eq!(
        PersonalFinanceDomainContext::suggested_tools(),
        vec!["budget_tracker", "spreadsheet", "calculator", "web_search"]
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
        "sess-pfin"
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
    let mut adapter = PersonalFinanceCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(
        seen,
        format!("{}\n\nhelp", PersonalFinanceDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = PersonalFinanceCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-pfin");
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = PersonalFinanceCompanionAdapter::new(RecordingSession::new());

    adapter.build_budget("R30k", "R25k").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(PersonalFinanceDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("50/30/20 rule"));

    // Money rendered via the plain slot (integer -> no decimal point).
    adapter.design_savings_goal("Emergency fund", 30000.0, 12).unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("save 30000 for 'Emergency fund' in 12 months"));
}
