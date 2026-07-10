//! security_aethernet::gated_session — Rust port of
//! `CircleAI.Security.AetherNet/MeshGatedCompanionSession.cs`.
//!
//! A decorator over [`ICompanionSession`] that consults [`MeshSecurityGate`]
//! before EVERY message-producing call (`send`, `stream`, `agent`). When the
//! gate says the session's `identity_id` is blocked by an active mesh directive,
//! the decorator returns a blocked error instead of reaching the underlying
//! generator. Diagnostic / metadata calls (`get_context`, `history`,
//! `refresh_context`, `signal_feedback`) pass through unguarded — gating them
//! would stop a blocked user from even seeing their own state, which goes beyond
//! "stop the chat" into "punish".
//!
//! The decorator never modifies or impersonates the inner session; it strictly
//! adds the gate check.

use std::sync::Arc;

use crate::companion::{CompanionContext, CompanionTurn, ICompanionSession, InterfaceKind};

use super::directive_store::{MeshSecurityBlockedError, MeshSecurityGate};

/// Error surface of a [`MeshGatedCompanionSession`]: either the mesh blocked the
/// identity, or the inner session produced its own error.
#[derive(Debug)]
pub enum MeshGatedError<E> {
    /// The mesh has an active block directive against the session identity.
    Blocked(MeshSecurityBlockedError),
    /// The wrapped session failed.
    Inner(E),
}

impl<E: std::fmt::Display> std::fmt::Display for MeshGatedError<E> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            MeshGatedError::Blocked(e) => write!(f, "{e}"),
            MeshGatedError::Inner(e) => write!(f, "{e}"),
        }
    }
}

impl<E: std::error::Error + 'static> std::error::Error for MeshGatedError<E> {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            MeshGatedError::Blocked(e) => Some(e),
            MeshGatedError::Inner(e) => Some(e),
        }
    }
}

/// Wraps an inner [`ICompanionSession`] and enforces the mesh's "block this user"
/// directives via [`MeshSecurityGate`] on every message-producing call.
pub struct MeshGatedCompanionSession<S: ICompanionSession> {
    inner: S,
    gate: Arc<MeshSecurityGate>,
}

impl<S: ICompanionSession> MeshGatedCompanionSession<S> {
    pub fn new(inner: S, gate: Arc<MeshSecurityGate>) -> Self {
        Self { inner, gate }
    }

    /// Consumes the decorator, returning the wrapped session.
    pub fn into_inner(self) -> S {
        self.inner
    }

    /// Runs the gate against the session identity, mapping a block to
    /// [`MeshGatedError::Blocked`].
    fn enforce(&self) -> Result<(), MeshGatedError<S::Error>> {
        self.gate
            .enforce(self.inner.identity_id())
            .map_err(MeshGatedError::Blocked)
    }
}

impl<S> ICompanionSession for MeshGatedCompanionSession<S>
where
    S: ICompanionSession,
    S::Error: 'static,
{
    type Error = MeshGatedError<S::Error>;

    // ── Pass-through identity / properties ────────────────────────────────

    fn session_id(&self) -> &str {
        self.inner.session_id()
    }

    fn identity_id(&self) -> &str {
        self.inner.identity_id()
    }

    fn interface(&self) -> InterfaceKind {
        self.inner.interface()
    }

    fn history(&self) -> &[CompanionTurn] {
        self.inner.history()
    }

    // ── Guarded entry points ──────────────────────────────────────────────

    fn send(&mut self, message: &str) -> Result<String, Self::Error> {
        self.enforce()?;
        self.inner.send(message).map_err(MeshGatedError::Inner)
    }

    fn stream(
        &mut self,
        message: &str,
    ) -> Result<Box<dyn Iterator<Item = Result<String, Self::Error>>>, Self::Error> {
        self.enforce()?;
        let inner_iter = self.inner.stream(message).map_err(MeshGatedError::Inner)?;
        // Re-wrap each yielded item's error into our error type.
        Ok(Box::new(
            inner_iter.map(|item| item.map_err(MeshGatedError::Inner)),
        ))
    }

    fn agent(&mut self, instruction: &str) -> Result<String, Self::Error> {
        self.enforce()?;
        self.inner.agent(instruction).map_err(MeshGatedError::Inner)
    }

    // ── Unguarded pass-through ────────────────────────────────────────────

    fn get_context(&self) -> &CompanionContext {
        self.inner.get_context()
    }

    fn refresh_context(&mut self) -> Result<(), Self::Error> {
        self.inner.refresh_context().map_err(MeshGatedError::Inner)
    }

    fn signal_feedback(&mut self, positive: bool, note: Option<&str>) -> Result<(), Self::Error> {
        self.inner
            .signal_feedback(positive, note)
            .map_err(MeshGatedError::Inner)
    }
}
