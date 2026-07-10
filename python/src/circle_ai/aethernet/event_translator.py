# event_translator.py
#
# Port of CircleAI.AetherNet.EventTranslator (C# — the EXACT spec).
#
# Internal one-way mapping from the AetherNet extensibility event records into
# CircleAI.Aether records. Every AetherNet event has a 1:1 CircleAI counterpart
# (designed in parallel) — pure record projection with enum re-mapping where the
# value sets differ.
#
# The translator NEVER references AetherNet runtime services; it operates on the
# event records only. Keeps the dependency graph thin.
#
# Enum folding preserved exactly from the C# switch expressions:
#   Transport:  WiFiDirect -> WiFi, NearLink -> Unknown, HttpRelay -> Cellular
#   Directive:  bidirectional 1:1 (with default-arm fallbacks)
#   ThreatLevel: bidirectional 1:1

from __future__ import annotations

from ..aether.events import (
    AetherNetworkEvent,
    AetherNetworkEventKind,
    AetherNodeEvent,
    AetherNodeEventKind,
    AetherNodeHealth,
    AetherRouteEvent,
    AetherRouteEventKind,
    AetherSecurityEvent,
    AetherSecurityEventKind,
    AetherThreatLevel,
    AetherTransportEvent,
    AetherTransportEventKind,
    AetherTransportKind,
)
from ..aether.security_layer import SecurityDirectiveKind
from .extensibility import (
    AetherNetNetworkEvent,
    AetherNetNetworkEventKind,
    AetherNetNodeEvent,
    AetherNetNodeEventKind,
    AetherNetNodeHealth,
    AetherNetRouteEvent,
    AetherNetRouteEventKind,
    AetherNetSecurityDirectiveKind,
    AetherNetSecurityEvent,
    AetherNetSecurityEventKind,
    AetherNetThreatLevel,
    AetherNetTransportEvent,
    AetherNetTransportEventKind,
    AetherNetTransportKind,
)


