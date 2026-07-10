# extensibility.py
#
# The AetherNet-runtime seam that the CircleAI <-> AetherNet adapters bridge to.
#
# In the C# solution these types live in the separate AetherNet.Extensibility,
# AetherNet.Messaging, AetherNet.Protocol and AetherNet.Constants assemblies —
# the live mesh runtime. They are an EXTERNAL dependency of the CircleAI tree.
# Following the injection rule ("inject external/native/cloud/socket dependencies
# behind interfaces"), this module reproduces that seam as Python contracts +
# records so the adapters (EventTranslator, the telemetry/context/directive/AI
# bridges, and the companion-state channel) port faithfully and stay fully
# testable with in-memory fakes.
#
# The AetherNet event vocabulary was designed in parallel with CircleAI.Aether's,
# so the records here are 1:1 shape-compatible with circle_ai.aether.events —
# only the names differ (AetherNet* prefix). EventTranslator does the projection.

from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from enum import IntEnum
from typing import Callable, List, Mapping, Optional, Sequence
from uuid import UUID, uuid4


# ── Constants (AetherNet.Constants.ProtocolConstants) ─────────────────────────

# The current AetherNet wire-protocol version. In C# this is
# AetherNet.Constants.ProtocolConstants.CurrentProtocolVersion, resolved from the
# live runtime assembly. Exposed here as the default the context adapter reports;
# a host can override it at adapter construction.
CURRENT_PROTOCOL_VERSION = 2


# ── Event kinds + records (AetherNet.Extensibility.Events) ────────────────────


class AetherNetNodeEventKind(IntEnum):
    JOINED = 0
    LEFT = 1
    HEALTH_CHANGED = 2


@dataclass(frozen=True, slots=True)
class AetherNetNodeHealth:
    trust_score: float
    is_reachable: bool
    latency: timedelta
    hop_count: int


@dataclass(frozen=True, slots=True)
class AetherNetNodeEvent:
    node_id: str
    kind: AetherNetNodeEventKind
    health: AetherNetNodeHealth
    occurred_at: datetime


class AetherNetTransportKind(IntEnum):
    # AetherNet has more transports than CircleAI's OS-classification enum;
    # EventTranslator folds the extras (WiFiDirect, NearLink, HttpRelay).
    BLUETOOTH = 0
    WIFI = 1
    WIFI_DIRECT = 2
    LORA = 3
    NFC = 4
    NEAR_LINK = 5
    HTTP_RELAY = 6


class AetherNetTransportEventKind(IntEnum):
    SELECTED = 0
    CHANGED = 1
    LATENCY_MEASURED = 2
    PACKET_LOSS = 3


@dataclass(frozen=True, slots=True)
class AetherNetTransportEvent:
    node_id: str
    kind: AetherNetTransportEventKind
    transport: AetherNetTransportKind
    latency: Optional[timedelta]
    packet_loss_rate: Optional[float]
    occurred_at: datetime


class AetherNetRouteEventKind(IntEnum):
    DISCOVERED = 0
    CHANGED = 1
    FAILED = 2


@dataclass(frozen=True, slots=True)
class AetherNetRouteEvent:
    source_node_id: str
    destination_node_id: str
    path: Sequence[str]
    kind: AetherNetRouteEventKind
    failure_reason: Optional[str]
    occurred_at: datetime


class AetherNetSecurityEventKind(IntEnum):
    NODE_AUTH_ATTEMPT = 0
    ROUTING_ANOMALY = 1
    NODE_BEHAVIOUR_CHANGE = 2
    ENCRYPTION_EVENT = 3
    INTRUSION_SIGNAL = 4
    PRIVILEGE_ATTEMPT = 5


class AetherNetThreatLevel(IntEnum):
    NONE = 0
    LOW = 1
    MEDIUM = 2
    HIGH = 3
    CRITICAL = 4


@dataclass(frozen=True, slots=True)
class AetherNetSecurityEvent:
    node_id: str
    kind: AetherNetSecurityEventKind
    threat_level: AetherNetThreatLevel
    description: str
    metadata: Mapping[str, str]
    occurred_at: datetime


class AetherNetNetworkEventKind(IntEnum):
    TOPOLOGY_CHANGED = 0
    CONGESTION_DETECTED = 1
    PARTITION_DETECTED = 2


@dataclass(frozen=True, slots=True)
class AetherNetNetworkEvent:
    kind: AetherNetNetworkEventKind
    node_count: int
    active_route_count: int
    congestion_level: float
    occurred_at: datetime


# ── Telemetry bus (AetherNet.Extensibility.IAetherNetTelemetry) ───────────────


class IDisposable(ABC):
    """Subscription handle mirroring C# ``IDisposable``."""

    @abstractmethod
    def dispose(self) -> None:
        ...

    def __enter__(self) -> "IDisposable":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()


