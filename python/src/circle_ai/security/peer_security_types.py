# peer_security_types.py
#
# Port of CircleAI.Security.PeerSecurityTypes (C# — the EXACT spec).
#
# Transport-agnostic security primitives. These types are deliberately free of
# any transport dependency (Aether, WiFi, BLE, NearLink, HTTP, etc.). Every
# transport adapter translates its own event vocabulary into these types before
# feeding the security layer.
#
# Type map:
#   PeerSecurityEventKind  — what happened (transport-neutral event category)
#   PeerThreatLevel        — how severe (None -> Critical)
#   PeerSecurityEvent      — one security incident from any transport
#   PeerDirectiveKind      — what the security layer recommends
#   PeerDirective          — a directive issued to all IPeerDirectiveConsumer subscribers
#   PeerTrustScoreUpdate   — one change notification emitted by NodeTrustRegistry
#   PeerSecurityPosture    — aggregate snapshot of security state
#   PeerNetworkHealthReport  — aggregate health across all observed peers
#   PeerThreatAssessment   — per-node threat confidence + indicators
#   PeerRoutingAdvice      — trust-aware path recommendation
#
# Interfaces:
#   IPeerDirectiveConsumer — receives PeerDirective instances from any security layer
#   IPeerSecurityLayer     — lifecycle + query surface for the transport-agnostic layer
#   IPeerIntelligence      — read-only intelligence queries (health, threat, routing)
#   IPeerSecurityEventFeed — implemented by transport adapters to register an event source

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import AsyncIterator, Awaitable, Callable, List, Optional, Sequence


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ── Enumerations ─────────────────────────────────────────────────────────────


class PeerSecurityEventKind(IntEnum):
    """Transport-neutral classification of a peer security event."""

    # Authentication attempt (login, handshake, re-auth).
    AUTH_ATTEMPT = 0

    # Anomalous routing behaviour detected (loop, black-hole, etc.).
    ROUTING_ANOMALY = 1

    # Peer behaviour changed unexpectedly (rate, pattern, protocol).
    BEHAVIOUR_CHANGE = 2

    # Encryption negotiation event (downgrade, cipher mismatch).
    ENCRYPTION_EVENT = 3

    # Active intrusion probe or exploitation attempt.
    INTRUSION_SIGNAL = 4

    # Privilege escalation or capability violation attempt.
    PRIVILEGE_ATTEMPT = 5

    # Unusual connection pattern (port scan, rapid reconnect).
    CONNECTION_ANOMALY = 6

    # Suspected data exfiltration (volume, destination anomaly).
    DATA_EXFILTRATION = 7

    # Denial-of-service signal (flooding, resource exhaustion).
    DENIAL_OF_SERVICE = 8

    # Catch-all for events that do not map to a specific category.
    UNKNOWN = 9


class PeerThreatLevel(IntEnum):
    """Severity level for a peer security event or threat assessment.

    Values match the intuitive ordering: None is safest, Critical is worst.
    """

    # No threat — event carries no security significance.
    NONE = 0

    # Low-level anomaly — monitor but no action required.
    LOW = 1

    # Notable anomaly — elevated monitoring recommended.
    MEDIUM = 2

    # Significant threat — routing around the peer recommended.
    HIGH = 3

    # Active or confirmed attack — quarantine the peer.
    CRITICAL = 4


class PeerDirectiveKind(IntEnum):
    """The action recommended by the security layer for a given peer."""

    # Increase observation cadence; no traffic restriction yet.
    ELEVATE_MONITORING = 0

    # Exclude the peer from routing; still accept inbound connections.
    AVOID_NODE = 1

    # Hard-block the peer — no traffic to or from it.
    QUARANTINE_NODE = 2

    # Lift a previous directive; the peer has recovered sufficient trust.
    # Not issued automatically — requires explicit operator action.
    RELEASE_NODE = 3


