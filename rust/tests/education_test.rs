//! education_test.rs
//!
//! Ports the behaviour of `CircleAI.Education`: courses, lessons (ordered by
//! index), student enrolment + progress update, per-course average progress,
//! the static domain descriptor, and the domain adapter.

use std::cell::RefCell;

use chrono::Duration;
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};
use circle_ai::education::{
    Course, EducationCompanionAdapter, EducationDomainContext, IEducationBoard,
    InMemoryEducationBoard, Lesson, StudentRecord,
};

#[test]
fn add_and_get_course() {
    let board = InMemoryEducationBoard::new();
    assert!(board.get_course("c1").is_none());
    board.add_course(Course::new("c1", "Algebra I", "Maths", "Grade 8"));
    assert_eq!(board.get_course("c1").unwrap().name, "Algebra I");
}

#[test]
fn lessons_ordered_by_index() {
    let board = InMemoryEducationBoard::new();
    board.add_lesson(Lesson::new("l3", "c1", "Third", Duration::minutes(30), 3));
    board.add_lesson(Lesson::new("l1", "c1", "First", Duration::minutes(30), 1));
    board.add_lesson(Lesson::new("l2", "c1", "Second", Duration::minutes(30), 2));
    board.add_lesson(Lesson::new("l-other", "c2", "Other", Duration::minutes(30), 1));

    let ids: Vec<String> = board.lessons_for("c1").into_iter().map(|l| l.lesson_id).collect();
    assert_eq!(ids, vec!["l1", "l2", "l3"]);
}

#[test]
fn enrol_update_progress_and_average() {
    let board = InMemoryEducationBoard::new();
    board.enrol(StudentRecord::new("s1", "Ann", "c1", 20.0));
    board.enrol(StudentRecord::new("s2", "Ben", "c1", 40.0));
    board.enrol(StudentRecord::new("s3", "Cid", "c2", 100.0));

    assert_eq!(board.students_for("c1").len(), 2);
    assert!((board.avg_progress_for("c1") - 30.0).abs() < 1e-9);

    board.update_progress("s1", 60.0);
    assert!((board.avg_progress_for("c1") - 50.0).abs() < 1e-9);

    // Empty course averages to 0.0.
    assert_eq!(board.avg_progress_for("nope"), 0.0);
}

#[test]
#[should_panic(expected = "Unknown student")]
fn update_progress_unknown_panics() {
    InMemoryEducationBoard::new().update_progress("nope", 10.0);
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(EducationDomainContext::SYSTEM_PROMPT_SNIPPET.starts_with("[DOMAIN: Education]"));
    assert!(EducationDomainContext::SYSTEM_PROMPT_SNIPPET.contains("CAPS/NCS"));
    assert_eq!(
        EducationDomainContext::compliance_flags(),
        vec!["SASA", "CAPS_NCS", "POPIA", "PAIA"]
    );
    assert_eq!(
        EducationDomainContext::suggested_tools(),
        vec!["learning_management", "document_editor", "assessment_tools", "web_search"]
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
        "sess-edu"
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
    let mut adapter = EducationCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(seen, format!("{}\n\nhelp", EducationDomainContext::SYSTEM_PROMPT_SNIPPET));
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = EducationCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-edu");
    assert_eq!(adapter.interface(), InterfaceKind::Mobile);
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = EducationCompanionAdapter::new(RecordingSession::new());
    adapter.generate_rubric("essay", "Grade 10").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(EducationDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("Grade 10"));
}
