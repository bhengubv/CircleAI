# mqtt.py
#
# CircleAI.Networking.Mqtt — MQTT-broker-backed network transport module.
#
# Ported faithfully from the C# spec:
#   MqttTransportCommons.cs -> MqttQos (enum), MqttTopicDescriptor,
#       MqttRetainedMessage, MqttClientDescriptor (records), InMemoryMqttBroker
#   MqttNetworkTransport.cs -> MqttNetworkTransport (INetworkTransport over an
#       MQTT broker), IMqttClient (the injected MQTTnet IMqttClient seam)
#
# The real C# transport wraps an MQTTnet ``IMqttClient``. Here the client is
# injected behind :class:`IMqttClient` (in-memory, no sockets). The transport
# publishes to ``circle/payloads/{destinationId}`` (or ``circle/payloads/broadcast``)
# and subscribes to ``circle/payloads/{localClientId}/#`` — ported exactly, as is
# the QoS mapping (Priority >= High -> ExactlyOnce, else AtLeastOnce). A working
# deterministic :class:`InMemoryMqttClient` bound to an :class:`InMemoryMqttBroker`
# round-trips published payloads to matching subscribers without a real broker.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime
from enum import IntEnum
from typing import (
    AsyncIterator,
    Callable,
    Dict,
    List,
    Optional,
    Sequence,
    Set,
)

from ._inbound import InboundChannel
from .interfaces import INetworkTransport
from .network_types import MessagePriority, NetworkPayload, TransportKind


class MqttQos(IntEnum):
    """MQTT quality-of-service level.

    Ordinals match the C# ``enum MqttQos { AtMostOnce = 0, AtLeastOnce = 1,
    ExactlyOnce = 2 }`` (identical to the MQTT wire QoS byte).
    """

    AT_MOST_ONCE = 0
    AT_LEAST_ONCE = 1
    EXACTLY_ONCE = 2


@dataclass(frozen=True, slots=True)
class MqttTopicDescriptor:
    """A topic + its QoS. Faithful port of the C# record."""

    topic: str
    qos: MqttQos


@dataclass(frozen=True, slots=True)
class MqttRetainedMessage:
    """A retained message for a topic. Faithful port of the C# record.

    ``payload`` is ``bytes`` (the C# ``ReadOnlyMemory<byte>``).
    """

    topic: str
    payload: bytes
    retained_at_utc: datetime


@dataclass(frozen=True, slots=True)
class MqttClientDescriptor:
    """Static configuration of an MQTT client. Faithful port of the C# record.

    ``keep_alive`` is seconds (the C# ``TimeSpan``).
    """

    client_id: str
    host: str
    port: int
    use_tls: bool
    keep_alive: float  # seconds


