# security_layer.py
#
# Port of CircleAI.Aether.IAISecurityLayer.cs (C# — the EXACT spec).
#
# Contract 4 — Security Layer.
#
# BhenguAI reasons over Aether telemetry and publishes SecurityDirectives.
# Aether's policy engine consumes those directives — but Aether never calls into
# BhenguAI directly. The boundary is strictly one-way.
#
# External Aether adopters can opt in by implementing ISecurityDirectiveConsumer
# on their policy engine. It is never mandatory.
#
# Ships:
#   SecurityDirectiveKind        — the recommended action enum
#   SecurityDirective            — one instruction to the policy engine
#   SecurityPosture              — point-in-time posture snapshot
#   ISecurityDirectiveConsumer   — the policy-engine sink
#   IAISecurityLayer             — the security-layer contract
#   InMemoryAISecurityLayer      — a working, deterministic implementation

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import Dict, List, Optional

from .events import AetherNodeEvent, AetherSecurityEvent, AetherThreatLevel
from .telemetry import IAetherTelemetry, IAetherTelemetryObserver, IDisposable
from .events import (
    AetherNetworkEvent,
    AetherRouteEvent,
    AetherTransportEvent,
)


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class SecurityDirectiveKind(IntEnum):
    """The action BhenguAI is recommending to Aether's policy engine."""

    # Adjust the recorded trust score for a node.
    UPDATE_NODE_TRUST = 0

    # Exclude the node from routing decisions (soft block).
    AVOID_NODE = 1

    # Hard block — no traffic to or from the node until released.
    QUARANTINE_NODE = 2

    # Lift an AvoidNode or QuarantineNode directive.
    RELEASE_NODE = 3

    # Request that the user re-authenticates before a sensitive operation.
    REQUEST_REAUTH = 4

    # Increase telemetry verbosity for the target node.
    ELEVATE_MONITORING = 5


@dataclass(frozen=True, slots=True)
class SecurityDirective:
    """An instruction published by the AI Security Layer to Aether's policy
    engine. Aether is never required to honour a directive — adoption is a policy
    decision for each deployment.
    """

    kind: SecurityDirectiveKind
    target_node_id: Optional[str]
    trust_score_override: Optional[float]
    threat_level: AetherThreatLevel
    reason: str
    duration: Optional[timedelta]
    issued_at: datetime

    @property
    def has_target(self) -> bool:
        """True when the directive targets a specific node."""
        return bool(self.target_node_id and self.target_node_id.strip())

    @property
    def is_permanent(self) -> bool:
        """True when duration is None — the directive has no automatic expiry."""
        return self.duration is None


@dataclass(frozen=True, slots=True)
class SecurityPosture:
    """Point-in-time summary of the AI Security Layer's current posture."""

    overall_threat_level: AetherThreatLevel
    quarantined_node_count: int
    monitored_node_count: int
    is_active: bool
    assessed_at: datetime


class ISecurityDirectiveConsumer(ABC):
    """Receives security directives from the AI Security Layer. Implement this on
    Aether's policy engine to participate in AI-guided security decisions.
    """

    @abstractmethod
    def on_directive(self, directive: SecurityDirective) -> None:
        """Called each time BhenguAI issues a security directive. Implementations
        decide whether and how to honour it.
        """
        ...


class IAISecurityLayer(ABC):
    """The AI Security Layer contract. BhenguAI implements this by subscribing to
    :class:`IAetherTelemetry` and producing :class:`SecurityDirective` outputs
    consumed by Aether's policy engine via :class:`ISecurityDirectiveConsumer`.
    """

    @abstractmethod
    async def start_async(
        self, telemetry: IAetherTelemetry, ct: Optional[object] = None
    ) -> None:
        """Wire the security layer to an Aether telemetry feed and begin
        processing events.
        """
        ...

    @abstractmethod
    async def stop_async(self, ct: Optional[object] = None) -> None:
        """Stop processing and release all telemetry subscriptions."""
        ...

    @abstractmethod
    def subscribe_to_directives(
        self, consumer: ISecurityDirectiveConsumer
    ) -> IDisposable:
        """Subscribe a policy engine to receive security directives. Dispose the
        returned handle to unsubscribe.
        """
        ...

    @abstractmethod
    async def get_posture_async(
        self, ct: Optional[object] = None
    ) -> SecurityPosture:
        """Returns the current security posture snapshot."""
        ...


# ── Working in-memory implementation ──────────────────────────────────────────

# Trust degradation per observed security-event threat level.
_THREAT_DEGRADATION: Dict[AetherThreatLevel, float] = {
    AetherThreatLevel.NONE: 0.0,
    AetherThreatLevel.LOW: 0.05,
    AetherThreatLevel.MEDIUM: 0.15,
    AetherThreatLevel.HIGH: 0.35,
    AetherThreatLevel.CRITICAL: 0.60,
}

# Directive thresholds (upper-bound inclusive), most severe first.
_QUARANTINE_THRESHOLD = 0.25
_AVOID_THRESHOLD = 0.50
_MONITOR_THRESHOLD = 0.75


