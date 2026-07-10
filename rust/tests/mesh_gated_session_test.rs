//! mesh_gated_session_test.rs
//!
//! Ports `CircleAI.Security.AetherNet/MeshGatedCompanionSession.cs`: the
//! decorator gates every message-producing call (send / stream / agent) on the
//! mesh block state for the session identity, while diagnostic / metadata calls
//! pass through unguarded. A blocked identity yields `MeshGatedError::Blocked`;
//! an unblocked identity reaches the inner session unchanged.

use std::sync::Arc;

use chrono::Utc;
use circle_ai::aether::{AetherThreatLevel, ISecurityDirectiveConsumer, SecurityDirective, SecurityDirectiveKind};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};
use circle_ai::security_aethernet::{
    MeshDirectiveStore, MeshGatedCompanionSession, MeshGatedError, MeshSecurityGate,
};

// ── Fake inner session ────────────────────────────────────────────────────────

#[derive(Debug)]
struct FakeError(String);
impl std::fmt::Display for FakeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.0)
    }
}
impl std::error::Error for FakeError {}

/// Minimal session that echoes calls and counts how many times each guarded
/// entry point actually reached it.
struct FakeSession {
    identity_id: String,
    context: CompanionContext,
    history: Vec<CompanionTurn>,
    send_calls: usize,
    stream_calls: usize,
    agent_calls: usize,
}

impl FakeSession {
    fn new(identity_id: &str) -> Self {
        let context = CompanionContext::new(
            identity_id,
            "Test User",
            None,
            InterfaceKind::Headless,
            "",
            "",
            Vec::new(),
            Vec::new(),
        );
        Self {
            identity_id: identity_id.to_string(),
            context,
            history: vec![CompanionTurn::user("hi")],
            send_calls: 0,
            stream_calls: 0,
            agent_calls: 0,
        }
    }
}

impl ICompanionSession for FakeSession {
    type Error = FakeError;

    fn session_id(&self) -> &str {
        "sess-1"
    }
    fn identity_id(&self) -> &str {
        &self.identity_id
    }
    fn interface(&self) -> InterfaceKind {
        InterfaceKind::Headless
    }

    fn send(&mut self, message: &str) -> Result<String, Self::Error> {
        self.send_calls += 1;
        Ok(format!("echo: {message}"))
    }

    fn stream(
        &mut self,
        message: &str,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        self.stream_calls += 1;
        let chunks = vec![Ok(format!("chunk1:{message}")), Ok("chunk2".to_string())];
        Ok(Box::new(chunks.into_iter()))
    }

    fn agent(&mut self, instruction: &str) -> Result<String, Self::Error> {
        self.agent_calls += 1;
        Ok(format!("agent: {instruction}"))
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

// ── Helpers ───────────────────────────────────────────────────────────────────

fn block(store: &MeshDirectiveStore, id: &str) {
    store.on_directive(&SecurityDirective::new(
        SecurityDirectiveKind::QuarantineNode,
        Some(id.into()),
        None,
        AetherThreatLevel::Critical,
        "blocked by mesh",
        None,
        Utc::now(),
    ));
}

// ── Tests ─────────────────────────────────────────────────────────────────────

#[test]
fn unblocked_identity_passes_through_all_entry_points() {
    let store = Arc::new(MeshDirectiveStore::new());
    let gate = Arc::new(MeshSecurityGate::new(store.clone()));
    let mut gated = MeshGatedCompanionSession::new(FakeSession::new("alice"), gate);

    assert_eq!(gated.send("hello").unwrap(), "echo: hello");
    assert_eq!(gated.agent("do it").unwrap(), "agent: do it");
    let chunks: Vec<String> = gated.stream("go").unwrap().map(|r| r.unwrap()).collect();
    assert_eq!(chunks, vec!["chunk1:go".to_string(), "chunk2".to_string()]);

    // Identity / metadata pass through.
    assert_eq!(gated.session_id(), "sess-1");
    assert_eq!(gated.identity_id(), "alice");
    assert_eq!(gated.interface(), InterfaceKind::Headless);
    assert_eq!(gated.history().len(), 1);
    assert_eq!(gated.get_context().identity_id, "alice");
    assert!(gated.refresh_context().is_ok());
    assert!(gated.signal_feedback(true, None).is_ok());
}

#[test]
fn blocked_identity_is_denied_on_send_stream_agent() {
    let store = Arc::new(MeshDirectiveStore::new());
    block(&store, "mallory");
    let gate = Arc::new(MeshSecurityGate::new(store.clone()));
    let mut gated = MeshGatedCompanionSession::new(FakeSession::new("mallory"), gate);

    match gated.send("hello") {
        Err(MeshGatedError::Blocked(e)) => {
            assert_eq!(e.blocked_id, "mallory");
            assert_eq!(e.reason, "blocked by mesh");
        }
        other => panic!("expected Blocked, got {other:?}"),
    }
    assert!(matches!(gated.agent("x"), Err(MeshGatedError::Blocked(_))));
    assert!(matches!(gated.stream("x"), Err(MeshGatedError::Blocked(_))));
}

#[test]
fn blocked_identity_still_reads_own_state() {
    // Gating metadata would prevent a blocked user from seeing their own state —
    // the decorator deliberately leaves those unguarded.
    let store = Arc::new(MeshDirectiveStore::new());
    block(&store, "mallory");
    let gate = Arc::new(MeshSecurityGate::new(store.clone()));
    let mut gated = MeshGatedCompanionSession::new(FakeSession::new("mallory"), gate);

    assert_eq!(gated.identity_id(), "mallory");
    assert_eq!(gated.get_context().identity_id, "mallory");
    assert_eq!(gated.history().len(), 1);
    assert!(gated.refresh_context().is_ok());
    assert!(gated.signal_feedback(false, Some("note")).is_ok());
}

#[test]
fn guarded_calls_do_not_reach_inner_when_blocked() {
    let store = Arc::new(MeshDirectiveStore::new());
    block(&store, "mallory");
    let gate = Arc::new(MeshSecurityGate::new(store.clone()));
    let mut gated = MeshGatedCompanionSession::new(FakeSession::new("mallory"), gate);

    let _ = gated.send("x");
    let _ = gated.agent("y");
    let _ = gated.stream("z");

    // None of the guarded calls should have reached the inner session.
    let inner = gated.into_inner();
    assert_eq!(inner.send_calls, 0);
    assert_eq!(inner.stream_calls, 0);
    assert_eq!(inner.agent_calls, 0);
}
