//! safety_child_test.rs
//!
//! Ports the behaviour of `CircleAI.Safety.Child` (`ChildSafetyPrimitives.cs`,
//! `SafetyChildDomainContext.cs`, `SafetyChildCompanionAdapter.cs`): the
//! trusted-adult ring ordered by priority, geofence containment via Haversine,
//! per-child check-in history with a limit, the static domain descriptor, and the
//! domain adapter that prefixes the child-safety system-prompt snippet.

use std::cell::RefCell;

use chrono::{Duration, Utc};
use circle_ai::companion::types::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};
use circle_ai::safety_child::{
    haversine_meters, CheckIn, Geofence, IChildSafetyBoard, InMemoryChildSafetyBoard,
    SafetyChildCompanionAdapter, SafetyChildDomainContext, TrustedAdult,
};

// ── Trusted-adult ring ──────────────────────────────────────────────────────

#[test]
fn ring_ordered_by_ascending_priority() {
    let board = InMemoryChildSafetyBoard::new();
    board.add_adult(TrustedAdult::new("a3", "Carol", "3", "aunt", 3));
    board.add_adult(TrustedAdult::new("a1", "Alice", "1", "mother", 1));
    board.add_adult(TrustedAdult::new("a2", "Bob", "2", "father", 2));

    let ring = board.ring_ordered();
    let ids: Vec<&str> = ring.iter().map(|a| a.adult_id.as_str()).collect();
    assert_eq!(ids, vec!["a1", "a2", "a3"]);
    assert_eq!(ring[0].name, "Alice");
}

#[test]
fn add_adult_overwrites_by_id() {
    let board = InMemoryChildSafetyBoard::new();
    board.add_adult(TrustedAdult::new("a1", "Alice", "1", "mother", 5));
    board.add_adult(TrustedAdult::new("a1", "Alice M", "1", "mother", 1));
    let ring = board.ring_ordered();
    assert_eq!(ring.len(), 1);
    assert_eq!(ring[0].name, "Alice M");
    assert_eq!(ring[0].ring_priority, 1);
}

#[test]
fn ring_empty_initially() {
    assert!(InMemoryChildSafetyBoard::new().ring_ordered().is_empty());
}

// ── Geofences ───────────────────────────────────────────────────────────────

#[test]
fn define_and_get_geofence() {
    let board = InMemoryChildSafetyBoard::new();
    board.define_geofence(Geofence::new("home", "Home", -26.2041, 28.0473, 200.0));
    let g = board.get_geofence("home").unwrap();
    assert_eq!(g.name, "Home");
    assert!((g.radius_meters - 200.0).abs() < 1e-9);
    assert!(board.get_geofence("missing").is_none());
}

#[test]
fn point_inside_fence_is_detected() {
    let board = InMemoryChildSafetyBoard::new();
    // 500 m radius around a Johannesburg point.
    board.define_geofence(Geofence::new("school", "School", -26.2041, 28.0473, 500.0));
    // Same centre -> distance 0 -> inside.
    assert!(board.is_inside_any_fence(-26.2041, 28.0473));
    // ~100 m north (0.0009 deg lat ~ 100 m) -> still inside 500 m.
    assert!(board.is_inside_any_fence(-26.2050, 28.0473));
}

#[test]
fn point_outside_all_fences_is_rejected() {
    let board = InMemoryChildSafetyBoard::new();
    board.define_geofence(Geofence::new("school", "School", -26.2041, 28.0473, 300.0));
    // ~2 km away (roughly 0.018 deg lat) -> outside 300 m.
    assert!(!board.is_inside_any_fence(-26.2220, 28.0473));
}

#[test]
fn inside_any_of_multiple_fences() {
    let board = InMemoryChildSafetyBoard::new();
    board.define_geofence(Geofence::new("home", "Home", -26.2041, 28.0473, 100.0));
    board.define_geofence(Geofence::new("gran", "Gran", 40.0, -74.0, 100.0));
    // Near gran's fence in New York -> inside (matches ANY fence).
    assert!(board.is_inside_any_fence(40.0005, -74.0));
}

#[test]
fn no_fences_means_never_inside() {
    let board = InMemoryChildSafetyBoard::new();
    assert!(!board.is_inside_any_fence(0.0, 0.0));
}

#[test]
fn haversine_matches_known_distance() {
    // Johannesburg (-26.2041, 28.0473) to Pretoria (-25.7479, 28.2293) ~ 52-54 km.
    let d = haversine_meters(-26.2041, 28.0473, -25.7479, 28.2293);
    assert!(
        (50_000.0..56_000.0).contains(&d),
        "distance was {d} m, expected ~52 km"
    );
    // Zero distance for identical points.
    assert!(haversine_meters(1.0, 2.0, 1.0, 2.0).abs() < 1e-6);
}

// ── Check-ins ───────────────────────────────────────────────────────────────

