//! networking_websocket_test.rs
//!
//! Ports the `CircleAI.Networking.WebSocket` surface: `WebSocketLinkState` /
//! `WebSocketMessageType`, `WebSocketEndpointDescriptor` / `WebSocketFrameSummary`,
//! `InMemoryWebSocketSessionRegistry`, `IWebSocket` / `InMemoryWebSocket`, and
//! `WebSocketTransport`.

use std::sync::Arc;
use std::time::Duration;

use chrono::Utc;
use circle_ai::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};
use circle_ai::networking_transports::{
    IWebSocket, InMemoryWebSocket, InMemoryWebSocketSessionRegistry, WebSocketEndpointDescriptor,
    WebSocketFrameSummary, WebSocketLinkState, WebSocketMessageType, WebSocketTransport,
};

fn descriptor(uri: &str) -> WebSocketEndpointDescriptor {
    WebSocketEndpointDescriptor::new(uri, None, Duration::from_secs(30), vec!["circle".into()])
}

// ── InMemoryWebSocketSessionRegistry ─────────────────────────────────────────

#[test]
fn registry_register_and_get() {
    let reg = InMemoryWebSocketSessionRegistry::new();
    reg.register("s1", descriptor("wss://host/ws"));
    assert_eq!(reg.get("s1").unwrap().uri, "wss://host/ws");
    assert!(reg.get("nope").is_none());
}

#[test]
fn registry_state_defaults_to_closed() {
    let reg = InMemoryWebSocketSessionRegistry::new();
    assert_eq!(reg.state("x"), WebSocketLinkState::Closed);
    reg.set_state("s1", WebSocketLinkState::Open);
    assert_eq!(reg.state("s1"), WebSocketLinkState::Open);
    reg.set_state("s2", WebSocketLinkState::ClosedError);
    assert_eq!(reg.state("s2"), WebSocketLinkState::ClosedError);
}

#[test]
fn registry_total_bytes_and_frame_count() {
    let reg = InMemoryWebSocketSessionRegistry::new();
    let now = Utc::now();
    reg.record_frame(WebSocketFrameSummary::new("s1", WebSocketMessageType::Binary, 100, now));
    reg.record_frame(WebSocketFrameSummary::new("s1", WebSocketMessageType::Binary, 50, now));
    reg.record_frame(WebSocketFrameSummary::new("s1", WebSocketMessageType::Ping, 4, now));
    reg.record_frame(WebSocketFrameSummary::new("s2", WebSocketMessageType::Binary, 999, now));

    assert_eq!(reg.total_bytes("s1"), 154);
    assert_eq!(reg.frame_count("s1", WebSocketMessageType::Binary), 2);
    assert_eq!(reg.frame_count("s1", WebSocketMessageType::Ping), 1);
    assert_eq!(reg.frame_count("s1", WebSocketMessageType::Close), 0);
    assert_eq!(reg.total_bytes("s2"), 999);
}

// ── InMemoryWebSocket ────────────────────────────────────────────────────────

#[test]
fn socket_send_requires_open_state() {
    let ws = InMemoryWebSocket::new();
    assert_eq!(ws.state(), WebSocketLinkState::Closed);
    assert_eq!(
        ws.send_binary(&[1]),
        Err(TransportError::NotAvailable(TransportKind::WebSocket))
    );
    ws.connect();
    assert_eq!(ws.state(), WebSocketLinkState::Open);
    ws.send_binary(&[1, 2]).unwrap();
    assert_eq!(ws.sent_frames(), vec![vec![1, 2]]);
}

// ── WebSocketTransport ───────────────────────────────────────────────────────

#[test]
fn transport_kind_and_availability() {
    let ws = Arc::new(InMemoryWebSocket::new());
    let t = WebSocketTransport::new(Arc::clone(&ws) as Arc<_>);
    assert_eq!(t.kind(), TransportKind::WebSocket);
    assert!(!t.is_available());
    t.start();
    assert!(t.is_available());
}

#[test]
fn transport_send_transmits_binary_frame() {
    let ws = Arc::new(InMemoryWebSocket::new());
    let t = WebSocketTransport::new(Arc::clone(&ws) as Arc<_>);
    t.start();
    t.send(&NetworkPayload::of(vec![4, 5, 6])).unwrap();
    assert_eq!(ws.sent_frames(), vec![vec![4, 5, 6]]);
}

#[test]
fn transport_receive_loop_buffers_binary_frames() {
    let ws = Arc::new(InMemoryWebSocket::new());
    let t = WebSocketTransport::new(Arc::clone(&ws) as Arc<_>);
    t.start();
    ws.simulate_inbound(WebSocketMessageType::Binary, vec![1]);
    ws.simulate_inbound(WebSocketMessageType::Binary, vec![2]);
    let drained = t.drain();
    assert_eq!(drained.len(), 2);
    assert_eq!(drained[0].data, vec![1]);
    assert!(t.drain().is_empty());
}

#[test]
fn transport_close_frame_terminates_pump() {
    let ws = Arc::new(InMemoryWebSocket::new());
    let t = WebSocketTransport::new(Arc::clone(&ws) as Arc<_>);
    t.start();
    ws.simulate_inbound(WebSocketMessageType::Binary, vec![1]);
    // A Close frame stops the pump (the C# `break`).
    ws.simulate_inbound(WebSocketMessageType::Close, vec![]);
    // Anything after Close is dropped.
    ws.simulate_inbound(WebSocketMessageType::Binary, vec![2]);
    let drained = t.drain();
    assert_eq!(drained.len(), 1);
    assert_eq!(drained[0].data, vec![1]);
}

#[test]
fn transport_inbound_before_start_is_dropped() {
    let ws = Arc::new(InMemoryWebSocket::new());
    let t = WebSocketTransport::new(Arc::clone(&ws) as Arc<_>);
    // Not open: simulate_inbound is a no-op.
    ws.simulate_inbound(WebSocketMessageType::Binary, vec![1]);
    assert!(t.drain().is_empty());
}

#[test]
fn transport_stop_closes_socket() {
    let ws = Arc::new(InMemoryWebSocket::new());
    let t = WebSocketTransport::new(Arc::clone(&ws) as Arc<_>);
    t.start();
    t.stop();
    assert_eq!(ws.state(), WebSocketLinkState::Closed);
    assert!(!t.is_available());
}
