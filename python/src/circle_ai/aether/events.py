# events.py
#
# Port of CircleAI.Aether.Events.* (C# — the EXACT spec).
#
# The five telemetry event families Aether emits at the protocol layer, plus
# the supporting enums and the AetherThreatLevel severity ladder. Every record
# is an immutable value object; every enum carries stable ordinals so the wire
# vocabulary matches across languages.
#
# Type map (C# file -> Python symbol):
#   AetherNodeEvent.cs      -> AetherNodeEventKind, AetherNodeHealth, AetherNodeEvent
#   AetherTransportEvent.cs -> AetherTransportKind, AetherTransportEventKind, AetherTransportEvent
#   AetherRouteEvent.cs     -> AetherRouteEventKind, AetherRouteEvent
#   AetherSecurityEvent.cs  -> AetherSecurityEventKind, AetherThreatLevel, AetherSecurityEvent
#   AetherNetworkEvent.cs   -> AetherNetworkEventKind, AetherNetworkEvent

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta
from enum import IntEnum
from typing import Mapping, Optional, Sequence


# ── Node ──────────────────────────────────────────────────────────────────────


class AetherNodeEventKind(IntEnum):
    """Kinds of node lifecycle transitions Aether can emit."""

    JOINED = 0
    LEFT = 1
    HEALTH_CHANGED = 2


@dataclass(frozen=True, slots=True)
class AetherNodeHealth:
    """Point-in-time health snapshot for a single mesh node.

    :param trust_score: 0.0 (untrusted) to 1.0 (fully trusted). Maintained by
        the AI Security Layer when active; defaults to 1.0 for all nodes when
        the security layer is off.
    """

    trust_score: float
    is_reachable: bool
    latency: timedelta
    hop_count: int

    @property
    def is_valid(self) -> bool:
        """Returns true when trust_score is within the valid 0-1 range."""
        return 0.0 <= self.trust_score <= 1.0


@dataclass(frozen=True, slots=True)
class AetherNodeEvent:
    """Emitted by Aether whenever a node joins, leaves, or changes health.
    Consumed by :class:`IAetherTelemetry` subscribers — BhenguAI never writes
    back into Aether directly.
    """

    node_id: str
    kind: AetherNodeEventKind
    health: AetherNodeHealth
    occurred_at: datetime

    @property
    def is_exit(self) -> bool:
        """Convenience: true when this is a departure event."""
        return self.kind is AetherNodeEventKind.LEFT


# ── Transport ───────────────────────────────────────────────────────────────


class AetherTransportKind(IntEnum):
    """Physical or logical transport medium Aether is using."""

    WIFI = 0
    BLUETOOTH = 1
    LORA = 2
    NFC = 3
    CELLULAR = 4
    ETHERNET = 5
    UNKNOWN = 6


class AetherTransportEventKind(IntEnum):
    """Kinds of transport-layer observations Aether can emit."""

    SELECTED = 0
    CHANGED = 1
    LATENCY_MEASURED = 2
    PACKET_LOSS = 3


@dataclass(frozen=True, slots=True)
class AetherTransportEvent:
    """Emitted when Aether selects, changes, or measures quality on a transport
    channel. The AI layer uses this to correlate transport behaviour with threat
    patterns.
    """

    node_id: str
    kind: AetherTransportEventKind
    transport: AetherTransportKind
    latency: Optional[timedelta]
    packet_loss_rate: Optional[float]
    occurred_at: datetime

    def exceeds_loss(self, threshold: float) -> bool:
        """Returns true when packet_loss_rate is set and exceeds the given
        threshold (0.0-1.0).
        """
        return self.packet_loss_rate is not None and self.packet_loss_rate > threshold


# ── Route ─────────────────────────────────────────────────────────────────────


class AetherRouteEventKind(IntEnum):
    """Kinds of routing changes Aether can emit."""

    DISCOVERED = 0
    CHANGED = 1
    FAILED = 2


@dataclass(frozen=True, slots=True)
class AetherRouteEvent:
    """Emitted when Aether discovers, updates, or loses a route between two
    nodes. The path list describes the sequence of node IDs traversed.
    """

    source_node_id: str
    destination_node_id: str
    path: Sequence[str]
    kind: AetherRouteEventKind
    failure_reason: Optional[str]
    occurred_at: datetime

    @property
    def hop_count(self) -> int:
        """Number of hops in this route, including source and destination."""
        return len(self.path)

    @property
    def is_failed(self) -> bool:
        """True when this event represents a routing failure."""
        return self.kind is AetherRouteEventKind.FAILED


# ── Security ────────────────────────────────────────────────────────────────


class AetherSecurityEventKind(IntEnum):
    """Categories of security-relevant observations Aether can detect at the
    protocol layer, without requiring AI. The AI Security Layer consumes these
    events to produce threat assessments and directives.
    """

    # A node attempted to authenticate into the mesh.
    NODE_AUTH_ATTEMPT = 0

    # Traffic was observed deviating from expected routing paths.
    ROUTING_ANOMALY = 1

    # A node's behaviour deviated from its established baseline.
    NODE_BEHAVIOUR_CHANGE = 2

    # A key exchange or certificate validation event occurred.
    ENCRYPTION_EVENT = 3

    # Active attack signature detected (e.g. replay, spoofing).
    INTRUSION_SIGNAL = 4

    # A node requested capabilities beyond its granted level.
    PRIVILEGE_ATTEMPT = 5


class AetherThreatLevel(IntEnum):
    """Protocol-level threat severity as assessed by Aether itself, before any
    AI reasoning is applied.
    """

    NONE = 0
    LOW = 1
    MEDIUM = 2
    HIGH = 3
    CRITICAL = 4


@dataclass(frozen=True, slots=True)
class AetherSecurityEvent:
    """Emitted by Aether when a security-relevant event occurs at the protocol
    layer. This is the primary feed for the AI Security Layer. Aether never
    calls into BhenguAI — it only emits; BhenguAI subscribes.
    """

    node_id: str
    kind: AetherSecurityEventKind
    threat_level: AetherThreatLevel
    description: str
    metadata: Mapping[str, str]
    occurred_at: datetime

    @property
    def is_high_severity(self) -> bool:
        """True when threat_level is High or Critical."""
        return self.threat_level in (AetherThreatLevel.HIGH, AetherThreatLevel.CRITICAL)


# ── Network ─────────────────────────────────────────────────────────────────


class AetherNetworkEventKind(IntEnum):
    """Mesh-wide topology and congestion observations."""

    TOPOLOGY_CHANGED = 0
    CONGESTION_DETECTED = 1
    PARTITION_DETECTED = 2


@dataclass(frozen=True, slots=True)
class AetherNetworkEvent:
    """Emitted when the mesh topology or overall network health changes.
    Provides aggregate context that the AI layer uses alongside individual node
    events.
    """

    kind: AetherNetworkEventKind
    node_count: int
    active_route_count: int
    congestion_level: float
    occurred_at: datetime

    @property
    def is_high_congestion(self) -> bool:
        """True when congestion_level exceeds 0.75 — a useful default alert
        threshold. Callers may apply their own thresholds.
        """
        return self.congestion_level > 0.75
