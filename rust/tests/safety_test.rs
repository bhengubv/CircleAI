//! safety_test.rs
//!
//! Ports the behaviour of `CircleAI.Safety` (`SafetyPrimitives.cs`,
//! `SafetyDomainContext.cs`, `SafetyCompanionAdapter.cs`): incident logging +
//! newest-first ordering, severity routing, the hazard ledger keyed by id, the
//! contact ring, the static domain descriptor, and the domain adapter that
//! prefixes the safety system-prompt snippet onto free-form turns.

use std::cell::RefCell;

use chrono::{Duration, Utc};
use circle_ai::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};
use circle_ai::safety::{
    EmergencyContact, Hazard, ISafetyBoard, Incident, IncidentSeverity, InMemorySafetyBoard,
    SafetyCompanionAdapter, SafetyDomainContext,
};

// ── InMemorySafetyBoard: incidents ──────────────────────────────────────────

fn incident(id: &str, sev: IncidentSeverity, minutes_ago: i64) -> Incident {
    Incident::new(
        id,
        sev,
        format!("desc-{id}"),
        Some(-26.2),
        Some(28.0),
        Utc::now() - Duration::minutes(minutes_ago),
    )
}

#[test]
fn active_returns_incidents_newest_first() {
    let board = InMemorySafetyBoard::new();
    board.log(incident("old", IncidentSeverity::Info, 30));
    board.log(incident("mid", IncidentSeverity::Warning, 20));
    board.log(incident("new", IncidentSeverity::Critical, 5));

    let active = board.active();
    let ids: Vec<&str> = active.iter().map(|i| i.incident_id.as_str()).collect();
    assert_eq!(ids, vec!["new", "mid", "old"]);
}

#[test]
fn active_is_empty_initially() {
    let board = InMemorySafetyBoard::new();
    assert!(board.active().is_empty());
}

#[test]
fn at_or_above_severity_filters_and_orders() {
    let board = InMemorySafetyBoard::new();
    board.log(incident("info", IncidentSeverity::Info, 40));
    board.log(incident("warn", IncidentSeverity::Warning, 30));
    board.log(incident("crit", IncidentSeverity::Critical, 20));
    board.log(incident("emer", IncidentSeverity::Emergency, 10));

    let at_warn = board.at_or_above_severity(IncidentSeverity::Warning);
    let ids: Vec<&str> = at_warn.iter().map(|i| i.incident_id.as_str()).collect();
    // Warning, Critical, Emergency pass; ordered newest-first.
    assert_eq!(ids, vec!["emer", "crit", "warn"]);

    let at_emer = board.at_or_above_severity(IncidentSeverity::Emergency);
    assert_eq!(at_emer.len(), 1);
    assert_eq!(at_emer[0].incident_id, "emer");
}

#[test]
fn severity_ordering_is_info_lt_emergency() {
    assert!(IncidentSeverity::Info < IncidentSeverity::Warning);
    assert!(IncidentSeverity::Warning < IncidentSeverity::Critical);
    assert!(IncidentSeverity::Critical < IncidentSeverity::Emergency);
    assert_eq!(IncidentSeverity::Info as i32, 0);
    assert_eq!(IncidentSeverity::Emergency as i32, 3);
}

// ── InMemorySafetyBoard: hazards ────────────────────────────────────────────

#[test]
fn hazards_are_keyed_by_id_and_newest_first() {
    let board = InMemorySafetyBoard::new();
    board.note_hazard(Hazard::new("h1", "wet floor", "slip", Utc::now() - Duration::minutes(10)));
    board.note_hazard(Hazard::new("h2", "loose wire", "electrical", Utc::now() - Duration::minutes(5)));
    // Re-noting the same id overwrites (dictionary semantics).
    board.note_hazard(Hazard::new("h1", "wet floor (mopped)", "slip", Utc::now()));

    let hazards = board.hazards();
    assert_eq!(hazards.len(), 2, "h1 overwritten, not duplicated");
    // h1 now has the most recent noted_utc -> first.
    assert_eq!(hazards[0].hazard_id, "h1");
    assert_eq!(hazards[0].description, "wet floor (mopped)");
    assert_eq!(hazards[1].hazard_id, "h2");
}

#[test]
fn hazards_empty_initially() {
    assert!(InMemorySafetyBoard::new().hazards().is_empty());
}

// ── InMemorySafetyBoard: contacts ───────────────────────────────────────────

