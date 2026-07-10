//! networking_transports::mqtt — Rust port of `CircleAI.Networking.Mqtt`
//! (`src/CircleAI.Networking.Mqtt/*.cs`).
//!
//! MQTT-broker binding of the [`crate::networking::INetworkTransport`] contract.
//! Faithful ports:
//!
//!   * [`MqttQos`]                         — port of the C# enum (0/1/2).
//!   * [`MqttTopicDescriptor`] / [`MqttRetainedMessage`] / [`MqttClientDescriptor`]
//!     — the C# `record`s.
//!   * [`InMemoryMqttBroker`]              — the C# in-memory broker: client table,
//!     retained-message store, subscription table, and the `Matches` topic-filter
//!     algorithm (ported wildcard-for-wildcard) + `MatchingSubscribers`.
//!   * [`IMqttClient`]                     — the MQTT client dependency (trait), a
//!     port of the MQTTnet `IMqttClient` surface used by the transport
//!     (Connect/Subscribe/Publish/Disconnect + inbound message callback), with a
//!     working [`InMemoryMqttClient`].
//!   * [`MqttNetworkTransport`]            — `INetworkTransport` over MQTT: publishes
//!     to `circle/payloads/{destinationId}` (or `circle/payloads/broadcast`),
//!     subscribes to `circle/payloads/{localClientId}/#`, QoS `ExactlyOnce` for
//!     `>= High` priority else `AtLeastOnce`, unbounded inbound buffer. Port of the
//!     C# transport.
//!
//! `ReadOnlyMemory<byte>` → `Vec<u8>`; `DateTimeOffset` → `chrono::DateTime<Utc>`;
//! `TimeSpan` → `std::time::Duration`.

use std::collections::{HashMap, HashSet, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::networking::{
    INetworkTransport, MessagePriority, NetworkPayload, TransportError, TransportKind,
};

// ─────────────────────────────────────────────────────────────────────────────
// MqttQos — port of the C# enum
// ─────────────────────────────────────────────────────────────────────────────

/// MQTT quality-of-service level. 1:1 with the C# `MqttQos` (values 0/1/2).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum MqttQos {
    AtMostOnce = 0,
    AtLeastOnce = 1,
    ExactlyOnce = 2,
}

// ─────────────────────────────────────────────────────────────────────────────
// Value records
// ─────────────────────────────────────────────────────────────────────────────

/// A topic + its QoS. Port of the C# `MqttTopicDescriptor`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MqttTopicDescriptor {
    pub topic: String,
    pub qos: MqttQos,
}

impl MqttTopicDescriptor {
    pub fn new(topic: impl Into<String>, qos: MqttQos) -> Self {
        Self {
            topic: topic.into(),
            qos,
        }
    }
}

/// A retained MQTT message. Port of the C# `MqttRetainedMessage`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MqttRetainedMessage {
    pub topic: String,
    pub payload: Vec<u8>,
    pub retained_at_utc: DateTime<Utc>,
}

impl MqttRetainedMessage {
    pub fn new(
        topic: impl Into<String>,
        payload: Vec<u8>,
        retained_at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            topic: topic.into(),
            payload,
            retained_at_utc,
        }
    }
}

/// A connected client's descriptor. Port of the C# `MqttClientDescriptor`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct MqttClientDescriptor {
    pub client_id: String,
    pub host: String,
    pub port: i32,
    pub use_tls: bool,
    pub keep_alive: Duration,
}

