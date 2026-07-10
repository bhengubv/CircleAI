//! aethernet::event_translator — Rust port of `CircleAI.AetherNet/EventTranslator.cs`.
//!
//! One-way projection of `AetherNet.Extensibility.Events.*` records into
//! `CircleAI.Aether::*` records, plus the enum re-mappings where value sets
//! differ. The translator never references mesh runtime services — it operates
//! on event records only. Every mapping is explicit, with the same fold rules
//! and default arms as the C# switch expressions.

use crate::aether::events::{
    AetherNetworkEvent, AetherNetworkEventKind, AetherNodeEvent, AetherNodeEventKind,
    AetherNodeHealth, AetherRouteEvent, AetherRouteEventKind, AetherSecurityEvent,
    AetherSecurityEventKind, AetherThreatLevel, AetherTransportEvent, AetherTransportEventKind,
    AetherTransportKind,
};
use crate::aether::security_layer::SecurityDirectiveKind;

use super::mesh_extensibility::{
    AetherNetNetworkEvent, AetherNetNetworkEventKind, AetherNetNodeEvent, AetherNetNodeEventKind,
    AetherNetNodeHealth, AetherNetRouteEvent, AetherNetRouteEventKind, AetherNetSecurityEvent,
    AetherNetSecurityEventKind, AetherNetThreatLevel, AetherNetTransportEvent,
    AetherNetTransportEventKind, AetherNetTransportKind, MeshSecurityDirectiveKind,
};

// ── Record projections ───────────────────────────────────────────────────────

pub fn translate_node_health(h: &AetherNetNodeHealth) -> AetherNodeHealth {
    AetherNodeHealth::new(h.trust_score, h.is_reachable, h.latency, h.hop_count)
}

pub fn translate_node_event(e: &AetherNetNodeEvent) -> AetherNodeEvent {
    AetherNodeEvent::new(
        e.node_id.clone(),
        map_node_kind(e.kind),
        translate_node_health(&e.health),
        e.occurred_at,
    )
}

pub fn translate_transport_event(e: &AetherNetTransportEvent) -> AetherTransportEvent {
    AetherTransportEvent::new(
        e.node_id.clone(),
        map_transport_kind(e.kind),
        map_transport(e.transport),
        e.latency,
        e.packet_loss_rate,
        e.occurred_at,
    )
}

pub fn translate_route_event(e: &AetherNetRouteEvent) -> AetherRouteEvent {
    AetherRouteEvent::new(
        e.source_node_id.clone(),
        e.destination_node_id.clone(),
        e.path.clone(),
        map_route_kind(e.kind),
        e.failure_reason.clone(),
        e.occurred_at,
    )
}

pub fn translate_security_event(e: &AetherNetSecurityEvent) -> AetherSecurityEvent {
    AetherSecurityEvent::new(
        e.node_id.clone(),
        map_security_kind(e.kind),
        map_threat_level(e.threat_level),
        e.description.clone(),
        e.metadata.clone(),
        e.occurred_at,
    )
}

pub fn translate_network_event(e: &AetherNetNetworkEvent) -> AetherNetworkEvent {
    AetherNetworkEvent::new(
        map_network_kind(e.kind),
        e.node_count,
        e.active_route_count,
        e.congestion_level,
        e.occurred_at,
    )
}

// ── Enum mappings ────────────────────────────────────────────────────────────

fn map_node_kind(k: AetherNetNodeEventKind) -> AetherNodeEventKind {
    match k {
        AetherNetNodeEventKind::Joined => AetherNodeEventKind::Joined,
        AetherNetNodeEventKind::Left => AetherNodeEventKind::Left,
        AetherNetNodeEventKind::HealthChanged => AetherNodeEventKind::HealthChanged,
    }
}

fn map_transport_kind(k: AetherNetTransportEventKind) -> AetherTransportEventKind {
    match k {
        AetherNetTransportEventKind::Selected => AetherTransportEventKind::Selected,
        AetherNetTransportEventKind::Changed => AetherTransportEventKind::Changed,
        AetherNetTransportEventKind::LatencyMeasured => AetherTransportEventKind::LatencyMeasured,
        AetherNetTransportEventKind::PacketLoss => AetherTransportEventKind::PacketLoss,
    }
}

/// AetherNet has more transports (Wi-Fi Direct, NearLink, HTTP relay); CircleAI's
/// enum is broader OS-classification. Fold related kinds exactly as the C# does:
/// WiFiDirect → WiFi, NearLink → Unknown, HttpRelay → Cellular.
fn map_transport(k: AetherNetTransportKind) -> AetherTransportKind {
    match k {
        AetherNetTransportKind::Bluetooth => AetherTransportKind::Bluetooth,
        AetherNetTransportKind::WiFi => AetherTransportKind::WiFi,
        AetherNetTransportKind::WiFiDirect => AetherTransportKind::WiFi,
        AetherNetTransportKind::LoRa => AetherTransportKind::LoRa,
        AetherNetTransportKind::Nfc => AetherTransportKind::Nfc,
        AetherNetTransportKind::NearLink => AetherTransportKind::Unknown,
        AetherNetTransportKind::HttpRelay => AetherTransportKind::Cellular,
    }
}

