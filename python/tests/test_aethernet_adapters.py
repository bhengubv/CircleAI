"""test_aethernet_adapters.py — CircleAI <-> AetherNet bridge adapters.

Covers EventTranslator enum folding, the context adapter, telemetry translation
end-to-end, the bidirectional directive pipeline (sink + inbound bridge), the
CircleAI-brain-into-AetherNet-AI-seat provider, and the companion-state channel
JSON round-trip over the messaging seam.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.aether import (
    AetherThreatLevel,
    InMemoryAetherIntelligence,
    AetherSecurityEvent,
    AetherSecurityEventKind,
    SecurityDirective,
    SecurityDirectiveKind,
    ISecurityDirectiveConsumer,
    IAetherTelemetryObserver,
)
from circle_ai.aethernet import (
    AetherNetContextAdapter,
    AetherNetCompanionStateChannel,
    AetherNetDirectiveSink,
    AetherNetInboundDirectiveBridge,
    AetherNetNodeEvent,
    AetherNetNodeEventKind,
    AetherNetNodeHealth,
    AetherNetSecurityDirective,
    AetherNetSecurityDirectiveKind,
    AetherNetSecurityEvent,
    AetherNetSecurityEventKind,
    AetherNetTelemetryAdapter,
    AetherNetThreatLevel,
    AetherNetTransportKind,
    CircleAiAetherNetAiProvider,
    AiThreatLevel,
    EventTranslator,
    InMemoryAetherNetTelemetry,
    InMemoryMessagingService,
    MeshPacket,
    RecordingAetherNetDirectiveConsumer,
)
from circle_ai.memory.sync import SyncEnvelope, SyncEnvelopeKind, StateVectorEntry


def _now() -> datetime:
    return datetime.now(timezone.utc)


# ── EventTranslator folding ──────────────────────────────────────────────────


def test_translator_transport_folding():
    m = EventTranslator._map_transport
    from circle_ai.aether import AetherTransportKind as CA

    assert m(AetherNetTransportKind.WIFI_DIRECT) == CA.WIFI
    assert m(AetherNetTransportKind.NEAR_LINK) == CA.UNKNOWN
    assert m(AetherNetTransportKind.HTTP_RELAY) == CA.CELLULAR
    assert m(AetherNetTransportKind.BLUETOOTH) == CA.BLUETOOTH


def test_translator_threat_level_roundtrip():
    for lvl in AetherNetThreatLevel:
        ca = EventTranslator.map_threat_level(lvl)
        back = EventTranslator.map_threat_level_to_mesh(ca)
        assert int(back) == int(lvl)


def test_translator_directive_kind_roundtrip():
    for k in AetherNetSecurityDirectiveKind:
        ca = EventTranslator.map_directive_kind_from_mesh(k)
        back = EventTranslator.map_directive_kind_to_mesh(ca)
        assert int(back) == int(k)


def test_translator_node_event_projection():
    e = AetherNetNodeEvent(
        "n",
        AetherNetNodeEventKind.JOINED,
        AetherNetNodeHealth(0.9, True, timedelta(milliseconds=5), 3),
        _now(),
    )
    ca = EventTranslator.translate_node(e)
    assert ca.node_id == "n"
    assert ca.kind.name == "JOINED"
    assert ca.health.trust_score == 0.9
    assert ca.health.hop_count == 3


# ── Context adapter ──────────────────────────────────────────────────────────


def test_context_adapter_reports_app_level_and_version():
    from circle_ai.aether import AetherInstallLevel

    adapter = AetherNetContextAdapter(minimum_required="2.0.0", protocol_version=2)
    assert adapter.install_level == AetherInstallLevel.APP
    assert adapter.is_available
    assert adapter.is_enabled
    assert not adapter.requires_auth
    assert adapter.is_sufficient  # runtime 2.0.0.0 >= 2.0.0
    assert str(adapter.runtime_version) == "2.0.0.0"


def test_context_adapter_insufficient_when_minimum_higher():
    adapter = AetherNetContextAdapter(minimum_required="9.0.0", protocol_version=2)
    assert not adapter.is_sufficient


# ── Telemetry adapter end-to-end ─────────────────────────────────────────────


def test_telemetry_adapter_translates_and_fans_out():
    mesh = InMemoryAetherNetTelemetry()
    adapter = AetherNetTelemetryAdapter(mesh)

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

    o = _Obs()
    handle = adapter.subscribe(o)
    mesh.publish_security_event(
        AetherNetSecurityEvent(
            "n", AetherNetSecurityEventKind.INTRUSION_SIGNAL, AetherNetThreatLevel.HIGH, "x", {}, _now()
        )
    )
    assert len(o.security) == 1
    # Translated into the CircleAI shape.
    assert o.security[0].kind == AetherSecurityEventKind.INTRUSION_SIGNAL
    assert o.security[0].threat_level == AetherThreatLevel.HIGH
    handle.dispose()
    assert mesh.subscriber_count == 0


# ── Directive pipeline (outbound sink + inbound bridge) ──────────────────────


def test_directive_sink_forwards_to_mesh():
    mesh_consumer = RecordingAetherNetDirectiveConsumer()
    sink = AetherNetDirectiveSink(mesh_consumer)
    d = SecurityDirective(
        SecurityDirectiveKind.QUARANTINE_NODE, "n", 0.1, AetherThreatLevel.CRITICAL,
        "bad", timedelta(minutes=5), _now(),
    )
    sink.on_directive(d)
    assert len(mesh_consumer.received) == 1
    m = mesh_consumer.received[0]
    assert m.kind == AetherNetSecurityDirectiveKind.QUARANTINE_NODE
    assert m.target_node_id == "n"
    assert m.threat_level == AetherNetThreatLevel.CRITICAL
    assert m.duration == timedelta(minutes=5)


def test_inbound_bridge_forwards_to_circle():
    class _Rec(ISecurityDirectiveConsumer):
        def __init__(self):
            self.received = []

        def on_directive(self, directive):
            self.received.append(directive)

    circle = _Rec()
    bridge = AetherNetInboundDirectiveBridge(circle)
    m = AetherNetSecurityDirective(
        AetherNetSecurityDirectiveKind.AVOID_NODE, "n", 0.4, AetherNetThreatLevel.HIGH,
        "suspect", None, _now(),
    )
    bridge.on_directive(m)
    assert len(circle.received) == 1
    c = circle.received[0]
    assert c.kind == SecurityDirectiveKind.AVOID_NODE
    assert c.threat_level == AetherThreatLevel.HIGH
    assert c.is_permanent


# ── AI provider bridge ───────────────────────────────────────────────────────


async def test_ai_provider_delegates_to_intelligence():
    intel = InMemoryAetherIntelligence()
    provider = CircleAiAetherNetAiProvider(intel)
    assert provider.is_available

    # No routes / empty destination -> empty.
    assert list(await provider.suggest_routes_async("", 100)) == []
    # Biases are always empty (no signal).
    assert dict(await provider.get_transport_biases_async(100)) == {}

    # Threat: CRITICAL folds to HIGH in the AI seat.
    intel.observe_security_event(
        AetherSecurityEvent(
            "n1", AetherSecurityEventKind.INTRUSION_SIGNAL, AetherThreatLevel.CRITICAL, "i", {}, _now()
        )
    )
    intel.observe_security_event(
        AetherSecurityEvent(
            "n1", AetherSecurityEventKind.INTRUSION_SIGNAL, AetherThreatLevel.CRITICAL, "i", {}, _now()
        )
    )
    lvl = await provider.assess_threat_async(MeshPacket("n1"))
    assert lvl == AiThreatLevel.HIGH

    # Network health passes through.
    health = await provider.get_network_health_async()
    assert 0.0 <= health.overall_score <= 1.0


async def test_ai_provider_null_packet_is_none():
    provider = CircleAiAetherNetAiProvider(InMemoryAetherIntelligence())
    assert await provider.assess_threat_async(MeshPacket("  ")) == AiThreatLevel.NONE


async def test_ai_provider_suggest_routes_returns_path():
    intel = InMemoryAetherIntelligence()
    intel.observe_node_event  # ensure attr exists
    provider = CircleAiAetherNetAiProvider(intel)
    # A trusted (unknown -> default 1.0) destination yields a direct path.
    routes = list(await provider.suggest_routes_async("dest", 10))
    assert len(routes) == 1
    assert list(routes[0].path) == ["dest"]


# ── Companion state channel over the messaging seam ──────────────────────────


async def test_companion_channel_roundtrip():
    # Two nodes on one loopback messaging bus. A -> B delivers the envelope.
    bus = InMemoryMessagingService()
    chan_a = AetherNetCompanionStateChannel(bus, "A", ["B"])
    chan_b = AetherNetCompanionStateChannel(bus, "B", ["A"])

    received: list[SyncEnvelope] = []

    async def handler(env, ct):
        received.append(env)

    chan_b.subscribe(handler)

    env = SyncEnvelope(
        kind=SyncEnvelopeKind.ANNOUNCE,
        from_node_id="A",
        state_vector=[StateVectorEntry("PersonaState", 7)],
        requests=None,
        entries=None,
    )
    await chan_a.send_async(env)
    # Handler dispatch is scheduled on the running loop; let it run.
    await asyncio.sleep(0.01)

    assert len(received) == 1
    assert received[0].from_node_id == "A"
    assert received[0].kind == SyncEnvelopeKind.ANNOUNCE
    assert received[0].state_vector[0].entity_type == "PersonaState"
    assert received[0].state_vector[0].max_known_version == 7

    chan_a.dispose()
    chan_b.dispose()


async def test_companion_channel_skips_self_loopback():
    bus = InMemoryMessagingService()
    # A also lists itself as a peer; the inbound filter must drop self-sent msgs.
    chan_a = AetherNetCompanionStateChannel(bus, "A", ["A", "B"])
    got = []

    async def handler(env, ct):
        got.append(env)

    chan_a.subscribe(handler)
    env = SyncEnvelope(SyncEnvelopeKind.PUSH, "A", None, None, None)
    await chan_a.send_async(env)
    await asyncio.sleep(0.01)
    # Both A and B were peers, but A's own inbound copy is self-loopback -> dropped.
    assert got == []
    chan_a.dispose()


async def test_companion_channel_empty_peers_is_noop():
    bus = InMemoryMessagingService()
    chan = AetherNetCompanionStateChannel(bus, "A", [])
    env = SyncEnvelope(SyncEnvelopeKind.ANNOUNCE, "A", None, None, None)
    await chan.send_async(env)  # no peers -> no send
    assert bus.delivered == []
    chan.dispose()


def test_companion_channel_requires_local_uhid():
    bus = InMemoryMessagingService()
    with pytest.raises(ValueError):
        AetherNetCompanionStateChannel(bus, "  ", ["B"])
