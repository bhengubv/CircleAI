# circle_ai.networking — the transport ABSTRACTION the 10 concrete transports
# implement. In-memory / deterministic; a real socket is injected behind
# INetworkTransport.
#
# Ported faithfully from CircleAI.Networking (C# — the spec). SyncDeliveryMode
# is NOT redefined here — it is reused from circle_ai.sync (SyncDelta /
# ISyncChannel / SchedulingHint likewise live in the sync module).

from __future__ import annotations

from .network_types import (
    ConnectivityState,
    MessagePriority,
    NetworkContext,
    NetworkPayload,
    PeerInfo,
    PeerRole,
    TransportKind,
)
from .network_policy import (
    DefaultNetworkPolicy,
    INetworkPolicy,
    NetworkPolicyBuilder,
)
from .interfaces import (
    IConnectivityMonitor,
    IMeshNetwork,
    IMessageChannel,
    INetworkTransport,
    IPeerDiscovery,
    ITransportSelector,
)
from .transport_selector import DefaultTransportSelector
from .in_memory_transport import (
    InMemoryMeshNetwork,
    InMemoryNetworkTransport,
    InMemoryWire,
)
from .message_channel import (
    IMessageSerializer,
    JsonMessageSerializer,
    TransportMessageChannel,
)
from .connectivity_monitor import InMemoryConnectivityMonitor
from ._inbound import InboundChannel

# ── concrete transports (Networking wave A) ──────────────────────────────────
from .aethernet import (
    AetherHopTelemetry,
    AetherNetworkTransport,
    AetherPacketSummary,
    AetherPeer,
    AetherPeerDiscovery,
    AetherPeerKind,
    AetherSyncChannel,
    IAetherMeshEngine,
    InMemoryAetherMeshEngine,
    InMemoryAetherNetRegistry,
)
from .bluetooth import (
    BluetoothCapabilityProfile,
    BluetoothCapabilityProfiles,
    BluetoothConnectionState,
    BluetoothEndpointDescriptor,
    BluetoothNetworkTransport,
    BluetoothThroughputSample,
    IBleGattAdapter,
    InMemoryBleGattAdapter,
    InMemoryBluetoothTransportRegistry,
)
from .dtn import (
    DtnBundle,
    DtnCustodyRecord,
    DtnPriority,
    DtnSyncChannel,
    InMemoryDtnBundleStore,
)
from .grpc import (
    GrpcCallSummary,
    GrpcChannelDescriptor,
    GrpcChannelState,
    GrpcConnectionState,
    GrpcDeadline,
    GrpcNetworkTransport,
    GrpcReconnectPolicy,
    GrpcRetryPolicies,
    GrpcRetryPolicy,
    GrpcSendNotSupportedError,
    IGrpcChannel,
    InMemoryGrpcCallMetrics,
    InMemoryGrpcChannel,
)
from .http import (
    HttpCacheKey,
    HttpEndpointDescriptor,
    HttpNetworkTransport,
    HttpRequestException,
    HttpRequestFailedError,
    HttpRequestSummary,
    HttpStatusFamily,
    HttpTransientError,
    IHttpMessageSender,
    InMemoryHttpMessageSender,
    InMemoryHttpRequestMetrics,
)

# ── concrete transports (Networking wave B) ──────────────────────────────────
from .mqtt import (
    IMqttClient,
    InMemoryMqttBroker,
    InMemoryMqttClient,
    MqttClientDescriptor,
    MqttNetworkTransport,
    MqttQos,
    MqttRetainedMessage,
    MqttTopicDescriptor,
)
from .nearlink import (
    INearLinkAdapter,
    InMemoryNearLinkAdapter,
    InMemoryNearLinkRegistry,
    NearLinkDevice,
    NearLinkPairingState,
    NearLinkPowerProfile,
    NearLinkSession,
    NearLinkThroughputSample,
    NearLinkTransport,
)
from .tcp import (
    ITcpStream,
    InMemoryTcpConnectionRegistry,
    InMemoryTcpStream,
    TcpConnectionState,
    TcpEndpointDescriptor,
    TcpKnownPorts,
    TcpNetworkTransport,
    TcpStreamClosedError,
    TcpThroughputSample,
)
from .websocket import (
    IWebSocketConnection,
    InMemoryWebSocketConnection,
    InMemoryWebSocketSessionRegistry,
    WebSocketEndpointDescriptor,
    WebSocketFrameSummary,
    WebSocketLinkState,
    WebSocketMessageType,
    WebSocketReceiveResult,
    WebSocketTransport,
)
from .wifi import (
    BROADCAST_ADDRESS,
    IUdpSocket,
    InMemoryUdpBus,
    InMemoryUdpSocket,
    UdpReceiveResult,
    WiFiNetworkTransport,
    WiFiPeerDiscovery,
)

