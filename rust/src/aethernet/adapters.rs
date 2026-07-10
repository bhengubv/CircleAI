//! aethernet::adapters — the CircleAI ↔ AetherNet adapter family.
//!
//! Rust port of the bridge classes in `CircleAI.AetherNet/*.cs` that wire the
//! CircleAI Aether contracts onto the live mesh runtime boundary
//! ([`crate::aethernet::mesh_extensibility`]):
//!
//!   [`AetherNetContextAdapter`]        — `IAetherContext` over the mesh version.
//!   [`AetherNetTelemetryAdapter`]      — `IAetherTelemetry` over mesh telemetry.
//!   [`AetherNetDirectiveSink`]         — CircleAI → mesh directive forwarder.
//!   [`AetherNetInboundDirectiveBridge`]— mesh → CircleAI directive forwarder.
//!   [`CircleAiAetherNetAiProvider`]    — plugs CircleAI's brain into the mesh
//!                                        AI seat.

use std::collections::BTreeMap;
use std::sync::Arc;

use crate::aether::context::{AetherVersion, IAetherContext};
use crate::aether::events::AetherInstallLevel;
use crate::aether::intelligence::IAetherIntelligence;
use crate::aether::security_layer::{ISecurityDirectiveConsumer, SecurityDirective};
use crate::aether::telemetry::{
    IAetherTelemetry, IAetherTelemetryObserver, TelemetrySubscription,
};

use super::event_translator::{
    map_directive_kind_from_mesh, map_directive_kind_to_mesh, map_threat_level,
    map_threat_level_to_mesh, translate_network_event, translate_node_event, translate_route_event,
    translate_security_event, translate_transport_event,
};
use super::mesh_extensibility::{
    AetherNetNetworkEvent, AetherNetNodeEvent, AetherNetRouteEvent, AetherNetSecurityEvent,
    AetherNetTransportEvent, AiNetworkHealthReport, AiRouteSuggestion, AiThreatLevel,
    IAetherNetAiProvider, IAetherNetTelemetry, IAetherNetTelemetryObserver,
    IMeshSecurityDirectiveConsumer, MeshPacket, MeshSecurityDirective, MeshTelemetrySubscription,
    CURRENT_PROTOCOL_VERSION,
};

// ─────────────────────────────────────────────────────────────────────────────
// AetherNetContextAdapter
// ─────────────────────────────────────────────────────────────────────────────

/// Reports the presence and capability of AetherNet to CircleAI consumers via
/// the [`IAetherContext`] contract. Install level is fixed at
/// [`AetherInstallLevel::App`] — AetherNet runs as an in-process library. Runtime
/// version is `new Version(CURRENT_PROTOCOL_VERSION, 0, 0, 0)`.
#[derive(Debug, Clone)]
pub struct AetherNetContextAdapter {
    minimum_required: Option<AetherVersion>,
    is_enabled: bool,
    runtime_version: AetherVersion,
}

impl AetherNetContextAdapter {
    /// Constructs the adapter. `minimum_required = None` treats any installed
    /// version as sufficient; `is_enabled` defaults to `true` at the call site
    /// (mirrors the C# default parameter).
    pub fn new(minimum_required: Option<AetherVersion>, is_enabled: bool) -> Self {
        Self {
            minimum_required,
            is_enabled,
            runtime_version: AetherVersion::full(CURRENT_PROTOCOL_VERSION, 0, 0, 0),
        }
    }
}

impl Default for AetherNetContextAdapter {
    fn default() -> Self {
        Self::new(None, true)
    }
}

impl IAetherContext for AetherNetContextAdapter {
    fn install_level(&self) -> AetherInstallLevel {
        AetherInstallLevel::App
    }

    fn runtime_version(&self) -> Option<AetherVersion> {
        Some(self.runtime_version)
    }

    fn minimum_required(&self) -> Option<AetherVersion> {
        self.minimum_required
    }

    fn is_enabled(&self) -> bool {
        self.is_enabled
    }