class EventTranslator:
    """Static one-way projection helpers from AetherNet events to CircleAI
    events, plus the bidirectional directive/threat-level mappings used by the
    directive sink and inbound bridge.
    """

    # ── Event record projections ──────────────────────────────────────────────

    @staticmethod
    def translate_node(e: AetherNetNodeEvent) -> AetherNodeEvent:
        return AetherNodeEvent(
            e.node_id,
            EventTranslator._map_node_kind(e.kind),
            EventTranslator.translate_node_health(e.health),
            e.occurred_at,
        )

    @staticmethod
    def translate_node_health(h: AetherNetNodeHealth) -> AetherNodeHealth:
        return AetherNodeHealth(h.trust_score, h.is_reachable, h.latency, h.hop_count)

    @staticmethod
    def translate_transport(e: AetherNetTransportEvent) -> AetherTransportEvent:
        return AetherTransportEvent(
            e.node_id,
            EventTranslator._map_transport_kind(e.kind),
            EventTranslator._map_transport(e.transport),
            e.latency,
            e.packet_loss_rate,
            e.occurred_at,
        )

    @staticmethod
    def translate_route(e: AetherNetRouteEvent) -> AetherRouteEvent:
        return AetherRouteEvent(
            e.source_node_id,
            e.destination_node_id,
            e.path,
            EventTranslator._map_route_kind(e.kind),
            e.failure_reason,
            e.occurred_at,
        )

    @staticmethod
    def translate_security(e: AetherNetSecurityEvent) -> AetherSecurityEvent:
        return AetherSecurityEvent(
            e.node_id,
            EventTranslator._map_security_kind(e.kind),
            EventTranslator.map_threat_level(e.threat_level),
            e.description,
            e.metadata,
            e.occurred_at,
        )

    @staticmethod
    def translate_network(e: AetherNetNetworkEvent) -> AetherNetworkEvent:
        return AetherNetworkEvent(
            EventTranslator._map_network_kind(e.kind),
            e.node_count,
            e.active_route_count,
            e.congestion_level,
            e.occurred_at,
        )

    # ── Enum mappings ─────────────────────────────────────────────────────────

    @staticmethod
    def _map_node_kind(k: AetherNetNodeEventKind) -> AetherNodeEventKind:
        return {
            AetherNetNodeEventKind.JOINED: AetherNodeEventKind.JOINED,
            AetherNetNodeEventKind.LEFT: AetherNodeEventKind.LEFT,
            AetherNetNodeEventKind.HEALTH_CHANGED: AetherNodeEventKind.HEALTH_CHANGED,
        }.get(k, AetherNodeEventKind.HEALTH_CHANGED)

    @staticmethod
    def _map_transport_kind(
        k: AetherNetTransportEventKind,
    ) -> AetherTransportEventKind:
        return {
            AetherNetTransportEventKind.SELECTED: AetherTransportEventKind.SELECTED,
            AetherNetTransportEventKind.CHANGED: AetherTransportEventKind.CHANGED,
            AetherNetTransportEventKind.LATENCY_MEASURED: AetherTransportEventKind.LATENCY_MEASURED,
            AetherNetTransportEventKind.PACKET_LOSS: AetherTransportEventKind.PACKET_LOSS,
        }.get(k, AetherTransportEventKind.SELECTED)

    @staticmethod
    def _map_transport(k: AetherNetTransportKind) -> AetherTransportKind:
        # AetherNet has more transports (Wi-Fi Direct, NearLink, HTTP relay);
        # CircleAI's enum is broader OS-classification. Fold related kinds.
        return {
            AetherNetTransportKind.BLUETOOTH: AetherTransportKind.BLUETOOTH,
            AetherNetTransportKind.WIFI: AetherTransportKind.WIFI,
            AetherNetTransportKind.WIFI_DIRECT: AetherTransportKind.WIFI,
            AetherNetTransportKind.LORA: AetherTransportKind.LORA,
            AetherNetTransportKind.NFC: AetherTransportKind.NFC,
            AetherNetTransportKind.NEAR_LINK: AetherTransportKind.UNKNOWN,
            AetherNetTransportKind.HTTP_RELAY: AetherTransportKind.CELLULAR,
        }.get(k, AetherTransportKind.UNKNOWN)

    @staticmethod
    def _map_route_kind(k: AetherNetRouteEventKind) -> AetherRouteEventKind:
        return {
            AetherNetRouteEventKind.DISCOVERED: AetherRouteEventKind.DISCOVERED,
            AetherNetRouteEventKind.CHANGED: AetherRouteEventKind.CHANGED,
            AetherNetRouteEventKind.FAILED: AetherRouteEventKind.FAILED,
        }.get(k, AetherRouteEventKind.CHANGED)

    @staticmethod
    def _map_security_kind(
        k: AetherNetSecurityEventKind,
    ) -> AetherSecurityEventKind:
        return {
            AetherNetSecurityEventKind.NODE_AUTH_ATTEMPT: AetherSecurityEventKind.NODE_AUTH_ATTEMPT,
            AetherNetSecurityEventKind.ROUTING_ANOMALY: AetherSecurityEventKind.ROUTING_ANOMALY,
            AetherNetSecurityEventKind.NODE_BEHAVIOUR_CHANGE: AetherSecurityEventKind.NODE_BEHAVIOUR_CHANGE,
            AetherNetSecurityEventKind.ENCRYPTION_EVENT: AetherSecurityEventKind.ENCRYPTION_EVENT,
            AetherNetSecurityEventKind.INTRUSION_SIGNAL: AetherSecurityEventKind.INTRUSION_SIGNAL,
            AetherNetSecurityEventKind.PRIVILEGE_ATTEMPT: AetherSecurityEventKind.PRIVILEGE_ATTEMPT,
        }.get(k, AetherSecurityEventKind.ROUTING_ANOMALY)

    @staticmethod
    def _map_network_kind(
        k: AetherNetNetworkEventKind,
    ) -> AetherNetworkEventKind:
        return {
            AetherNetNetworkEventKind.TOPOLOGY_CHANGED: AetherNetworkEventKind.TOPOLOGY_CHANGED,
            AetherNetNetworkEventKind.CONGESTION_DETECTED: AetherNetworkEventKind.CONGESTION_DETECTED,
            AetherNetNetworkEventKind.PARTITION_DETECTED: AetherNetworkEventKind.PARTITION_DETECTED,
        }.get(k, AetherNetworkEventKind.TOPOLOGY_CHANGED)

    # ── Threat level (bidirectional) ──────────────────────────────────────────

    @staticmethod
    def map_threat_level(level: AetherNetThreatLevel) -> AetherThreatLevel:
        return {
            AetherNetThreatLevel.NONE: AetherThreatLevel.NONE,
            AetherNetThreatLevel.LOW: AetherThreatLevel.LOW,
            AetherNetThreatLevel.MEDIUM: AetherThreatLevel.MEDIUM,
            AetherNetThreatLevel.HIGH: AetherThreatLevel.HIGH,
            AetherNetThreatLevel.CRITICAL: AetherThreatLevel.CRITICAL,
        }.get(level, AetherThreatLevel.NONE)

    @staticmethod
    def map_threat_level_to_mesh(level: AetherThreatLevel) -> AetherNetThreatLevel:
        return {
            AetherThreatLevel.NONE: AetherNetThreatLevel.NONE,
            AetherThreatLevel.LOW: AetherNetThreatLevel.LOW,
            AetherThreatLevel.MEDIUM: AetherNetThreatLevel.MEDIUM,
            AetherThreatLevel.HIGH: AetherNetThreatLevel.HIGH,
            AetherThreatLevel.CRITICAL: AetherNetThreatLevel.CRITICAL,
        }.get(level, AetherNetThreatLevel.NONE)

    # ── Directive kind (bidirectional) ────────────────────────────────────────

    @staticmethod
    def map_directive_kind_to_mesh(
        k: SecurityDirectiveKind,
    ) -> AetherNetSecurityDirectiveKind:
        return {
            SecurityDirectiveKind.UPDATE_NODE_TRUST: AetherNetSecurityDirectiveKind.UPDATE_NODE_TRUST,
            SecurityDirectiveKind.AVOID_NODE: AetherNetSecurityDirectiveKind.AVOID_NODE,
            SecurityDirectiveKind.QUARANTINE_NODE: AetherNetSecurityDirectiveKind.QUARANTINE_NODE,
            SecurityDirectiveKind.RELEASE_NODE: AetherNetSecurityDirectiveKind.RELEASE_NODE,
            SecurityDirectiveKind.REQUEST_REAUTH: AetherNetSecurityDirectiveKind.REQUEST_REAUTH,
            SecurityDirectiveKind.ELEVATE_MONITORING: AetherNetSecurityDirectiveKind.ELEVATE_MONITORING,
        }.get(k, AetherNetSecurityDirectiveKind.UPDATE_NODE_TRUST)

    @staticmethod
    def map_directive_kind_from_mesh(
        k: AetherNetSecurityDirectiveKind,
    ) -> SecurityDirectiveKind:
        return {
            AetherNetSecurityDirectiveKind.UPDATE_NODE_TRUST: SecurityDirectiveKind.UPDATE_NODE_TRUST,
            AetherNetSecurityDirectiveKind.AVOID_NODE: SecurityDirectiveKind.AVOID_NODE,
            AetherNetSecurityDirectiveKind.QUARANTINE_NODE: SecurityDirectiveKind.QUARANTINE_NODE,
            AetherNetSecurityDirectiveKind.RELEASE_NODE: SecurityDirectiveKind.RELEASE_NODE,
            AetherNetSecurityDirectiveKind.REQUEST_REAUTH: SecurityDirectiveKind.REQUEST_REAUTH,
            AetherNetSecurityDirectiveKind.ELEVATE_MONITORING: SecurityDirectiveKind.ELEVATE_MONITORING,
        }.get(k, SecurityDirectiveKind.UPDATE_NODE_TRUST)
