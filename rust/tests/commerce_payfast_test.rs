//! commerce_payfast_test.rs
//!
//! Ports the behaviour of `CircleAI.Commerce.Integration.PayFast`: the MD5
//! signature builder (URL-encode + trailing-`&` handling + optional passphrase),
//! ITN merchant-id verification, the newest-first webhook recorder, the static
//! domain descriptor, and the domain adapter.
//!
//! Expected signature hashes are independent MD5 reference vectors of the exact
//! pre-hash string the C# builds (`key=value&...` with the trailing `&` dropped
//! when there is no passphrase, or `...&passphrase=<enc>` appended otherwise).

use std::cell::RefCell;

use circle_ai::commerce_payfast::{
    CommerceIntegrationPayFastCompanionAdapter, CommerceIntegrationPayFastDomainContext,
    IPayFastBoard, InMemoryPayFastBoard, PayFastConfig, PayFastItnPayload,
};
use circle_ai::companion::types::{
    CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind,
};

fn cfg(passphrase: &str) -> PayFastConfig {
    PayFastConfig::new("10000100", "key123", passphrase, true)
}

fn itn(merchant_id: &str) -> PayFastItnPayload {
    PayFastItnPayload::new(merchant_id, "pay-1", "COMPLETE", 99.0, "m-1", "sig")
}

#[test]
fn signature_no_passphrase_drops_trailing_amp() {
    // "merchant_id=10000100" -> MD5.
    let board = InMemoryPayFastBoard::new(cfg(""));
    let sig = board.signature_for(&[("merchant_id".into(), "10000100".into())]);
    assert_eq!(sig, "036c31e640eea59940d54b803a3473c6");
}

#[test]
fn signature_with_passphrase_appends_it() {
    // "merchant_id=10000100&passphrase=secret" -> MD5.
    let board = InMemoryPayFastBoard::new(cfg("secret"));
    let sig = board.signature_for(&[("merchant_id".into(), "10000100".into())]);
    assert_eq!(sig, "51284aafb3831f9e43c404c13bd6491e");
}

#[test]
fn signature_url_encodes_spaces_as_plus() {
    // "name_first=John+Doe" -> MD5 (no passphrase, single field).
    let board = InMemoryPayFastBoard::new(cfg(""));
    let sig = board.signature_for(&[("name_first".into(), "John Doe".into())]);
    assert_eq!(sig, "704c0620ba5ea6e1fde520259eedeef6");
}

#[test]
fn verify_itn_matches_merchant_id() {
    let board = InMemoryPayFastBoard::new(cfg(""));
    assert_eq!(board.config().merchant_id, "10000100");
    assert!(board.verify_itn(&itn("10000100")));
    assert!(!board.verify_itn(&itn("99999999")));
}

#[test]
fn recent_webhooks_newest_first_and_limited() {
    let board = InMemoryPayFastBoard::new(cfg(""));
    for i in 0..5 {
        board.record_webhook(PayFastItnPayload::new(
            "10000100",
            format!("pay-{i}"),
            "COMPLETE",
            1.0,
            format!("m-{i}"),
            "s",
        ));
    }
    let recent = board.recent_webhooks(3);
    let ids: Vec<&str> = recent.iter().map(|w| w.payment_id.as_str()).collect();
    assert_eq!(ids, vec!["pay-4", "pay-3", "pay-2"]);
}

#[test]
fn domain_context_snippet_and_flags() {
    assert!(CommerceIntegrationPayFastDomainContext::SYSTEM_PROMPT_SNIPPET
        .starts_with("[DOMAIN: Commerce.Integration.PayFast]"));
    assert!(CommerceIntegrationPayFastDomainContext::SYSTEM_PROMPT_SNIPPET.contains("ITN"));
    assert_eq!(
        CommerceIntegrationPayFastDomainContext::compliance_flags(),
        vec!["PCI_DSS", "POPIA", "PASA", "Consumer_Protection_Act"]
    );
    assert_eq!(
        CommerceIntegrationPayFastDomainContext::suggested_tools(),
        vec!["payfast_api", "webhook_debugger", "document_editor"]
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
        "sess-pf"
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
    let mut adapter = CommerceIntegrationPayFastCompanionAdapter::new(RecordingSession::new());
    adapter.send("help").unwrap();
    let seen = adapter.inner().last_send.borrow().clone().unwrap();
    assert_eq!(
        seen,
        format!("{}\n\nhelp", CommerceIntegrationPayFastDomainContext::SYSTEM_PROMPT_SNIPPET)
    );
}

#[test]
fn adapter_metadata_passthrough() {
    let adapter = CommerceIntegrationPayFastCompanionAdapter::new(RecordingSession::new());
    assert_eq!(adapter.session_id(), "sess-pf");
    assert_eq!(adapter.history().len(), 1);
}

#[test]
fn domain_helpers_use_raw_instructions() {
    let mut adapter = CommerceIntegrationPayFastCompanionAdapter::new(RecordingSession::new());
    adapter.diagnose_itn("{...}").unwrap();
    let seen = adapter.inner().last_agent.borrow().clone().unwrap();
    assert!(!seen.starts_with(CommerceIntegrationPayFastDomainContext::SYSTEM_PROMPT_SNIPPET));
    assert!(seen.contains("{...}"));
}