class InMemoryMqttBroker:
    """In-memory MQTT broker: retained store, client registry, subscription
    matcher. Faithful port of the C# ``InMemoryMqttBroker``.

    The :meth:`matches` topic-filter algorithm (``#`` multi-level, ``+``
    single-level wildcards) is ported verbatim from the C# ``Matches``.
    """

    def __init__(self) -> None:
        self._retained: Dict[str, MqttRetainedMessage] = {}
        self._clients: Dict[str, MqttClientDescriptor] = {}
        self._subscriptions: Dict[str, Set[str]] = {}
        # In-memory routing hook (NOT part of the C# broker API): per-client
        # inbound sinks the injected InMemoryMqttClient registers so a publish
        # can be delivered to matching subscribers without a real broker.
        self._delivery: Dict[str, "Callable[[str, bytes], None]"] = {}
        self._lock = threading.Lock()

    def connect(self, c: MqttClientDescriptor) -> None:
        if c is None:
            raise ValueError("client descriptor required")
        with self._lock:
            self._clients[c.client_id] = c

    def disconnect(self, client_id: str) -> None:
        with self._lock:
            self._clients.pop(client_id, None)

    @property
    def connected_clients(self) -> Sequence[MqttClientDescriptor]:
        with self._lock:
            return list(self._clients.values())

    def subscribe(self, client_id: str, topic_filter: str) -> None:
        if client_id is None or client_id.strip() == "":
            raise ValueError("clientId required")
        if topic_filter is None or topic_filter.strip() == "":
            raise ValueError("topicFilter required")
        with self._lock:
            self._subscriptions.setdefault(client_id, set()).add(topic_filter)

    def matches(self, topic: str, topic_filter: str) -> bool:
        """Return whether ``topic`` matches ``topic_filter``.

        Ported verbatim from the C# ``Matches``:
          • ``#`` matches this level and every deeper level (returns True).
          • ``+`` matches exactly one level (continue).
          • a literal segment must equal the topic's segment (ordinal).
          • otherwise the two must have equal segment counts.
        """
        if not topic or not topic_filter:
            return False
        t = topic.split("/")
        f = topic_filter.split("/")
        for i in range(len(f)):
            if f[i] == "#":
                return True
            if i >= len(t):
                return False
            if f[i] == "+":
                continue
            if f[i] != t[i]:
                return False
        return len(t) == len(f)

    def publish_retained(self, m: MqttRetainedMessage) -> None:
        if m is None:
            raise ValueError("retained message required")
        with self._lock:
            self._retained[m.topic] = m

    def get_retained(self, topic: str) -> Optional[MqttRetainedMessage]:
        with self._lock:
            return self._retained.get(topic)

    def matching_subscribers(self, topic: str) -> Sequence[str]:
        """Every client id with at least one subscription filter that matches
        ``topic`` (C#: ``Where(kv => kv.Value.Any(f => Matches(topic, f)))``).
        """
        with self._lock:
            items = list(self._subscriptions.items())
        return [
            client_id
            for client_id, filters in items
            if any(self.matches(topic, f) for f in filters)
        ]

    # ── in-memory delivery seam (used by InMemoryMqttClient) ──────────────────

    def _register_delivery(
        self, client_id: str, sink: "Callable[[str, bytes], None]"
    ) -> None:
        """Register a per-client inbound sink. Not part of the C# broker API —
        this is the in-memory routing hook the injected client uses to receive
        messages published to topics it is subscribed to.
        """
        with self._lock:
            self._delivery[client_id] = sink

    def _unregister_delivery(self, client_id: str) -> None:
        with self._lock:
            self._delivery.pop(client_id, None)

    def _deliver(self, topic: str, payload: bytes) -> None:
        """Route ``payload`` published on ``topic`` to every subscribed client's
        registered sink. Snapshot under the lock, release, then deliver.
        """
        with self._lock:
            targets = [
                sink
                for client_id, sink in self._delivery.items()
                if client_id in self._subscriptions
                and any(
                    self.matches(topic, f)
                    for f in self._subscriptions[client_id]
                )
            ]
        for sink in targets:
            sink(topic, payload)


