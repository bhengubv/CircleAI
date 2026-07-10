//! threat_detector.rs
//!
//! Pure static threat logic — Rust port of `ThreatDetector.cs`.
//! No state, no DI, fully testable in isolation.
//!
//! Two responsibilities:
//!   1. `compute_degradation`: how much trust a single security event should cost.
//!   2. `detect_indicators`:   which behavioural patterns are visible in a window.
//!
//! Transport-agnostic: operates on `PeerSecurityEvent` / `PeerSecurityEventKind`
//! / `PeerThreatLevel` — no dependency on any specific transport package.

use std::collections::HashSet;

use chrono::{Duration, Utc};

use super::peer_security_types::{PeerSecurityEvent, PeerSecurityEventKind, PeerThreatLevel};

/// Stateless threat analysis helpers used by
/// [`crate::security::SecurityLayerService`] and
/// [`crate::security::PeerIntelligenceService`].
pub struct ThreatDetector;

impl ThreatDetector {
    // ─── Degradation weights by event kind ───────────────────────────────────

    fn base_weight(kind: PeerSecurityEventKind) -> f64 {
        match kind {
            PeerSecurityEventKind::AuthAttempt => 0.05,
            PeerSecurityEventKind::RoutingAnomaly => 0.10,
            PeerSecurityEventKind::BehaviourChange => 0.08,
            PeerSecurityEventKind::EncryptionEvent => 0.06,
            PeerSecurityEventKind::IntrusionSignal => 0.15,
            PeerSecurityEventKind::PrivilegeAttempt => 0.12,
            PeerSecurityEventKind::ConnectionAnomaly => 0.07,
            PeerSecurityEventKind::DataExfiltration => 0.14,
            PeerSecurityEventKind::DenialOfService => 0.13,
            PeerSecurityEventKind::Unknown => 0.05,
        }
    }

    // ─── Multipliers by threat level ─────────────────────────────────────────

    fn threat_multiplier(level: PeerThreatLevel) -> f64 {
        match level {
            PeerThreatLevel::None => 0.0,
            PeerThreatLevel::Low => 0.5,
            PeerThreatLevel::Medium => 1.0,
            PeerThreatLevel::High => 2.0,
            PeerThreatLevel::Critical => 3.0,
        }
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    /// Returns the trust-score degradation amount for a security event,
    /// calculated as `base_weight(kind) * threat_multiplier(level)`.
    /// Returns `0` when [`PeerThreatLevel::None`].
    pub fn compute_degradation(e: &PeerSecurityEvent) -> f64 {
        Self::base_weight(e.kind) * Self::threat_multiplier(e.threat_level)
    }

    /// Derives human-readable threat indicator tags from a set of recent events
    /// within the given `window`. Returns an empty list when no patterns are
    /// detected.
    pub fn detect_indicators<'a, I>(recent_events: I, window: Duration) -> Vec<String>
    where
        I: IntoIterator<Item = &'a PeerSecurityEvent>,
    {
        let cutoff = Utc::now() - window;
        let windowed: Vec<&PeerSecurityEvent> = recent_events
            .into_iter()
            .filter(|e| e.occurred_at >= cutoff)
            .collect();

        if windowed.is_empty() {
            return Vec::new();
        }

        let mut indicators: Vec<String> = Vec::with_capacity(6);

        // ≥ 3 auth attempts within the window → brute-force signal
        if windowed
            .iter()
            .filter(|e| e.kind == PeerSecurityEventKind::AuthAttempt)
            .count()
            >= 3
        {
            indicators.push("repeated-auth-attempts".to_string());
        }

        // Any intrusion signal → explicit probe or exploit
        if windowed
            .iter()
            .any(|e| e.kind == PeerSecurityEventKind::IntrusionSignal)
        {
            indicators.push("intrusion-signal-detected".to_string());
        }

        // High or Critical event → severity flag
        if windowed.iter().any(|e| {
            matches!(
                e.threat_level,
                PeerThreatLevel::High | PeerThreatLevel::Critical
            )
        }) {
            indicators.push("high-severity-event".to_string());
        }

        // ≥ 3 distinct event kinds → multi-vector activity
        let distinct: HashSet<PeerSecurityEventKind> = windowed.iter().map(|e| e.kind).collect();
        if distinct.len() >= 3 {
            indicators.push("multi-vector-activity".to_string());
        }

        // Privilege escalation attempt
        if windowed
            .iter()
            .any(|e| e.kind == PeerSecurityEventKind::PrivilegeAttempt)
        {
            indicators.push("privilege-escalation-attempt".to_string());
        }

        // Data exfiltration signal
        if windowed
            .iter()
            .any(|e| e.kind == PeerSecurityEventKind::DataExfiltration)
        {
            indicators.push("data-exfiltration-signal".to_string());
        }

        indicators
    }
}
