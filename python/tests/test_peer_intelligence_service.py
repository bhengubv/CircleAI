"""test_peer_intelligence_service.py — PeerIntelligenceService (IPeerIntelligence).

Covers network-health aggregation + summary bands, per-peer threat assessment
(level, confidence from deficit + indicators), trust-aware routing advice
(avoid-list, direct-path gating, reasoning bands), the live trust-score stream,
and the AetherIntelligenceService alias.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone

import pytest

from circle_ai.security import (
    AetherIntelligenceService,
    NodeTrustRegistry,
    PeerIntelligenceService,
    PeerSecurityEvent,
    PeerSecurityEventKind,
    PeerThreatLevel,
    SecurityOptions,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _event(node, kind=PeerSecurityEventKind.INTRUSION_SIGNAL, level=PeerThreatLevel.HIGH):
    return PeerSecurityEvent(node, kind, level, "probe", "wifi", _now())


def _wire():
    opts = SecurityOptions()
    reg = NodeTrustRegistry(opts)
    intel = PeerIntelligenceService(reg, opts)
    return opts, reg, intel


async def test_health_no_peers():
    _, _, intel = _wire()
    h = await intel.get_network_health_async()
    assert h.overall_score == 1.0
    assert h.trusted_peer_count == 0
    assert h.suspicious_peer_count == 0
    assert h.summary == "No peers observed."


async def test_health_excellent_when_all_trusted():
    _, reg, intel = _wire()
    reg.get_or_create("a")  # 1.0
    reg.get_or_create("b")  # 1.0
    h = await intel.get_network_health_async()
    assert h.overall_score == 1.0
    assert h.trusted_peer_count == 2
    assert h.suspicious_peer_count == 0
    assert h.summary == "Network health is excellent."


async def test_health_summary_band_good():
    _, reg, intel = _wire()
    # Single node at 0.80 -> "good" band (>0.75, <=0.90).
    reg.apply_degradation(_event("a"), 0.20)
    h = await intel.get_network_health_async()
    assert h.summary == "Network health is good; minor anomalies detected."


async def test_health_summary_band_degraded_and_poor():
    _, reg, intel = _wire()
    # 0.60 -> "degraded" band (>0.50, <=0.75).
    reg.apply_degradation(_event("a"), 0.40)
    h = await intel.get_network_health_async()
    assert h.summary == "Network health is degraded; elevated monitoring active."
    # 0.40 -> "poor" band (>0.25, <=0.50).
    reg.apply_degradation(_event("a"), 0.20)
    h2 = await intel.get_network_health_async()
    assert h2.summary == "Network health is poor; routing around compromised peers."


async def test_health_summary_critical_band():
    _, reg, intel = _wire()
    reg.apply_degradation(_event("a"), 1.0)  # -> 0.0
    h = await intel.get_network_health_async()
    assert h.overall_score == 0.0
    assert h.summary == "Network health is critical; quarantine directives in effect."
    assert h.suspicious_peer_count == 1
    assert h.trusted_peer_count == 0


async def test_assess_threat_levels_by_score():
    opts, reg, intel = _wire()
    # Fully trusted -> NONE, confidence 0.
    a = await intel.assess_threat_async("trusted")  # unknown -> 1.0
    assert a.threat_level == PeerThreatLevel.NONE
    assert a.confidence == 0.0
    # Drive to 0.0 -> CRITICAL.
    reg.apply_degradation(_event("bad"), 1.0)
    c = await intel.assess_threat_async("bad")
    assert c.threat_level == PeerThreatLevel.CRITICAL


async def test_assess_confidence_boosted_by_indicators():
    opts, reg, intel = _wire()
    # Deficit 0.5, plus repeated auth attempts indicator (+0.1).
    reg.apply_degradation(_event("x"), 0.5)  # deficit 0.5
    for _ in range(3):
        reg.apply_degradation(
            _event("x", kind=PeerSecurityEventKind.AUTH_ATTEMPT, level=PeerThreatLevel.LOW),
            0.0,
        )
    a = await intel.assess_threat_async("x")
    assert "repeated-auth-attempts" in a.indicators
    # confidence = min(1.0, deficit + indicators*0.1) >= 0.5 + 0.1
    assert a.confidence >= 0.6 - 1e-9


async def test_routing_advice_direct_when_trusted():
    _, reg, intel = _wire()
    reg.get_or_create("dest")  # 1.0
    adv = await intel.get_routing_advice_async("dest")
    assert adv.recommended_path == ["dest"]
    assert adv.avoid_node_ids == []
    assert adv.confidence == 1.0
    assert "trusted" in adv.reasoning


async def test_routing_advice_avoids_low_trust_and_no_path():
    _, reg, intel = _wire()
    reg.apply_degradation(_event("dest"), 0.9)  # -> 0.1 (below avoid + quarantine)
    adv = await intel.get_routing_advice_async("dest")
    assert adv.recommended_path == []  # not above avoid threshold
    assert "dest" in adv.avoid_node_ids
    assert "quarantined" in adv.reasoning


async def test_routing_advice_monitoring_band():
    _, reg, intel = _wire()
    reg.apply_degradation(_event("dest"), 0.4)  # -> 0.6 (>0.5, <=0.75)
    adv = await intel.get_routing_advice_async("dest")
    assert adv.recommended_path == ["dest"]  # 0.6 > avoid 0.5
    assert "monitoring" in adv.reasoning


async def test_stream_trust_scores():
    _, reg, intel = _wire()
    seen = []

    async def consume(n):
        async for u in intel.stream_trust_scores_async():
            seen.append(u)
            if len(seen) >= n:
                break

    task = asyncio.create_task(consume(1))
    await asyncio.sleep(0.01)
    reg.apply_degradation(_event("s"), 0.3)
    await asyncio.wait_for(task, timeout=2)
    assert seen[0].node_id == "s"


def test_alias_is_same_class():
    assert AetherIntelligenceService is PeerIntelligenceService
