//! personal_mental_test.rs
//!
//! Ports the behaviour of `CircleAI.Personal.Mental`: mood logging + 7-day
//! window (oldest-first) + mean (`NaN` when empty), journal entries
//! (newest-first, blank-id guard), the case-insensitive coping-strategy library,
//! the static domain descriptor, and the domain adapter.

use std::cell::RefCell;

use chrono::{Duration, Utc};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};
use circle_ai::personal_mental::{
    CopingStrategy, IMentalHealthBoard, InMemoryMentalHealthBoard, JournalEntry, Mood, MoodLog,
    PersonalMentalCompanionAdapter, PersonalMentalDomainContext,
};

#[test]
fn last_7_days_windowed_oldest_first() {
    let board = InMemoryMentalHealthBoard::new();
    board.log_mood(MoodLog::new(Mood::Great, Utc::now() - Duration::days(10), None)); // out of window
    board.log_mood(MoodLog::new(Mood::Low, Utc::now() - Duration::days(3), None));
    board.log_mood(MoodLog::new(Mood::Good, Utc::now() - Duration::days(1), None));

    let window = board.last_7_days();
    let moods: Vec<Mood> = window.iter().map(|m| m.mood).collect();
    assert_eq!(moods, vec![Mood::Low, Mood::Good]);
}

#[test]
fn avg_mood_7_day_uses_int_discriminants_and_nan_when_empty() {
    let board = InMemoryMentalHealthBoard::new();
    assert!(board.avg_mood_7_day().is_nan());

    board.log_mood(MoodLog::new(Mood::Low, Utc::now() - Duration::days(1), None)); // 1
    board.log_mood(MoodLog::new(Mood::Good, Utc::now() - Duration::hours(2), None)); // 3
    // (1 + 3) / 2 = 2.0.
    assert!((board.avg_mood_7_day() - 2.0).abs() < 1e-9);
}

#[test]
fn entries_newest_first() {
    let board = InMemoryMentalHealthBoard::new();
    board.add_entry(JournalEntry::new("e-old", "Old", "body", Utc::now() - Duration::days(2)));
    board.add_entry(JournalEntry::new("e-new", "New", "body", Utc::now()));
    let ids: Vec<String> = board.entries().into_iter().map(|e| e.entry_id).collect();
    assert_eq!(ids, vec!["e-new", "e-old"]);
}

#[test]
#[should_panic(expected = "EntryId required")]
fn add_entry_blank_id_panics() {
    let board = InMemoryMentalHealthBoard::new();
    board.add_entry(JournalEntry::new("   ", "T", "B", Utc::now()));
}

#[test]
fn strategies_by_tag_case_insensitive() {
    let board = InMemoryMentalHealthBoard::new();
    board.register_strategy(CopingStrategy::new(
        "s1",
        "Box breathing",
        "4-4-4-4",
        vec!["Anxiety".into(), "Panic".into()],
    ));
    board.register_strategy(CopingStrategy::new("s2", "Journaling", "write it out", vec!["Reflection".into()]));

    let ids: Vec<String> = board.strategies_by_tag("panic").into_iter().map(|s| s.strategy_id).collect();
    assert_eq!(ids, vec!["s1"]);
    assert!(board.strategies_by_tag("unknown").is_empty());
}

#[test]
#[should_panic(expected = "tag required")]
fn strategies_by_blank_tag_panics() {
    InMemoryMentalHealthBoard::new().strategies_by_tag("");
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(PersonalMentalDomainContext::SYSTEM_PROMPT_SNIPPET.starts_with("[DOMAIN: Personal.Mental]"));
    assert!(PersonalMentalDomainContext::SYSTEM_PROMPT_SNIPPET.contains("SADAG"));
    assert_eq!(
        PersonalMentalDomainContext::compliance_flags(),
        vec!["POPIA", "Mental_Health_Care_Act_17_2002", "Not_Therapy", "Crisis_Protocol"]
    );
    assert_eq!(
        PersonalMentalDomainContext::suggested_tools(),
        vec!["journal", "breathing_tools", "mood_tracker", "web_search"]
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
        "sess-pmental"
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
    let mut adapter = PersonalMentalCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(
        seen,
        format!("{}\n\nhelp", PersonalMentalDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = PersonalMentalCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-pmental");
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = PersonalMentalCompanionAdapter::new(RecordingSession::new());

    adapter.check_in("anxious").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(PersonalMentalDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("I am feeling: anxious"));

    adapter.guide_mindfulness("5-minute").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("5-minute mindfulness"));
}
