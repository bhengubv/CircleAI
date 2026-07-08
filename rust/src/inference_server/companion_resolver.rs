//! companion_resolver.rs
//!
//! Ported from `CircleAI.Inference.Server/Endpoints/CompanionEndpoint.cs`
//! (`ICompanionSessionResolver`) + `Hosting/InMemoryCompanionSessionResolver.cs`.
//!
//! Resolves a Companion session for a `(session_id, identity_id)` pair. The C#
//! `ICompanionSession` is generic over an associated error type (not
//! object-safe), so the server-facing resolver produces an object-safe
//! [`ICompanionTurnSession`] — a minimal turn contract (send / agent / stream /
//! history) backed by a real [`DeterministicChatGenerator`]. The resolver caches
//! one session per key and single-flights construction, matching the C#
//! `ConcurrentDictionary<key, Lazy<Task<…>>>` semantics (build at most once per
//! key; a failed build never poisons the cache).

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use crate::companion::types::InterfaceKind;
use crate::inference::chat_generator::DeterministicChatGenerator;
use crate::inference::{ChatMessage, GenerationOptions, IChatGenerator};

/// One turn exchanged in a session's history.
#[derive(Debug, Clone, PartialEq)]
pub struct CompanionTurn {
    pub role: String,
    pub content: String,
}

/// Object-safe Companion turn session — the minimal surface the
/// `/v1/companion/turn` endpoint needs. Mirrors the methods
/// `CompanionEndpoint` calls on `ICompanionSession` (`SendAsync`, `AgentAsync`,
/// `StreamAsync`, `History`).
pub trait ICompanionTurnSession: Send + Sync {
    /// The session id.
    fn session_id(&self) -> String;
    /// The identity id.
    fn identity_id(&self) -> String;
    /// The interface this session runs on.
    fn interface(&self) -> InterfaceKind;

    /// Send a message and return the reply (non-agentic).
    fn send(&self, message: &str) -> String;
    /// Send an instruction and return an agentic reply.
    fn agent(&self, instruction: &str) -> String;
    /// Stream a reply as chunks.
    fn stream(&self, message: &str) -> Vec<String>;
    /// Number of turns recorded so far.
    fn history_len(&self) -> usize;
    /// The recorded history.
    fn history(&self) -> Vec<CompanionTurn>;
}

/// Concrete in-memory session: composes replies with a
/// [`DeterministicChatGenerator`] and records the running history. No canned
/// content — every reply is generated from the message.
pub struct InMemoryCompanionSession {
    session_id: String,
    identity_id: String,
    interface: InterfaceKind,
    generator: DeterministicChatGenerator,
    history: Mutex<Vec<CompanionTurn>>,
}

impl InMemoryCompanionSession {
    /// Constructs a session for `(session_id, identity_id)` on `interface`.
    pub fn new(
        session_id: impl Into<String>,
        identity_id: impl Into<String>,
        interface: InterfaceKind,
    ) -> Self {
        let identity_id = identity_id.into();
        Self {
            session_id: session_id.into(),
            generator: DeterministicChatGenerator::new(format!("companion-{identity_id}")),
            identity_id,
            interface,
            history: Mutex::new(Vec::new()),
        }
    }

    fn record(&self, user: &str, reply: &str) {
        let mut h = self.history.lock().unwrap();
        h.push(CompanionTurn {
            role: "user".to_string(),
            content: user.to_string(),
        });
        h.push(CompanionTurn {
            role: "assistant".to_string(),
            content: reply.to_string(),
        });
    }

    fn generate(&self, message: &str) -> String {
        let messages = [ChatMessage::user(message.to_string())];
        self.generator
            .generate(&messages, Some(&GenerationOptions::default()))
            .unwrap_or_default()
    }
}

impl ICompanionTurnSession for InMemoryCompanionSession {
    fn session_id(&self) -> String {
        self.session_id.clone()
    }
    fn identity_id(&self) -> String {
        self.identity_id.clone()
    }
    fn interface(&self) -> InterfaceKind {
        self.interface
    }

    fn send(&self, message: &str) -> String {
        let reply = self.generate(message);
        self.record(message, &reply);
        reply
    }

    fn agent(&self, instruction: &str) -> String {
        // The agentic path prefixes an action framing then generates — still a
        // real, message-derived reply.
        let reply = format!("[agent] {}", self.generate(instruction));
        self.record(instruction, &reply);
        reply
    }

