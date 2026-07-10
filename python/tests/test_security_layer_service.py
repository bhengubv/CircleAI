"""test_security_layer_service.py — SecurityLayerService (IPeerSecurityLayer).

Covers event handling -> trust degradation -> graduated directive issuance
(most-severe-first, one per event), posture snapshots, the background recovery
loop lifecycle, and the AISecurityLayerService alias.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone

import pytest

from circle_ai.security import (
    AISecurityLayerService,
    DirectivePublisher,
    IPeerDirectiveConsumer,
    NodeTrustRegistry,
    PeerDirectiveKind,
    PeerSecurityEvent,
    PeerSecurityEventKind,
    PeerThreatLevel,
    SecurityLayerService,
    SecurityOptions,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _event(node="n1", kind=PeerSecurityEventKind.INTRUSION_SIGNAL,
           level=PeerThreatLevel.LOW):
    return PeerSecurityEvent(node, kind, level, "probe", "wifi", _now())


class _Collector(IPeerDirectiveConsumer):
    def __init__(self):
        self.kinds = []

    def on_directive(self, d):
        self.kinds.append(d.kind)


def _wire():
    opts = SecurityOptions()
    reg = NodeTrustRegistry(opts)
    pub = DirectivePublisher()
    layer = SecurityLayerService(reg, opts, pub)
    return opts, reg, pub, layer


def test_none_level_event_has_no_trust_impact():
    _, reg, _, layer = _wire()
    layer.handle_peer_event(_event(level=PeerThreatLevel.NONE))
    assert reg.get_trust_score("n1") == 1.0
    assert reg.all_node_ids == []  # never created — degradation was 0


def test_graduated_directives_fire_in_order_once_each():
    _, reg, _, layer = _wire()
    c = _Collector()
    layer.subscribe_to_directives(c)
    # Small steps (0.15 * 0.5 = 0.075) so the score crosses each threshold
    # on a distinct event.
    for _ in range(60):
        layer.handle_peer_event(_event(level=PeerThreatLevel.LOW))
    assert reg.get_trust_score("n1") == 0.0
    # Each directive appears exactly once, in most-severe-later order.
    assert c.kinds.count(PeerDirectiveKind.ELEVATE_MONITORING) == 1
    assert c.kinds.count(PeerDirectiveKind.AVOID_NODE) == 1
    assert c.kinds.count(PeerDirectiveKind.QUARANTINE_NODE) == 1
    assert c.kinds == [
        PeerDirectiveKind.ELEVATE_MONITORING,
        PeerDirectiveKind.AVOID_NODE,
        PeerDirectiveKind.QUARANTINE_NODE,
    ]


def test_large_step_skips_intermediate_directive():
    # A single big drop past two thresholds issues only the most-severe one
    # (quarantine is evaluated first and returns).
    _, reg, _, layer = _wire()
    c = _Collector()
    layer.subscribe_to_directives(c)
    # First event: 1.0 -> 0.55 (crosses elevate only) => ELEVATE
    layer.handle_peer_event(_event(level=PeerThreatLevel.CRITICAL))  # 0.15*3 = 0.45
    # Second event: 0.55 -> 0.10 (crosses avoid AND quarantine) => QUARANTINE only
    layer.handle_peer_event(_event(level=PeerThreatLevel.CRITICAL))
    assert c.kinds == [
        PeerDirectiveKind.ELEVATE_MONITORING,
        PeerDirectiveKind.QUARANTINE_NODE,
    ]
    assert PeerDirectiveKind.AVOID_NODE not in c.kinds


async def test_posture_counts_quarantined_and_monitored():
    opts, reg, _, layer = _wire()
    # Node A: quarantined (drive to 0).
    for _ in range(10):
        layer.handle_peer_event(_event("a", level=PeerThreatLevel.CRITICAL))
    # Node B: monitored band (between quarantine and elevate).
    layer.handle_peer_event(_event("b", level=PeerThreatLevel.CRITICAL))  # 1.0 -> 0.55
    posture = await layer.get_posture_async()
    assert posture.quarantined_peer_count == 1
    assert posture.monitored_peer_count == 1
    assert posture.overall_threat_level == PeerThreatLevel.CRITICAL  # worst = A at 0


async def test_posture_empty_is_none_threat():
    _, _, _, layer = _wire()
    posture = await layer.get_posture_async()
    assert posture.quarantined_peer_count == 0
    assert posture.monitored_peer_count == 0
    assert posture.overall_threat_level == PeerThreatLevel.NONE
    assert posture.is_active is False


async def test_lifecycle_start_marks_active_stop_clears():
    _, _, _, layer = _wire()
    await layer.start_async()
    posture = await layer.get_posture_async()
    assert posture.is_active is True
    # Starting again is a no-op.
    await layer.start_async()
    await layer.stop_async()
    posture2 = await layer.get_posture_async()
    assert posture2.is_active is False


async def test_stop_without_start_is_safe():
    _, _, _, layer = _wire()
    await layer.stop_async()  # must not raise


def test_handle_peer_left_is_noop_and_retains_history():
    _, reg, _, layer = _wire()
    layer.handle_peer_event(_event("a", level=PeerThreatLevel.HIGH))
    score_before = reg.get_trust_score("a")
    layer.handle_peer_left("a")
    assert reg.get_trust_score("a") == score_before
    assert "a" in reg.all_node_ids


def test_alias_is_same_class():
    assert AISecurityLayerService is SecurityLayerService
