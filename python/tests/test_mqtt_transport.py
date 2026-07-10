"""test_mqtt_transport.py

Verifies the MQTT transport module: MqttQos ordinals, the topic /
retained-message / client-descriptor records, the InMemoryMqttBroker
subscription matcher + retained store, the IMqttClient seam +
InMemoryMqttClient, and MqttNetworkTransport start/stop/send/receive wired to an
InMemoryMqttClient (topic scheme, QoS mapping, and the Wave-1 concurrency
guarantees).

Mirrors CircleAI.Networking.Mqtt MqttTransportCommons.cs /
MqttNetworkTransport.cs (C# — the spec).
"""
from __future__ import annotations

import asyncio
import dataclasses
from datetime import datetime, timezone

import pytest

from circle_ai.networking import (
    IMqttClient,
    InMemoryMqttBroker,
    InMemoryMqttClient,
    MqttClientDescriptor,
    MqttNetworkTransport,
    MqttQos,
    MqttRetainedMessage,
    MqttTopicDescriptor,
    NetworkPayload,
    TransportKind,
)
from circle_ai.networking.network_types import MessagePriority


def _now() -> datetime:
    return datetime.now(timezone.utc)


async def _next(it, timeout: float = 1.0):
    return await asyncio.wait_for(it.__anext__(), timeout=timeout)


def _client(broker: InMemoryMqttBroker, client_id: str) -> InMemoryMqttClient:
    return InMemoryMqttClient(
        broker, MqttClientDescriptor(client_id, "h", 1883, False, 60.0)
    )


# ── MqttQos ──────────────────────────────────────────────────────────────────


def test_qos_ordinals_match_csharp() -> None:
    assert int(MqttQos.AT_MOST_ONCE) == 0
    assert int(MqttQos.AT_LEAST_ONCE) == 1
    assert int(MqttQos.EXACTLY_ONCE) == 2


# ── records ──────────────────────────────────────────────────────────────────


def test_topic_descriptor_is_frozen() -> None:
    d = MqttTopicDescriptor("a/b", MqttQos.AT_LEAST_ONCE)
    assert d.topic == "a/b"
    assert d.qos is MqttQos.AT_LEAST_ONCE
    with pytest.raises(dataclasses.FrozenInstanceError):
        d.topic = "x"  # type: ignore[misc]


def test_retained_message_and_client_descriptor_records() -> None:
    m = MqttRetainedMessage("t", b"payload", _now())
    assert m.payload == b"payload"
    c = MqttClientDescriptor("cid", "broker.local", 8883, True, 30.0)
    assert c.use_tls is True
    assert c.keep_alive == 30.0
    with pytest.raises(dataclasses.FrozenInstanceError):
        c.port = 1  # type: ignore[misc]


# ── InMemoryMqttBroker: subscription matcher (ported verbatim) ────────────────


def test_matches_multi_level_hash_wildcard() -> None:
    b = InMemoryMqttBroker()
    assert b.matches("a/b/c", "a/#") is True
    assert b.matches("a/b/c", "#") is True
    # '#' matches zero trailing levels too.
    assert b.matches("a", "a/#") is True


def test_matches_single_level_plus_wildcard() -> None:
    b = InMemoryMqttBroker()
    assert b.matches("a/b/c", "a/+/c") is True
    assert b.matches("a/b/c", "a/+/d") is False
    assert b.matches("a/b/c/d", "a/+/c") is False  # length mismatch


def test_matches_exact_and_length_rules() -> None:
    b = InMemoryMqttBroker()
    assert b.matches("a/b", "a/b") is True
    assert b.matches("a/b/c", "a/b") is False  # filter shorter, no '#'
    assert b.matches("a/b", "a/b/c") is False  # filter longer
    assert b.matches("", "a") is False
    assert b.matches("a", "") is False


# ── InMemoryMqttBroker: clients / subscriptions / retained ────────────────────


def test_connect_disconnect_tracks_clients() -> None:
    b = InMemoryMqttBroker()
    c = MqttClientDescriptor("c1", "h", 1883, False, 60.0)
    b.connect(c)
    assert [x.client_id for x in b.connected_clients] == ["c1"]
    b.disconnect("c1")
    assert list(b.connected_clients) == []


def test_connect_rejects_none() -> None:
    b = InMemoryMqttBroker()
    with pytest.raises(ValueError):
        b.connect(None)  # type: ignore[arg-type]


def test_subscribe_validates_args() -> None:
    b = InMemoryMqttBroker()
    with pytest.raises(ValueError):
        b.subscribe("  ", "topic")
    with pytest.raises(ValueError):
        b.subscribe("c1", "  ")


def test_matching_subscribers_uses_filters() -> None:
    b = InMemoryMqttBroker()
    b.subscribe("c1", "circle/payloads/c1/#")
    b.subscribe("c2", "circle/payloads/+/urgent")
    assert set(b.matching_subscribers("circle/payloads/c1")) == {"c1"}
    assert set(b.matching_subscribers("circle/payloads/x/urgent")) == {"c2"}
    assert list(b.matching_subscribers("other/topic")) == []


