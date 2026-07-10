//! networking_mqtt_test.rs
//!
//! Ports the `CircleAI.Networking.Mqtt` surface: `MqttQos`, `MqttTopicDescriptor` /
//! `MqttRetainedMessage` / `MqttClientDescriptor`, `InMemoryMqttBroker` (topic
//! matcher + retained + subscribers), `IMqttClient` / `InMemoryMqttClient`, and
//! `MqttNetworkTransport`.

use std::sync::Arc;
use std::time::Duration;

use chrono::Utc;
use circle_ai::networking::{
    INetworkTransport, MessagePriority, NetworkPayload, TransportError, TransportKind,
};
use circle_ai::networking_transports::{
    IMqttClient, InMemoryMqttBroker, InMemoryMqttClient, MqttClientDescriptor, MqttNetworkTransport,
    MqttQos, MqttRetainedMessage,
};

fn payload_to(dest: Option<&str>, priority: MessagePriority, data: Vec<u8>) -> NetworkPayload {
    NetworkPayload::create(
        data,
        dest.map(|s| s.to_string()),
        priority,
        "application/octet-stream",
        None,
    )
}

// ── MqttQos discriminants ────────────────────────────────────────────────────

#[test]
fn qos_discriminants_match_csharp() {
    assert_eq!(MqttQos::AtMostOnce as i32, 0);
    assert_eq!(MqttQos::AtLeastOnce as i32, 1);
    assert_eq!(MqttQos::ExactlyOnce as i32, 2);
}

// ── InMemoryMqttBroker: topic-filter matcher ─────────────────────────────────

#[test]
fn broker_matches_exact_topic() {
    assert!(InMemoryMqttBroker::matches("a/b/c", "a/b/c"));
    assert!(!InMemoryMqttBroker::matches("a/b/c", "a/b"));
    assert!(!InMemoryMqttBroker::matches("a/b", "a/b/c"));
}

#[test]
fn broker_matches_multi_level_wildcard() {
    assert!(InMemoryMqttBroker::matches("a/b/c/d", "a/#"));
    assert!(InMemoryMqttBroker::matches("a", "#"));
    assert!(InMemoryMqttBroker::matches("circle/payloads/node1/x", "circle/payloads/node1/#"));
}

#[test]
fn broker_matches_single_level_wildcard() {
    assert!(InMemoryMqttBroker::matches("a/b/c", "a/+/c"));
    assert!(!InMemoryMqttBroker::matches("a/b/c/d", "a/+/c"));
    assert!(InMemoryMqttBroker::matches("a/b", "+/b"));
}

#[test]
fn broker_matches_empty_is_false() {
    assert!(!InMemoryMqttBroker::matches("", "a"));
    assert!(!InMemoryMqttBroker::matches("a", ""));
}

// ── InMemoryMqttBroker: clients / retained / subscribers ─────────────────────

#[test]
fn broker_connect_and_disconnect_clients() {
    let broker = InMemoryMqttBroker::new();
    broker.connect(MqttClientDescriptor::new(
        "c1",
        "broker.local",
        1883,
        false,
        Duration::from_secs(60),
    ));
    assert_eq!(broker.connected_clients().len(), 1);
    broker.disconnect("c1");
    assert!(broker.connected_clients().is_empty());
}

#[test]
fn broker_retained_roundtrip() {
    let broker = InMemoryMqttBroker::new();
    assert!(broker.get_retained("t").is_none());
    broker.publish_retained(MqttRetainedMessage::new("t", vec![9, 9], Utc::now()));
    assert_eq!(broker.get_retained("t").unwrap().payload, vec![9, 9]);
    // Overwrite.
    broker.publish_retained(MqttRetainedMessage::new("t", vec![1], Utc::now()));
    assert_eq!(broker.get_retained("t").unwrap().payload, vec![1]);
}

#[test]
fn broker_matching_subscribers() {
    let broker = InMemoryMqttBroker::new();
    broker.subscribe("c1", "circle/payloads/c1/#");
    broker.subscribe("c2", "circle/+/broadcast");
    broker.subscribe("c3", "other/#");

    let mut m = broker.matching_subscribers("circle/payloads/c1/msg");
    m.sort();
    assert_eq!(m, vec!["c1"]);

    let m2 = broker.matching_subscribers("circle/payloads/broadcast");
    assert_eq!(m2, vec!["c2"]);

    assert!(broker.matching_subscribers("nomatch/here").is_empty());
}

#[test]
fn broker_subscribe_ignores_blank_args() {
    let broker = InMemoryMqttBroker::new();
    broker.subscribe("", "a/#");
    broker.subscribe("c1", "   ");
    assert!(broker.matching_subscribers("a/b").is_empty());
}

