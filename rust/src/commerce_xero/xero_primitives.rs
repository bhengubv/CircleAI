//! xero_primitives.rs
//!
//! (3.3.0) Xero integration primitives — Rust port of
//! `src/CircleAI.Commerce.Integration.Xero/XeroPrimitives.cs`: token storage,
//! tenant tracking, webhook recorder. HTTP plumbing is host-supplied.
//!
//! Tokens live in a `Mutex<HashMap>` (C# `ConcurrentDictionary`); the per-user
//! tenant lists and the global event log live under a single `Mutex` (the C#
//! `object _lock`), which also guards the tenant `ConcurrentDictionary`'s inner
//! `List` mutation. `RecentEvents` reproduces
//! `OrderByDescending(e => e.AtUtc)` (stable).

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (3.3.0) A stored OAuth token set.
///
/// Mirrors `sealed record XeroTokens(string AccessToken, string RefreshToken,
/// DateTimeOffset ExpiresAtUtc, string IdToken)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct XeroTokens {
    pub access_token: String,
    pub refresh_token: String,
    pub expires_at_utc: DateTime<Utc>,
    pub id_token: String,
}

impl XeroTokens {
    /// Constructs a token set, mirroring the positional C# record constructor.
    pub fn new(
        access_token: impl Into<String>,
        refresh_token: impl Into<String>,
        expires_at_utc: DateTime<Utc>,
        id_token: impl Into<String>,
    ) -> Self {
        Self {
            access_token: access_token.into(),
            refresh_token: refresh_token.into(),
            expires_at_utc,
            id_token: id_token.into(),
        }
    }
}

/// (3.3.0) A connected Xero tenant.
///
/// Mirrors `sealed record XeroTenant(string TenantId, string TenantName,
/// string TenantType)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct XeroTenant {
    pub tenant_id: String,
    pub tenant_name: String,
    pub tenant_type: String,
}

impl XeroTenant {
    /// Constructs a tenant, mirroring the positional C# record constructor.
    pub fn new(
        tenant_id: impl Into<String>,
        tenant_name: impl Into<String>,
        tenant_type: impl Into<String>,
    ) -> Self {
        Self {
            tenant_id: tenant_id.into(),
            tenant_name: tenant_name.into(),
            tenant_type: tenant_type.into(),
        }
    }
}

/// (3.3.0) A Xero webhook event.
///
/// Mirrors `sealed record XeroWebhookEvent(string TenantId, string ResourceType,
/// string ResourceId, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct XeroWebhookEvent {
    pub tenant_id: String,
    pub resource_type: String,
    pub resource_id: String,
    pub at_utc: DateTime<Utc>,
}

impl XeroWebhookEvent {
    /// Constructs an event, mirroring the positional C# record constructor.
    pub fn new(
        tenant_id: impl Into<String>,
        resource_type: impl Into<String>,
        resource_id: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            tenant_id: tenant_id.into(),
            resource_type: resource_type.into(),
            resource_id: resource_id.into(),
            at_utc,
        }
    }
}

/// (3.3.0) The Xero board contract.
///
/// Mirrors `interface IXeroBoard`.
pub trait IXeroBoard {
    /// Stores (or overwrites) a user's tokens.
    fn store_tokens(&self, user_id: &str, t: XeroTokens);
    /// Looks up a user's tokens.
    fn get_tokens(&self, user_id: &str) -> Option<XeroTokens>;
    /// Whether a user's tokens are missing or expired at `now`.
    fn tokens_expired(&self, user_id: &str, now: DateTime<Utc>) -> bool;
    /// Adds a tenant for a user (deduplicated by `tenant_id`).
    fn add_tenant(&self, user_id: &str, t: XeroTenant);
    /// Tenants connected for a user.
    fn tenants_for(&self, user_id: &str) -> Vec<XeroTenant>;
    /// Records a webhook event.
    fn record_webhook(&self, e: XeroWebhookEvent);
    /// Up to `limit` most-recent events, newest-first.
    fn recent_events(&self, limit: usize) -> Vec<XeroWebhookEvent>;
}

/// The mutable state guarded by the single lock (C# `object _lock`).
#[derive(Default)]
struct XeroState {
    tenants: HashMap<String, Vec<XeroTenant>>,
    events: Vec<XeroWebhookEvent>,
}

/// (3.3.0) In-memory [`IXeroBoard`].
pub struct InMemoryXeroBoard {
    tokens: Mutex<HashMap<String, XeroTokens>>,
    state: Mutex<XeroState>,
}

impl InMemoryXeroBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            tokens: Mutex::new(HashMap::new()),
            state: Mutex::new(XeroState::default()),
        }
    }
}

impl Default for InMemoryXeroBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IXeroBoard for InMemoryXeroBoard {
    fn store_tokens(&self, user_id: &str, t: XeroTokens) {
        self.tokens.lock().unwrap().insert(user_id.to_string(), t);
    }

    fn get_tokens(&self, user_id: &str) -> Option<XeroTokens> {
        self.tokens.lock().unwrap().get(user_id).cloned()
    }

    fn tokens_expired(&self, user_id: &str, now: DateTime<Utc>) -> bool {
        match self.tokens.lock().unwrap().get(user_id) {
            None => true,
            Some(t) => now >= t.expires_at_utc,
        }
    }

    fn add_tenant(&self, user_id: &str, t: XeroTenant) {
        let mut state = self.state.lock().unwrap();
        let list = state.tenants.entry(user_id.to_string()).or_default();
        if !list.iter().any(|x| x.tenant_id == t.tenant_id) {
            list.push(t);
        }
    }

    fn tenants_for(&self, user_id: &str) -> Vec<XeroTenant> {
        self.state
            .lock()
            .unwrap()
            .tenants
            .get(user_id)
            .cloned()
            .unwrap_or_default()
    }

    fn record_webhook(&self, e: XeroWebhookEvent) {
        self.state.lock().unwrap().events.push(e);
    }

    fn recent_events(&self, limit: usize) -> Vec<XeroWebhookEvent> {
        let mut out: Vec<XeroWebhookEvent> = self.state.lock().unwrap().events.clone();
        out.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        out.truncate(limit);
        out
    }
}

/// The default `recent_events` limit in the C# `RecentEvents(int limit = 20)`.
pub const DEFAULT_EVENT_LIMIT: usize = 20;
