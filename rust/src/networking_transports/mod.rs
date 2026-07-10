//! networking_transports — Rust port of the concrete `CircleAI.Networking.*`
//! transports that implement the [`crate::networking::INetworkTransport`] core
//! abstraction (Wave: "Networking transports A").
//!
//! Each submodule ports one C# transport package faithfully. Every transport is
//! in-memory / deterministic; the real socket / native / mesh / cloud dependency
//! is injected behind a trait, with a working in-memory implementation provided
//! (no stubs). Families:
//!
//!   * [`aethernet`] — `CircleAI.Networking.AetherNet`: [`AetherNetworkTransport`],
//!     [`AetherPeer`], [`AetherPeerDiscovery`], [`AetherSyncChannel`],
//!     [`AetherPeerKind`] (+ registry, hop/packet telemetry, injected
//!     [`IAetherRouter`]).
//!   * [`bluetooth`] — `CircleAI.Networking.Bluetooth`:
//!     [`BluetoothNetworkTransport`], [`IBleGattAdapter`],
//!     [`BluetoothEndpointDescriptor`], [`BluetoothCapabilityProfile`],
//!     [`BluetoothConnectionState`] (+ profile table, registry).
//!   * [`dtn`] — `CircleAI.Networking.Dtn`: [`DtnSyncChannel`], [`DtnBundle`],
//!     [`DtnCustodyRecord`], [`InMemoryDtnBundleStore`], [`DtnPriority`].
//!   * [`grpc`] — `CircleAI.Networking.Grpc`: [`GrpcNetworkTransport`],
//!     [`GrpcChannelDescriptor`], [`GrpcRetryPolicy`], [`GrpcCallSummary`],
//!     [`GrpcChannelState`] (+ policy table, metrics, injected [`IGrpcChannel`]).
//!   * [`http`] — `CircleAI.Networking.Http`: [`HttpNetworkTransport`],
//!     [`HttpEndpointDescriptor`], [`HttpCacheKey`], [`HttpRequestSummary`]
//!     (+ status-family classifiers, metrics, injected [`IHttpMessageSender`]).
//!
//! Wave "Networking transports B" adds the second batch (same in-memory /
//! injected-socket discipline):
//!
//!   * [`mqtt`] — `CircleAI.Networking.Mqtt`: [`MqttNetworkTransport`],
//!     [`MqttTopicDescriptor`], [`MqttQos`], [`InMemoryMqttBroker`]
//!     (+ retained store, topic-filter matcher, injected [`IMqttClient`]).
//!   * [`nearlink`] — `CircleAI.Networking.NearLink`: [`NearLinkTransport`],
//!     [`INearLinkAdapter`], [`NearLinkDevice`], [`NearLinkPairingState`],
//!     [`NearLinkPowerProfile`] (+ registry, injected adapter).
//!   * [`tcp`] — `CircleAI.Networking.Tcp`: [`TcpNetworkTransport`],
//!     [`TcpEndpointDescriptor`], [`TcpConnectionState`],
//!     [`InMemoryTcpConnectionRegistry`] (+ known ports, framing, injected
//!     [`ITcpConnection`]).
//!   * [`websocket`] — `CircleAI.Networking.WebSocket`: [`WebSocketTransport`],
//!     [`WebSocketEndpointDescriptor`], [`WebSocketLinkState`],
//!     [`WebSocketMessageType`] (+ session registry, injected [`IWebSocket`]).
//!   * [`wifi`] — `CircleAI.Networking.WiFi`: [`WiFiNetworkTransport`],
//!     [`WiFiPeerDiscovery`] (+ UDP beacon discovery, injected
//!     [`IWiFiDatagramSocket`]).

pub mod aethernet;
pub mod bluetooth;
pub mod dtn;
pub mod grpc;
pub mod http;
pub mod mqtt;
pub mod nearlink;
pub mod tcp;
pub mod websocket;
pub mod wifi;

// ── Re-exports (module-flat) ─────────────────────────────────────────────────

pub use aethernet::{
    AetherAvailability, AetherHopTelemetry, AetherNetworkTransport, AetherPacketSummary,
    AetherPeer, AetherPeerDiscovery, AetherPeerKind, AetherSyncChannel, FixedAetherAvailability,
    IAetherRouter, InMemoryAetherNetRegistry, InMemoryAetherRouter, AETHER_DTN_DEFAULT_TTL,
};

pub use bluetooth::{
    BluetoothCapabilityProfile, BluetoothCapabilityProfiles, BluetoothConnectionState,
    BluetoothEndpointDescriptor, BluetoothNetworkTransport, BluetoothThroughputSample,
    IBleGattAdapter, InMemoryBleGattAdapter, InMemoryBluetoothTransportRegistry, InboundSink,
};

pub use dtn::{
    DtnBundle, DtnCustodyRecord, DtnPriority, DtnSyncChannel, InMemoryDtnBundleStore,
    DTN_DEFAULT_TTL,
};

pub use grpc::{
    GrpcCallSummary, GrpcChannelDescriptor, GrpcChannelState, GrpcNetworkTransport, GrpcRetryPolicies,
    GrpcRetryPolicy, IGrpcChannel, InMemoryGrpcCallMetrics, InMemoryGrpcChannel,
    GRPC_SEND_NOT_SUPPORTED,
};

pub use http::{
    HttpCacheKey, HttpEndpointDescriptor, HttpNetworkTransport, HttpPostRequest, HttpPostResult,
    HttpRequestSummary, HttpSendError, HttpStatusFamily, IHttpMessageSender,
    InMemoryHttpMessageSender, InMemoryHttpRequestMetrics,
};

pub use mqtt::{
    IMqttClient, InMemoryMqttBroker, InMemoryMqttClient, MqttClientDescriptor, MqttInboundSink,
    MqttNetworkTransport, MqttPublish, MqttQos, MqttRetainedMessage, MqttTopicDescriptor,
};

pub use nearlink::{
    INearLinkAdapter, InMemoryNearLinkAdapter, InMemoryNearLinkRegistry, NearLinkDevice,
    NearLinkInboundSink, NearLinkPairingState, NearLinkPowerProfile, NearLinkSession,
    NearLinkThroughputSample, NearLinkTransport,
};

pub use tcp::{
    ITcpConnection, InMemoryTcpConnection, InMemoryTcpConnectionRegistry, TcpConnectionState,
    TcpEndpointDescriptor, TcpInboundSink, TcpKnownPorts, TcpNetworkTransport, TcpThroughputSample,
};

pub use websocket::{
    IWebSocket, InMemoryWebSocket, InMemoryWebSocketSessionRegistry, WebSocketEndpointDescriptor,
    WebSocketFrameSummary, WebSocketInboundSink, WebSocketLinkState, WebSocketMessageType,
    WebSocketTransport,
};

pub use wifi::{
    IWiFiDatagramSocket, InMemoryWiFiDatagramSocket, WiFiDatagram, WiFiInboundSink,
    WiFiNetworkTransport, WiFiPeerDiscovery, BEACON_MAGIC, BROADCAST_ADDR, DATA_PORT,
    DISCOVERY_PORT,
};