fn map_route_kind(k: AetherNetRouteEventKind) -> AetherRouteEventKind {
    match k {
        AetherNetRouteEventKind::Discovered => AetherRouteEventKind::Discovered,
        AetherNetRouteEventKind::Changed => AetherRouteEventKind::Changed,
        AetherNetRouteEventKind::Failed => AetherRouteEventKind::Failed,
    }
}

fn map_security_kind(k: AetherNetSecurityEventKind) -> AetherSecurityEventKind {
    match k {
        AetherNetSecurityEventKind::NodeAuthAttempt => AetherSecurityEventKind::NodeAuthAttempt,
        AetherNetSecurityEventKind::RoutingAnomaly => AetherSecurityEventKind::RoutingAnomaly,
        AetherNetSecurityEventKind::NodeBehaviourChange => {
            AetherSecurityEventKind::NodeBehaviourChange
        }
        AetherNetSecurityEventKind::EncryptionEvent => AetherSecurityEventKind::EncryptionEvent,
        AetherNetSecurityEventKind::IntrusionSignal => AetherSecurityEventKind::IntrusionSignal,
        AetherNetSecurityEventKind::PrivilegeAttempt => AetherSecurityEventKind::PrivilegeAttempt,
    }
}

fn map_network_kind(k: AetherNetNetworkEventKind) -> AetherNetworkEventKind {
    match k {
        AetherNetNetworkEventKind::TopologyChanged => AetherNetworkEventKind::TopologyChanged,
        AetherNetNetworkEventKind::CongestionDetected => AetherNetworkEventKind::CongestionDetected,
        AetherNetNetworkEventKind::PartitionDetected => AetherNetworkEventKind::PartitionDetected,
    }
}

/// Mesh → CircleAI threat level.
pub fn map_threat_level(l: AetherNetThreatLevel) -> AetherThreatLevel {
    match l {
        AetherNetThreatLevel::None => AetherThreatLevel::None,
        AetherNetThreatLevel::Low => AetherThreatLevel::Low,
        AetherNetThreatLevel::Medium => AetherThreatLevel::Medium,
        AetherNetThreatLevel::High => AetherThreatLevel::High,
        AetherNetThreatLevel::Critical => AetherThreatLevel::Critical,
    }
}

/// CircleAI → mesh threat level (reverse direction).
pub fn map_threat_level_to_mesh(l: AetherThreatLevel) -> AetherNetThreatLevel {
    match l {
        AetherThreatLevel::None => AetherNetThreatLevel::None,
        AetherThreatLevel::Low => AetherNetThreatLevel::Low,
        AetherThreatLevel::Medium => AetherNetThreatLevel::Medium,
        AetherThreatLevel::High => AetherNetThreatLevel::High,
        AetherThreatLevel::Critical => AetherNetThreatLevel::Critical,
    }
}

/// CircleAI → mesh directive kind (outbound, for `AetherNetDirectiveSink`).
pub fn map_directive_kind_to_mesh(k: SecurityDirectiveKind) -> MeshSecurityDirectiveKind {
    match k {
        SecurityDirectiveKind::UpdateNodeTrust => MeshSecurityDirectiveKind::UpdateNodeTrust,
        SecurityDirectiveKind::AvoidNode => MeshSecurityDirectiveKind::AvoidNode,
        SecurityDirectiveKind::QuarantineNode => MeshSecurityDirectiveKind::QuarantineNode,
        SecurityDirectiveKind::ReleaseNode => MeshSecurityDirectiveKind::ReleaseNode,
        SecurityDirectiveKind::RequestReauth => MeshSecurityDirectiveKind::RequestReauth,
        SecurityDirectiveKind::ElevateMonitoring => MeshSecurityDirectiveKind::ElevateMonitoring,
    }
}

/// Mesh → CircleAI directive kind (inbound, for `AetherNetInboundDirectiveBridge`).
pub fn map_directive_kind_from_mesh(k: MeshSecurityDirectiveKind) -> SecurityDirectiveKind {
    match k {
        MeshSecurityDirectiveKind::UpdateNodeTrust => SecurityDirectiveKind::UpdateNodeTrust,
        MeshSecurityDirectiveKind::AvoidNode => SecurityDirectiveKind::AvoidNode,
        MeshSecurityDirectiveKind::QuarantineNode => SecurityDirectiveKind::QuarantineNode,
        MeshSecurityDirectiveKind::ReleaseNode => SecurityDirectiveKind::ReleaseNode,
        MeshSecurityDirectiveKind::RequestReauth => SecurityDirectiveKind::RequestReauth,
        MeshSecurityDirectiveKind::ElevateMonitoring => SecurityDirectiveKind::ElevateMonitoring,
    }
}
