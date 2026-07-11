//! commerce_xero_test.rs
//!
//! Ports the behaviour of `CircleAI.Commerce.Integration.Xero`: per-user token
//! storage + expiry, tenant tracking with dedup, the newest-first webhook event
//! log, the static domain descriptor, and the domain adapter.

use std::cell::RefCell;

use chrono::{Duration, Utc};
use circle_ai::commerce_xero::{
    CommerceIntegrationXeroCompanionAdapter, CommerceIntegrationXeroDomainContext, IXeroBoard,
    InMemoryXeroBoard, XeroTenant, XeroTokens, XeroWebhookEvent,
};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};

#[test]
fn store_and_get_tokens() {
    let board = InMemoryXeroBoard::new();
    assert!(board.get_tokens("u1").is_none());
    let expires = Utc::now() + Duration::hours(1);
    board.store_tokens("u1", XeroTokens::new("at", "rt", expires, "id"));
    assert_eq!(board.get_tokens("u1").unwrap().access_token, "at");
}

#[test]
fn tokens_expired_logic() {
    let board = InMemoryXeroBoard::new();
    // Missing user -> expired.
    assert!(board.tokens_expired("missing", Utc::now()));

    let expires = Utc::now() + Duration::hours(1);
    board.store_tokens("u1", XeroTokens::new("at", "rt", expires, "id"));
    assert!(!board.tokens_expired("u1", Utc::now()));
    // now >= expiry -> expired.
    assert!(board.tokens_expired("u1", expires + Duration::seconds(1)));
    assert!(board.tokens_expired("u1", expires));
}

#[test]
fn add_tenant_deduplicates_by_id() {
    let board = InMemoryXeroBoard::new();
    board.add_tenant("u1", XeroTenant::new("t1", "Org One", "ORGANISATION"));
    board.add_tenant("u1", XeroTenant::new("t1", "Org One (dup)", "ORGANISATION"));
    board.add_tenant("u1", XeroTenant::new("t2", "Org Two", "ORGANISATION"));

    let tenants = board.tenants_for("u1");
    assert_eq!(tenants.len(), 2);
    // Original (first-seen) is kept for the duplicated id.
    assert_eq!(tenants[0].tenant_name, "Org One");
    assert!(board.tenants_for("other").is_empty());
}

#[test]
fn recent_events_newest_first_and_limited() {
    let board = InMemoryXeroBoard::new();
    board.record_webhook(XeroWebhookEvent::new("t1", "INVOICE", "r-old", Utc::now() - Duration::hours(2)));
    board.record_webhook(XeroWebhookEvent::new("t1", "INVOICE", "r-new", Utc::now()));
    board.record_webhook(XeroWebhookEvent::new("t1", "CONTACT", "r-mid", Utc::now() - Duration::hours(1)));

    let ids: Vec<String> = board.recent_events(2).into_iter().map(|e| e.resource_id).collect();
    assert_eq!(ids, vec!["r-new", "r-mid"]);
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(CommerceIntegrationXeroDomainContext::SYSTEM_PROMPT_SNIPPET
        .starts_with("[DOMAIN: Commerce.Integration.Xero]"));
    assert!(CommerceIntegrationXeroDomainContext::SYSTEM_PROMPT_SNIPPET.contains("chart of accounts"));
    assert_eq!(
        CommerceIntegrationXeroDomainContext::compliance_flags(),
        vec!["SARS", "IFRS", "Xero_Data_Standards", "POPIA"]
    );
    assert_eq!(
        CommerceIntegrationXeroDomainContext::suggested_tools(),
        vec!["xero_api", "spreadsheet", "document_editor"]
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
        "sess-xero"
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
    let mut adapter = CommerceIntegrationXeroCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(
        seen,
        format!("{}\n\nhelp", CommerceIntegrationXeroDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = CommerceIntegrationXeroCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-xero");
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = CommerceIntegrationXeroCompanionAdapter::new(RecordingSession::new());
    adapter.explain_xero_code("610").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(CommerceIntegrationXeroDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("Explain Xero transaction code '610'"));
}