class InMemoryAISecurityLayer(IAISecurityLayer):
    """A deterministic, thread-safe :class:`IAISecurityLayer`. On
    :meth:`start_async` it subscribes to the supplied telemetry feed. Each
    inbound :class:`AetherSecurityEvent` degrades the target node's trust score;
    when a score crosses a band boundary it publishes exactly one directive
    (most-severe wins) to every subscribed consumer.

    Directives are fanned out under a snapshot-then-release-lock discipline so a
    consumer that re-enters the layer cannot self-deadlock.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._scores: Dict[str, float] = {}
        self._consumers: List[ISecurityDirectiveConsumer] = []
        self._subscription: Optional[IDisposable] = None
        self._active = False

    # ── IAISecurityLayer ──────────────────────────────────────────────────────

    async def start_async(
        self, telemetry: IAetherTelemetry, ct: Optional[object] = None
    ) -> None:
        if telemetry is None:
            raise ValueError("telemetry must not be None")
        with self._lock:
            if self._active:
                return
            self._active = True
        # Subscribe synchronously so no event published right after start is lost.
        self._subscription = telemetry.subscribe(_Observer(self))

    async def stop_async(self, ct: Optional[object] = None) -> None:
        sub: Optional[IDisposable]
        with self._lock:
            self._active = False
            sub = self._subscription
            self._subscription = None
        if sub is not None:
            sub.dispose()

    def subscribe_to_directives(
        self, consumer: ISecurityDirectiveConsumer
    ) -> IDisposable:
        if consumer is None:
            raise ValueError("consumer must not be None")
        with self._lock:
            self._consumers.append(consumer)
        return _DirectiveSubscription(self, consumer)

    async def get_posture_async(
        self, ct: Optional[object] = None
    ) -> SecurityPosture:
        with self._lock:
            scores = dict(self._scores)
            active = self._active

        quarantined = sum(1 for s in scores.values() if s <= _QUARANTINE_THRESHOLD)
        monitored = sum(
            1
            for s in scores.values()
            if _QUARANTINE_THRESHOLD < s <= _MONITOR_THRESHOLD
        )
        worst = min(scores.values()) if scores else 1.0
        return SecurityPosture(
            overall_threat_level=_score_to_threat_level(worst),
            quarantined_node_count=quarantined,
            monitored_node_count=monitored,
            is_active=active,
            assessed_at=_utc_now(),
        )

    # ── Event intake ──────────────────────────────────────────────────────────

    def _handle_security_event(self, e: AetherSecurityEvent) -> None:
        degradation = _THREAT_DEGRADATION.get(e.threat_level, 0.0)
        if degradation <= 0.0:
            return
        with self._lock:
            previous = self._scores.get(e.node_id, 1.0)
            current = max(0.0, previous - degradation)
            self._scores[e.node_id] = current
        self._evaluate_thresholds(e.node_id, previous, current, e.description)

    def _evaluate_thresholds(
        self, node_id: str, previous: float, current: float, reason: str
    ) -> None:
        # Most-severe to least; issue at most one directive per event.
        if previous > _QUARANTINE_THRESHOLD and current <= _QUARANTINE_THRESHOLD:
            self._issue(
                SecurityDirectiveKind.QUARANTINE_NODE,
                node_id,
                current,
                reason,
                AetherThreatLevel.CRITICAL,
            )
            return
        if previous > _AVOID_THRESHOLD and current <= _AVOID_THRESHOLD:
            self._issue(
                SecurityDirectiveKind.AVOID_NODE,
                node_id,
                current,
                reason,
                AetherThreatLevel.HIGH,
            )
            return
        if previous > _MONITOR_THRESHOLD and current <= _MONITOR_THRESHOLD:
            self._issue(
                SecurityDirectiveKind.ELEVATE_MONITORING,
                node_id,
                current,
                reason,
                AetherThreatLevel.MEDIUM,
            )

    def _issue(
        self,
        kind: SecurityDirectiveKind,
        node_id: str,
        trust_score: float,
        reason: str,
        threat_level: AetherThreatLevel,
    ) -> None:
        directive = SecurityDirective(
            kind=kind,
            target_node_id=node_id,
            trust_score_override=trust_score,
            threat_level=threat_level,
            reason=reason,
            duration=None,  # permanent until ReleaseNode
            issued_at=_utc_now(),
        )
        with self._lock:
            snapshot = list(self._consumers)
        for c in snapshot:
            c.on_directive(directive)

    def _unsubscribe(self, consumer: ISecurityDirectiveConsumer) -> None:
        with self._lock:
            try:
                self._consumers.remove(consumer)
            except ValueError:
                pass


class _Observer(IAetherTelemetryObserver):
    """Bridges telemetry callbacks into the security layer. Only security and
    node events matter to scoring; the rest are ignored.
    """

    def __init__(self, layer: InMemoryAISecurityLayer) -> None:
        self._layer = layer

    def on_security_event(self, e: AetherSecurityEvent) -> None:
        self._layer._handle_security_event(e)

    def on_node_event(self, e: AetherNodeEvent) -> None:
        # A join seeds the node's baseline score if not yet tracked.
        with self._layer._lock:
            if e.node_id not in self._layer._scores:
                self._layer._scores[e.node_id] = e.health.trust_score

    def on_transport_event(self, e: AetherTransportEvent) -> None:
        pass

    def on_route_event(self, e: AetherRouteEvent) -> None:
        pass

    def on_network_event(self, e: AetherNetworkEvent) -> None:
        pass


class _DirectiveSubscription(IDisposable):
    def __init__(
        self, owner: InMemoryAISecurityLayer, consumer: ISecurityDirectiveConsumer
    ) -> None:
        self._owner = owner
        self._consumer = consumer
        self._disposed = False
        self._lock = threading.Lock()

    def dispose(self) -> None:
        with self._lock:
            if self._disposed:
                return
            self._disposed = True
        self._owner._unsubscribe(self._consumer)


def _score_to_threat_level(score: float) -> AetherThreatLevel:
    if score <= 0.25:
        return AetherThreatLevel.CRITICAL
    if score <= 0.50:
        return AetherThreatLevel.HIGH
    if score <= 0.75:
        return AetherThreatLevel.MEDIUM
    if score <= 0.90:
        return AetherThreatLevel.LOW
    return AetherThreatLevel.NONE
