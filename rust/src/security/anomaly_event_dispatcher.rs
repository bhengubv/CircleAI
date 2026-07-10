//! anomaly_event_dispatcher.rs
//!
//! Safe-by-default composer around [`ISecurityWatchdog`] — Rust port of
//! `IAnomalyEventDispatcher.cs`.
//!
//! The bare `on_anomaly_detected` path requires the caller to verify the signal
//! (origin trust, schema, threshold gate) and dedupe (by id) themselves. The
//! dispatcher folds verify → dedup → invoke into one call so a production
//! consumer cannot accidentally accept an unverified or replayed signal. No
//! error is returned on rejection — the caller branches on the outcome.

use std::collections::HashSet;
use std::sync::{Arc, Mutex};

use uuid::Uuid;

use super::anomaly_signal::AnomalySignal;
use super::security_checkpoint::SecurityCheckpoint;
use super::security_response::SecurityResponse;
use super::security_watchdog::ISecurityWatchdog;

/// Outcome of a [`IAnomalyEventDispatcher::verify_and_dispatch`] call.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
#[repr(u8)]
pub enum AnomalyDispatchOutcome {
    /// Signal accepted; watchdog was invoked.
    Dispatched = 0,
    /// Signal id was already seen — deduped silently.
    Duplicate = 1,
    /// Confidence was below the configured threshold — ignored.
    BelowThreshold = 2,
    /// Signal failed the origin/signature verification step.
    Unverified = 3,
    /// Cancellation was requested before dispatch.
    Cancelled = 4,
}

/// Result of a dispatch attempt.
#[derive(Debug, Clone)]
pub struct AnomalyDispatchResult {
    /// What the dispatcher did with the signal.
    pub outcome: AnomalyDispatchOutcome,
    /// The watchdog response, when `outcome` is
    /// [`AnomalyDispatchOutcome::Dispatched`]. `None` otherwise.
    pub response: Option<SecurityResponse>,
}

impl AnomalyDispatchResult {
    fn new(outcome: AnomalyDispatchOutcome, response: Option<SecurityResponse>) -> Self {
        Self { outcome, response }
    }
}

/// Verify, dedup, and dispatch an [`AnomalySignal`] in a single call.
pub trait IAnomalyEventDispatcher: Send + Sync {
    /// Runs the verification pipeline configured on this dispatcher (confidence
    /// threshold, id dedup) and, when all gates pass, hands the signal to the
    /// wrapped [`ISecurityWatchdog`]. Returns the dispatch outcome along with
    /// the watchdog response if invocation was reached.
    ///
    /// `cancelled` mirrors the C# `CancellationToken.IsCancellationRequested`
    /// pre-dispatch check.
    fn verify_and_dispatch(
        &self,
        signal: &AnomalySignal,
        checkpoint: Option<&SecurityCheckpoint>,
        cancelled: bool,
    ) -> AnomalyDispatchResult;
}

/// Default in-process dispatcher. Threshold-gated, id-deduped, no signature
/// verification (compose your own signature-verifying wrapper when running over
/// an untrusted transport).
pub struct DefaultAnomalyEventDispatcher {
    watchdog: Arc<dyn ISecurityWatchdog>,
    minimum_confidence: f32,
    seen: Mutex<HashSet<Uuid>>,
}

impl DefaultAnomalyEventDispatcher {
    /// Creates the dispatcher with the default minimum confidence (0.30, which
    /// matches the default watchdog rotation threshold so signals that would
    /// have been no-ops aren't even dispatched).
    pub fn new(watchdog: Arc<dyn ISecurityWatchdog>) -> Self {
        Self::with_minimum_confidence(watchdog, 0.30)
    }

    /// Creates the dispatcher, dropping signals whose confidence is below
    /// `minimum_confidence` (clamped to `[0, 1]`).
    pub fn with_minimum_confidence(
        watchdog: Arc<dyn ISecurityWatchdog>,
        minimum_confidence: f32,
    ) -> Self {
        Self {
            watchdog,
            minimum_confidence: minimum_confidence.clamp(0.0, 1.0),
            seen: Mutex::new(HashSet::new()),
        }
    }

    /// The configured minimum confidence threshold.
    pub fn minimum_confidence(&self) -> f32 {
        self.minimum_confidence
    }
}

impl IAnomalyEventDispatcher for DefaultAnomalyEventDispatcher {
    fn verify_and_dispatch(
        &self,
        signal: &AnomalySignal,
        checkpoint: Option<&SecurityCheckpoint>,
        cancelled: bool,
    ) -> AnomalyDispatchResult {
        if cancelled {
            return AnomalyDispatchResult::new(AnomalyDispatchOutcome::Cancelled, None);
        }

        if signal.confidence < self.minimum_confidence {
            return AnomalyDispatchResult::new(AnomalyDispatchOutcome::BelowThreshold, None);
        }

        {
            let mut seen = self.seen.lock().unwrap();
            if !seen.insert(signal.id) {
                return AnomalyDispatchResult::new(AnomalyDispatchOutcome::Duplicate, None);
            }
        }

        let response = self.watchdog.on_anomaly_detected(signal, checkpoint);
        AnomalyDispatchResult::new(AnomalyDispatchOutcome::Dispatched, Some(response))
    }
}