class IMqttClient(ABC):
    """The injected MQTT client seam (replaces the MQTTnet ``IMqttClient``).

    ``MqttNetworkTransport`` drives connect / subscribe / publish through this
    seam and registers an inbound handler that the client invokes when a message
    arrives (the C# ``ApplicationMessageReceivedAsync`` event).
    """

    @property
    @abstractmethod
    def is_connected(self) -> bool:
        """Whether the client is currently connected to a broker."""
        ...

    @abstractmethod
    def set_message_handler(
        self, handler: Optional[Callable[[str, bytes], None]]
    ) -> None:
        """Register (or clear with ``None``) the inbound-message callback.

        The callback receives ``(topic, payload_bytes)`` for each delivered
        application message — the seam equivalent of subscribing to the C#
        ``ApplicationMessageReceivedAsync`` event.
        """
        ...

    @abstractmethod
    async def connect_async(self, *, ct: Optional[object] = None) -> None:
        """Open the broker connection (the C# ``ConnectAsync``)."""
        ...

    @abstractmethod
    async def disconnect_async(self, *, ct: Optional[object] = None) -> None:
        """Close the broker connection (the C# ``DisconnectAsync``)."""
        ...

    @abstractmethod
    async def subscribe_async(
        self, topic_filter: str, *, ct: Optional[object] = None
    ) -> None:
        """Subscribe to ``topic_filter`` (the C# ``SubscribeAsync``)."""
        ...

    @abstractmethod
    async def publish_async(
        self,
        topic: str,
        payload: bytes,
        qos: MqttQos,
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Publish ``payload`` to ``topic`` at ``qos`` (the C# ``PublishAsync``)."""
        ...

    def dispose(self) -> None:
        """Release the client (the C# ``Dispose``). Default no-op."""
        ...


class MqttNetworkTransport(INetworkTransport):
    """`INetworkTransport` backed by an MQTT broker. Faithful port of the C#
    ``MqttNetworkTransport``.

    Publishes to ``circle/payloads/{destinationId}`` (or
    ``circle/payloads/broadcast`` when no destination) and subscribes to
    ``circle/payloads/{localClientId}/#`` on start. QoS follows the payload
    priority: ``>= High`` -> :attr:`MqttQos.EXACTLY_ONCE`, else
    :attr:`MqttQos.AT_LEAST_ONCE` — exactly as C#. Received messages are pushed
    into an unbounded inbound channel that :meth:`receive_async` streams.
    """

    def __init__(self, client: IMqttClient, local_client_id: str) -> None:
        if client is None:
            raise ValueError("client required")
        if local_client_id is None or local_client_id.strip() == "":
            raise ValueError("local_client_id required")
        self._client = client
        self._local_client_id = local_client_id
        self._inbound: "InboundChannel[NetworkPayload]" = InboundChannel()
        # Wire the inbound handler now (the C# ctor subscribes to the event).
        self._client.set_message_handler(self._on_message_received)

    @property
    def kind(self) -> TransportKind:
        return TransportKind.MQTT

    @property
    def is_available(self) -> bool:
        return self._client.is_connected

    async def start_async(self, *, ct: Optional[object] = None) -> None:
        await self._client.connect_async(ct=ct)
        await self._client.subscribe_async(
            f"circle/payloads/{self._local_client_id}/#", ct=ct
        )

    async def stop_async(self, *, ct: Optional[object] = None) -> None:
        await self._client.disconnect_async(ct=ct)
        self._inbound.try_complete()

    async def send_async(
        self, payload: NetworkPayload, *, ct: Optional[object] = None
    ) -> None:
        if payload is None:
            raise ValueError("payload required")
        dest = payload.destination_id
        topic = (
            f"circle/payloads/{dest}"
            if dest
            else "circle/payloads/broadcast"
        )
        qos = (
            MqttQos.EXACTLY_ONCE
            if payload.priority >= MessagePriority.HIGH
            else MqttQos.AT_LEAST_ONCE
        )
        await self._client.publish_async(topic, payload.data, qos, ct=ct)

    def receive_async(
        self, *, ct: Optional[object] = None
    ) -> AsyncIterator[NetworkPayload]:
        return self._inbound.read_all()

    def _on_message_received(self, topic: str, payload: bytes) -> None:
        """Inbound handler (the C# ``OnMessageReceived``): wrap the raw bytes in
        a fresh :class:`NetworkPayload` and enqueue it (``TryWrite``).
        """
        self._inbound.write(NetworkPayload.create(bytes(payload)))

    def dispose(self) -> None:
        """Detach the handler and dispose the client (the C# ``DisposeAsync``)."""
        self._client.set_message_handler(None)
        self._client.dispose()


class InMemoryMqttClient(IMqttClient):
    """A working, deterministic :class:`IMqttClient` bound to an
    :class:`InMemoryMqttBroker`.

    :meth:`connect_async` registers this client with the broker; each
    :meth:`subscribe_async` records the filter on the broker and re-registers
    the delivery sink; :meth:`publish_async` routes the message through the
    broker to every subscribed client's sink (including this one — MQTT delivers
    a client's own publish back if it is subscribed to a matching filter). The
    inbound sink invokes the handler registered by the transport.
    """

    def __init__(
        self,
        broker: InMemoryMqttBroker,
        descriptor: MqttClientDescriptor,
    ) -> None:
        if broker is None:
            raise ValueError("broker required")
        if descriptor is None:
            raise ValueError("descriptor required")
        self._broker = broker
        self._descriptor = descriptor
        self._connected = False
        self._handler: Optional[Callable[[str, bytes], None]] = None
        self._published: List[tuple] = []
        self._lock = threading.Lock()

    @property
    def client_id(self) -> str:
        return self._descriptor.client_id

    @property
    def is_connected(self) -> bool:
        return self._connected

    @property
    def published(self) -> Sequence[tuple]:
        """Every (topic, payload, qos) published, in order (test/observability)."""
        with self._lock:
            return list(self._published)

    def set_message_handler(
        self, handler: Optional[Callable[[str, bytes], None]]
    ) -> None:
        self._handler = handler

    async def connect_async(self, *, ct: Optional[object] = None) -> None:
        self._connected = True
        self._broker.connect(self._descriptor)
        self._broker._register_delivery(
            self._descriptor.client_id, self._sink
        )

    async def disconnect_async(self, *, ct: Optional[object] = None) -> None:
        self._connected = False
        self._broker._unregister_delivery(self._descriptor.client_id)
        self._broker.disconnect(self._descriptor.client_id)

    async def subscribe_async(
        self, topic_filter: str, *, ct: Optional[object] = None
    ) -> None:
        self._broker.subscribe(self._descriptor.client_id, topic_filter)
        # Ensure the delivery sink is registered (idempotent).
        self._broker._register_delivery(
            self._descriptor.client_id, self._sink
        )

    async def publish_async(
        self,
        topic: str,
        payload: bytes,
        qos: MqttQos,
        *,
        ct: Optional[object] = None,
    ) -> None:
        with self._lock:
            self._published.append((topic, bytes(payload), qos))
        self._broker._deliver(topic, bytes(payload))

    def _sink(self, topic: str, payload: bytes) -> None:
        handler = self._handler
        if handler is not None:
            handler(topic, payload)

    def dispose(self) -> None:
        self._connected = False
        self._broker._unregister_delivery(self._descriptor.client_id)
