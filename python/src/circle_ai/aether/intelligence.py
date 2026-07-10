# intelligence.py
#
# Port of CircleAI.Aether.IAetherIntelligence.cs (C# — the EXACT spec).
#
# Contract 3 — Intelligence Output.
#
# What BhenguAI produces after reasoning over Aether telemetry. Aether never
# sees this interface — it flows upward only.
#
# Ships:
#   NetworkHealthReport / ThreatAssessment / RoutingAdvice / TrustScoreUpdate
#   IAetherIntelligence         — the intelligence output surface
#   InMemoryAetherIntelligence  — a working, deterministic implementation

from __future__ import annotations

import asyncio
import math
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import AsyncIterator, Dict, List, Optional, Sequence

from .events import (
    AetherNodeEvent,
    AetherSecurityEvent,
    AetherThreatLevel,
)


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ── Intelligence records ──────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class NetworkHealthReport:
    """Aggregate health of the mesh as assessed by BhenguAI."""

    overall_score: float
    trusted_node_count: int
    suspicious_node_count: int
    summary: str
    generated_at: datetime

    @property
    def is_valid(self) -> bool:
        """True when overall_score is within the valid 0-1 range."""
        return 0.0 <= self.overall_score <= 1.0


@dataclass(frozen=True, slots=True)
class ThreatAssessment:
    """BhenguAI's assessment of the threat posed by a specific node."""

    node_id: str
    threat_confidence: float
    level: AetherThreatLevel
    indicators: Sequence[str]
    assessed_at: datetime

    @property
    def is_valid(self) -> bool:
        """True when threat_confidence is within the valid 0-1 range."""
        return 0.0 <= self.threat_confidence <= 1.0


@dataclass(frozen=True, slots=True)
class RoutingAdvice:
    """BhenguAI's recommendation for routing to a destination node, taking trust
    scores and current threat assessments into account.
    """

    destination_node_id: str
    recommended_path: Sequence[str]
    avoid_nodes: Sequence[str]
    confidence: float
    reasoning: str
    generated_at: datetime


@dataclass(frozen=True, slots=True)
class TrustScoreUpdate:
    """Emitted when BhenguAI revises the trust score for a node."""

    node_id: str
    previous_score: float
    current_score: float
    reason: str
    updated_at: datetime

    @property
    def has_changed(self) -> bool:
        """True when the score moved in either direction."""
        return abs(self.current_score - self.previous_score) > 0.001

    @property
    def is_degraded(self) -> bool:
        """True when the score decreased."""
        return self.current_score < self.previous_score


# ── Interface ─────────────────────────────────────────────────────────────────


class IAetherIntelligence(ABC):
    """The intelligence output surface produced by BhenguAI from Aether
    telemetry. Consumed by apps and the Security Layer; never by Aether.
    """

    @abstractmethod
    async def get_network_health_async(
        self, ct: Optional[object] = None
    ) -> NetworkHealthReport:
        """Returns an aggregate health report for the current mesh state."""
        ...

    @abstractmethod
    async def assess_threat_async(
        self, node_id: str, ct: Optional[object] = None
    ) -> ThreatAssessment:
        """Assesses the current threat level of a specific node. Returns a
        zero-confidence assessment when the node is unknown.
        """
        ...

    @abstractmethod
    async def get_routing_advice_async(
        self, destination_node_id: str, ct: Optional[object] = None
    ) -> RoutingAdvice:
        """Returns a routing recommendation for reaching the given destination,
        factoring out nodes with low trust scores.
        """
        ...

    @abstractmethod
    def stream_trust_scores_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[TrustScoreUpdate]:
        """Streams trust score updates as BhenguAI observes new telemetry.
        Useful for live dashboards and security monitoring UIs.
        """
        ...


# ── Working in-memory implementation ──────────────────────────────────────────

# Trust deltas applied when a security event is observed, keyed by the Aether
# threat level of the event. Higher severity degrades trust more.
_THREAT_DEGRADATION: Dict[AetherThreatLevel, float] = {
    AetherThreatLevel.NONE: 0.0,
    AetherThreatLevel.LOW: 0.05,
    AetherThreatLevel.MEDIUM: 0.15,
    AetherThreatLevel.HIGH: 0.35,
    AetherThreatLevel.CRITICAL: 0.60,
}

# Trust-score band boundaries (upper-bound inclusive), most severe first.
_AVOID_THRESHOLD = 0.50
_MONITOR_THRESHOLD = 0.75


class _UnboundedChannel:
    """Unbounded single-queue channel mirroring the C# ``Channel.CreateUnbounded``
    used to stream trust score updates: ``try_write`` never blocks and buffers
    even when no reader is attached; ``read_all_async`` competes readers.
    """

    def __init__(self) -> None:
        self._queue: "asyncio.Queue[TrustScoreUpdate]" = asyncio.Queue()

    def try_write(self, update: TrustScoreUpdate) -> bool:
        self._queue.put_nowait(update)
        return True

    async def read_all_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[TrustScoreUpdate]:
        while True:
            update = await self._queue.get()
            yield update