# ── Records ───────────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class PeerSecurityEvent:
    """One security incident observed on any transport.

    :param node_id: Stable identifier of the peer that generated the event.
    :param kind: Transport-neutral event category.
    :param threat_level: Assessed severity at the time of observation.
    :param description: Human-readable description of the event.
    :param transport_id: Identifier for the transport that produced the event
        (e.g. ``"aether"``, ``"wifi"``, ``"ble"``, ``"nearlink"``, ``"http"``).
    :param occurred_at: UTC timestamp of the event.
    """

    node_id: str
    kind: PeerSecurityEventKind
    threat_level: PeerThreatLevel
    description: str
    transport_id: str
    occurred_at: datetime


@dataclass(frozen=True, slots=True)
class PeerDirective:
    """A security directive issued to all registered
    :class:`IPeerDirectiveConsumer` subscribers when a peer's trust crosses a
    threshold.

    :param kind: The recommended action.
    :param target_node_id: The peer to which the directive applies.
    :param trust_score: Current trust score of the peer at time of issue.
    :param threat_level: Threat level at time of issue.
    :param reason: Human-readable explanation for the directive.
    :param duration: Optional duration after which the directive should be
        re-evaluated. ``None`` means permanent until an explicit
        :attr:`PeerDirectiveKind.RELEASE_NODE` directive is issued.
    :param issued_at: UTC timestamp of issue.
    """

    kind: PeerDirectiveKind
    target_node_id: str
    trust_score: float
    threat_level: PeerThreatLevel
    reason: str
    duration: Optional[timedelta]
    issued_at: datetime


@dataclass(frozen=True, slots=True)
class PeerTrustScoreUpdate:
    """Notification emitted by :class:`NodeTrustRegistry` whenever a node's
    trust score changes.

    :param node_id: The peer whose score changed.
    :param previous_score: Score before this change.
    :param new_score: Score after this change.
    :param reason: Short description of the cause (event description or
        ``"passive-recovery"``).
    :param changed_at: UTC timestamp of the change.
    """

    node_id: str
    previous_score: float
    new_score: float
    reason: str
    changed_at: datetime


@dataclass(frozen=True, slots=True)
class PeerSecurityPosture:
    """Snapshot of the overall security posture across all observed peers.

    :param overall_threat_level: Worst-case threat level in the current peer set.
    :param quarantined_peer_count: Number of peers at or below the quarantine
        threshold.
    :param monitored_peer_count: Number of peers elevated beyond the monitoring
        threshold but not yet quarantined.
    :param is_active: Whether the security layer is currently running.
    :param generated_at: UTC timestamp of this snapshot.
    """

    overall_threat_level: PeerThreatLevel
    quarantined_peer_count: int
    monitored_peer_count: int
    is_active: bool
    generated_at: datetime


@dataclass(frozen=True, slots=True)
class PeerNetworkHealthReport:
    """Aggregate network health across all observed peers.

    :param overall_score: Average trust score ``[0.0, 1.0]`` across all peers.
    :param trusted_peer_count: Peers above the avoid-node threshold.
    :param suspicious_peer_count: Peers at or below the monitoring threshold.
    :param summary: Human-readable health summary.
    :param generated_at: UTC timestamp of this report.
    """

    overall_score: float
    trusted_peer_count: int
    suspicious_peer_count: int
    summary: str
    generated_at: datetime


@dataclass(frozen=True, slots=True)
class PeerThreatAssessment:
    """Per-peer threat assessment: confidence score, threat level, and detected
    indicators.

    :param node_id: The assessed peer.
    :param confidence: Likelihood that the peer is a genuine threat
        ``[0.0, 1.0]``. Derived from trust deficit + indicator count.
    :param threat_level: Classified severity.
    :param indicators: Human-readable indicator tags (e.g. ``"brute-force-auth"``,
        ``"intrusion-signal"``).
    :param assessed_at: UTC timestamp of this assessment.
    """

    node_id: str
    confidence: float
    threat_level: PeerThreatLevel
    indicators: Sequence[str]
    assessed_at: datetime


