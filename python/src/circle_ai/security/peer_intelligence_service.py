# peer_intelligence_service.py
#
# Port of CircleAI.Security.PeerIntelligenceService (C# — the EXACT spec).
#
# NOTE: in the C# tree this class lives in the file AetherIntelligenceService.cs
# but the public type is `PeerIntelligenceService`. There is no separate
# `AetherIntelligenceService` type — Aether transports adapt to IPeerIntelligence
# via a bridge package. This module is that canonical implementation.
#
# Transport-agnostic intelligence output — full implementation of
# IPeerIntelligence.
#
# Reads trust scores and event history from NodeTrustRegistry and packages them
# as the four intelligence outputs consumed by apps and the security layer:
#   PeerNetworkHealthReport   aggregate health (overall score, counts)
#   PeerThreatAssessment      per-peer confidence + level + indicators
#   PeerRoutingAdvice         trust-aware path with avoid-list
#   PeerTrustScoreUpdate      live channel of every score change

from __future__ import annotations

from datetime import datetime, timezone
from typing import AsyncIterator, List, Optional

from .node_trust_registry import NodeTrustRegistry
from .peer_security_types import (
    IPeerIntelligence,
    PeerNetworkHealthReport,
    PeerRoutingAdvice,
    PeerThreatAssessment,
    PeerThreatLevel,
    PeerTrustScoreUpdate,
)
from .security_options import SecurityOptions
from .threat_detector import ThreatDetector


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class PeerIntelligenceService(IPeerIntelligence):
    """Reads :class:`NodeTrustRegistry` state to produce transport-agnostic
    intelligence outputs. Wires directly to the registry's
    :attr:`NodeTrustRegistry.trust_score_updates` channel for the streaming API.
    """

    def __init__(self, registry: NodeTrustRegistry, options: SecurityOptions) -> None:
        self._registry = registry
        self._options = options

    # ── IPeerIntelligence ─────────────────────────────────────────────────────

    async def get_network_health_async(
        self, ct: Optional[object] = None
    ) -> PeerNetworkHealthReport:
        node_ids = self._registry.all_node_ids

        if len(node_ids) == 0:
            return PeerNetworkHealthReport(
                overall_score=1.0,
                trusted_peer_count=0,
                suspicious_peer_count=0,
                summary="No peers observed.",
                generated_at=_utc_now(),
            )

        scores = [self._registry.get_trust_score(nid) for nid in node_ids]
        overall = sum(scores) / len(scores)
        trusted = sum(1 for s in scores if s > self._options.avoid_node_threshold)
        suspicious = sum(
            1 for s in scores if s <= self._options.elevate_monitoring_threshold
        )

        if overall > 0.90:
            summary = "Network health is excellent."
        elif overall > 0.75:
            summary = "Network health is good; minor anomalies detected."
        elif overall > 0.50:
            summary = "Network health is degraded; elevated monitoring active."
        elif overall > 0.25:
            summary = "Network health is poor; routing around compromised peers."
        else:
            summary = "Network health is critical; quarantine directives in effect."

        return PeerNetworkHealthReport(
            overall, trusted, suspicious, summary, _utc_now()
        )

    async def assess_threat_async(
        self, node_id: str, ct: Optional[object] = None
    ) -> PeerThreatAssessment:
        score = self._registry.get_trust_score(node_id)
        deficit = 1.0 - score  # 0 = fully trusted, 1 = fully lost

        indicators = ThreatDetector.detect_indicators(
            self._registry.get_recent_events(node_id), self._options.event_window
        )

        if score <= 0.25:
            level = PeerThreatLevel.CRITICAL
        elif score <= 0.50:
            level = PeerThreatLevel.HIGH
        elif score <= 0.75:
            level = PeerThreatLevel.MEDIUM
        elif score <= 0.90:
            level = PeerThreatLevel.LOW
        else:
            level = PeerThreatLevel.NONE

        # Confidence is proportional to trust deficit, boosted by each indicator.
        confidence = min(1.0, deficit + len(indicators) * 0.1)

        return PeerThreatAssessment(
            node_id, confidence, level, indicators, _utc_now()
        )

    async def get_routing_advice_async(
        self, destination_node_id: str, ct: Optional[object] = None
    ) -> PeerRoutingAdvice:
        all_nodes = self._registry.all_node_ids
        avoid_nodes = [
            nid
            for nid in all_nodes
            if self._registry.get_trust_score(nid) <= self._options.avoid_node_threshold
        ]

        dest_score = self._registry.get_trust_score(destination_node_id)

        # Recommended path is direct only when destination is above
        # avoid-threshold.
        recommended: List[str] = (
            [destination_node_id]
            if dest_score > self._options.avoid_node_threshold
            else []
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

        return PeerRoutingAdvice(
            destination_node_id,
            recommended,
            avoid_nodes,
            dest_score,
            reasoning,
            _utc_now(),
        )

    async def stream_trust_scores_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[PeerTrustScoreUpdate]:
        async for update in self._registry.trust_score_updates.read_all_async(ct):
            yield update