__all__ = [
    # enums
    "TransportKind",
    "ConnectivityState",
    "MessagePriority",
    "PeerRole",
    # records
    "NetworkPayload",
    "NetworkContext",
    "PeerInfo",
    # policy
    "INetworkPolicy",
    "DefaultNetworkPolicy",
    "NetworkPolicyBuilder",
    # contracts
    "INetworkTransport",
    "IMeshNetwork",
    "IMessageChannel",
    "IConnectivityMonitor",
    "ITransportSelector",
    "IPeerDiscovery",
    # working implementations
    "DefaultTransportSelector",
    "InMemoryWire",
    "InMemoryNetworkTransport",
    "InMemoryMeshNetwork",
    "IMessageSerializer",
    "JsonMessageSerializer",
    "TransportMessageChannel",
    "InMemoryConnectivityMonitor",
    "InboundChannel",
    # AetherNet transport
    "AetherPeerKind",
    "AetherPeer",
    "AetherHopTelemetry",
    "AetherPacketSummary",
    "InMemoryAetherNetRegistry",
    "IAetherMeshEngine",
    "InMemoryAetherMeshEngine",
    "AetherNetworkTransport",
    "AetherPeerDiscovery",
    "AetherSyncChannel",
    # Bluetooth transport
    "BluetoothConnectionState",
    "BluetoothEndpointDescriptor",
    "BluetoothCapabilityProfile",
    "BluetoothThroughputSample",
    "BluetoothCapabilityProfiles",
    "InMemoryBluetoothTransportRegistry",
    "IBleGattAdapter",
    "BluetoothNetworkTransport",
    "InMemoryBleGattAdapter",
    # DTN transport
    "DtnPriority",
    "DtnBundle",
    "DtnCustodyRecord",
    "InMemoryDtnBundleStore",
    "DtnSyncChannel",
    # gRPC transport
    "GrpcChannelState",
    "GrpcConnectionState",
    "GrpcChannelDescriptor",
    "GrpcRetryPolicy",
    "GrpcCallSummary",
    "GrpcRetryPolicies",
    "GrpcReconnectPolicy",
    "GrpcDeadline",
    "InMemoryGrpcCallMetrics",
    "IGrpcChannel",
    "InMemoryGrpcChannel",
    "GrpcNetworkTransport",
    "GrpcSendNotSupportedError",
    # HTTP transport
    "HttpStatusFamily",
    "HttpEndpointDescriptor",
    "HttpRequestSummary",
    "HttpCacheKey",
    "InMemoryHttpRequestMetrics",
    "IHttpMessageSender",
    "HttpNetworkTransport",
    "InMemoryHttpMessageSender",
    "HttpRequestException",
    "HttpTransientError",
    "HttpRequestFailedError",
    # MQTT transport
    "MqttQos",
    "MqttTopicDescriptor",
    "MqttRetainedMessage",
    "MqttClientDescriptor",
    "InMemoryMqttBroker",
    "IMqttClient",
    "MqttNetworkTransport",
    "InMemoryMqttClient",
    # NearLink transport
    "NearLinkPairingState",
    "NearLinkPowerProfile",
    "NearLinkDevice",
    "NearLinkSession",
    "NearLinkThroughputSample",
    "InMemoryNearLinkRegistry",
    "INearLinkAdapter",
    "NearLinkTransport",
    "InMemoryNearLinkAdapter",
    # TCP transport
    "TcpConnectionState",
    "TcpEndpointDescriptor",
    "TcpThroughputSample",
    "TcpKnownPorts",
    "InMemoryTcpConnectionRegistry",
    "TcpStreamClosedError",
    "ITcpStream",
    "TcpNetworkTransport",
    "InMemoryTcpStream",
    # WebSocket transport
    "WebSocketLinkState",
    "WebSocketMessageType",
    "WebSocketEndpointDescriptor",
    "WebSocketFrameSummary",
    "InMemoryWebSocketSessionRegistry",
    "WebSocketReceiveResult",
    "IWebSocketConnection",
    "WebSocketTransport",
    "InMemoryWebSocketConnection",
    # WiFi transport
    "BROADCAST_ADDRESS",
    "UdpReceiveResult",
    "InMemoryUdpBus",
    "IUdpSocket",
    "InMemoryUdpSocket",
    "WiFiNetworkTransport",
    "WiFiPeerDiscovery",
]