#[test]
fn contacts_preserve_insertion_order_and_first() {
    let board = InMemorySafetyBoard::new();
    assert!(board.first_contact().is_none());

    board.add_contact(EmergencyContact::new("c1", "Alice", "0111", "sister"));
    board.add_contact(EmergencyContact::new("c2", "Bob", "0222", "neighbour"));

    let contacts = board.contacts();
    assert_eq!(contacts.len(), 2);
    assert_eq!(contacts[0].contact_id, "c1");
    assert_eq!(contacts[1].contact_id, "c2");

    let first = board.first_contact().unwrap();
    assert_eq!(first.name, "Alice");
    assert_eq!(first.relationship, "sister");
}

// ── SafetyDomainContext ─────────────────────────────────────────────────────

#[test]
fn domain_context_snippet_and_flags() {
    assert!(SafetyDomainContext::SYSTEM_PROMPT_SNIPPET.starts_with("[DOMAIN: Safety]"));
    assert!(SafetyDomainContext::SYSTEM_PROMPT_SNIPPET.contains("10111"));
    assert_eq!(
        SafetyDomainContext::compliance_flags(),
        vec!["POPIA", "OHS_Act", "Emergency_Protocol_10111"]
    );
    assert_eq!(
        SafetyDomainContext::suggested_tools(),
        vec!["emergency_contacts", "document_editor", "map", "web_search"]
    );
}

// ── SafetyCompanionAdapter ──────────────────────────────────────────────────

#[derive(Debug)]
struct FakeError(String);
impl std::fmt::Display for FakeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}
impl std::error::Error for FakeError {}

/// Records the exact message handed to each entry point so tests can assert the
/// domain-prefix (`E`) wrapping and the raw domain-helper instructions.
struct RecordingSession {
    context: CompanionContext,
    history: Vec<CompanionTurn>,
    last_send: RefCell<Option<String>>,
    last_stream: RefCell<Option<String>>,
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
            last_stream: RefCell::new(None),
            last_agent: RefCell::new(None),
        }
    }
}

impl ICompanionSession for RecordingSession {
    type Error = FakeError;

    fn session_id(&self) -> &str {
        "sess-safety"
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
        *self.last_stream.borrow_mut() = Some(message.to_string());
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
    fn signal_feedback(&mut self, _positive: bool, _note: Option<&str>) -> Result<(), Self::Error> {
        Ok(())
    }
}

#[test]
fn adapter_prefixes_domain_snippet_on_send() {
    let mut adapter = SafetyCompanionAdapter::new(RecordingSession::new());
    adapter.send("help me").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    let expected = format!("{}\n\nhelp me", SafetyDomainContext::SYSTEM_PROMPT_SNIPPET);
    assert_eq!(seen, expected);
}

#[test]
fn adapter_prefixes_domain_snippet_on_stream_and_agent() {
    let mut adapter = SafetyCompanionAdapter::new(RecordingSession::new());
    let _ = adapter.stream("go").unwrap().count();
    let _ = adapter.agent("act").unwrap();

    let stream_seen = adapter.inner().last_stream.borrow().clone().unwrap();
    let agent_seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert_eq!(
        stream_seen,
        format!("{}\n\ngo", SafetyDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
    assert_eq!(
        agent_seen,
        format!("{}\n\nact", SafetyDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
}

#[test]
fn adapter_passes_metadata_through() {
    let adapter = SafetyCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-safety");
    assert_eq!(adapter.identity_id(), "id-1");
    assert_eq!(adapter.interface(), InterfaceKind::Mobile);
    assert_eq!(adapter.history().len(), 1);
    assert_eq!(adapter.get_context().identity_id, "id-1");
}

#[test]
fn domain_helper_emergency_plan_uses_raw_instruction() {
    let mut adapter = SafetyCompanionAdapter::new(RecordingSession::new());
    adapter.create_emergency_plan("4", "Sandton").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    // Domain helpers call the inner agent WITHOUT the E() prefix.
    assert!(!seen.starts_with(SafetyDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("4-person household in Sandton"));
    assert!(seen.contains("go-bag checklist"));
}

#[test]
fn domain_helpers_format_expected_prompts() {
    let mut adapter = SafetyCompanionAdapter::new(RecordingSession::new());

    adapter.assess_security("townhouse", "burglary").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("Assess home security for a townhouse. Concerns: burglary."));

    adapter.conduct_risk_assessment("welding", "workshop").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("risk assessment for welding in workshop"));

    adapter.draft_emergency_response("fire", "warehouse").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("emergency response steps for fire at warehouse"));

    adapter.brief_safety_toolbox("scaffolding", "falls").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("toolbox talk for task: scaffolding. Top hazards: falls."));

    adapter.review_incident_report("worker slipped").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("Review this incident narrative: worker slipped."));
}