class IAetherNetTelemetryObserver(ABC):
    """The AetherNet-side telemetry observer contract."""

    @abstractmethod
    def on_node_event(self, e: AetherNetNodeEvent) -> None:
        ...

    @abstractmethod
    def on_transport_event(self, e: AetherNetTransportEvent) -> None:
        ...

    @abstractmethod
    def on_route_event(self, e: AetherNetRouteEvent) -> None:
        ...

    @abstractmethod
    def on_security_event(self, e: AetherNetSecurityEvent) -> None:
        ...

    @abstractmethod
    def on_network_event(self, e: AetherNetNetworkEvent) -> None:
        ...


class IAetherNetTelemetry(ABC):
    """The AetherNet mesh telemetry publisher. The CircleAI telemetry adapter
    subscribes here and translates each event into the CircleAI shape.
    """

    @abstractmethod
    def subscribe(self, observer: IAetherNetTelemetryObserver) -> IDisposable:
        ...


class InMemoryAetherNetTelemetry(IAetherNetTelemetry):
    """A working in-memory AetherNet telemetry bus — the fake a host or test
    uses in place of the live mesh runtime. Thread-safe fan-out; callbacks fire
    outside the lock.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._observers: List[IAetherNetTelemetryObserver] = []

    def subscribe(self, observer: IAetherNetTelemetryObserver) -> IDisposable:
        if observer is None:
            raise ValueError("observer must not be None")
        with self._lock:
            self._observers.append(observer)
        return _TelemetrySubscription(self, observer)

    @property
    def subscriber_count(self) -> int:
        with self._lock:
            return len(self._observers)

    def _snapshot(self) -> List[IAetherNetTelemetryObserver]:
        with self._lock:
            return list(self._observers)

    def _unsubscribe(self, observer: IAetherNetTelemetryObserver) -> None:
        with self._lock:
            try:
                self._observers.remove(observer)
            except ValueError:
                pass

    def publish_node_event(self, e: AetherNetNodeEvent) -> None:
        for o in self._snapshot():
            o.on_node_event(e)

    def publish_transport_event(self, e: AetherNetTransportEvent) -> None:
        for o in self._snapshot():
            o.on_transport_event(e)

    def publish_route_event(self, e: AetherNetRouteEvent) -> None:
        for o in self._snapshot():
            o.on_route_event(e)

    def publish_security_event(self, e: AetherNetSecurityEvent) -> None:
        for o in self._snapshot():
            o.on_security_event(e)

    def publish_network_event(self, e: AetherNetNetworkEvent) -> None:
        for o in self._snapshot():
            o.on_network_event(e)


class _TelemetrySubscription(IDisposable):
    def __init__(
        self, owner: InMemoryAetherNetTelemetry, observer: IAetherNetTelemetryObserver
    ) -> None:
        self._owner = owner
        self._observer = observer
        self._disposed = False
        self._lock = threading.Lock()

    def dispose(self) -> None:
        with self._lock:
            if self._disposed:
                return
            self._disposed = True
        self._owner._unsubscribe(self._observer)


# ── Directives (AetherNet.Extensibility.SecurityDirective + consumer) ─────────


class AetherNetSecurityDirectiveKind(IntEnum):
    UPDATE_NODE_TRUST = 0
    AVOID_NODE = 1
    QUARANTINE_NODE = 2
    RELEASE_NODE = 3
    REQUEST_REAUTH = 4
    ELEVATE_MONITORING = 5


@dataclass(frozen=True, slots=True)
class AetherNetSecurityDirective:
    """The AetherNet-side directive record (the mesh policy engine's vocabulary).
    Shape-identical to CircleAI's SecurityDirective; AetherNetDirectiveSink and
    AetherNetInboundDirectiveBridge translate between the two.
    """

    kind: AetherNetSecurityDirectiveKind
    target_node_id: Optional[str]
    trust_score_override: Optional[float]
    threat_level: AetherNetThreatLevel
    reason: str
    duration: Optional[timedelta]
    issued_at: datetime


class IAetherNetSecurityDirectiveConsumer(ABC):
    """The AetherNet policy-engine directive sink. AetherNetDirectiveSink forwards
    CircleAI directives here; the mesh runtime calls every registered consumer.
    """

    @abstractmethod
    def on_directive(self, directive: AetherNetSecurityDirective) -> None:
        ...


class RecordingAetherNetDirectiveConsumer(IAetherNetSecurityDirectiveConsumer):
    """An in-memory mesh policy-engine fake that records directives it receives —
    the stand-in for the live AetherNet policy engine in tests/hosts.
    """

    def __init__(self) -> None:
        self.received: List[AetherNetSecurityDirective] = []

    def on_directive(self, directive: AetherNetSecurityDirective) -> None:
        self.received.append(directive)


# ── AI provider seat (AetherNet.Extensibility.IAetherNetAiProvider) ───────────


class AiThreatLevel(IntEnum):
    """AetherNet's AI-seat threat level — only four values (no Critical).
    CircleAI's Critical folds to High when crossing into this enum.
    """

    NONE = 0
    LOW = 1
    MEDIUM = 2
    HIGH = 3


@dataclass(frozen=True, slots=True)
class AiRouteSuggestion:
    """A route the AI seat suggests to AetherNet's router."""

    path: Sequence[str]
    confidence: float


@dataclass(frozen=True, slots=True)
class AiNetworkHealthReport:
    """AetherNet's AI-seat network-health shape."""

    overall_score: float
    trusted_node_count: int
    suspicious_node_count: int
    summary: str
    generated_at: datetime


@dataclass(frozen=True, slots=True)
class MeshPacket:
    """A minimal AetherNet.Protocol.MeshPacket — enough for the AI seat's threat
    assessment, which only reads the source UHID.
    """

    source_uhid: str


class IAetherNetAiProvider(ABC):
    """AetherNet's AI extension seat. CircleAiAetherNetAiProvider implements this
    by delegating to CircleAI's IAetherIntelligence.
    """

    @property
    @abstractmethod
    def is_available(self) -> bool:
        ...

    @abstractmethod
    async def suggest_routes_async(
        self, destination_uhid: str, payload_bytes: int, cancellation_token: object = None
    ) -> Sequence[AiRouteSuggestion]:
        ...

    @abstractmethod
    async def get_transport_biases_async(
        self, payload_bytes: int, cancellation_token: object = None
    ) -> Mapping[str, float]:
        ...

    @abstractmethod
    async def assess_threat_async(
        self, packet: MeshPacket, cancellation_token: object = None
    ) -> AiThreatLevel:
        ...

    @abstractmethod
    async def get_network_health_async(
        self, cancellation_token: object = None
    ) -> AiNetworkHealthReport:
        ...


# ── Messaging (AetherNet.Messaging.IMessagingService + MeshMessage) ───────────


class MessageStatus(IntEnum):
    PENDING = 0
    SENT = 1
    DELIVERED = 2
    FAILED = 3


@dataclass
class MeshMessage:
    """Port of AetherNet.Messaging.Models.MeshMessage — the envelope the
    companion-state channel wraps sync payloads in. Mutable, matching the C#
    object-initialiser usage.
    """

    id: UUID = field(default_factory=uuid4)
    sender_uhid: str = ""
    recipient_uhid: str = ""
    message_type: str = ""
    priority: int = 0
    encrypted_content: bytes = b""
    status: MessageStatus = MessageStatus.PENDING
    created_at: Optional[datetime] = None


# handler(sender, message) -> None  (mirrors the C# EventHandler<MeshMessage>)
MessageReceivedHandler = Callable[[object, MeshMessage], None]


class IMessagingService(ABC):
    """Port of AetherNet.Messaging.IMessagingService — the live mesh messaging
    seam. AetherNetCompanionStateChannel sends MeshMessages here and subscribes
    to inbound ones. The service applies the Signal-Protocol E2E layer to the
    plaintext argument; the channel stays unaware of encryption.
    """

    @abstractmethod
    async def send_async(
        self, message: MeshMessage, plaintext: bytes, ct: Optional[object] = None
    ) -> None:
        ...

    @abstractmethod
    def add_message_received(self, handler: MessageReceivedHandler) -> None:
        """Subscribe to inbound messages (C# ``MessageReceived +=``)."""
        ...

    @abstractmethod
    def remove_message_received(self, handler: MessageReceivedHandler) -> None:
        """Unsubscribe from inbound messages (C# ``MessageReceived -=``)."""
        ...


class InMemoryMessagingService(IMessagingService):
    """A working in-memory :class:`IMessagingService`. Loopback bus that, on
    ``send_async``, decrypts nothing (the plaintext IS the delivered content, as
    the real service would after E2E) and dispatches to every subscribed
    handler. Handlers are snapshotted outside dispatch so a handler that
    re-subscribes cannot self-deadlock.

    ``delivered`` records every sent (message, plaintext) pair for assertions.
    """

    def __init__(self, deliver_locally: bool = True) -> None:
        self._lock = threading.Lock()
        self._handlers: List[MessageReceivedHandler] = []
        self._deliver_locally = deliver_locally
        self.delivered: List[tuple[MeshMessage, bytes]] = []

    async def send_async(
        self, message: MeshMessage, plaintext: bytes, ct: Optional[object] = None
    ) -> None:
        self.delivered.append((message, plaintext))
        if not self._deliver_locally:
            return
        # The live service delivers the DECRYPTED plaintext to peers. Model that
        # by stamping the plaintext onto encrypted_content of the delivered copy.
        from dataclasses import replace

        delivered = replace(message, encrypted_content=plaintext)
        with self._lock:
            handlers = list(self._handlers)
        for h in handlers:
            h(self, delivered)

    def add_message_received(self, handler: MessageReceivedHandler) -> None:
        with self._lock:
            self._handlers.append(handler)

    def remove_message_received(self, handler: MessageReceivedHandler) -> None:
        with self._lock:
            try:
                self._handlers.remove(handler)
            except ValueError:
                pass