def test_retained_store_roundtrip() -> None:
    b = InMemoryMqttBroker()
    assert b.get_retained("t") is None
    m = MqttRetainedMessage("t", b"r", _now())
    b.publish_retained(m)
    assert b.get_retained("t") is m
    with pytest.raises(ValueError):
        b.publish_retained(None)  # type: ignore[arg-type]


# ── MqttNetworkTransport ─────────────────────────────────────────────────────


def test_transport_kind_is_mqtt() -> None:
    b = InMemoryMqttBroker()
    t = MqttNetworkTransport(_client(b, "cli"), "cli")
    assert t.kind is TransportKind.MQTT


def test_transport_rejects_bad_args() -> None:
    b = InMemoryMqttBroker()
    with pytest.raises(ValueError):
        MqttNetworkTransport(None, "cli")  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        MqttNetworkTransport(_client(b, "cli"), "  ")


async def test_start_connects_and_subscribes_self_topic() -> None:
    b = InMemoryMqttBroker()
    client = _client(b, "cli1")
    t = MqttNetworkTransport(client, "cli1")
    assert t.is_available is False
    await t.start_async()
    assert t.is_available is True
    # Subscribed to circle/payloads/cli1/#  -> receives messages on cli1.
    assert set(b.matching_subscribers("circle/payloads/cli1")) == {"cli1"}


async def test_send_publishes_and_receives_own_topic() -> None:
    b = InMemoryMqttBroker()
    client = _client(b, "cli1")
    t = MqttNetworkTransport(client, "cli1")
    await t.start_async()
    rx = t.receive_async()
    await t.send_async(
        NetworkPayload.create(b"hi", destination_id="cli1")
    )
    got = await _next(rx)
    assert got.data == b"hi"
    # Published to the destination topic.
    assert client.published[-1][0] == "circle/payloads/cli1"


async def test_send_qos_maps_priority() -> None:
    b = InMemoryMqttBroker()
    client = _client(b, "cli1")
    t = MqttNetworkTransport(client, "cli1")
    await t.start_async()
    # Normal -> AtLeastOnce
    await t.send_async(
        NetworkPayload.create(b"n", destination_id="cli1",
                              priority=MessagePriority.NORMAL)
    )
    assert client.published[-1][2] is MqttQos.AT_LEAST_ONCE
    # High -> ExactlyOnce
    await t.send_async(
        NetworkPayload.create(b"h", destination_id="cli1",
                              priority=MessagePriority.HIGH)
    )
    assert client.published[-1][2] is MqttQos.EXACTLY_ONCE
    # Emergency (>= High) -> ExactlyOnce
    await t.send_async(
        NetworkPayload.create(b"e", destination_id="cli1",
                              priority=MessagePriority.EMERGENCY)
    )
    assert client.published[-1][2] is MqttQos.EXACTLY_ONCE


async def test_send_without_destination_uses_broadcast_topic() -> None:
    b = InMemoryMqttBroker()
    client = _client(b, "cli1")
    t = MqttNetworkTransport(client, "cli1")
    await t.start_async()
    await t.send_async(NetworkPayload.create(b"x"))
    assert client.published[-1][0] == "circle/payloads/broadcast"


async def test_two_clients_deliver_across_broker() -> None:
    b = InMemoryMqttBroker()
    rx_client = _client(b, "rx")
    tx_client = _client(b, "tx")
    rx_t = MqttNetworkTransport(rx_client, "rx")
    tx_t = MqttNetworkTransport(tx_client, "tx")
    await rx_t.start_async()
    await tx_t.start_async()
    rx = rx_t.receive_async()
    # tx sends addressed to rx -> topic circle/payloads/rx -> rx is subscribed.
    await tx_t.send_async(NetworkPayload.create(b"cross", destination_id="rx"))
    got = await _next(rx)
    assert got.data == b"cross"


async def test_message_immediately_after_subscribe_not_lost() -> None:
    b = InMemoryMqttBroker()
    client = _client(b, "cli1")
    t = MqttNetworkTransport(client, "cli1")
    await t.start_async()
    rx = t.receive_async()  # registers synchronously
    await t.send_async(NetworkPayload.create(b"race", destination_id="cli1"))
    assert (await _next(rx)).data == b"race"


async def test_unbounded_buffering_retains_predrain_messages() -> None:
    b = InMemoryMqttBroker()
    client = _client(b, "cli1")
    t = MqttNetworkTransport(client, "cli1")
    await t.start_async()
    rx = t.receive_async()
    for i in range(12):
        await t.send_async(
            NetworkPayload.create(str(i).encode(), destination_id="cli1")
        )
    received = [(await _next(rx)).data for _ in range(12)]
    assert received == [str(i).encode() for i in range(12)]


async def test_stop_completes_receive_without_deadlock() -> None:
    b = InMemoryMqttBroker()
    client = _client(b, "cli1")
    t = MqttNetworkTransport(client, "cli1")
    await t.start_async()
    rx = t.receive_async()
    await t.stop_async()
    assert t.is_available is False
    collected = [item async for item in rx]
    assert collected == []