@dataclass(frozen=True, slots=True)
class PeerRoutingAdvice:
    """Trust-aware routing recommendation for reaching a destination peer.

    :param destination_node_id: The target peer.
    :param recommended_path: Ordered list of peer IDs forming the recommended
        path. Empty when no safe path is available.
    :param avoid_node_ids: Peers that should be excluded from routing.
    :param confidence: Confidence in the recommendation ``[0.0, 1.0]``.
    :param reasoning: Human-readable explanation.
    :param generated_at: UTC timestamp of this advice.
    """

    destination_node_id: str
    recommended_path: Sequence[str]
    avoid_node_ids: Sequence[str]
    confidence: float
    reasoning: str
    generated_at: datetime


# ── Interfaces ────────────────────────────────────────────────────────────────


class IPeerDirectiveConsumer(ABC):
    """Receives security directives from any :class:`IPeerSecurityLayer`
    implementation.
    """

    @abstractmethod
    def on_directive(self, directive: PeerDirective) -> None:
        """Called when the security layer issues a directive for a peer."""
        ...


class IDisposable(ABC):
    """A resource that can be released. Mirrors C# ``IDisposable`` — the
    subscription handle returned by :meth:`DirectivePublisher.subscribe` and
    :meth:`IPeerSecurityLayer.subscribe_to_directives`.

    Supports use as a context manager (``with layer.subscribe_to_directives(c):``).
    """

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class IPeerSecurityLayer(ABC):
    """Transport-agnostic security layer lifecycle and posture surface."""

    @abstractmethod
    async def start_async(self, ct: Optional[object] = None) -> None:
        """Starts the background trust-recovery loop."""
        ...

    @abstractmethod
    async def stop_async(self, ct: Optional[object] = None) -> None:
        """Stops the recovery loop and releases resources."""
        ...

    @abstractmethod
    def handle_peer_event(self, e: PeerSecurityEvent) -> None:
        """Feed a security event from any transport into the security layer.

        The layer will degrade the peer's trust score and issue directives as
        needed.
        """
        ...

    @abstractmethod
    def subscribe_to_directives(self, consumer: IPeerDirectiveConsumer) -> IDisposable:
        """Subscribe to receive directives. Dispose the returned handle to
        unsubscribe.
        """
        ...

    @abstractmethod
    async def get_posture_async(
        self, ct: Optional[object] = None
    ) -> PeerSecurityPosture:
        """Returns a snapshot of the current security posture."""
        ...


class IPeerIntelligence(ABC):
    """Transport-agnostic intelligence queries over accumulated trust data."""

    @abstractmethod
    async def get_network_health_async(
        self, ct: Optional[object] = None
    ) -> PeerNetworkHealthReport:
        """Returns aggregate network health across all observed peers."""
        ...

    @abstractmethod
    async def assess_threat_async(
        self, node_id: str, ct: Optional[object] = None
    ) -> PeerThreatAssessment:
        """Returns a threat assessment for a specific peer."""
        ...

    @abstractmethod
    async def get_routing_advice_async(
        self, destination_node_id: str, ct: Optional[object] = None
    ) -> PeerRoutingAdvice:
        """Returns trust-aware routing advice toward a destination peer."""
        ...

    @abstractmethod
    def stream_trust_scores_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[PeerTrustScoreUpdate]:
        """Streams every trust score change as it occurs.

        Completes when ``ct`` is cancelled.
        """
        ...


# handler(event) -> None
PeerEventHandler = Callable[[PeerSecurityEvent], None]


class IPeerSecurityEventFeed(ABC):
    """Implemented by transport adapters to register an event source with the
    security layer. The security layer calls :meth:`start_async` once to begin
    pumping events.
    """

    @property
    @abstractmethod
    def transport_id(self) -> str:
        """Human-readable identifier for this transport (e.g. ``"wifi"``,
        ``"ble"``, ``"aether"``).
        """
        ...

    @abstractmethod
    async def start_async(
        self, handler: PeerEventHandler, ct: Optional[object] = None
    ) -> None:
        """Begins feeding events into ``handler`` until ``ct`` is cancelled."""
        ...
