//! session_factory.rs
//!
//! `ICompanionSessionFactory` + `CompanionSessionFactory` — creates
//! per-identity, per-surface [`CompanionSession`]s with backing services
//! resolved from the injected providers, so callers never construct a session
//! directly. Ported from `CompanionSessionFactory.cs`.
//!
//! The C# factory pulls every optional service from an `IServiceProvider`; the
//! Rust crate has no DI container, so the factory instead holds the concrete
//! backing services (episodic store, recall, and the per-call generator factory)
//! plus an optional [`IIdentityNameResolver`] that supplies a rich display name
//! and preferred language for the identity — the exact two fields the C# factory
//! reads from `IIdentityProvider.GetCurrentIdentityAsync`.

use std::sync::Arc;

use crate::brain::BrainError;
use crate::companion::belief::SelfBeliefStore;
use crate::companion::memory_encoder::CompanionMemoryEncoder;
use crate::companion::session::{CompanionSession, CompanionSessionOptions, EmbedderFn};
use crate::companion::types::InterfaceKind;
use crate::inference::IChatGenerator;
use crate::memory::episodic::InMemoryEpisodicStore;
use crate::memory::recall::IRecall;

/// Resolves a rich display name + preferred language for an identity — the two
/// fields the C# `IIdentityProvider` contributes to the session. Optional.
pub trait IIdentityNameResolver: Send + Sync {
    /// Returns `(display_name, preferred_language)` for the current identity, or
    /// `None` if no richer identity is available (fall back to the id).
    fn resolve(&self, identity_id: &str) -> Option<(String, Option<String>)>;
}

/// A resolved identity view, mirrored from the C# `CircleIdentity` fields the
/// factory reads.
#[derive(Debug, Clone, PartialEq)]
pub struct ResolvedIdentity {
    pub display_name: String,
    pub preferred_language: Option<String>,
}

/// Builds the concrete chat generator for a new session. A host wires the
/// on-device model here; the factory owns none of that plumbing.
pub type GeneratorFactory<G> = Arc<dyn Fn() -> G + Send + Sync>;

/// Contract for creating per-identity, per-surface Companion sessions.
pub trait ICompanionSessionFactory {
    /// The concrete session type produced (generic over the chat generator).
    type Session;

    /// Creates a new session for `identity_id` on the given `interface`,
    /// resolving all available backing services.
    fn create(
        &self,
        identity_id: &str,
        interface: InterfaceKind,
    ) -> Result<Self::Session, BrainError>;
}

/// Default [`ICompanionSessionFactory`]. Holds the backing services shared across
/// sessions and an optional identity-name resolver; each `create` builds a fresh
/// [`CompanionSession`].
pub struct CompanionSessionFactory<G: IChatGenerator> {
    generator_factory: GeneratorFactory<G>,
    episodic: Arc<InMemoryEpisodicStore>,
    recall: Arc<dyn IRecall>,
    identity: Option<Arc<dyn IIdentityNameResolver>>,
    encoder: Option<Arc<CompanionMemoryEncoder>>,
    beliefs: Option<Arc<SelfBeliefStore>>,
    embedder: Option<Arc<EmbedderFn>>,
    recall_top_k: usize,
    app_context: Option<String>,
    persona_hints: String,
    affect_summary: String,
}

impl<G: IChatGenerator> CompanionSessionFactory<G> {
    /// Creates a factory with the required backing services. Optional services
    /// default to none; use the `with_*` setters to attach them.
    pub fn new(
        generator_factory: GeneratorFactory<G>,
        episodic: Arc<InMemoryEpisodicStore>,
        recall: Arc<dyn IRecall>,
    ) -> Self {
        Self {
            generator_factory,
            episodic,
            recall,
            identity: None,
            encoder: None,
            beliefs: None,
            embedder: None,
            recall_top_k: 0,
            app_context: None,
            persona_hints: String::new(),
            affect_summary: String::new(),
        }
    }

    /// Attaches an identity-name resolver (the C# `IIdentityProvider`).
    pub fn with_identity(mut self, identity: Arc<dyn IIdentityNameResolver>) -> Self {
        self.identity = Some(identity);
        self
    }

    /// Attaches the background memory encoder.
    pub fn with_encoder(mut self, encoder: Arc<CompanionMemoryEncoder>) -> Self {
        self.encoder = Some(encoder);
        self
    }

    /// Attaches the user-belief store.
    pub fn with_beliefs(mut self, beliefs: Arc<SelfBeliefStore>) -> Self {
        self.beliefs = Some(beliefs);
        self
    }

    /// Attaches an embedder for associative recall.
    pub fn with_embedder(mut self, embedder: Arc<EmbedderFn>) -> Self {
        self.embedder = Some(embedder);
        self
    }

    /// Overrides the recall top-k (0 keeps the session default of 5).
    pub fn with_recall_top_k(mut self, k: usize) -> Self {
        self.recall_top_k = k;
        self
    }

    /// Stamps an app context onto persisted episodes.
    pub fn with_app_context(mut self, ctx: impl Into<String>) -> Self {
        self.app_context = Some(ctx.into());
        self
    }

    /// Sets the static persona-hint block.
    pub fn with_persona_hints(mut self, hints: impl Into<String>) -> Self {
        self.persona_hints = hints.into();
        self
    }

    /// Sets the static affect-summary block.
    pub fn with_affect_summary(mut self, summary: impl Into<String>) -> Self {
        self.affect_summary = summary.into();
        self
    }
}

impl<G: IChatGenerator> ICompanionSessionFactory for CompanionSessionFactory<G>
where
    G::Error: 'static,
{
    type Session = CompanionSession<G>;

    fn create(
        &self,
        identity_id: &str,
        interface: InterfaceKind,
    ) -> Result<Self::Session, BrainError> {
        if identity_id.trim().is_empty() {
            return Err(BrainError::new("identityId required"));
        }

        // Resolve a richer display name / language when an identity provider is
        // wired; otherwise fall back to the id (matches the C# default).
        let (display_name, preferred_language) = match &self.identity {
            Some(resolver) => resolver
                .resolve(identity_id)
                .unwrap_or_else(|| (identity_id.to_string(), None)),
            None => (identity_id.to_string(), None),
        };

        let opts = CompanionSessionOptions {
            session_id: uuid::Uuid::new_v4().simple().to_string(),
            identity_id: identity_id.to_string(),
            interface,
            display_name,
            preferred_language,
            persona_hints: self.persona_hints.clone(),
            affect_summary: self.affect_summary.clone(),
            active_goals: Vec::new(),
            recall_top_k: self.recall_top_k,
            app_context: self.app_context.clone(),
            encoder: self.encoder.clone(),
            beliefs: self.beliefs.clone(),
            embedder: self.embedder.clone(),
        };

        CompanionSession::new(
            (self.generator_factory)(),
            self.episodic.clone(),
            self.recall.clone(),
            opts,
        )
    }
}
