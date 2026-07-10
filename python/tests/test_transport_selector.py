"""test_transport_selector.py

Verifies DefaultTransportSelector implements the cascade documented on the C#
ITransportSelector interface:
  gRPC -> WebSocket -> HTTP -> MQTT -> TCP ->
  WiFi -> Bluetooth -> NearLink -> Aether -> DTN -> LocalStore
and honours the INetworkPolicy gates (force / mesh-first / no-cloud / permits)
and the NetworkContext availability set.

Mirrors CircleAI.Networking.ITransportSelector (C# — the spec).
"""
from __future__ import annotations

from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    ConnectivityState,
    DefaultTransportSelector,
    NetworkContext,
    NetworkPayload,
    NetworkPolicyBuilder,
    TransportKind,
)


def _ctx(available, state=ConnectivityState.ONLINE) -> NetworkContext:
    return NetworkContext(
        state=state,
        preferred_transport=TransportKind.GRPC,
        available_transports=list(available),
        signal_strength_dbm=None,
        estimated_bandwidth_bps=None,
        latency_ms=None,
        nearby_peer_count=len(list(available)),
        snapshot_at=datetime.now(timezone.utc),
    )


_ALL = [
    TransportKind.GRPC,
    TransportKind.WEB_SOCKET,
    TransportKind.HTTP,
    TransportKind.MQTT,
    TransportKind.TCP,
    TransportKind.WIFI,
    TransportKind.BLUETOOTH,
    TransportKind.NEAR_LINK,
    TransportKind.AETHER,
    TransportKind.DTN,
]
_PAYLOAD = NetworkPayload.create(b"x")


def test_default_cascade_order_when_all_available() -> None:
    sel = DefaultTransportSelector()
    cascade = sel.get_cascade(_PAYLOAD, _ctx(_ALL))
    # LocalStore is always appended as the terminal offline fallback.
    assert cascade == _ALL + [TransportKind.LOCAL_STORE]
    assert sel.select_best(_PAYLOAD, _ctx(_ALL)) is TransportKind.GRPC


def test_cascade_filters_to_available_transports() -> None:
    sel = DefaultTransportSelector()
    ctx = _ctx([TransportKind.HTTP, TransportKind.WIFI])
    cascade = sel.get_cascade(_PAYLOAD, ctx)
    assert cascade == [
        TransportKind.HTTP,
        TransportKind.WIFI,
        TransportKind.LOCAL_STORE,
    ]
    assert sel.select_best(_PAYLOAD, ctx) is TransportKind.HTTP


def test_local_store_present_even_when_not_in_available_set() -> None:
    # The offline queue is a device-local capability, not a live link.
    sel = DefaultTransportSelector()
    cascade = sel.get_cascade(_PAYLOAD, _ctx([]))
    assert cascade == [TransportKind.LOCAL_STORE]
    assert sel.select_best(_PAYLOAD, _ctx([])) is TransportKind.LOCAL_STORE


def test_disable_queue_drops_local_store() -> None:
    sel = DefaultTransportSelector(NetworkPolicyBuilder().disable_queue().build())
    ctx = _ctx([TransportKind.WIFI])
    cascade = sel.get_cascade(_PAYLOAD, ctx)
    assert cascade == [TransportKind.WIFI]
    assert TransportKind.LOCAL_STORE not in cascade


def test_disable_queue_with_nothing_available_raises() -> None:
    sel = DefaultTransportSelector(NetworkPolicyBuilder().disable_queue().build())
    with pytest.raises(RuntimeError):
        sel.select_best(_PAYLOAD, _ctx([]))


def test_mesh_first_reorders_mesh_ahead_of_cloud() -> None:
    sel = DefaultTransportSelector(NetworkPolicyBuilder().mesh_first().build())
    cascade = sel.get_cascade(_PAYLOAD, _ctx(_ALL))
    assert cascade == [
        TransportKind.WIFI,
        TransportKind.BLUETOOTH,
        TransportKind.NEAR_LINK,
        TransportKind.AETHER,
        TransportKind.DTN,
        TransportKind.LOCAL_STORE,
        TransportKind.GRPC,
        TransportKind.WEB_SOCKET,
        TransportKind.HTTP,
        TransportKind.MQTT,
        TransportKind.TCP,
    ]
    assert sel.select_best(_PAYLOAD, _ctx(_ALL)) is TransportKind.WIFI


def test_no_cloud_removes_cloud_transports() -> None:
    sel = DefaultTransportSelector(NetworkPolicyBuilder().no_cloud().build())
    cascade = sel.get_cascade(_PAYLOAD, _ctx(_ALL))
    assert TransportKind.GRPC not in cascade
    assert TransportKind.WEB_SOCKET not in cascade
    assert TransportKind.HTTP not in cascade
    assert TransportKind.MQTT not in cascade
    assert cascade[0] is TransportKind.TCP


def test_allow_list_restricts_cascade() -> None:
    sel = DefaultTransportSelector(
        NetworkPolicyBuilder().allow(TransportKind.AETHER, TransportKind.DTN).build()
    )
    cascade = sel.get_cascade(_PAYLOAD, _ctx(_ALL))
    # LocalStore not in the allow-list -> excluded.
    assert cascade == [TransportKind.AETHER, TransportKind.DTN]


def test_force_transport_is_entire_cascade_when_permitted() -> None:
    sel = DefaultTransportSelector(
        NetworkPolicyBuilder().force(TransportKind.MQTT).build()
    )
    # Even though MQTT is not in the available set, an explicit force wins.
    cascade = sel.get_cascade(_PAYLOAD, _ctx([TransportKind.WIFI]))
    assert cascade == [TransportKind.MQTT]
    assert sel.select_best(_PAYLOAD, _ctx([TransportKind.WIFI])) is TransportKind.MQTT


def test_force_transport_blocked_by_no_cloud_yields_empty_and_raises() -> None:
    # force(HTTP) but no_cloud() forbids HTTP -> forced cascade is empty and
    # a forced-but-impossible selection raises rather than silently substituting.
    sel = DefaultTransportSelector(
        NetworkPolicyBuilder().force(TransportKind.HTTP).no_cloud().build()
    )
    assert sel.get_cascade(_PAYLOAD, _ctx(_ALL)) == []
    with pytest.raises(RuntimeError):
        sel.select_best(_PAYLOAD, _ctx(_ALL))


def test_offline_context_still_offers_local_store() -> None:
    sel = DefaultTransportSelector()
    ctx = _ctx([], state=ConnectivityState.OFFLINE)
    assert sel.select_best(_PAYLOAD, ctx) is TransportKind.LOCAL_STORE
