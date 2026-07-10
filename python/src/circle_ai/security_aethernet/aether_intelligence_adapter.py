# aether_intelligence_adapter.py
#
# Port of CircleAI.Security.AetherNet.AetherIntelligenceAdapter (C# — the spec).
#
# Implements the Aether IAetherIntelligence contract by delegating to the
# transport-agnostic PeerIntelligenceService and mapping result types:
#
#   PeerNetworkHealthReport -> NetworkHealthReport
#   PeerThreatAssessment    -> ThreatAssessment
#   PeerRoutingAdvice       -> RoutingAdvice
#   PeerTrustScoreUpdate    -> TrustScoreUpdate (streaming)
#
# Callers that only need transport-agnostic intelligence should use
# PeerIntelligenceService (circle_ai.security) directly.

from __future__ import annotations

from typing import AsyncIterator, Optional

from ..aether.intelligence import (
    IAetherIntelligence,
    NetworkHealthReport,
    RoutingAdvice,
    ThreatAssessment,
    TrustScoreUpdate,
)
from ..security.peer_intelligence_service import PeerIntelligenceService
from .aether_mapper import AetherMapper


class AetherIntelligenceAdapter(IAetherIntelligence):
    """Implements :class:`IAetherIntelligence` by wrapping
    :class:`PeerIntelligenceService` and mapping transport-agnostic result types
    to their Aether equivalents.
    """

    def __init__(self, inner: PeerIntelligenceService) -> None:
        if inner is None:
            raise ValueError("inner must not be None")
        self._inner = inner

    async def get_network_health_async(
        self, ct: Optional[object] = None
    ) -> NetworkHealthReport:
        r = await self._inner.get_network_health_async(ct)
        return NetworkHealthReport(
            r.overall_score,
            r.trusted_peer_count,
            r.suspicious_peer_count,
            r.summary,
            r.generated_at,
        )

    async def assess_threat_async(
        self, node_id: str, ct: Optional[object] = None
    ) -> ThreatAssessment:
        a = await self._inner.assess_threat_async(node_id, ct)
        return ThreatAssessment(
            a.node_id,
            a.confidence,
            AetherMapper.to_aether_threat_level(a.threat_level),
            a.indicators,
            a.assessed_at,
        )

    async def get_routing_advice_async(
        self, destination_node_id: str, ct: Optional[object] = None
    ) -> RoutingAdvice:
        r = await self._inner.get_routing_advice_async(destination_node_id, ct)
        return RoutingAdvice(
            r.destination_node_id,
            r.recommended_path,
            r.avoid_node_ids,
            r.confidence,
            r.reasoning,
            r.generated_at,
        )

    async def stream_trust_scores_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[TrustScoreUpdate]:
        async for u in self._inner.stream_trust_scores_async(ct):
            yield TrustScoreUpdate(
                u.node_id,
                u.previous_score,
                u.new_score,
                u.reason,
                u.changed_at,
            )
