//! session_factory_test.rs
//!
//! Verifies CompanionSessionFactory: builds a session with backing services and
//! resolves a display name / preferred language from the identity resolver.
//! Mirrors the C# CompanionSessionFactory intent.

use std::sync::Arc;

use circle_ai::brain::BrainError;
use circle_ai::companion::session_factory::{
    CompanionSessionFactory, ICompanionSessionFactory, IIdentityNameResolver,
};
use circle_ai::companion::types::{ICompanionSession, InterfaceKind};
use circle_ai::inference::{GenerationOptions, IChatGenerator};
use circle_ai::memory::episodic::InMemoryEpisodicStore;
use circle_ai::memory::recall::FusedRecall;
use circle_ai::models::ChatMessage;

/// A trivial generator that echoes a fixed reply.
struct EchoGenerator;
impl IChatGenerator for EchoGenerator {
    type Error = BrainError;
    fn generate(
        &self,
        _messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<String, Self::Error> {
        Ok("ok".to_string())
    }
    fn stream(
        &self,
        _messages: &[ChatMessage],
        _opts: Option<&GenerationOptions>,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        Ok(Box::new(std::iter::once(Ok("ok".to_string()))))
    }
}

/// A resolver that maps a known id to a rich name + language.
struct FixedResolver;
impl IIdentityNameResolver for FixedResolver {
    fn resolve(&self, identity_id: &str) -> Option<(String, Option<String>)> {
        if identity_id == "u1" {
            Some(("Thabo Bhengu".to_string(), Some("zu".to_string())))
        } else {
            None
        }
    }
}

fn make_factory(
    resolver: Option<Arc<dyn IIdentityNameResolver>>,
) -> CompanionSessionFactory<EchoGenerator> {
    let episodic = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let recall = Arc::new(FusedRecall::new(episodic.clone(), None, None).expect("recall"));
    let gen_factory = Arc::new(|| EchoGenerator);
    let mut f = CompanionSessionFactory::new(gen_factory, episodic, recall);
    if let Some(r) = resolver {
        f = f.with_identity(r);
    }
    f
}

#[test]
fn create_resolves_rich_display_name() {
    let f = make_factory(Some(Arc::new(FixedResolver)));
    let session = f.create("u1", InterfaceKind::Mobile).expect("create");
    assert_eq!(session.identity_id(), "u1");
    assert_eq!(session.interface(), InterfaceKind::Mobile);
    assert_eq!(session.get_context().display_name, "Thabo Bhengu");
    assert_eq!(session.get_context().preferred_language.as_deref(), Some("zu"));
    // A session id was minted.
    assert!(!session.session_id().is_empty());
}

#[test]
fn create_falls_back_to_id_without_resolver() {
    let f = make_factory(None);
    let session = f.create("anon", InterfaceKind::Web).expect("create");
    assert_eq!(session.get_context().display_name, "anon");
    assert_eq!(session.get_context().preferred_language, None);
}

#[test]
fn create_falls_back_when_resolver_misses() {
    let f = make_factory(Some(Arc::new(FixedResolver)));
    // "other" is unknown to the resolver → falls back to the id.
    let session = f.create("other", InterfaceKind::Desktop).expect("create");
    assert_eq!(session.get_context().display_name, "other");
}

#[test]
fn create_rejects_blank_identity() {
    let f = make_factory(None);
    // CompanionSession isn't Debug, so match rather than unwrap_err().
    match f.create("  ", InterfaceKind::Mobile) {
        Ok(_) => panic!("expected blank-identity rejection"),
        Err(e) => assert!(e.message().contains("identityId required")),
    }
}

#[test]
fn created_session_is_usable() {
    let f = make_factory(None)
        .with_persona_hints("Be warm.")
        .with_recall_top_k(3);
    let mut session = f.create("u9", InterfaceKind::Headless).expect("create");
    let reply = session.send("hello").expect("send");
    assert_eq!(reply, "ok");
    assert_eq!(session.history().len(), 2);
}
