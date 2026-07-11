//! legal_test.rs
//!
//! Ports the behaviour of `CircleAI.Legal`: matter open/close + active-matters
//! ordering, contract expiry queries, deadline queries, the case-insensitive
//! clause library, the static domain descriptor, and the domain adapter.

use std::cell::RefCell;

use chrono::{Duration, NaiveDate, Utc};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};
use circle_ai::legal::{
    Clause, Contract, ILegalBoard, InMemoryLegalBoard, LegalCompanionAdapter, LegalDeadline,
    LegalDomainContext, Matter,
};

fn d(y: i32, m: u32, day: u32) -> NaiveDate {
    NaiveDate::from_ymd_opt(y, m, day).unwrap()
}

#[test]
fn open_get_and_close_matter() {
    let board = InMemoryLegalBoard::new();
    board.open(Matter::new("m1", "Acme v Beta", "ZA", "Acme", Utc::now(), true));
    assert!(board.get_matter("m1").unwrap().open);
    board.close("m1");
    assert!(!board.get_matter("m1").unwrap().open);
}

#[test]
#[should_panic(expected = "Unknown matter")]
fn close_unknown_matter_panics() {
    InMemoryLegalBoard::new().close("nope");
}

#[test]
fn active_matters_newest_opened_first_excludes_closed() {
    let board = InMemoryLegalBoard::new();
    board.open(Matter::new("m-old", "Old", "ZA", "C", Utc::now() - Duration::days(2), true));
    board.open(Matter::new("m-new", "New", "ZA", "C", Utc::now(), true));
    board.open(Matter::new("m-closed", "Closed", "ZA", "C", Utc::now() - Duration::days(1), false));

    let ids: Vec<String> = board.active_matters().into_iter().map(|m| m.matter_id).collect();
    assert_eq!(ids, vec!["m-new", "m-old"]);
}

#[test]
fn contracts_expiring_before_soonest_first_ignores_open_ended() {
    let board = InMemoryLegalBoard::new();
    board.add_contract(Contract::new("c-late", "m1", "Late", d(2026, 1, 1), Some(d(2026, 12, 31)), vec![]));
    board.add_contract(Contract::new("c-soon", "m1", "Soon", d(2026, 1, 1), Some(d(2026, 6, 30)), vec![]));
    board.add_contract(Contract::new("c-none", "m1", "Perpetual", d(2026, 1, 1), None, vec![]));
    board.add_contract(Contract::new("c-after", "m1", "After", d(2026, 1, 1), Some(d(2027, 1, 1)), vec![]));

    let ids: Vec<String> = board
        .contracts_expiring_before(d(2026, 12, 31))
        .into_iter()
        .map(|c| c.contract_id)
        .collect();
    assert_eq!(ids, vec!["c-soon", "c-late"]);
}

#[test]
fn upcoming_deadlines_soonest_first_from_now() {
    let board = InMemoryLegalBoard::new();
    board.add(LegalDeadline::new("d-past", "m1", "past", d(2026, 1, 1)));
    board.add(LegalDeadline::new("d-far", "m1", "far", d(2026, 9, 1)));
    board.add(LegalDeadline::new("d-near", "m1", "near", d(2026, 7, 1)));

    let ids: Vec<String> = board
        .upcoming_deadlines(d(2026, 6, 1))
        .into_iter()
        .map(|x| x.deadline_id)
        .collect();
    assert_eq!(ids, vec!["d-near", "d-far"]);
}

#[test]
fn clauses_by_tag_is_case_insensitive() {
    let board = InMemoryLegalBoard::new();
    board.add_clause(Clause::new("cl1", "Indemnity", "body", vec!["Risk".into(), "Liability".into()]));
    board.add_clause(Clause::new("cl2", "Warranty", "body", vec!["Quality".into()]));

    let ids: Vec<String> = board.clauses_by_tag("risk").into_iter().map(|c| c.clause_id).collect();
    assert_eq!(ids, vec!["cl1"]);
    assert!(board.clauses_by_tag("nonexistent").is_empty());
}

#[test]
#[should_panic(expected = "tag required")]
fn clauses_by_blank_tag_panics() {
    InMemoryLegalBoard::new().clauses_by_tag("   ");
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(LegalDomainContext::SYSTEM_PROMPT_SNIPPET.starts_with("[DOMAIN: Legal]"));
    assert!(LegalDomainContext::SYSTEM_PROMPT_SNIPPET.contains("not legal advice"));
    assert_eq!(
        LegalDomainContext::compliance_flags(),
        vec![
            "Legal_Practice_Act_28_2014",
            "Attorneys_Act",
            "POPIA",
            "Professional_Legal_Privilege"
        ]
    );
    assert_eq!(
        LegalDomainContext::suggested_tools(),
        vec!["legal_research", "document_editor", "contract_analyser"]
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
        "sess-legal"
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
    let mut adapter = LegalCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(seen, format!("{}\n\nhelp", LegalDomainContext::SYSTEM_PROMPT_SNIPPET));
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = LegalCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-legal");
    assert_eq!(adapter.identity_id(), "id-1");
    assert_eq!(adapter.interface(), InterfaceKind::Mobile);
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = LegalCompanionAdapter::new(RecordingSession::new());

    adapter.review_contract_clauses("MSA text", "liability").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(LegalDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("Review the following contract for liability issues"));
    assert!(seen.contains("MSA text"));

    adapter.draft_clause("indemnity", "supplier", "ZA").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("Draft a indemnity clause favouring the supplier in ZA"));
}
