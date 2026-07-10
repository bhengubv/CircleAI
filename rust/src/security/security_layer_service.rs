//! security_layer_service.rs
//!
//! Transport-agnostic AI Security Layer — Rust port of the
//! `SecurityLayerService` in `AISecurityLayerService.cs`. Full implementation of
//! [`IPeerSecurityLayer`].
//!
//! Lifecycle:
//!   `start`        → marks the layer active (recovery bookkeeping enabled).
//!   (running)      → security events arrive via `handle_peer_event`. Each event
//!                    degrades the peer's trust score; threshold evaluation
//!                    decides which `PeerDirective` (if any) to issue.
//!   `tick_recovery`→ applies passive trust recovery for the elapsed interval.
//!   `stop`         → marks the layer inactive.
//!
//! The C# reference spins a 30-second background recovery loop; the sync port
//! drives recovery explicitly via [`SecurityLayerService::tick_recovery`],
//! matching the deterministic in-memory convention (the host may drive it from
//! any scheduler).
//!
//! Directives issued (most-severe wins per event):
//!   `QuarantineNode`     trust ≤ QuarantineThreshold
//!   `AvoidNode`          trust ≤ AvoidNodeThreshold
//!   `ElevateMonitoring`  trust ≤ ElevateMonitoringThreshold
//!   `ReleaseNode`        not issued automatically — requires operator action

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use chrono::Utc;

use super::directive_publisher::{DirectivePublisher, DirectiveSubscription};
use super::node_trust_registry::NodeTrustRegistry;
use super::peer_security_types::{
    IPeerDirectiveConsumer, IPeerSecurityLayer, PeerDirective, PeerDirectiveKind,
    PeerSecurityEvent, PeerSecurityPosture, PeerThreatLevel,
};
use super::security_options::SecurityOptions;
use super::threat_detector::ThreatDetector;

/// Recommended recovery interval matching the C# background loop cadence.
pub const RECOVERY_INTERVAL_SECONDS: i64 = 30;

/// Transport-agnostic AI Security Layer. Degrades per-peer trust scores via
/// [`ThreatDetector`] and issues [`PeerDirective`] recommendations to all
/// registered [`IPeerDirectiveConsumer`] subscribers.
pub struct SecurityLayerService {
    registry: Arc<NodeTrustRegistry>,
    options: SecurityOptions,
    publisher: Arc<DirectivePublisher>,
    active: AtomicBool,
}

impl SecurityLayerService {
    /// Creates the security layer over a shared trust registry and directive
    /// publisher.
    pub fn new(
        registry: Arc<NodeTrustRegistry>,
        options: SecurityOptions,
        publisher: Arc<DirectivePublisher>,
    ) -> Self {
        Self {
            registry,
            options,
            publisher,
            active: AtomicBool::new(false),
        }
    }

    /// Whether the layer is currently marked active.
    pub fn is_active(&self) -> bool {
        self.active.load(Ordering::SeqCst)
    }

    /// Notify the security layer that a peer has left. Trust entry is preserved
    /// for historical queries; no directive is issued.
    pub fn handle_peer_left(&self, _node_id: &str) {
        // Trust entry retained for forensic queries; no action on departure.
    }

    /// Applies passive trust recovery for `elapsed`. Drive this from a scheduler
    /// on the [`RECOVERY_INTERVAL_SECONDS`] cadence to reproduce the C# loop.
    /// No-op unless the layer is active.
    pub fn tick_recovery(&self, elapsed: chrono::Duration) {
        if !self.active.load(Ordering::SeqCst) {
            return;
        }
        self.registry.apply_recovery(elapsed);
    }

    // ─── Threshold evaluation ─────────────────────────────────────────────────

    fn evaluate_thresholds(&self, node_id: &str, previous: f64, current: f64, reason: &str) {
        // Evaluate from most-severe to least; issue at most one directive.
        if previous > self.options.quarantine_threshold
            && current <= self.options.quarantine_threshold
        {
            self.issue_directive(
                PeerDirectiveKind::QuarantineNode,
                node_id,
                current,
                reason,
                PeerThreatLevel::Critical,
            );
            return;
        }

        if previous > self.options.avoid_node_threshold
            && current <= self.options.avoid_node_threshold
        {
            self.issue_directive(
                PeerDirectiveKind::AvoidNode,
                node_id,
                current,
                reason,
                PeerThreatLevel::High,
            );
            return;
        }

        if previous > self.options.elevate_monitoring_threshold
            && current <= self.options.elevate_monitoring_threshold
        {
            self.issue_directive(
                PeerDirectiveKind::ElevateMonitoring,
                node_id,
                current,
                reason,
                PeerThreatLevel::Medium,
            );
        }
    }

    fn issue_directive(
        &self,
        kind: PeerDirectiveKind,
        node_id: &str,
        trust_score: f64,
        reason: &str,
        threat_level: PeerThreatLevel,
    ) {
        self.publisher.publish(&PeerDirective {
            kind,
            target_node_id: node_id.to_string(),
            trust_score,
            threat_level,
            reason: reason.to_string(),
            duration: None, // permanent until ReleaseNode
            issued_at: Utc::now(),
        });
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    fn score_to_threat_level(score: f64) -> PeerThreatLevel {
        if score <= 0.25 {
            PeerThreatLevel::Critical
        } else if score <= 0.50 {
            PeerThreatLevel::High
        } else if score <= 0.75 {
            PeerThreatLevel::Medium
        } else if score <= 0.90 {
            PeerThreatLevel::Low
        } else {
            PeerThreatLevel::None
        }
    }
}

impl IPeerSecurityLayer for SecurityLayerService {
    fn start(&self) {
        self.active.store(true, Ordering::SeqCst);
    }

    fn stop(&self) {
        self.active.store(false, Ordering::SeqCst);
    }

    fn handle_peer_event(&self, e: &PeerSecurityEvent) {
        let degradation = ThreatDetector::compute_degradation(e);
        if degradation <= 0.0 {
            return; // PeerThreatLevel::None — no trust impact
        }

        let (previous, current) = self.registry.apply_degradation(e, degradation);
        self.evaluate_thresholds(&e.node_id, previous, current, &e.description);
    }

    fn subscribe_to_directives(
        &self,
        consumer: Arc<dyn IPeerDirectiveConsumer>,
    ) -> DirectiveSubscription {
        self.publisher.subscribe(consumer)
    }

    fn get_posture(&self) -> PeerSecurityPosture {
        let node_ids = self.registry.all_node_ids();

        let quarantined = node_ids
            .iter()
            .filter(|id| self.registry.get_trust_score(id) <= self.options.quarantine_threshold)
            .count() as i32;

        let monitored = node_ids
            .iter()
            .filter(|id| {
                let s = self.registry.get_trust_score(id);
                s <= self.options.elevate_monitoring_threshold
                    && s > self.options.quarantine_threshold
            })
            .count() as i32;

        let worst_score = if node_ids.is_empty() {
            1.0
        } else {
            node_ids
                .iter()
                .map(|id| self.registry.get_trust_score(id))
                .fold(f64::INFINITY, f64::min)
        };
        let overall_threat = Self::score_to_threat_level(worst_score);

        PeerSecurityPosture {
            overall_threat_level: overall_threat,
            quarantined_peer_count: quarantined,
            monitored_peer_count: monitored,
            is_active: self.active.load(Ordering::SeqCst),
            generated_at: Utc::now(),
        }
    }
}