fn check_in(child: &str, status: &str, minutes_ago: i64) -> CheckIn {
    CheckIn::new(
        child,
        status,
        Some(-26.2),
        Some(28.0),
        Utc::now() - Duration::minutes(minutes_ago),
    )
}

#[test]
fn recent_check_ins_filtered_by_child_and_newest_first() {
    let board = InMemoryChildSafetyBoard::new();
    board.record_check_in(check_in("kid-a", "at-school", 30));
    board.record_check_in(check_in("kid-b", "at-home", 25));
    board.record_check_in(check_in("kid-a", "left-school", 10));
    board.record_check_in(check_in("kid-a", "arrived-home", 2));

    let a = board.recent_check_ins("kid-a", 20);
    let statuses: Vec<&str> = a.iter().map(|c| c.status.as_str()).collect();
    assert_eq!(statuses, vec!["arrived-home", "left-school", "at-school"]);

    let b = board.recent_check_ins("kid-b", 20);
    assert_eq!(b.len(), 1);
    assert_eq!(b[0].status, "at-home");
}

#[test]
fn recent_check_ins_respects_limit() {
    let board = InMemoryChildSafetyBoard::new();
    for i in 0..10 {
        board.record_check_in(check_in("kid", &format!("s{i}"), (10 - i) as i64));
    }
    let recent = board.recent_check_ins("kid", 3);
    assert_eq!(recent.len(), 3, "limited to 3 most recent");
    // Most recent has the smallest minutes_ago (i=9 -> 1 minute ago).
    assert_eq!(recent[0].status, "s9");
    assert_eq!(recent[2].status, "s7");
}

#[test]
fn recent_check_ins_unknown_child_is_empty() {
    let board = InMemoryChildSafetyBoard::new();
    board.record_check_in(check_in("kid", "ok", 1));
    assert!(board.recent_check_ins("ghost", 20).is_empty());
}

#[test]
#[should_panic(expected = "limit must be greater than zero")]
fn recent_check_ins_zero_limit_panics() {
    let board = InMemoryChildSafetyBoard::new();
    board.recent_check_ins("kid", 0);
}

// ── SafetyChildDomainContext ────────────────────────────────────────────────

#[test]
fn child_domain_context_snippet_and_flags() {
    assert!(SafetyChildDomainContext::SYSTEM_PROMPT_SNIPPET.starts_with("[DOMAIN: Safety.Child]"));
    assert!(SafetyChildDomainContext::SYSTEM_PROMPT_SNIPPET.contains("Childline (116)"));
    assert_eq!(
        SafetyChildDomainContext::compliance_flags(),
        vec![
            "Childrens_Act_38_2005",
            "POPIA_Children",
            "Films_Publications_Act",
            "Cybercrimes_Act",
            "Emergency_116"
        ]
    );
    assert_eq!(
        SafetyChildDomainContext::suggested_tools(),
        vec!["parental_controls", "web_search", "document_editor", "reporting_tools"]
    );
}

// ── SafetyChildCompanionAdapter ─────────────────────────────────────────────

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
                "id-c",
                "Parent",
                None,
                InterfaceKind::Web,
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
        "sess-child"
    }
    fn identity_id(&self) -> &str {
        "id-c"
    }
    fn interface(&self) -> InterfaceKind {
        InterfaceKind::Web
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
    fn signal_feedback(&mut self, _positive: bool, _note: Option<&str>) -> Result<(), Self::Error> {
        Ok(())
    }
}

#[test]
fn child_adapter_prefixes_domain_snippet_on_send() {
    let mut adapter = SafetyChildCompanionAdapter::new(RecordingSession::new());
    adapter.send("is TikTok safe?").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(
        seen,
        format!(
            "{}\n\nis TikTok safe?",
            SafetyChildDomainContext::SYSTEM_PROMPT_SNIPPET
        )
    );
}

#[test]
fn child_adapter_metadata_passthrough() {
    let adapter = SafetyChildCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-child");
    assert_eq!(adapter.identity_id(), "id-c");
    assert_eq!(adapter.interface(), InterfaceKind::Web);
}

#[test]
fn child_domain_helpers_use_raw_instructions() {
    let mut adapter = SafetyChildCompanionAdapter::new(RecordingSession::new());

    adapter.set_digital_rules("10").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(SafetyChildDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("digital safety rules for a 10-year-old"));

    adapter.educate_online_risks("8").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("appropriate for a 8-year-old"));

    adapter.design_safety_conversation("9", "strangers").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("safety conversation for 9 on: strangers"));

    adapter.assess_online_risk("Roblox", "11", "chatting with strangers").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("online risk on Roblox for 11-year-old showing chatting with strangers"));

    adapter.verify_trusted_adults("gran, coach, neighbour").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("vet trusted-adult ring from: gran, coach, neighbour"));

    adapter.draft_school_notification("bullying", "screenshots").unwrap();
    assert!(adapter
        .inner()
        .last_agent
        .borrow()
        .as_deref()
        .unwrap()
        .contains("school notification about: bullying. Evidence: screenshots."));
}
