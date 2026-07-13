//! networking_transports::grpc — Rust port of `CircleAI.Networking.Grpc`
//! (`src/CircleAI.Networking.Grpc/*.cs`).
//!
//! gRPC-channel binding of the [`crate::networking::INetworkTransport`] contract.
//! Faithful ports:
//!
//!   * [`GrpcChannelState`]        — port of the C# enum.
//!   * [`GrpcChannelDescriptor`] / [`GrpcRetryPolicy`] / [`GrpcCallSummary`] — the
//!     C# `record`s.
//!   * [`GrpcRetryPolicies`]       — the static Default / Aggressive / NoRetry
//!     policy table, values byte-identical to the C#.
//!   * [`InMemoryGrpcCallMetrics`] — channel registry + per-channel state + call
//!     log, matching the C# ordering / call-id format (`grpc-{n}`).
//!   * [`IGrpcChannel`]            — the gRPC channel dependency (trait), port of
//!     the C# `GrpcChannel`, with a working [`InMemoryGrpcChannel`].
//!   * [`GrpcNetworkTransport`]    — `INetworkTransport` over a gRPC channel;
//!     `send` returns [`crate::networking::TransportError::NotSupported`] exactly
//!     as the C# `SendAsync` throws `NotSupportedException` (gRPC calls are
//!     proto-service-specific; callers use the channel directly).

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};

/// The exact message the C# `GrpcNetworkTransport.SendAsync` throws.
pub const GRPC_SEND_NOT_SUPPORTED: &str =
    "Use the gRPC channel directly for typed proto clients. \
GrpcNetworkTransport.SendAsync is not a generic send path.";

// ─────────────────────────────────────────────────────────────────────────────
// GrpcChannelState — port of the C# enum
// ─────────────────────────────────────────────────────────────────────────────

/// Connectivity state of a gRPC channel. 1:1 with the C# `GrpcChannelState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum GrpcChannelState {
    Idle,
    Connecting,
    Ready,
    TransientFailure,
    Shutdown,
}

/// Lifecycle state of a managed gRPC connection, mirroring the connectivity states
/// a channel steps through as reconnection is driven. 1:1 with the C#
/// `GrpcConnectionState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum GrpcConnectionState {
    Idle,
    Connecting,
    Ready,
    TransientFailure,
    Shutdown,
}

// ─────────────────────────────────────────────────────────────────────────────
// Value records
// ─────────────────────────────────────────────────────────────────────────────

/// Configuration for a gRPC channel. Port of the C# `GrpcChannelDescriptor`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct GrpcChannelDescriptor {
    pub target: String,
    pub use_tls: bool,
    pub max_receive_bytes: i32,
    pub max_send_bytes: i32,
    pub keep_alive_interval: Duration,
}

impl GrpcChannelDescriptor {
    pub fn new(
        target: impl Into<String>,
        use_tls: bool,
        max_receive_bytes: i32,
        max_send_bytes: i32,
        keep_alive_interval: Duration,
    ) -> Self {
        Self {
            target: target.into(),
            use_tls,
            max_receive_bytes,
            max_send_bytes,
            keep_alive_interval,
        }
    }
}

/// A retry policy for gRPC calls. Port of the C# `GrpcRetryPolicy`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct GrpcRetryPolicy {
    pub max_attempts: i32,
    pub initial_backoff: Duration,
    pub max_backoff: Duration,
    pub multiplier: f64,
    pub retryable_status_codes: Vec<String>,
}

impl GrpcRetryPolicy {
    pub fn new(
        max_attempts: i32,
        initial_backoff: Duration,
        max_backoff: Duration,
        multiplier: f64,
        retryable_status_codes: Vec<String>,
    ) -> Self {
        Self {
            max_attempts,
            initial_backoff,
            max_backoff,
            multiplier,
            retryable_status_codes,
        }
    }

    /// Whether `status_code` is in the retryable set.
    pub fn is_retryable(&self, status_code: &str) -> bool {
        self.retryable_status_codes.iter().any(|c| c == status_code)
    }

    /// The exponential backoff for a 0-indexed `attempt`, capped at
    /// [`max_backoff`](Self::max_backoff): `initial * multiplier^attempt`. A
    /// deterministic helper on top of the ported policy fields (the wire client
    /// would consume this schedule).
    pub fn backoff_for_attempt(&self, attempt: u32) -> Duration {
        let base = self.initial_backoff.as_secs_f64();
        let scaled = base * self.multiplier.powi(attempt as i32);
        let capped = scaled.min(self.max_backoff.as_secs_f64());
        Duration::from_secs_f64(capped.max(0.0))
    }
}

