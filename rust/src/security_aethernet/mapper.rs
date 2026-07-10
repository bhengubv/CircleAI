//! security_aethernet::mapper — Rust port of `CircleAI.Security.AetherNet/AetherMapper.cs`.
//!
//! Static translation helpers between the Aether-specific types
//! ([`crate::aether`]) and the transport-agnostic Peer types
//! ([`crate::security`]). Every mapping is an explicit match with the same
//! default arms as the C# switch expressions.

use crate::aether::events::{AetherSecurityEventKind, AetherThreatLevel};
use crate::aether::security_layer::SecurityDirectiveKind;
use crate::security::{PeerDirectiveKind, PeerSecurityEventKind, PeerThreatLevel};

/// `AetherSecurityEventKind` → `PeerSecurityEventKind`. Unknown kinds fold to
/// [`PeerSecurityEventKind::Unknown`].
pub fn to_peer_event_kind(kind: AetherSecurityEventKind) -> PeerSecurityEventKind {
    match kind {
        AetherSecurityEventKind::NodeAuthAttempt => PeerSecurityEventKind::AuthAttempt,
        AetherSecurityEventKind::RoutingAnomaly => PeerSecurityEventKind::RoutingAnomaly,
        AetherSecurityEventKind::NodeBehaviourChange => PeerSecurityEventKind::BehaviourChange,
        AetherSecurityEventKind::EncryptionEvent => PeerSecurityEventKind::EncryptionEvent,
        AetherSecurityEventKind::IntrusionSignal => PeerSecurityEventKind::IntrusionSignal,
        AetherSecurityEventKind::PrivilegeAttempt => PeerSecurityEventKind::PrivilegeAttempt,
    }
}

/// `AetherThreatLevel` → `PeerThreatLevel`.
pub fn to_peer_threat_level(level: AetherThreatLevel) -> PeerThreatLevel {
    match level {
        AetherThreatLevel::None => PeerThreatLevel::None,
        AetherThreatLevel::Low => PeerThreatLevel::Low,
        AetherThreatLevel::Medium => PeerThreatLevel::Medium,
        AetherThreatLevel::High => PeerThreatLevel::High,
        AetherThreatLevel::Critical => PeerThreatLevel::Critical,
    }
}

/// `PeerThreatLevel` → `AetherThreatLevel`.
pub fn to_aether_threat_level(level: PeerThreatLevel) -> AetherThreatLevel {
    match level {
        PeerThreatLevel::None => AetherThreatLevel::None,
        PeerThreatLevel::Low => AetherThreatLevel::Low,
        PeerThreatLevel::Medium => AetherThreatLevel::Medium,
        PeerThreatLevel::High => AetherThreatLevel::High,
        PeerThreatLevel::Critical => AetherThreatLevel::Critical,
    }
}

/// `PeerDirectiveKind` → `SecurityDirectiveKind`. The Peer set has no
/// `UpdateNodeTrust`/`RequestReauth`; the C# default arm maps anything unmatched
/// to [`SecurityDirectiveKind::ElevateMonitoring`].
pub fn to_security_directive_kind(kind: PeerDirectiveKind) -> SecurityDirectiveKind {
    match kind {
        PeerDirectiveKind::ElevateMonitoring => SecurityDirectiveKind::ElevateMonitoring,
        PeerDirectiveKind::AvoidNode => SecurityDirectiveKind::AvoidNode,
        PeerDirectiveKind::QuarantineNode => SecurityDirectiveKind::QuarantineNode,
        PeerDirectiveKind::ReleaseNode => SecurityDirectiveKind::ReleaseNode,
    }
}