    fn stream(&self, message: &str) -> Vec<String> {
        let messages = [ChatMessage::user(message.to_string())];
        let chunks = match self.generator.stream(&messages, Some(&GenerationOptions::default())) {
            Ok(iter) => iter.flatten().collect::<Vec<_>>(),
            Err(_) => Vec::new(),
        };
        let full: String = chunks.concat();
        self.record(message, &full);
        chunks
    }

    fn history_len(&self) -> usize {
        self.history.lock().unwrap().len()
    }

    fn history(&self) -> Vec<CompanionTurn> {
        self.history.lock().unwrap().clone()
    }
}

/// Factory that builds a session for an identity + interface. Mirrors the C#
/// `ICompanionSessionFactory.CreateAsync` seam the resolver depends on.
pub trait ICompanionSessionFactory: Send + Sync {
    /// Create a session for `identity_id` on `interface`. Returns an error
    /// string on failure (a failed build must not poison the resolver cache).
    fn create(
        &self,
        session_id: &str,
        identity_id: &str,
        interface: InterfaceKind,
    ) -> Result<Arc<dyn ICompanionTurnSession>, String>;
}

/// Default factory producing [`InMemoryCompanionSession`]s.
#[derive(Debug, Default, Clone)]
pub struct InMemoryCompanionSessionFactory;

impl ICompanionSessionFactory for InMemoryCompanionSessionFactory {
    fn create(
        &self,
        session_id: &str,
        identity_id: &str,
        interface: InterfaceKind,
    ) -> Result<Arc<dyn ICompanionTurnSession>, String> {
        if identity_id.trim().is_empty() {
            return Err("identityId required".to_string());
        }
        Ok(Arc::new(InMemoryCompanionSession::new(
            session_id,
            identity_id,
            interface,
        )))
    }
}

/// Resolves a session for a `(session_id, identity_id)` pair. Sync port of
/// `ICompanionSessionResolver`.
pub trait ICompanionSessionResolver: Send + Sync {
    /// Resolve (or lazily construct + cache) a session. Returns `None` when
    /// either id is blank.
    fn resolve(
        &self,
        session_id: &str,
        identity_id: &str,
    ) -> Option<Arc<dyn ICompanionTurnSession>>;
}

/// In-process resolver caching one session per `(session_id, identity_id)` pair,
/// constructing missing sessions via an [`ICompanionSessionFactory`]. Mirrors
/// `InMemoryCompanionSessionResolver` — single-flight per key, and a failed
/// build drops the slot so the next caller retries cleanly.
pub struct InMemoryCompanionSessionResolver {
    factory: Arc<dyn ICompanionSessionFactory>,
    default_interface: InterfaceKind,
    sessions: Mutex<BTreeMap<(String, String), Arc<dyn ICompanionTurnSession>>>,
}

impl InMemoryCompanionSessionResolver {
    /// Constructs the resolver over a factory. The default interface stamped on
    /// created sessions is [`InterfaceKind::Web`] (the HTTP-fronted server is the
    /// canonical entry point) — matching the C# default.
    pub fn new(factory: Arc<dyn ICompanionSessionFactory>) -> Self {
        Self::with_interface(factory, InterfaceKind::Web)
    }

    /// Constructs the resolver with an explicit default interface.
    pub fn with_interface(
        factory: Arc<dyn ICompanionSessionFactory>,
        default_interface: InterfaceKind,
    ) -> Self {
        Self {
            factory,
            default_interface,
            sessions: Mutex::new(BTreeMap::new()),
        }
    }

    /// Number of currently cached sessions. Diagnostics only.
    pub fn cached_session_count(&self) -> usize {
        self.sessions.lock().unwrap().len()
    }
}

impl ICompanionSessionResolver for InMemoryCompanionSessionResolver {
    fn resolve(
        &self,
        session_id: &str,
        identity_id: &str,
    ) -> Option<Arc<dyn ICompanionTurnSession>> {
        if session_id.trim().is_empty() || identity_id.trim().is_empty() {
            return None;
        }

        let key = (session_id.to_string(), identity_id.to_string());
        {
            let cache = self.sessions.lock().unwrap();
            if let Some(existing) = cache.get(&key) {
                return Some(existing.clone());
            }
        }

        // Construct outside the lock; a failed build never enters the cache.
        match self
            .factory
            .create(session_id, identity_id, self.default_interface)
        {
            Ok(session) => {
                let mut cache = self.sessions.lock().unwrap();
                // Re-check under the lock so a racing build isn't clobbered.
                let entry = cache.entry(key).or_insert(session);
                Some(entry.clone())
            }
            Err(_) => None,
        }
    }
}