    // C#: `IsAvailable => true` unconditionally (live in-process runtime).
    fn is_available(&self) -> bool {
        true
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AetherNetTelemetryAdapter
// ─────────────────────────────────────────────────────────────────────────────

/// Bridges AetherNet's telemetry bus to CircleAI's [`IAetherTelemetry`] contract.
/// Each subscriber gets an independent mesh subscription, so disposal cleans up
/// exactly one downstream handle.
pub struct AetherNetTelemetryAdapter {
    mesh_telemetry: Arc<dyn IAetherNetTelemetry>,
}

impl AetherNetTelemetryAdapter {
    pub fn new(mesh_telemetry: Arc<dyn IAetherNetTelemetry>) -> Self {
        Self { mesh_telemetry }
    }
}

impl IAetherTelemetry for AetherNetTelemetryAdapter {
    fn subscribe(&self, observer: Arc<dyn IAetherTelemetryObserver>) -> TelemetrySubscription {
        let bridge: Arc<dyn IAetherNetTelemetryObserver> = Arc::new(ObserverBridge {
            target: observer,
        });
        let mesh_sub = self.mesh_telemetry.subscribe(bridge);
        // Own the mesh subscription inside our handle; dropping the CircleAI
        // handle drops the mesh subscription, unhooking exactly this observer.
        wrap_mesh_subscription(mesh_sub)
    }
}

/// Receives AetherNet events and forwards them to a CircleAI observer after type
/// translation.
struct ObserverBridge {
    target: Arc<dyn IAetherTelemetryObserver>,
}

impl IAetherNetTelemetryObserver for ObserverBridge {
    fn on_node_event(&self, e: &AetherNetNodeEvent) {
        self.target.on_node_event(&translate_node_event(e));
    }
    fn on_transport_event(&self, e: &AetherNetTransportEvent) {
        self.target.on_transport_event(&translate_transport_event(e));
    }
    fn on_route_event(&self, e: &AetherNetRouteEvent) {
        self.target.on_route_event(&translate_route_event(e));
    }
    fn on_security_event(&self, e: &AetherNetSecurityEvent) {
        self.target.on_security_event(&translate_security_event(e));
    }
    fn on_network_event(&self, e: &AetherNetNetworkEvent) {
        self.target.on_network_event(&translate_network_event(e));
    }
}

/// Wraps a mesh subscription in a CircleAI [`TelemetrySubscription`] so the outer
/// handle's lifetime governs the inner one.
fn wrap_mesh_subscription(mesh_sub: MeshTelemetrySubscription) -> TelemetrySubscription {
    use std::sync::Mutex;
    // Move the mesh sub into a closure captured by the CircleAI handle's remover.
    let holder = Arc::new(Mutex::new(Some(mesh_sub)));
    TelemetrySubscription::from_remover(move || {
        // Dropping the held mesh subscription unhooks the mesh observer.
        holder.lock().unwrap().take();
    })
}

// ─────────────────────────────────────────────────────────────────────────────
// AetherNetDirectiveSink   (CircleAI → mesh, outbound)
// ─────────────────────────────────────────────────────────────────────────────

/// Forwards CircleAI security directives to the AetherNet policy engine.
/// Implements CircleAI's [`ISecurityDirectiveConsumer`] so it can be registered
/// as a directive sink on the CircleAI side.
pub struct AetherNetDirectiveSink {
    mesh_consumer: Arc<dyn IMeshSecurityDirectiveConsumer>,
}

impl AetherNetDirectiveSink {
    pub fn new(mesh_consumer: Arc<dyn IMeshSecurityDirectiveConsumer>) -> Self {
        Self { mesh_consumer }
    }
}

impl ISecurityDirectiveConsumer for AetherNetDirectiveSink {
    fn on_directive(&self, directive: &SecurityDirective) {
        let mesh_directive = MeshSecurityDirective {
            kind: map_directive_kind_to_mesh(directive.kind),
            target_node_id: directive.target_node_id.clone(),
            trust_score_override: directive.trust_score_override,
            threat_level: map_threat_level_to_mesh(directive.threat_level),
            reason: directive.reason.clone(),
            duration: directive.duration,
            issued_at: directive.issued_at,
        };
        self.mesh_consumer.on_directive(&mesh_directive);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AetherNetInboundDirectiveBridge   (mesh → CircleAI, inbound)
// ─────────────────────────────────────────────────────────────────────────────

/// Receives mesh-side [`MeshSecurityDirective`]s and forwards them into CircleAI's
/// [`ISecurityDirectiveConsumer`] (typically a `MeshDirectiveStore`). The inverse
/// of [`AetherNetDirectiveSink`].
pub struct AetherNetInboundDirectiveBridge {
    circle_consumer: Arc<dyn ISecurityDirectiveConsumer>,
}

impl AetherNetInboundDirectiveBridge {
    pub fn new(circle_consumer: Arc<dyn ISecurityDirectiveConsumer>) -> Self {
        Self { circle_consumer }
    }
}

impl IMeshSecurityDirectiveConsumer for AetherNetInboundDirectiveBridge {
    fn on_directive(&self, mesh_directive: &MeshSecurityDirective) {
        let circle_directive = SecurityDirective::new(
            map_directive_kind_from_mesh(mesh_directive.kind),
            mesh_directive.target_node_id.clone(),
            mesh_directive.trust_score_override,
            map_threat_level(mesh_directive.threat_level),
            mesh_directive.reason.clone(),
            mesh_directive.duration,
            mesh_directive.issued_at,
        );
        self.circle_consumer.on_directive(&circle_directive);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CircleAiAetherNetAiProvider
// ─────────────────────────────────────────────────────────────────────────────

/// Bridges CircleAI's [`IAetherIntelligence`] to AetherNet's AI-provider seat
/// ([`IAetherNetAiProvider`]). The mesh routing layer asks for route advice,
/// threat assessments, and network health; each call forwards to CircleAI's
/// intelligence surface and translates the result. Signals CircleAI does not yet
/// produce (transport biases, structured route suggestions beyond the path)
/// return sensible defaults so the mesh falls back to its own logic.
pub struct CircleAiAetherNetAiProvider {
    intelligence: Arc<dyn IAetherIntelligence>,
}

impl CircleAiAetherNetAiProvider {
    pub fn new(intelligence: Arc<dyn IAetherIntelligence>) -> Self {
        Self { intelligence }
    }

    /// AetherNet's `AiThreatLevel` has only 4 values (None..High). CircleAI's
    /// `AetherThreatLevel` has Critical; fold Critical → High because that's the
    /// strongest signal the AI seat can carry.
    fn map_to_mesh_threat_level(
        l: crate::aether::events::AetherThreatLevel,
    ) -> AiThreatLevel {
        use crate::aether::events::AetherThreatLevel as L;
        match l {
            L::None => AiThreatLevel::None,
            L::Low => AiThreatLevel::Low,
            L::Medium => AiThreatLevel::Medium,
            L::High => AiThreatLevel::High,
            L::Critical => AiThreatLevel::High,
        }
    }
}

impl IAetherNetAiProvider for CircleAiAetherNetAiProvider {
    fn is_available(&self) -> bool {
        true
    }

    fn suggest_routes(
        &self,
        destination_uhid: &str,
        _payload_bytes: i32,
    ) -> Vec<AiRouteSuggestion> {
        if destination_uhid.trim().is_empty() {
            return Vec::new();
        }
        let advice = self.intelligence.get_routing_advice(destination_uhid);
        if advice.recommended_path.is_empty() {
            return Vec::new();
        }
        vec![AiRouteSuggestion {
            path: advice.recommended_path,
            confidence: advice.confidence,
        }]
    }

    fn get_transport_biases(&self, _payload_bytes: i32) -> BTreeMap<String, f64> {
        // CircleAI does not yet model per-transport biases — empty tells
        // AetherNet to use its built-in selector without AI adjustment.
        BTreeMap::new()
    }

    fn assess_threat(&self, packet: &MeshPacket) -> AiThreatLevel {
        if packet.source_uhid.trim().is_empty() {
            return AiThreatLevel::None;
        }
        let assessment = self.intelligence.assess_threat(&packet.source_uhid);
        Self::map_to_mesh_threat_level(assessment.level)
    }

    fn get_network_health(&self) -> AiNetworkHealthReport {
        let health = self.intelligence.get_network_health();
        AiNetworkHealthReport {
            overall_score: health.overall_score,
            trusted_node_count: health.trusted_node_count,
            suspicious_node_count: health.suspicious_node_count,
            summary: health.summary,
            generated_at: health.generated_at,
        }
    }
}
