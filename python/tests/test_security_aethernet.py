"""test_security_aethernet.py — AetherNet-specific security bindings.

Covers the Aether<->Peer mapper, the lazy-expiry MeshDirectiveStore (+ Release
lifting blocks), the MeshSecurityGate decision/enforce surface, the
AetherSecurityBridge (Aether telemetry -> transport-agnostic layer -> Aether
directive), the AetherIntelligenceAdapter, and the MeshGatedCompanionSession
decorator.
"""
from __future__ import annotations

import asyncio
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.aether import (
    AetherSecurityEvent,
    AetherSecurityEventKind,
    AetherThreatLevel,
    InMemoryAetherTelemetry,
    ISecurityDirectiveConsumer,
    SecurityDirective,
    SecurityDirectiveKind,
)
from circle_ai.security import (
    DirectivePublisher,
    NodeTrustRegistry,
    PeerDirectiveKind,
    PeerIntelligenceService,
    PeerSecurityEventKind,
    PeerThreatLevel,
    SecurityLayerService,
    SecurityOptions,
)
from circle_ai.security_aethernet import (
    AetherIntelligenceAdapter,
    AetherMapper,
    AetherSecurityBridge,
    GateDecision,
    MeshDirectiveStore,
    MeshGatedCompanionSession,
    MeshSecurityBlockedException,
    MeshSecurityGate,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _directive(kind, node="n", reason="r", duration=None, issued=None):
    return SecurityDirective(
        kind=kind,
        target_node_id=node,
        trust_score_override=0.2,
        threat_level=AetherThreatLevel.HIGH,
        reason=reason,
        duration=duration,
        issued_at=issued or _now(),
    )


# ── AetherMapper ─────────────────────────────────────────────────────────────


def test_mapper_event_kind():
    assert (
        AetherMapper.to_peer_event_kind(AetherSecurityEventKind.NODE_AUTH_ATTEMPT)
        == PeerSecurityEventKind.AUTH_ATTEMPT
    )
    assert (
        AetherMapper.to_peer_event_kind(AetherSecurityEventKind.NODE_BEHAVIOUR_CHANGE)
        == PeerSecurityEventKind.BEHAVIOUR_CHANGE
    )


def test_mapper_threat_level_roundtrip():
    for lvl in AetherThreatLevel:
        peer = AetherMapper.to_peer_threat_level(lvl)
        back = AetherMapper.to_aether_threat_level(peer)
        assert int(back) == int(lvl)


def test_mapper_directive_kind():
    assert (
        AetherMapper.to_security_directive_kind(PeerDirectiveKind.QUARANTINE_NODE)
        == SecurityDirectiveKind.QUARANTINE_NODE
    )


# ── MeshDirectiveStore ───────────────────────────────────────────────────────


def test_store_blocks_on_quarantine_and_avoid():
    store = MeshDirectiveStore()
    store.on_directive(_directive(SecurityDirectiveKind.QUARANTINE_NODE, "n", "bad"))
    blocked, reason = store.is_blocked("n")
    assert blocked
    assert reason == "bad"


def test_store_non_block_kinds_do_not_block():
    store = MeshDirectiveStore()
    store.on_directive(_directive(SecurityDirectiveKind.ELEVATE_MONITORING, "n"))
    blocked, _ = store.is_blocked("n")
    assert not blocked
    # But it is still tracked as an active directive.
    assert len(store.get_active_directives("n")) == 1


def test_store_release_lifts_all_blocks():
    store = MeshDirectiveStore()
    store.on_directive(_directive(SecurityDirectiveKind.QUARANTINE_NODE, "n"))
    store.on_directive(_directive(SecurityDirectiveKind.AVOID_NODE, "n"))
    assert store.is_blocked("n")[0]
    store.on_directive(_directive(SecurityDirectiveKind.RELEASE_NODE, "n"))
    assert not store.is_blocked("n")[0]
    assert store.tracked_node_count == 0


def test_store_lazy_expiry_on_read():
    past = datetime(2020, 1, 1, tzinfo=timezone.utc)
    store = MeshDirectiveStore()
    # A directive issued in the past with a tiny duration is already expired.
    store.on_directive(
        _directive(
            SecurityDirectiveKind.QUARANTINE_NODE, "n",
            duration=timedelta(seconds=1), issued=past,
        )
    )
    blocked, _ = store.is_blocked("n")
    assert not blocked
    # Swept out on read.
    assert store.tracked_node_count == 0


def test_store_latest_block_reason_wins():
    t0 = datetime(2026, 7, 10, 12, 0, 0, tzinfo=timezone.utc)
    store = MeshDirectiveStore()
    store.on_directive(_directive(SecurityDirectiveKind.AVOID_NODE, "n", "old", issued=t0))
    store.on_directive(
        _directive(SecurityDirectiveKind.QUARANTINE_NODE, "n", "new",
                   issued=t0 + timedelta(seconds=5))
    )
    blocked, reason = store.is_blocked("n")
    assert blocked
    assert reason == "new"


def test_store_untargeted_directive_ignored():
    store = MeshDirectiveStore()
    d = SecurityDirective(
        SecurityDirectiveKind.AVOID_NODE, None, None, AetherThreatLevel.HIGH, "r", None, _now()
    )
    store.on_directive(d)
    assert store.tracked_node_count == 0


# ── MeshSecurityGate ─────────────────────────────────────────────────────────


def test_gate_decide_and_enforce():
    store = MeshDirectiveStore()
    store.on_directive(_directive(SecurityDirectiveKind.QUARANTINE_NODE, "n", "nope"))
    gate = MeshSecurityGate(store)

    dec = gate.decide("n")
    assert dec.is_blocked and dec.reason == "nope"
    assert gate.decide("clean") == GateDecision.allowed()
    assert gate.decide("  ").is_blocked is False

    with pytest.raises(MeshSecurityBlockedException) as ei:
        gate.enforce("n")
    assert ei.value.blocked_id == "n"
    gate.enforce("clean")  # no raise


# ── AetherSecurityBridge (end-to-end) ────────────────────────────────────────


async def test_security_bridge_telemetry_to_directive():
    opts = SecurityOptions()
    reg = NodeTrustRegistry(opts)
    pub = DirectivePublisher()
    layer = SecurityLayerService(reg, opts, pub)
    bridge = AetherSecurityBridge(layer)

    class _Rec(ISecurityDirectiveConsumer):
        def __init__(self):
            self.received = []

        def on_directive(self, d):
            self.received.append(d)

    rec = _Rec()
    bridge.subscribe_to_directives(rec)

    bus = InMemoryAetherTelemetry()
    await bridge.start_async(bus)

    # Feed enough CRITICAL security events to cross a directive threshold. The
    # transport-agnostic layer degrades trust and issues a PeerDirective, which
    # the bridge translates back to a CircleAI SecurityDirective.
    for _ in range(5):
        bus.publish_security_event(
            AetherSecurityEvent(
                "peerX", AetherSecurityEventKind.INTRUSION_SIGNAL,
                AetherThreatLevel.CRITICAL, "attack", {}, _now(),
            )
        )

    assert len(rec.received) >= 1
    kinds = {d.kind for d in rec.received}
    assert kinds & {
        SecurityDirectiveKind.AVOID_NODE,
        SecurityDirectiveKind.QUARANTINE_NODE,
        SecurityDirectiveKind.ELEVATE_MONITORING,
    }
    # All translated directives target the same node and carry Aether types.
    assert all(d.target_node_id == "peerX" for d in rec.received)

    posture = await bridge.get_posture_async()
    assert isinstance(posture.overall_threat_level, AetherThreatLevel)
    assert posture.is_active
    await bridge.stop_async()


# ── AetherIntelligenceAdapter ────────────────────────────────────────────────


async def test_intelligence_adapter_maps_peer_outputs():
    opts = SecurityOptions()
    reg = NodeTrustRegistry(opts)
    inner = PeerIntelligenceService(reg, opts)
    adapter = AetherIntelligenceAdapter(inner)

    health = await adapter.get_network_health_async()
    assert 0.0 <= health.overall_score <= 1.0

    assessment = await adapter.assess_threat_async("someNode")
    assert isinstance(assessment.level, AetherThreatLevel)
    # Aether record uses threat_confidence (mapped from peer confidence).
    assert 0.0 <= assessment.threat_confidence <= 1.0

    advice = await adapter.get_routing_advice_async("dest")
    assert advice.destination_node_id == "dest"


# ── MeshGatedCompanionSession ────────────────────────────────────────────────


class _FakeSession:
    """Minimal duck-typed companion session for decorator tests."""

    def __init__(self, identity_id="user1"):
        self._identity = identity_id
        self.on_proactive_message_ready = None
        self.sent = []

    @property
    def session_id(self):
        return "sess1"

    @property
    def identity_id(self):
        return self._identity

    @property
    def interface(self):
        from circle_ai.companion import InterfaceKind

        return InterfaceKind.MOBILE

    @property
    def history(self):
        return []

    async def send_async(self, message, *, ct=None):
        self.sent.append(message)
        return f"reply:{message}"

    async def stream_async(self, message, *, ct=None):
        for ch in ("a", "b"):
            yield ch

    async def agent_async(self, instruction, *, ct=None):
        return f"agent:{instruction}"

    def get_context(self):
        return "context"

    async def refresh_context_async(self, *, ct=None):
        return None

    async def signal_feedback_async(self, positive, note=None, *, ct=None):
        return None


async def test_gated_session_allows_when_not_blocked():
    inner = _FakeSession("user1")
    store = MeshDirectiveStore()
    gate = MeshSecurityGate(store)
    gated = MeshGatedCompanionSession(inner, gate)

    assert await gated.send_async("hi") == "reply:hi"
    chunks = [c async for c in gated.stream_async("go")]
    assert chunks == ["a", "b"]
    assert await gated.agent_async("do") == "agent:do"
    # Ungated pass-throughs work regardless.
    assert gated.get_context() == "context"
    assert gated.identity_id == "user1"


async def test_gated_session_blocks_message_paths_when_blocked():
    inner = _FakeSession("badUser")
    store = MeshDirectiveStore()
    store.on_directive(_directive(SecurityDirectiveKind.QUARANTINE_NODE, "badUser", "banned"))
    gate = MeshSecurityGate(store)
    gated = MeshGatedCompanionSession(inner, gate)

    with pytest.raises(MeshSecurityBlockedException):
        await gated.send_async("hi")
    with pytest.raises(MeshSecurityBlockedException):
        _ = [c async for c in gated.stream_async("go")]
    with pytest.raises(MeshSecurityBlockedException):
        await gated.agent_async("do")

    # Diagnostic pass-throughs remain available to a blocked user.
    assert gated.get_context() == "context"
    await gated.refresh_context_async()
    await gated.signal_feedback_async(True)
