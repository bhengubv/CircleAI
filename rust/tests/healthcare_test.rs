//! healthcare_test.rs
//!
//! Ports the behaviour of `CircleAI.Healthcare`: patient registry, appointment
//! scheduling + ordered listing, status update, prescription ledger
//! (newest-first), the static domain descriptor, and the domain adapter that
//! prefixes the healthcare system-prompt snippet onto free-form turns.

use std::cell::RefCell;

use chrono::{Duration, NaiveDate, Utc};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};
use circle_ai::healthcare::{
    HealthAppointment, HealthcareCompanionAdapter, HealthcareDomainContext, IHealthcareBoard,
    InMemoryHealthcareBoard, Patient, Prescription,
};

fn dob() -> NaiveDate {
    NaiveDate::from_ymd_opt(1990, 6, 15).unwrap()
}

#[test]
fn register_and_get_patient() {
    let board = InMemoryHealthcareBoard::new();
    assert!(board.get_patient("p1").is_none());
    board.register(Patient::new("p1", "Ada", dob()));
    let p = board.get_patient("p1").unwrap();
    assert_eq!(p.name, "Ada");
    assert_eq!(p.date_of_birth, dob());
}

#[test]
fn appointments_ordered_ascending_by_time() {
    let board = InMemoryHealthcareBoard::new();
    board.schedule(HealthAppointment::new(
        "a-late",
        "p1",
        "Dr A",
        Utc::now() + Duration::hours(3),
        "booked",
    ));
    board.schedule(HealthAppointment::new(
        "a-early",
        "p1",
        "Dr A",
        Utc::now() + Duration::hours(1),
        "booked",
    ));
    board.schedule(HealthAppointment::new(
        "a-other",
        "p2",
        "Dr B",
        Utc::now() + Duration::hours(2),
        "booked",
    ));

    let appts = board.appointments_for("p1");
    let ids: Vec<&str> = appts.iter().map(|a| a.appt_id.as_str()).collect();
    assert_eq!(ids, vec!["a-early", "a-late"]);
}

#[test]
fn update_status_mutates_appointment() {
    let board = InMemoryHealthcareBoard::new();
    board.schedule(HealthAppointment::new("a1", "p1", "Dr A", Utc::now(), "booked"));
    board.update_status("a1", "completed");
    assert_eq!(board.appointments_for("p1")[0].status, "completed");
}

#[test]
#[should_panic(expected = "Unknown appointment")]
fn update_status_unknown_panics() {
    InMemoryHealthcareBoard::new().update_status("nope", "x");
}

#[test]
fn prescriptions_newest_first() {
    let board = InMemoryHealthcareBoard::new();
    board.prescribe(Prescription::new(
        "rx-old",
        "p1",
        "Amoxicillin",
        "500mg",
        "TDS",
        Utc::now() - Duration::days(2),
    ));
    board.prescribe(Prescription::new(
        "rx-new",
        "p1",
        "Ibuprofen",
        "200mg",
        "PRN",
        Utc::now(),
    ));
    board.prescribe(Prescription::new(
        "rx-other",
        "p2",
        "Paracetamol",
        "1g",
        "QDS",
        Utc::now(),
    ));

    let rx = board.prescriptions_for("p1");
    let ids: Vec<&str> = rx.iter().map(|r| r.rx_id.as_str()).collect();
    assert_eq!(ids, vec!["rx-new", "rx-old"]);
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(HealthcareDomainContext::SYSTEM_PROMPT_SNIPPET.starts_with("[DOMAIN: Healthcare]"));
    assert!(HealthcareDomainContext::SYSTEM_PROMPT_SNIPPET.contains("ICD-10"));
    assert_eq!(
        HealthcareDomainContext::compliance_flags(),
        vec![
            "HIPAA",
            "POPIA",
            "Health_Professions_Act_56_1974",
            "NHA_61_2003",
            "ICD10"
        ]
    );
    assert_eq!(
        HealthcareDomainContext::suggested_tools(),
        vec![
            "ehr_system",
            "appointment_scheduler",
            "document_editor",
            "icd10_lookup"
        ]
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
        "sess-health"
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
    let mut adapter = HealthcareCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(
        seen,
        format!("{}\n\nhelp", HealthcareDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = HealthcareCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-health");
    assert_eq!(adapter.identity_id(), "id-1");
    assert_eq!(adapter.interface(), InterfaceKind::Mobile);
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = HealthcareCompanionAdapter::new(RecordingSession::new());

    adapter.suggest_icd10_codes("type 2 diabetes").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(HealthcareDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("ICD-10-CM codes"));
    assert!(seen.contains("type 2 diabetes"));

    adapter.triage_symptoms("34", "chest pain", "2h").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("Triage symptoms for 34-year-old: chest pain, duration 2h"));

    adapter.document_clinical_note("visit summary").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("structured SOAP clinical note"));
}
