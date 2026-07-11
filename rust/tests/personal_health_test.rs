//! personal_health_test.rs
//!
//! Ports the behaviour of `CircleAI.Personal.Health`: vital recording +
//! read-since (oldest-first) + latest (newest, ties keep first-inserted),
//! allergy registry, medication add/end + active listing (by name), the static
//! domain descriptor, and the domain adapter.

use std::cell::RefCell;

use chrono::{Duration, Utc};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};
use circle_ai::personal_health::{
    Allergy, IPersonalHealthBoard, InMemoryPersonalHealthBoard, Medication,
    PersonalHealthCompanionAdapter, PersonalHealthDomainContext, VitalKind, VitalReading,
};

#[test]
fn read_since_oldest_first_filtered_by_kind() {
    let board = InMemoryPersonalHealthBoard::new();
    let base = Utc::now() - Duration::days(10);
    board.record(VitalReading::new(VitalKind::WeightKg, 80.0, base, None));
    board.record(VitalReading::new(VitalKind::WeightKg, 79.0, base + Duration::days(2), None));
    board.record(VitalReading::new(VitalKind::GlucoseMgDl, 90.0, base + Duration::days(1), None));

    let readings = board.read_since(VitalKind::WeightKg, base);
    let values: Vec<f64> = readings.iter().map(|r| r.value).collect();
    assert_eq!(values, vec![80.0, 79.0]);

    // `since` filter excludes older readings.
    assert!(board
        .read_since(VitalKind::WeightKg, base + Duration::days(1))
        .iter()
        .all(|r| r.value == 79.0));
}

#[test]
fn latest_returns_newest_reading() {
    let board = InMemoryPersonalHealthBoard::new();
    let base = Utc::now() - Duration::days(5);
    board.record(VitalReading::new(VitalKind::HeartRateBpm, 70.0, base, None));
    board.record(VitalReading::new(VitalKind::HeartRateBpm, 65.0, base + Duration::days(1), None));
    assert_eq!(board.latest(VitalKind::HeartRateBpm).unwrap().value, 65.0);
    assert!(board.latest(VitalKind::OxygenPct).is_none());
}

#[test]
fn allergies_registry() {
    let board = InMemoryPersonalHealthBoard::new();
    assert!(board.allergies().is_empty());
    board.add_allergy(Allergy::new("al1", "Penicillin", "severe"));
    board.add_allergy(Allergy::new("al2", "Peanuts", "moderate"));
    assert_eq!(board.allergies().len(), 2);
}

#[test]
fn active_medications_excludes_ended_ordered_by_name() {
    let board = InMemoryPersonalHealthBoard::new();
    let start = Utc::now() - Duration::days(30);
    board.add_medication(Medication::new("m1", "Zeta", "10mg", "OD", start, None));
    board.add_medication(Medication::new("m2", "Alpha", "5mg", "BD", start, None));
    board.add_medication(Medication::new("m3", "Beta", "1g", "TDS", start, None));

    board.end_medication("m3", Utc::now());

    let names: Vec<String> = board.active_medications().into_iter().map(|m| m.name).collect();
    assert_eq!(names, vec!["Alpha", "Zeta"]);
}

#[test]
#[should_panic(expected = "Unknown medication")]
fn end_unknown_medication_panics() {
    InMemoryPersonalHealthBoard::new().end_medication("nope", Utc::now());
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(PersonalHealthDomainContext::SYSTEM_PROMPT_SNIPPET.starts_with("[DOMAIN: Personal.Health]"));
    assert!(PersonalHealthDomainContext::SYSTEM_PROMPT_SNIPPET.contains("not medical advice"));
    assert_eq!(
        PersonalHealthDomainContext::compliance_flags(),
        vec!["POPIA", "Health_Professions_Act", "Not_Medical_Advice"]
    );
    assert_eq!(
        PersonalHealthDomainContext::suggested_tools(),
        vec!["health_tracker", "symptom_checker_ref", "calendar", "document_editor"]
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
        "sess-phealth"
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
    let mut adapter = PersonalHealthCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(
        seen,
        format!("{}\n\nhelp", PersonalHealthDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = PersonalHealthCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-phealth");
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = PersonalHealthCompanionAdapter::new(RecordingSession::new());
    adapter.explain_health_term("hypertension").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(PersonalHealthDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("hypertension"));
    assert!(seen.contains("plain language"));
}