class InMemoryAetherIntelligence(IAetherIntelligence):
    """A deterministic, thread-safe :class:`IAetherIntelligence`. Maintains a
    per-node trust score that starts at 1.0 and degrades as security events are
    observed via :meth:`observe_security_event`. Every score change is published
    on the trust-score stream. All four intelligence outputs are derived from
    the accumulated scores.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._scores: Dict[str, float] = {}
        self._indicators: Dict[str, List[str]] = {}
        self._channel = _UnboundedChannel()

    # ── Telemetry intake (feeds the intelligence) ─────────────────────────────

    def observe_security_event(self, e: AetherSecurityEvent) -> None:
        """Fold a security event into the node's trust score. This is the intake
        an Aether telemetry feed drives; the C# reference feeds the equivalent
        via the security layer.
        """
        degradation = _THREAT_DEGRADATION.get(e.threat_level, 0.0)
        if degradation <= 0.0:
            return
        with self._lock:
            previous = self._scores.get(e.node_id, 1.0)
            current = max(0.0, previous - degradation)
            self._scores[e.node_id] = current
            self._indicators.setdefault(e.node_id, []).append(e.kind.name)
        if abs(current - previous) > 0.0:
            self._channel.try_write(
                TrustScoreUpdate(
                    node_id=e.node_id,
                    previous_score=previous,
                    current_score=current,
                    reason=e.description,
                    updated_at=_utc_now(),
                )
            )

    def observe_node_event(self, e: AetherNodeEvent) -> None:
        """Seed a node's trust score from a node-health event. Never publishes an
        update for a node already tracked (health events carry the current
        snapshot, not a security-driven change).
        """
        with self._lock:
            if e.node_id not in self._scores:
                self._scores[e.node_id] = e.health.trust_score

    # ── IAetherIntelligence ───────────────────────────────────────────────────

    async def get_network_health_async(
        self, ct: Optional[object] = None
    ) -> NetworkHealthReport:
        with self._lock:
            scores = list(self._scores.values())

        if not scores:
            return NetworkHealthReport(
                overall_score=1.0,
                trusted_node_count=0,
                suspicious_node_count=0,
                summary="No nodes observed.",
                generated_at=_utc_now(),
            )

        overall = sum(scores) / len(scores)
        trusted = sum(1 for s in scores if s > _AVOID_THRESHOLD)
        suspicious = sum(1 for s in scores if s <= _MONITOR_THRESHOLD)

        if overall > 0.90:
            summary = "Mesh health is excellent."
        elif overall > 0.75:
            summary = "Mesh health is good; minor anomalies detected."
        elif overall > 0.50:
            summary = "Mesh health is degraded; elevated monitoring active."
        elif overall > 0.25:
            summary = "Mesh health is poor; routing around compromised nodes."
        else:
            summary = "Mesh health is critical; quarantine directives in effect."

        return NetworkHealthReport(overall, trusted, suspicious, summary, _utc_now())

    async def assess_threat_async(
        self, node_id: str, ct: Optional[object] = None
    ) -> ThreatAssessment:
        with self._lock:
            score = self._scores.get(node_id)
            indicators = list(self._indicators.get(node_id, []))

        if score is None:
            # Unknown node — a zero-confidence assessment, as the contract states.
            return ThreatAssessment(
                node_id=node_id,
                threat_confidence=0.0,
                level=AetherThreatLevel.NONE,
                indicators=[],
                assessed_at=_utc_now(),
            )

        deficit = 1.0 - score
        level = _score_to_threat_level(score)
        confidence = min(1.0, deficit + len(indicators) * 0.1)
        return ThreatAssessment(node_id, confidence, level, indicators, _utc_now())

    async def get_routing_advice_async(
        self, destination_node_id: str, ct: Optional[object] = None
    ) -> RoutingAdvice:
        with self._lock:
            avoid = [
                nid for nid, s in self._scores.items() if s <= _AVOID_THRESHOLD
            ]
            dest_score = self._scores.get(destination_node_id, 1.0)

        recommended: List[str] = (
            [destination_node_id] if dest_score > _AVOID_THRESHOLD else []
        )

        if dest_score > 0.75:
            reasoning = (
                f"Direct path to {destination_node_id} is trusted "
                f"(score {dest_score:.2f})."
            )
        elif dest_score > 0.50:
            reasoning = (
                f"Destination {destination_node_id} is under monitoring; "
                f"routing with caution."
            )
        elif dest_score > 0.25:
            reasoning = (
                f"Destination {destination_node_id} has degraded trust; "
                f"avoid recommended."
            )
        else:
            reasoning = (
                f"Destination {destination_node_id} is quarantined; "
                f"no safe path available."
            )

        return RoutingAdvice(
            destination_node_id,
            recommended,
            avoid,
            dest_score,
            reasoning,
            _utc_now(),
        )

    async def stream_trust_scores_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[TrustScoreUpdate]:
        async for update in self._channel.read_all_async(ct):
            yield update


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
