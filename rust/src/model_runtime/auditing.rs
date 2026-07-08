//! auditing.rs
//!
//! Port of:
//!   - `CircleAI.Core.Auditing.ICircleAIAuditLog` (+ `CircleAIAuditEntry`,
//!     `CircleAIAuditQuery`)
//!   - `CircleAI.Core.Auditing.LoggerAuditLog`
//!   - `CircleAI.Core.Auditing.NoopAuditLog`
//!   - `CircleAI.Core.Auditing.CircleAIAuditing` (ambient sink)
//!
//! Also carries the canonical `Outcomes` strings the audit `Outcome` field draws
//! from (defined alongside `CircleAIDiagnostics.Outcomes` in C#).
//!
//! In C# the audit surface is async and query returns `IAsyncEnumerable`; per the
//! crate's sync convention `record` is `fn record(...)` and `query` returns a
//! `Vec`. `RecordAsync` MUST NOT throw — implementations fail open.

use std::sync::{Arc, Mutex, RwLock};

use chrono::{DateTime, Utc};

/// Canonical outcome strings — keep in sync across all components. Mirrors
/// `CircleAIDiagnostics.Outcomes`.
pub mod outcomes {
    /// Operation completed normally.
    pub const SUCCESS: &str = "success";
    /// Operation was cancelled by the caller.
    pub const CANCELLED: &str = "cancelled";
    /// Operation failed because an external dependency was unavailable.
    pub const UNAVAILABLE: &str = "unavailable";
    /// Operation failed because of a rate-limit response.
    pub const RATE_LIMITED: &str = "rate_limited";
    /// Operation failed because of unverified input.
    pub const INVALID: &str = "invalid";
    /// Catch-all for any other failure.
    pub const ERROR: &str = "error";
}

/// An immutable audit entry emitted by the CircleAI SDK. Mirrors
/// `CircleAIAuditEntry`.
#[derive(Debug, Clone, PartialEq)]
pub struct CircleAIAuditEntry {
    /// UTC timestamp of the action (required).
    pub at: DateTime<Utc>,
    /// Canonical component name (required).
    pub component: String,
    /// Logical operation name (required).
    pub operation: String,
    /// Outcome — one of [`outcomes`] (required).
    pub outcome: String,
    /// Tenant id, when running multi-tenant.
    pub tenant_id: Option<String>,
    /// User id (UHID) when scoped to a specific user.
    pub uhid_identity_id: Option<String>,
    /// Optional correlation id.
    pub correlation_id: Option<String>,
    /// Operation duration in milliseconds.
    pub duration_ms: f64,
    /// CLR/exception type when the outcome is not "success".
    pub error_type: Option<String>,
    /// Implementation-supplied error code.
    pub error_code: Option<String>,
    /// SHA-256 (hex) of any sensitive payload — never the raw payload itself.
    pub payload_sha256_hex: Option<String>,
}

impl CircleAIAuditEntry {
    /// Construct with the required fields; optionals default to `None`/`0.0`.
    pub fn new(
        at: DateTime<Utc>,
        component: impl Into<String>,
        operation: impl Into<String>,
        outcome: impl Into<String>,
    ) -> Self {
        Self {
            at,
            component: component.into(),
            operation: operation.into(),
            outcome: outcome.into(),
            tenant_id: None,
            uhid_identity_id: None,
            correlation_id: None,
            duration_ms: 0.0,
            error_type: None,
            error_code: None,
            payload_sha256_hex: None,
        }
    }
}

/// Query filter for [`ICircleAIAuditLog::query`]. Mirrors `CircleAIAuditQuery`.
#[derive(Debug, Clone, PartialEq)]
pub struct CircleAIAuditQuery {
    pub from_utc: Option<DateTime<Utc>>,
    pub to_utc: Option<DateTime<Utc>>,
    pub component: Option<String>,
    pub tenant_id: Option<String>,
    pub uhid_identity_id: Option<String>,
    pub outcome: Option<String>,
    /// Maximum entries to return. Defaults to 1000.
    pub max_items: i32,
}

impl Default for CircleAIAuditQuery {
    fn default() -> Self {
        Self {
            from_utc: None,
            to_utc: None,
            component: None,
            tenant_id: None,
            uhid_identity_id: None,
            outcome: None,
            max_items: 1000,
        }
    }
}

