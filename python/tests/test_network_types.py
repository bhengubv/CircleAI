"""test_network_types.py

Verifies the networking core enums, records, and policy primitives:
TransportKind/ConnectivityState/MessagePriority/PeerRole ordinals, the
NetworkPayload/NetworkContext/PeerInfo records, and the
INetworkPolicy/DefaultNetworkPolicy/NetworkPolicyBuilder policy layer.

Mirrors CircleAI.Networking NetworkTypes.cs / NetworkPayload.cs /
NetworkContext.cs / PeerInfo.cs / DefaultNetworkPolicy.cs /
NetworkPolicyBuilder.cs (C# — the spec).
"""
from __future__ import annotations

import dataclasses
from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    ConnectivityState,
    DefaultNetworkPolicy,
    MessagePriority,
    NetworkContext,
    NetworkPayload,
    NetworkPolicyBuilder,
    PeerInfo,
    PeerRole,
    TransportKind,
)


# ── enum ordinals (must match C# declaration order exactly) ──────────────────


def test_transport_kind_ordinals_match_csharp() -> None:
    assert [int(k) for k in TransportKind] == list(range(12))
    assert int(TransportKind.HTTP) == 0
    assert int(TransportKind.WEB_SOCKET) == 1
    assert int(TransportKind.GRPC) == 2
    assert int(TransportKind.MQTT) == 3
    assert int(TransportKind.TCP) == 4
    assert int(TransportKind.UDP) == 5
    assert int(TransportKind.WIFI) == 6
    assert int(TransportKind.BLUETOOTH) == 7
    assert int(TransportKind.NEAR_LINK) == 8
    assert int(TransportKind.AETHER) == 9
    assert int(TransportKind.DTN) == 10
    assert int(TransportKind.LOCAL_STORE) == 11


def test_connectivity_state_ordinals() -> None:
    assert int(ConnectivityState.ONLINE) == 0
    assert int(ConnectivityState.LOCAL_ONLY) == 1
    assert int(ConnectivityState.MESH_ONLY) == 2
    assert int(ConnectivityState.OFFLINE) == 3


def test_message_priority_ordinals() -> None:
    assert [int(p) for p in MessagePriority] == [0, 1, 2, 3, 4]
    assert int(MessagePriority.EMERGENCY) == 4


def test_peer_role_ordinals() -> None:
    assert int(PeerRole.PEER) == 0
    assert int(PeerRole.RELAY) == 1
    assert int(PeerRole.BRIDGE) == 2
    assert int(PeerRole.SINK) == 3


# ── NetworkPayload ───────────────────────────────────────────────────────────


def test_payload_create_defaults() -> None:
    p = NetworkPayload.create(b"abc")
    assert isinstance(p.id, str) and len(p.id) == 32  # Guid "N" -> 32 hex chars
    assert all(c in "0123456789abcdef" for c in p.id)
    assert p.source_id is None
    assert p.destination_id is None
    assert p.data == b"abc"
    assert p.priority is MessagePriority.NORMAL
    assert p.ttl is None
    assert p.content_type == "application/octet-stream"
    assert p.metadata == {}
    assert p.created_at.tzinfo is not None


def test_payload_create_overrides() -> None:
    p = NetworkPayload.create(
        b"x",
        destination_id="node-2",
        priority=MessagePriority.URGENT,
        content_type="application/json",
        ttl=30.0,
    )
    assert p.destination_id == "node-2"
    assert p.priority is MessagePriority.URGENT
    assert p.content_type == "application/json"
    assert p.ttl == 30.0


def test_payload_create_generates_unique_ids() -> None:
    ids = {NetworkPayload.create(b"").id for _ in range(50)}
    assert len(ids) == 50


def test_payload_is_frozen() -> None:
    p = NetworkPayload.create(b"x")
    with pytest.raises(dataclasses.FrozenInstanceError):
        p.data = b"y"  # type: ignore[misc]


# ── NetworkContext ───────────────────────────────────────────────────────────


def test_network_context_offline() -> None:
    c = NetworkContext.offline()
    assert c.state is ConnectivityState.OFFLINE
    assert c.preferred_transport is TransportKind.LOCAL_STORE
    assert list(c.available_transports) == []
    assert c.signal_strength_dbm is None
    assert c.estimated_bandwidth_bps is None
    assert c.latency_ms is None
    assert c.nearby_peer_count == 0
    assert c.snapshot_at.tzinfo is not None


