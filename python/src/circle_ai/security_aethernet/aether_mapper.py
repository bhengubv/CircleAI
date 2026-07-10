# aether_mapper.py
#
# Port of CircleAI.Security.AetherNet.AetherMapper (C# — the EXACT spec).
#
# Static translation helpers between Aether-specific types (circle_ai.aether) and
# the transport-agnostic Peer types (circle_ai.security). All mappings are
# explicit dict lookups with a default arm, matching the C# switch expressions.

from __future__ import annotations

from ..aether.events import AetherSecurityEventKind, AetherThreatLevel
from ..aether.security_layer import SecurityDirectiveKind
from ..security.peer_security_types import (
    PeerDirectiveKind,
    PeerSecurityEventKind,
    PeerThreatLevel,
)


class AetherMapper:
    """Static helpers that translate between Aether-specific types and the
    transport-agnostic Peer types defined in :mod:`circle_ai.security`.
    """

    # ── AetherSecurityEventKind -> PeerSecurityEventKind ──────────────────────

    @staticmethod
    def to_peer_event_kind(kind: AetherSecurityEventKind) -> PeerSecurityEventKind:
        return {
            AetherSecurityEventKind.NODE_AUTH_ATTEMPT: PeerSecurityEventKind.AUTH_ATTEMPT,
            AetherSecurityEventKind.ROUTING_ANOMALY: PeerSecurityEventKind.ROUTING_ANOMALY,
            AetherSecurityEventKind.NODE_BEHAVIOUR_CHANGE: PeerSecurityEventKind.BEHAVIOUR_CHANGE,
            AetherSecurityEventKind.ENCRYPTION_EVENT: PeerSecurityEventKind.ENCRYPTION_EVENT,
            AetherSecurityEventKind.INTRUSION_SIGNAL: PeerSecurityEventKind.INTRUSION_SIGNAL,
            AetherSecurityEventKind.PRIVILEGE_ATTEMPT: PeerSecurityEventKind.PRIVILEGE_ATTEMPT,
        }.get(kind, PeerSecurityEventKind.UNKNOWN)

    # ── AetherThreatLevel <-> PeerThreatLevel ─────────────────────────────────

    @staticmethod
    def to_peer_threat_level(level: AetherThreatLevel) -> PeerThreatLevel:
        return {
            AetherThreatLevel.NONE: PeerThreatLevel.NONE,
            AetherThreatLevel.LOW: PeerThreatLevel.LOW,
            AetherThreatLevel.MEDIUM: PeerThreatLevel.MEDIUM,
            AetherThreatLevel.HIGH: PeerThreatLevel.HIGH,
            AetherThreatLevel.CRITICAL: PeerThreatLevel.CRITICAL,
        }.get(level, PeerThreatLevel.NONE)

    @staticmethod
    def to_aether_threat_level(level: PeerThreatLevel) -> AetherThreatLevel:
        return {
            PeerThreatLevel.NONE: AetherThreatLevel.NONE,
            PeerThreatLevel.LOW: AetherThreatLevel.LOW,
            PeerThreatLevel.MEDIUM: AetherThreatLevel.MEDIUM,
            PeerThreatLevel.HIGH: AetherThreatLevel.HIGH,
            PeerThreatLevel.CRITICAL: AetherThreatLevel.CRITICAL,
        }.get(level, AetherThreatLevel.NONE)

    # ── PeerDirectiveKind -> SecurityDirectiveKind ────────────────────────────

    @staticmethod
    def to_security_directive_kind(kind: PeerDirectiveKind) -> SecurityDirectiveKind:
        return {
            PeerDirectiveKind.ELEVATE_MONITORING: SecurityDirectiveKind.ELEVATE_MONITORING,
            PeerDirectiveKind.AVOID_NODE: SecurityDirectiveKind.AVOID_NODE,
            PeerDirectiveKind.QUARANTINE_NODE: SecurityDirectiveKind.QUARANTINE_NODE,
            PeerDirectiveKind.RELEASE_NODE: SecurityDirectiveKind.RELEASE_NODE,
        }.get(kind, SecurityDirectiveKind.ELEVATE_MONITORING)
