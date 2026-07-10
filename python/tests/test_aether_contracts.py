"""test_aether_contracts.py — Aether Contracts 2-5 in-memory implementations.

Covers presence/version (IAetherContext), auth-challenge floor enforcement,
the telemetry fan-out bus, the AI security layer (event -> directive), and the
intelligence output surface incl. the concurrency-safe trust-score stream.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timezone

import pytest

from circle_ai.aether import (
    AetherInstallLevel,
    AetherNodeEvent,
    AetherNodeEventKind,
    AetherNodeHealth,
    AetherSecurityEvent,
    AetherSecurityEventKind,
    AetherThreatLevel,
    AetherVersion,
    AuthChallengeReason,
    AuthMethod,
    InMemoryAISecurityLayer,
    InMemoryAetherContext,
    InMemoryAetherIntelligence,
    InMemoryAetherTelemetry,
    InMemoryAuthChallenge,
    ISecurityDirectiveConsumer,
    SecurityDirective,
    SecurityDirectiveKind,
)
from datetime import timedelta


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ── Contract 2 — context ─────────────────────────────────────────────────────


def test_version_comparison_and_parse():
    assert AetherVersion.parse("2.5.0") == AetherVersion(2, 5, 0, 0)
    assert AetherVersion(2, 5) >= AetherVersion(2, 0)
    assert AetherVersion(2, 0) < AetherVersion(2, 5)
    assert str(AetherVersion(2, 5, 1)) == "2.5.1.0"


def test_context_sufficient_and_requires_auth():
    ctx = InMemoryAetherContext(
        AetherInstallLevel.OS, "2.5.0", "2.0.0", is_enabled=True
    )
    assert ctx.is_sufficient
    assert ctx.requires_auth
    assert ctx.is_available
    assert ctx.is_enabled


def test_context_insufficient_when_runtime_below_minimum():
    ctx = InMemoryAetherContext(AetherInstallLevel.APP, "1.0.0", "2.0.0")
    assert not ctx.is_sufficient
    assert not ctx.requires_auth  # App level


def test_context_none_minimum_is_always_sufficient():
    ctx = InMemoryAetherContext(AetherInstallLevel.APP, "1.0.0", None)
    assert ctx.is_sufficient


def test_context_disabled_not_available():
    ctx = InMemoryAetherContext(AetherInstallLevel.OS, "2.0.0", None, is_enabled=False)
    assert not ctx.is_available
    assert not ctx.is_enabled


def test_context_none_install_not_available():
    ctx = InMemoryAetherContext(AetherInstallLevel.NONE, None, None, is_enabled=True)
    assert not ctx.is_available


# ── Contract 5 — auth challenge ──────────────────────────────────────────────


async def test_auth_challenge_raises_weak_minimum_to_floor():
    auth = InMemoryAuthChallenge(should_succeed=True, satisfied_method=AuthMethod.BIOMETRIC)
    # Requested a weaker method than the floor; the used method is raised to it.
    r = await auth.challenge_async(
        AuthChallengeReason.MANUAL_REQUEST, AuthMethod.BIOMETRIC, "prompt"
    )
    assert r.succeeded
    assert r.method_used == AuthMethod.BIOMETRIC_AND_DEVICE_ADMIN


async def test_auth_challenge_none_minimum_defaults_to_floor():
    auth = InMemoryAuthChallenge()
    r = await auth.challenge_async(AuthChallengeReason.PRIVILEGED_OPERATION, None, "p")
    assert r.method_used >= AuthMethod.BIOMETRIC_AND_DEVICE_ADMIN


async def test_auth_challenge_failure_path():
    auth = InMemoryAuthChallenge(should_succeed=False)
    r = await auth.challenge_async(AuthChallengeReason.OS_LEVEL_TOGGLE, None, "p")
    assert not r.succeeded
    assert r.failure_reason


async def test_os_toggle_always_demands_full_floor():
    auth = InMemoryAuthChallenge(satisfied_method=AuthMethod.CUSTOM)
    r = await auth.request_os_toggle_async(True)
    assert r.succeeded
    assert r.method_used >= AuthMethod.BIOMETRIC_AND_DEVICE_ADMIN


# ── Contract 1 — telemetry bus ───────────────────────────────────────────────


def test_telemetry_bus_fanout_and_dispose():
    from circle_ai.aether import IAetherTelemetryObserver

    class _Obs(IAetherTelemetryObserver):
        def __init__(self):
            self.security = []

        def on_node_event(self, e):
            ...

        def on_transport_event(self, e):
            ...

        def on_route_event(self, e):
            ...

        def on_security_event(self, e):
            self.security.append(e)

        def on_network_event(self, e):
            ...

    bus = InMemoryAetherTelemetry()
    o = _Obs()
    handle = bus.subscribe(o)
    assert bus.subscriber_count == 1
    evt = AetherSecurityEvent(
        "n", AetherSecurityEventKind.INTRUSION_SIGNAL, AetherThreatLevel.HIGH, "x", {}, _now()
    )
    bus.publish_security_event(evt)
    assert len(o.security) == 1
    handle.dispose()
    assert bus.subscriber_count == 0
    bus.publish_security_event(evt)  # no subscriber; no error, no delivery
    assert len(o.security) == 1


# ── Contract 4 — AI security layer ───────────────────────────────────────────


class _Recorder(ISecurityDirectiveConsumer):
    def __init__(self):
        self.directives = []

    def on_directive(self, directive):
        self.directives.append(directive)


async def test_security_layer_issues_directive_on_threshold_crossing():
    bus = InMemoryAetherTelemetry()
    layer = InMemoryAISecurityLayer()
    rec = _Recorder()
    layer.subscribe_to_directives(rec)
    await layer.start_async(bus)

    # One CRITICAL event degrades trust from 1.0 by 0.60 -> 0.40 -> crosses the
    # 0.50 AVOID band (but not 0.25 quarantine): expect exactly one AVOID_NODE.
    bus.publish_security_event(
        AetherSecurityEvent(
            "bad", AetherSecurityEventKind.INTRUSION_SIGNAL, AetherThreatLevel.CRITICAL,
            "probe", {}, _now(),
        )
    )
    assert len(rec.directives) == 1
    assert rec.directives[0].kind == SecurityDirectiveKind.AVOID_NODE
    assert rec.directives[0].target_node_id == "bad"

    # A second CRITICAL event: 0.40 -> 0.0 crosses quarantine -> QUARANTINE_NODE.
    bus.publish_security_event(
        AetherSecurityEvent(
            "bad", AetherSecurityEventKind.INTRUSION_SIGNAL, AetherThreatLevel.CRITICAL,
            "again", {}, _now(),
        )
    )
    assert rec.directives[-1].kind == SecurityDirectiveKind.QUARANTINE_NODE

    posture = await layer.get_posture_async()
    assert posture.is_active
    assert posture.quarantined_node_count == 1
    assert posture.overall_threat_level == AetherThreatLevel.CRITICAL
    await layer.stop_async()
    stopped = await layer.get_posture_async()
    assert not stopped.is_active


async def test_security_layer_none_threat_is_noop():
    bus = InMemoryAetherTelemetry()
    layer = InMemoryAISecurityLayer()
    rec = _Recorder()
    layer.subscribe_to_directives(rec)
    await layer.start_async(bus)
    bus.publish_security_event(
        AetherSecurityEvent(
            "n", AetherSecurityEventKind.ENCRYPTION_EVENT, AetherThreatLevel.NONE, "x", {}, _now()
        )
    )
    assert rec.directives == []
    await layer.stop_async()


async def test_security_layer_directive_unsubscribe():
    bus = InMemoryAetherTelemetry()
    layer = InMemoryAISecurityLayer()
    rec = _Recorder()
    sub = layer.subscribe_to_directives(rec)
    await layer.start_async(bus)
    sub.dispose()
    bus.publish_security_event(
        AetherSecurityEvent(
            "n", AetherSecurityEventKind.INTRUSION_SIGNAL, AetherThreatLevel.CRITICAL, "x", {}, _now()
        )
    )
    assert rec.directives == []
    await layer.stop_async()


# ── Contract 3 — intelligence ────────────────────────────────────────────────


async def test_intelligence_health_and_threat():
    intel = InMemoryAetherIntelligence()
    # Unknown node -> zero-confidence assessment.
    a0 = await intel.assess_threat_async("ghost")
    assert a0.threat_confidence == 0.0
    assert a0.level == AetherThreatLevel.NONE

    # Degrade a node with a HIGH event (0.35) then a CRITICAL (0.60) -> 0.05.
    intel.observe_security_event(
        AetherSecurityEvent(
            "n1", AetherSecurityEventKind.ROUTING_ANOMALY, AetherThreatLevel.HIGH, "r", {}, _now()
        )
    )
    intel.observe_security_event(
        AetherSecurityEvent(
            "n1", AetherSecurityEventKind.INTRUSION_SIGNAL, AetherThreatLevel.CRITICAL, "i", {}, _now()
        )
    )
    a1 = await intel.assess_threat_async("n1")
    assert a1.level == AetherThreatLevel.CRITICAL
    assert a1.threat_confidence > 0.9
    assert len(a1.indicators) == 2

    health = await intel.get_network_health_async()
    assert health.is_valid
    assert health.suspicious_node_count == 1

    advice = await intel.get_routing_advice_async("n1")
    assert advice.recommended_path == []  # quarantined -> no safe path
    assert "n1" in advice.avoid_nodes


async def test_intelligence_stream_trust_scores():
    intel = InMemoryAetherIntelligence()

    collected = []

    async def consume():
        async for u in intel.stream_trust_scores_async():
            collected.append(u)
            if len(collected) >= 2:
                return

    task = asyncio.create_task(consume())
    await asyncio.sleep(0)  # let the consumer subscribe to the queue

    intel.observe_security_event(
        AetherSecurityEvent(
            "n", AetherSecurityEventKind.ROUTING_ANOMALY, AetherThreatLevel.MEDIUM, "m1", {}, _now()
        )
    )
    intel.observe_security_event(
        AetherSecurityEvent(
            "n", AetherSecurityEventKind.ROUTING_ANOMALY, AetherThreatLevel.MEDIUM, "m2", {}, _now()
        )
    )
    await asyncio.wait_for(task, timeout=2.0)
    assert len(collected) == 2
    assert collected[0].has_changed
    assert collected[0].is_degraded


async def test_intelligence_empty_health():
    intel = InMemoryAetherIntelligence()
    h = await intel.get_network_health_async()
    assert h.overall_score == 1.0
    assert h.trusted_node_count == 0