/// Tamper-aware audit surface for the CircleAI SDK. Mirrors
/// `CircleAI.Core.Auditing.ICircleAIAuditLog` (sync). `record` MUST NOT panic —
/// implementations fail open.
pub trait ICircleAIAuditLog: Send + Sync {
    /// Record an audit entry. Never panics.
    fn record(&self, entry: &CircleAIAuditEntry);

    /// Query historical entries.
    fn query(&self, query: &CircleAIAuditQuery) -> Vec<CircleAIAuditEntry>;
}

/// Default [`ICircleAIAuditLog`] — silently discards every entry and returns an
/// empty query result. Mirrors `NoopAuditLog`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NoopAuditLog;

impl NoopAuditLog {
    pub fn new() -> Self {
        NoopAuditLog
    }

    /// Shared singleton instance.
    pub fn instance() -> Arc<NoopAuditLog> {
        Arc::new(NoopAuditLog)
    }
}

impl ICircleAIAuditLog for NoopAuditLog {
    fn record(&self, _entry: &CircleAIAuditEntry) {}

    fn query(&self, _query: &CircleAIAuditQuery) -> Vec<CircleAIAuditEntry> {
        Vec::new()
    }
}

/// A line sink standing in for `Microsoft.Extensions.Logging.ILogger`. Each
/// recorded entry is formatted and pushed to this callback.
pub type LogSink = dyn Fn(String) + Send + Sync;

/// [`ICircleAIAuditLog`] implementation that writes structured entries to a log
/// sink. Mirrors `LoggerAuditLog` (which logs at `Information`). `query` always
/// returns empty — reading back from a logger isn't possible at the SDK layer.
pub struct LoggerAuditLog {
    logger: Arc<LogSink>,
}

impl LoggerAuditLog {
    /// Construct with a log sink.
    pub fn new(logger: Arc<LogSink>) -> Self {
        Self { logger }
    }

    /// Convenience constructor that captures emitted lines into a shared vector —
    /// useful for tests and for the in-memory query-free logging path.
    pub fn capturing() -> (Self, Arc<Mutex<Vec<String>>>) {
        let buf = Arc::new(Mutex::new(Vec::<String>::new()));
        let buf_for_sink = Arc::clone(&buf);
        let sink: Arc<LogSink> = Arc::new(move |line: String| {
            buf_for_sink.lock().unwrap().push(line);
        });
        (Self { logger: sink }, buf)
    }
}

impl ICircleAIAuditLog for LoggerAuditLog {
    fn record(&self, entry: &CircleAIAuditEntry) {
        // Mirrors the C# named-property template, rendered to a single line.
        let line = format!(
            "CircleAI audit {}.{} {} tenant={} uhid={} corr={} duration_ms={} error={}({}) payload_sha256={} at={}",
            entry.component,
            entry.operation,
            entry.outcome,
            entry.tenant_id.as_deref().unwrap_or("-"),
            entry.uhid_identity_id.as_deref().unwrap_or("-"),
            entry.correlation_id.as_deref().unwrap_or("-"),
            entry.duration_ms,
            entry.error_type.as_deref().unwrap_or("-"),
            entry.error_code.as_deref().unwrap_or("-"),
            entry.payload_sha256_hex.as_deref().unwrap_or("-"),
            entry.at.to_rfc3339(),
        );
        (self.logger)(line);
    }

    fn query(&self, _query: &CircleAIAuditQuery) -> Vec<CircleAIAuditEntry> {
        Vec::new()
    }
}

/// Process-wide ambient access point for the audit sink. Mirrors
/// `CircleAI.Core.Auditing.CircleAIAuditing`. Initial value is [`NoopAuditLog`].
pub struct CircleAIAuditing;

static AMBIENT_AUDIT: RwLock<Option<Arc<dyn ICircleAIAuditLog>>> = RwLock::new(None);

impl CircleAIAuditing {
    /// The current ambient audit sink. Defaults to [`NoopAuditLog`].
    pub fn default_sink() -> Arc<dyn ICircleAIAuditLog> {
        let guard = AMBIENT_AUDIT.read().unwrap();
        match guard.as_ref() {
            Some(a) => Arc::clone(a),
            None => NoopAuditLog::instance(),
        }
    }

    /// Replace the ambient audit sink.
    pub fn set_default(audit: Arc<dyn ICircleAIAuditLog>) {
        *AMBIENT_AUDIT.write().unwrap() = Some(audit);
    }

    /// Restore the default to [`NoopAuditLog`]. Test helper.
    pub fn reset_to_noop() {
        *AMBIENT_AUDIT.write().unwrap() = None;
    }
}