// ── InMemoryMqttClient ───────────────────────────────────────────────────────

#[test]
fn client_publish_requires_connection() {
    let client = InMemoryMqttClient::new();
    let msg = circle_ai::networking_transports::MqttPublish {
        topic: "t".into(),
        payload: vec![1],
        qos: MqttQos::AtLeastOnce,
    };
    assert_eq!(
        client.publish(&msg),
        Err(TransportError::NotAvailable(TransportKind::Mqtt))
    );
    client.connect();
    client.publish(&msg).unwrap();
    assert_eq!(client.published().len(), 1);
}

// ── MqttNetworkTransport ─────────────────────────────────────────────────────

#[test]
fn transport_kind_and_availability_delegate_to_client() {
    let client = Arc::new(InMemoryMqttClient::new());
    let t = MqttNetworkTransport::new(Arc::clone(&client) as Arc<_>, "node1");
    assert_eq!(t.kind(), TransportKind::Mqtt);
    assert!(!t.is_available());
    t.start();
    assert!(t.is_available());
}

#[test]
fn transport_start_subscribes_to_local_topic() {
    let client = Arc::new(InMemoryMqttClient::new());
    let t = MqttNetworkTransport::new(Arc::clone(&client) as Arc<_>, "node1");
    t.start();
    assert_eq!(client.subscriptions(), vec!["circle/payloads/node1/#"]);
}

#[test]
fn transport_send_publishes_to_destination_topic() {
    let client = Arc::new(InMemoryMqttClient::new());
    let t = MqttNetworkTransport::new(Arc::clone(&client) as Arc<_>, "node1");
    t.start();
    t.send(&payload_to(Some("peer2"), MessagePriority::Normal, vec![1, 2]))
        .unwrap();
    let pubs = client.published();
    assert_eq!(pubs.len(), 1);
    assert_eq!(pubs[0].topic, "circle/payloads/peer2");
    assert_eq!(pubs[0].payload, vec![1, 2]);
    // Normal priority → AtLeastOnce.
    assert_eq!(pubs[0].qos, MqttQos::AtLeastOnce);
}

#[test]
fn transport_send_without_destination_is_broadcast() {
    let client = Arc::new(InMemoryMqttClient::new());
    let t = MqttNetworkTransport::new(Arc::clone(&client) as Arc<_>, "node1");
    t.start();
    t.send(&payload_to(None, MessagePriority::Normal, vec![7]))
        .unwrap();
    assert_eq!(client.published()[0].topic, "circle/payloads/broadcast");
}

#[test]
fn transport_high_priority_uses_exactly_once() {
    let client = Arc::new(InMemoryMqttClient::new());
    let t = MqttNetworkTransport::new(Arc::clone(&client) as Arc<_>, "node1");
    t.start();
    t.send(&payload_to(Some("p"), MessagePriority::High, vec![1]))
        .unwrap();
    t.send(&payload_to(Some("p"), MessagePriority::Emergency, vec![2]))
        .unwrap();
    let pubs = client.published();
    assert_eq!(pubs[0].qos, MqttQos::ExactlyOnce);
    assert_eq!(pubs[1].qos, MqttQos::ExactlyOnce);
}

#[test]
fn transport_receive_loop_buffers_inbound() {
    let client = Arc::new(InMemoryMqttClient::new());
    let t = MqttNetworkTransport::new(Arc::clone(&client) as Arc<_>, "node1");
    t.start();
    // Broker delivers messages (as if via ApplicationMessageReceived).
    client.simulate_inbound(vec![1]);
    client.simulate_inbound(vec![2]);
    let drained = t.drain();
    assert_eq!(drained.len(), 2);
    assert_eq!(drained[0].data, vec![1]);
    assert_eq!(drained[1].data, vec![2]);
    assert!(t.drain().is_empty());
}

#[test]
fn transport_inbound_before_start_is_dropped() {
    let client = Arc::new(InMemoryMqttClient::new());
    let t = MqttNetworkTransport::new(Arc::clone(&client) as Arc<_>, "node1");
    // Not connected: sink is wired but the client drops inbound while disconnected.
    client.simulate_inbound(vec![1]);
    assert!(t.drain().is_empty());
}

#[test]
fn transport_stop_completes_buffer() {
    let client = Arc::new(InMemoryMqttClient::new());
    let t = MqttNetworkTransport::new(Arc::clone(&client) as Arc<_>, "node1");
    t.start();
    t.stop();
    assert!(!t.is_available());
    // After stop the client is disconnected; inbound is dropped.
    client.simulate_inbound(vec![1]);
    assert!(t.drain().is_empty());
}