/// A summary of one completed gRPC call. Port of the C# `GrpcCallSummary`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct GrpcCallSummary {
    pub method: String,
    pub attempts: i32,
    pub latency: Duration,
    pub status_code: String,
    pub at_utc: DateTime<Utc>,
}

impl GrpcCallSummary {
    pub fn new(
        method: impl Into<String>,
        attempts: i32,
        latency: Duration,
        status_code: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            method: method.into(),
            attempts,
            latency,
            status_code: status_code.into(),
            at_utc,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// GrpcRetryPolicies — port of the static policy table
// ─────────────────────────────────────────────────────────────────────────────

/// The canonical gRPC retry policies. Port of the C# static `GrpcRetryPolicies`
/// (Default / Aggressive / NoRetry), values byte-identical.
pub struct GrpcRetryPolicies;

impl GrpcRetryPolicies {
    /// 3 attempts, 100ms→2s backoff, ×2, retry on UNAVAILABLE/DEADLINE_EXCEEDED.
    pub fn default_policy() -> GrpcRetryPolicy {
        GrpcRetryPolicy::new(
            3,
            Duration::from_millis(100),
            Duration::from_secs(2),
            2.0,
            vec![
                "UNAVAILABLE".to_string(),
                "DEADLINE_EXCEEDED".to_string(),
            ],
        )
    }

    /// 6 attempts, 50ms→5s backoff, ×2, retry on
    /// UNAVAILABLE/DEADLINE_EXCEEDED/RESOURCE_EXHAUSTED.
    pub fn aggressive() -> GrpcRetryPolicy {
        GrpcRetryPolicy::new(
            6,
            Duration::from_millis(50),
            Duration::from_secs(5),
            2.0,
            vec![
                "UNAVAILABLE".to_string(),
                "DEADLINE_EXCEEDED".to_string(),
                "RESOURCE_EXHAUSTED".to_string(),
            ],
        )
    }

    /// 1 attempt, no backoff, ×1, no retryable codes.
    pub fn no_retry() -> GrpcRetryPolicy {
        GrpcRetryPolicy::new(1, Duration::ZERO, Duration::ZERO, 1.0, Vec::new())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryGrpcCallMetrics — port of the C# metrics
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory channel registry + per-channel state + call log. Port of the C#
/// `InMemoryGrpcCallMetrics`.
///
/// Matches the C#:
///   * [`state`](Self::state) defaults to `Idle` for unknown channels.
///   * [`log_call`](Self::log_call) returns a `grpc-{n}` id with a monotonic
///     per-instance counter (the C# `Interlocked.Increment`).
///   * [`recent_calls`](Self::recent_calls) returns the newest `limit` calls,
///     newest-first.
#[derive(Default)]
pub struct InMemoryGrpcCallMetrics {
    channels: Mutex<HashMap<String, GrpcChannelDescriptor>>,
    states: Mutex<HashMap<String, GrpcChannelState>>,
    calls: Mutex<Vec<GrpcCallSummary>>,
    seq: AtomicU64,
}

impl InMemoryGrpcCallMetrics {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) a channel descriptor keyed by `id`.
    pub fn register_channel(&self, id: impl Into<String>, d: GrpcChannelDescriptor) {
        self.channels.lock().unwrap().insert(id.into(), d);
    }

    /// The channel descriptor for `id`, if registered.
    pub fn get_channel(&self, id: &str) -> Option<GrpcChannelDescriptor> {
        self.channels.lock().unwrap().get(id).cloned()
    }

    /// Sets the state for channel `id`.
    pub fn set_state(&self, id: impl Into<String>, s: GrpcChannelState) {
        self.states.lock().unwrap().insert(id.into(), s);
    }

    /// The state for channel `id`; `Idle` if unknown. Mirrors `State`.
    pub fn state(&self, id: &str) -> GrpcChannelState {
        self.states
            .lock()
            .unwrap()
            .get(id)
            .copied()
            .unwrap_or(GrpcChannelState::Idle)
    }

    /// Logs a call and returns its `grpc-{n}` id (monotonic per instance). Mirrors
    /// `LogCall`.
    pub fn log_call(&self, c: GrpcCallSummary) -> String {
        self.calls.lock().unwrap().push(c);
        let n = self.seq.fetch_add(1, Ordering::SeqCst) + 1;
        format!("grpc-{n}")
    }

    /// The newest `limit` calls, newest-first. Mirrors `RecentCalls`.
    pub fn recent_calls(&self, limit: usize) -> Vec<GrpcCallSummary> {
        let mut v: Vec<GrpcCallSummary> = self.calls.lock().unwrap().clone();
        v.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        v.truncate(limit);
        v
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// GrpcReconnectPolicy — port of the C# reconnection record
// ─────────────────────────────────────────────────────────────────────────────

/// Reconnection strategy for a managed gRPC channel: how many attempts to make and
/// how to grow the backoff between them. Port of the C# `GrpcReconnectPolicy` —
/// fulfils the channel-lifecycle and reconnection promise of the transport without
/// any transport deps.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct GrpcReconnectPolicy {
    pub max_attempts: i32,
    pub initial_backoff: Duration,
    pub backoff_multiplier: f64,
    pub max_backoff: Duration,
}

impl GrpcReconnectPolicy {
    pub fn new(
        max_attempts: i32,
        initial_backoff: Duration,
        backoff_multiplier: f64,
        max_backoff: Duration,
    ) -> Self {
        Self {
            max_attempts,
            initial_backoff,
            backoff_multiplier,
            max_backoff,
        }
    }

    /// A sane default: 5 attempts, 200ms growing ×2 up to a 30s ceiling. Port of
    /// the C# `GrpcReconnectPolicy.Default`.
    pub fn default_policy() -> Self {
        Self::new(5, Duration::from_millis(200), 2.0, Duration::from_secs(30))
    }

    /// Backoff before a given 1-based `attempt`:
    /// `initial_backoff × multiplier^(attempt-1)`, capped at
    /// [`max_backoff`](Self::max_backoff). Attempt 1 returns
    /// [`initial_backoff`](Self::initial_backoff). Overflow-safe: an infinite or
    /// over-cap scaled value clamps to `max_backoff`, matching the C#
    /// `BackoffFor`.
    ///
    /// # Panics
    /// Panics when `attempt < 1` (the C# `ArgumentOutOfRangeException` — the
    /// attempt number is 1-based).
    pub fn backoff_for(&self, attempt: i32) -> Duration {
        assert!(attempt >= 1, "attempt is 1-based");
        let scaled =
            self.initial_backoff.as_millis() as f64 * self.backoff_multiplier.powi(attempt - 1);
        let cap_ms = self.max_backoff.as_millis() as f64;
        if scaled.is_infinite() || scaled > cap_ms {
            return self.max_backoff;
        }
        Duration::from_millis(scaled as u64)
    }

    /// True when the 1-based `attempt` number is still within the retry budget.
    /// Mirrors `ShouldRetry`.
    pub fn should_retry(&self, attempt: i32) -> bool {
        attempt < self.max_attempts
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// GrpcDeadline — port of the C# static deadline helpers
// ─────────────────────────────────────────────────────────────────────────────

/// Deadline math for gRPC calls: turns a relative timeout into the absolute UTC
/// instant a call must complete by, and reports remaining time against a clock.
/// Port of the C# static `GrpcDeadline`.
pub struct GrpcDeadline;

impl GrpcDeadline {
    /// Absolute deadline for a call started at `now_utc` with the given `timeout`.
    /// Mirrors `FromTimeout`. (A [`Duration`] is inherently non-negative, so the
    /// C# `timeout < TimeSpan.Zero` guard is unrepresentable here.)
    pub fn from_timeout(timeout: Duration, now_utc: DateTime<Utc>) -> DateTime<Utc> {
        // `from_std` only fails past ~292e6 years, unreachable for real timeouts;
        // the `zero()` fallback mirrors the crate's DTN idiom.
        now_utc + chrono::Duration::from_std(timeout).unwrap_or_else(|_| chrono::Duration::zero())
    }

    /// Time left before `deadline_utc`, clamped to zero once passed. Mirrors
    /// `Remaining`.
    pub fn remaining(deadline_utc: DateTime<Utc>, now_utc: DateTime<Utc>) -> Duration {
        let left = deadline_utc - now_utc;
        left.to_std().unwrap_or(Duration::ZERO)
    }

    /// True once `now_utc` has reached or passed the deadline. Mirrors `IsExpired`.
    pub fn is_expired(deadline_utc: DateTime<Utc>, now_utc: DateTime<Utc>) -> bool {
        now_utc >= deadline_utc
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IGrpcChannel — port of the C# GrpcChannel dependency
// ─────────────────────────────────────────────────────────────────────────────

/// The gRPC channel a [`GrpcNetworkTransport`] owns. Port of the C#
/// `Grpc.Net.Client.GrpcChannel`: it is created for an address, carries lifecycle
/// state, and is handed to typed proto clients. The transport does not send
/// generic bytes over it (see [`GrpcNetworkTransport::send`]).
pub trait IGrpcChannel: Send + Sync {
    /// The channel target address (the C# `ForAddress(address)`).
    fn target(&self) -> String;

    /// The current channel connectivity state.
    fn state(&self) -> GrpcChannelState;

    /// Disposes the channel (the C# `Dispose`).
    fn dispose(&self);
}

/// A working in-memory [`IGrpcChannel`]. Tracks its target + connectivity state +
/// whether it has been disposed.
pub struct InMemoryGrpcChannel {
    target: String,
    state: Mutex<GrpcChannelState>,
    disposed: AtomicBool,
}

impl InMemoryGrpcChannel {
    /// Creates a channel for `address` (the C# `GrpcChannel.ForAddress`). Starts
    /// `Idle`.
    pub fn for_address(address: impl Into<String>) -> Self {
        Self {
            target: address.into(),
            state: Mutex::new(GrpcChannelState::Idle),
            disposed: AtomicBool::new(false),
        }
    }

    /// Sets the channel connectivity state (a platform callback would drive this).
    pub fn set_state(&self, s: GrpcChannelState) {
        *self.state.lock().unwrap() = s;
    }

    /// Whether [`IGrpcChannel::dispose`] has been called.
    pub fn is_disposed(&self) -> bool {
        self.disposed.load(Ordering::SeqCst)
    }
}

impl IGrpcChannel for InMemoryGrpcChannel {
    fn target(&self) -> String {
        self.target.clone()
    }

    fn state(&self) -> GrpcChannelState {
        *self.state.lock().unwrap()
    }

    fn dispose(&self) {
        self.disposed.store(true, Ordering::SeqCst);
        *self.state.lock().unwrap() = GrpcChannelState::Shutdown;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// GrpcNetworkTransport — port of GrpcNetworkTransport.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`INetworkTransport`] backed by a gRPC channel. Port of the C#
/// `GrpcNetworkTransport`.
///
/// `is_available` reflects the running flag (set by [`start`]/[`stop`]); `send`
/// returns [`TransportError::NotSupported`] with the exact C# message — gRPC
/// streaming calls are proto-service-specific, so there is no untyped send path;
/// callers use [`GrpcNetworkTransport::channel`] directly for typed proto clients.
/// [`Drop`] disposes the underlying channel (the C# `Dispose`).
pub struct GrpcNetworkTransport {
    channel: Arc<dyn IGrpcChannel>,
    running: AtomicBool,
}

impl GrpcNetworkTransport {
    /// Builds a transport for `address` with an in-memory channel.
    pub fn for_address(address: impl Into<String>) -> Self {
        Self::new(Arc::new(InMemoryGrpcChannel::for_address(address)))
    }

    /// Builds a transport over an existing channel.
    pub fn new(channel: Arc<dyn IGrpcChannel>) -> Self {
        Self {
            channel,
            running: AtomicBool::new(false),
        }
    }

    /// The underlying channel, for typed gRPC client creation (the C# `Channel`
    /// property).
    pub fn channel(&self) -> Arc<dyn IGrpcChannel> {
        Arc::clone(&self.channel)
    }
}

impl INetworkTransport for GrpcNetworkTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::Grpc
    }

    fn is_available(&self) -> bool {
        self.running.load(Ordering::SeqCst)
    }

    fn start(&self) {
        self.running.store(true, Ordering::SeqCst);
    }

    fn stop(&self) {
        self.running.store(false, Ordering::SeqCst);
    }

    fn send(&self, _payload: &NetworkPayload) -> Result<(), TransportError> {
        // C#: throw new NotSupportedException(...). Surfaced as a typed error so the
        // caller can fall down the cascade rather than the process crashing.
        Err(TransportError::NotSupported(
            TransportKind::Grpc,
            GRPC_SEND_NOT_SUPPORTED.to_string(),
        ))
    }
}

impl Drop for GrpcNetworkTransport {
    fn drop(&mut self) {
        self.channel.dispose();
    }
}