def test_network_context_offline_stamps_fresh_time() -> None:
    a = NetworkContext.offline()
    b = NetworkContext.offline()
    # Each call stamps its own snapshot time (not a frozen-at-import constant).
    assert b.snapshot_at >= a.snapshot_at


def test_network_context_is_frozen() -> None:
    c = NetworkContext.offline()
    with pytest.raises(dataclasses.FrozenInstanceError):
        c.state = ConnectivityState.ONLINE  # type: ignore[misc]


# ── PeerInfo ─────────────────────────────────────────────────────────────────


def test_peer_info_record() -> None:
    now = datetime.now(timezone.utc)
    peer = PeerInfo(
        node_id="n1",
        display_name="Alice",
        supported_transports=[TransportKind.AETHER, TransportKind.WIFI],
        role=PeerRole.RELAY,
        signal_strength_dbm=-55,
        last_seen=now,
    )
    assert peer.node_id == "n1"
    assert peer.role is PeerRole.RELAY
    assert list(peer.supported_transports) == [
        TransportKind.AETHER,
        TransportKind.WIFI,
    ]
    with pytest.raises(dataclasses.FrozenInstanceError):
        peer.node_id = "n2"  # type: ignore[misc]


# ── DefaultNetworkPolicy ─────────────────────────────────────────────────────


def test_default_policy_is_permissive_singleton() -> None:
    d = DefaultNetworkPolicy.INSTANCE
    assert d is DefaultNetworkPolicy.INSTANCE
    p = NetworkPayload.create(b"x")
    assert all(d.permits(k, p) for k in TransportKind)
    assert d.force_transport is None
    assert d.mesh_first is False
    assert d.offline_queue_enabled is True
    assert d.allow_cloud_transports is True


# ── NetworkPolicyBuilder ─────────────────────────────────────────────────────


def test_builder_defaults_permit_everything() -> None:
    pol = NetworkPolicyBuilder().build()
    p = NetworkPayload.create(b"x")
    assert all(pol.permits(k, p) for k in TransportKind)
    assert pol.force_transport is None
    assert pol.mesh_first is False
    assert pol.offline_queue_enabled is True
    assert pol.allow_cloud_transports is True


def test_builder_allow_list_restricts() -> None:
    pol = NetworkPolicyBuilder().allow(
        TransportKind.WIFI, TransportKind.BLUETOOTH
    ).build()
    p = NetworkPayload.create(b"x")
    assert pol.permits(TransportKind.WIFI, p) is True
    assert pol.permits(TransportKind.BLUETOOTH, p) is True
    assert pol.permits(TransportKind.HTTP, p) is False
    assert pol.permits(TransportKind.AETHER, p) is False


def test_builder_no_cloud_blocks_cloud_transports() -> None:
    pol = NetworkPolicyBuilder().no_cloud().build()
    p = NetworkPayload.create(b"x")
    for cloud in (
        TransportKind.HTTP,
        TransportKind.WEB_SOCKET,
        TransportKind.GRPC,
        TransportKind.MQTT,
    ):
        assert pol.permits(cloud, p) is False
    # Non-cloud still permitted.
    assert pol.permits(TransportKind.WIFI, p) is True
    assert pol.permits(TransportKind.AETHER, p) is True
    assert pol.allow_cloud_transports is False


def test_builder_mesh_first_flag() -> None:
    pol = NetworkPolicyBuilder().mesh_first().build()
    assert pol.mesh_first is True


def test_builder_disable_queue_flag() -> None:
    pol = NetworkPolicyBuilder().disable_queue().build()
    assert pol.offline_queue_enabled is False


def test_builder_force_transport() -> None:
    pol = NetworkPolicyBuilder().force(TransportKind.MQTT).build()
    assert pol.force_transport is TransportKind.MQTT


def test_builder_is_chainable_and_returns_self() -> None:
    b = NetworkPolicyBuilder()
    assert (
        b.mesh_first()
        .no_cloud()
        .disable_queue()
        .force(TransportKind.TCP)
        .allow(TransportKind.TCP)
    ) is b


def test_builder_no_cloud_takes_precedence_over_allow() -> None:
    # Allowing a cloud transport does not override the no-cloud guard.
    pol = NetworkPolicyBuilder().no_cloud().allow(TransportKind.HTTP).build()
    p = NetworkPayload.create(b"x")
    assert pol.permits(TransportKind.HTTP, p) is False