impl MqttClientDescriptor {
    pub fn new(
        client_id: impl Into<String>,
        host: impl Into<String>,
        port: i32,
        use_tls: bool,
        keep_alive: Duration,
    ) -> Self {
        Self {
            client_id: client_id.into(),
            host: host.into(),
            port,
            use_tls,
            keep_alive,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryMqttBroker — port of the C# in-memory broker
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory MQTT broker: client table + retained-message store + subscription
/// table. Port of the C# `InMemoryMqttBroker`.
///
/// Matches the C#:
///   * [`connected_clients`](Self::connected_clients) returns the connected client
///     descriptors (unordered, like the C# `_clients.Values.ToArray()`).
///   * [`matches`](Self::matches) implements the MQTT topic-filter wildcard rules
///     (`#` multi-level, `+` single-level) exactly as the C# `Matches`.
///   * [`matching_subscribers`](Self::matching_subscribers) returns every client
///     with at least one filter matching `topic`.
#[derive(Default)]
pub struct InMemoryMqttBroker {
    retained: Mutex<HashMap<String, MqttRetainedMessage>>,
    clients: Mutex<HashMap<String, MqttClientDescriptor>>,
    subscriptions: Mutex<HashMap<String, HashSet<String>>>,
}

impl InMemoryMqttBroker {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) a connected client keyed by `client_id`. Port of the
    /// C# `Connect`.
    pub fn connect(&self, c: MqttClientDescriptor) {
        self.clients.lock().unwrap().insert(c.client_id.clone(), c);
    }

    /// Removes a client by id. Port of the C# `Disconnect`.
    pub fn disconnect(&self, client_id: &str) {
        self.clients.lock().unwrap().remove(client_id);
    }

    /// The connected client descriptors. Mirrors the C# `ConnectedClients`.
    pub fn connected_clients(&self) -> Vec<MqttClientDescriptor> {
        self.clients.lock().unwrap().values().cloned().collect()
    }

    /// Records `topic_filter` as a subscription for `client_id`. Port of the C#
    /// `Subscribe`; empty/whitespace arguments are ignored (the C# throws — here we
    /// keep the transport infallible and no-op, matching the deterministic port
    /// contract).
    pub fn subscribe(&self, client_id: &str, topic_filter: &str) {
        if client_id.trim().is_empty() || topic_filter.trim().is_empty() {
            return;
        }
        self.subscriptions
            .lock()
            .unwrap()
            .entry(client_id.to_string())
            .or_default()
            .insert(topic_filter.to_string());
    }

    /// Whether `topic` matches the MQTT `topic_filter`. Port of the C# `Matches`:
    ///   * `#` at filter position → match (multi-level wildcard).
    ///   * `+` at filter position → single-level wildcard (any one segment).
    ///   * otherwise segments must be ordinal-equal.
    ///   * a filter longer than the topic (before hitting `#`) fails.
    ///   * finally, lengths must be equal.
    pub fn matches(topic: &str, topic_filter: &str) -> bool {
        if topic.is_empty() || topic_filter.is_empty() {
            return false;
        }
        let t: Vec<&str> = topic.split('/').collect();
        let f: Vec<&str> = topic_filter.split('/').collect();
        for (i, seg) in f.iter().enumerate() {
            if *seg == "#" {
                return true;
            }
            if i >= t.len() {
                return false;
            }
            if *seg == "+" {
                continue;
            }
            if *seg != t[i] {
                return false;
            }
        }
        t.len() == f.len()
    }

    /// Retains `m` under its topic (overwriting any prior retained message). Port of
    /// the C# `PublishRetained`.
    pub fn publish_retained(&self, m: MqttRetainedMessage) {
        self.retained.lock().unwrap().insert(m.topic.clone(), m);
    }

    /// The retained message for `topic`, if any. Port of the C# `GetRetained`.
    pub fn get_retained(&self, topic: &str) -> Option<MqttRetainedMessage> {
        self.retained.lock().unwrap().get(topic).cloned()
    }

    /// Every client with at least one subscription filter matching `topic`. Port of
    /// the C# `MatchingSubscribers`.
    pub fn matching_subscribers(&self, topic: &str) -> Vec<String> {
        let guard = self.subscriptions.lock().unwrap();
        guard
            .iter()
            .filter(|(_, filters)| filters.iter().any(|f| Self::matches(topic, f)))
            .map(|(client, _)| client.clone())
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IMqttClient — port of the MQTTnet client dependency
// ─────────────────────────────────────────────────────────────────────────────

/// One published MQTT message the transport builds (the C#
/// `MqttApplicationMessageBuilder` output passed to `PublishAsync`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MqttPublish {
    pub topic: String,
    pub payload: Vec<u8>,
    pub qos: MqttQos,
}

/// The MQTT client dependency. Port of the MQTTnet `IMqttClient` surface used by
/// the transport: connect, subscribe, publish, disconnect, and an inbound-message
/// callback. Injecting it keeps [`MqttNetworkTransport`] deterministic;
/// [`InMemoryMqttClient`] is a working scriptable implementation (no real broker).
pub trait IMqttClient: Send + Sync {
    /// Whether the client is connected (the C# `IsConnected`).
    fn is_connected(&self) -> bool;

    /// Connect to the broker.
    fn connect(&self);

    /// Subscribe to `topic_filter`.
    fn subscribe(&self, topic_filter: &str);

    /// Publish `message`. Errors surface as [`TransportError`].
    fn publish(&self, message: &MqttPublish) -> Result<(), TransportError>;

    /// Disconnect from the broker.
    fn disconnect(&self);

    /// Register the sink invoked for each inbound application message (the C#
    /// `ApplicationMessageReceivedAsync += OnMessageReceived`). The sink receives
    /// the raw payload bytes.
    fn set_inbound_sink(&self, sink: MqttInboundSink);
}

/// The sink an [`IMqttClient`] pushes inbound message payloads into.
pub type MqttInboundSink = Arc<dyn Fn(Vec<u8>) + Send + Sync>;

/// A working, deterministic in-memory [`IMqttClient`]. `publish` records every
/// message; [`InMemoryMqttClient::simulate_inbound`] injects a payload as if the
/// broker delivered it. Connection state and subscriptions are tracked.
pub struct InMemoryMqttClient {
    connected: AtomicBool,
    published: Mutex<Vec<MqttPublish>>,
    subscriptions: Mutex<Vec<String>>,
    sink: Mutex<Option<MqttInboundSink>>,
}

impl Default for InMemoryMqttClient {
    fn default() -> Self {
        Self::new()
    }
}

impl InMemoryMqttClient {
    /// A new, disconnected client.
    pub fn new() -> Self {
        Self {
            connected: AtomicBool::new(false),
            published: Mutex::new(Vec::new()),
            subscriptions: Mutex::new(Vec::new()),
            sink: Mutex::new(None),
        }
    }

    /// Every message published so far, in order.
    pub fn published(&self) -> Vec<MqttPublish> {
        self.published.lock().unwrap().clone()
    }

    /// Every topic filter subscribed to, in order.
    pub fn subscriptions(&self) -> Vec<String> {
        self.subscriptions.lock().unwrap().clone()
    }

    /// Injects `payload` as if the broker delivered a message: forwarded to the
    /// inbound sink when connected. No-op when disconnected (no live session).
    pub fn simulate_inbound(&self, payload: Vec<u8>) {
        if !self.connected.load(Ordering::SeqCst) {
            return;
        }
        // Snapshot the sink under the lock, release, then fire outside it.
        let sink = self.sink.lock().unwrap().clone();
        if let Some(sink) = sink {
            sink(payload);
        }
    }
}

impl IMqttClient for InMemoryMqttClient {
    fn is_connected(&self) -> bool {
        self.connected.load(Ordering::SeqCst)
    }

    fn connect(&self) {
        self.connected.store(true, Ordering::SeqCst);
    }

    fn subscribe(&self, topic_filter: &str) {
        self.subscriptions
            .lock()
            .unwrap()
            .push(topic_filter.to_string());
    }

    fn publish(&self, message: &MqttPublish) -> Result<(), TransportError> {
        if !self.connected.load(Ordering::SeqCst) {
            return Err(TransportError::NotAvailable(TransportKind::Mqtt));
        }
        self.published.lock().unwrap().push(message.clone());
        Ok(())
    }

    fn disconnect(&self) {
        self.connected.store(false, Ordering::SeqCst);
    }

    fn set_inbound_sink(&self, sink: MqttInboundSink) {
        *self.sink.lock().unwrap() = Some(sink);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MqttNetworkTransport — port of MqttNetworkTransport.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`INetworkTransport`] backed by an MQTT broker. Port of the C#
/// `MqttNetworkTransport`.
///
/// Publishes to `circle/payloads/{destinationId}` (or `circle/payloads/broadcast`
/// when no destination), subscribes to `circle/payloads/{localClientId}/#` on
/// [`start`], and QoS is `ExactlyOnce` for `>= High` priority payloads else
/// `AtLeastOnce`. The inbound sink buffers received payloads into an unbounded
/// inbox for [`drain`] (the C# `Channel.CreateUnbounded` + `OnMessageReceived`).
pub struct MqttNetworkTransport {
    client: Arc<dyn IMqttClient>,
    local_client_id: String,
    /// Unbounded inbound buffer — the analogue of the C#
    /// `Channel.CreateUnbounded<NetworkPayload>()`.
    inbound: Arc<Mutex<VecDeque<NetworkPayload>>>,
    completed: Arc<AtomicBool>,
}

impl MqttNetworkTransport {
    /// Builds a transport over `client`, identified by `local_client_id`. The
    /// inbound sink is wired at construction (the C#
    /// `ApplicationMessageReceivedAsync += OnMessageReceived` in the constructor).
    pub fn new(client: Arc<dyn IMqttClient>, local_client_id: impl Into<String>) -> Self {
        let inbound: Arc<Mutex<VecDeque<NetworkPayload>>> = Arc::new(Mutex::new(VecDeque::new()));
        let completed = Arc::new(AtomicBool::new(false));

        // Wire the inbound sink synchronously at construction so a message
        // delivered right after connect cannot race the subscription.
        let inbox = Arc::clone(&inbound);
        let done = Arc::clone(&completed);
        let sink: MqttInboundSink = Arc::new(move |payload_bytes: Vec<u8>| {
            if done.load(Ordering::SeqCst) {
                return;
            }
            inbox
                .lock()
                .unwrap()
                .push_back(NetworkPayload::of(payload_bytes));
        });
        client.set_inbound_sink(sink);

        Self {
            client,
            local_client_id: local_client_id.into(),
            inbound,
            completed,
        }
    }

    /// The subscription topic for this client: `circle/payloads/{localClientId}/#`.
    fn subscription_topic(&self) -> String {
        format!("circle/payloads/{}/#", self.local_client_id)
    }

    /// Builds the publish topic for `destination_id` exactly as the C# does:
    /// `circle/payloads/{dest}` when non-empty, else `circle/payloads/broadcast`.
    pub fn publish_topic(destination_id: Option<&str>) -> String {
        match destination_id {
            Some(d) if !d.is_empty() => format!("circle/payloads/{d}"),
            _ => "circle/payloads/broadcast".to_string(),
        }
    }

    /// Maps a payload priority to QoS: `ExactlyOnce` for `>= High` else
    /// `AtLeastOnce` (the C# `payload.Priority >= MessagePriority.High ? ... : ...`).
    fn qos_for(priority: MessagePriority) -> MqttQos {
        if priority >= MessagePriority::High {
            MqttQos::ExactlyOnce
        } else {
            MqttQos::AtLeastOnce
        }
    }

    /// Drains every buffered inbound payload in arrival order. Pull side of the C#
    /// `ReceiveAsync` enumerable.
    pub fn drain(&self) -> Vec<NetworkPayload> {
        self.inbound.lock().unwrap().drain(..).collect()
    }
}

impl INetworkTransport for MqttNetworkTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::Mqtt
    }

    fn is_available(&self) -> bool {
        self.client.is_connected()
    }

    fn start(&self) {
        // C# StartAsync: ConnectAsync then SubscribeAsync(circle/payloads/{id}/#).
        self.completed.store(false, Ordering::SeqCst);
        self.client.connect();
        self.client.subscribe(&self.subscription_topic());
    }

    fn stop(&self) {
        // C# StopAsync: DisconnectAsync then complete the inbound writer.
        self.client.disconnect();
        self.completed.store(true, Ordering::SeqCst);
    }

    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        let topic = Self::publish_topic(payload.destination_id.as_deref());
        let message = MqttPublish {
            topic,
            payload: payload.data.clone(),
            qos: Self::qos_for(payload.priority),
        };
        self.client.publish(&message)
    }
}
