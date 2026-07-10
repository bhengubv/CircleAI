//! networking_tcp_test.rs
//!
//! Ports the `CircleAI.Networking.Tcp` surface: `TcpConnectionState`,
//! `TcpEndpointDescriptor` / `TcpThroughputSample`, `TcpKnownPorts`,
//! `InMemoryTcpConnectionRegistry`, `ITcpConnection` / `InMemoryTcpConnection`,
//! and `TcpNetworkTransport` (framing + client/listener modes).

use std::sync::Arc;
use std::time::Duration;

use chrono::Utc;
use circle_ai::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};
use circle_ai::networking_transports::{
    InMemoryTcpConnection, InMemoryTcpConnectionRegistry, TcpConnectionState, TcpEndpointDescriptor,
    TcpKnownPorts, TcpNetworkTransport, TcpThroughputSample,
};

fn descriptor(host: &str, port: i32) -> TcpEndpointDescriptor {
    TcpEndpointDescriptor::new(host, port, true, true, Duration::from_secs(5))
}

// ── TcpKnownPorts ────────────────────────────────────────────────────────────

#[test]
fn known_ports_match_csharp() {
    assert_eq!(TcpKnownPorts::HTTP, 80);
    assert_eq!(TcpKnownPorts::HTTPS, 443);
    assert_eq!(TcpKnownPorts::SSH, 22);
    assert_eq!(TcpKnownPorts::SMTP, 25);
    assert_eq!(TcpKnownPorts::IMAP, 143);
    assert_eq!(TcpKnownPorts::IMAP_SSL, 993);
    assert_eq!(TcpKnownPorts::POP3, 110);
    assert_eq!(TcpKnownPorts::POP3_SSL, 995);
    assert_eq!(TcpKnownPorts::MQTT, 1883);
    assert_eq!(TcpKnownPorts::MQTT_SSL, 8883);
}

// ── InMemoryTcpConnectionRegistry ────────────────────────────────────────────

#[test]
fn registry_register_and_get() {
    let reg = InMemoryTcpConnectionRegistry::new();
    reg.register("e1", descriptor("host", 443));
    assert_eq!(reg.get("e1").unwrap().port, 443);
    assert!(reg.get("nope").is_none());
}

#[test]
fn registry_state_defaults_to_disconnected() {
    let reg = InMemoryTcpConnectionRegistry::new();
    assert_eq!(reg.state("x"), TcpConnectionState::Disconnected);
    reg.set_state("e1", TcpConnectionState::Connected);
    assert_eq!(reg.state("e1"), TcpConnectionState::Connected);
}

#[test]
fn registry_total_bytes_sent_sums_samples() {
    let reg = InMemoryTcpConnectionRegistry::new();
    assert_eq!(reg.total_bytes_sent("e1"), 0);
    let now = Utc::now();
    reg.record_sample(TcpThroughputSample::new("e1", 100, 10, now));
    reg.record_sample(TcpThroughputSample::new("e1", 250, 20, now));
    reg.record_sample(TcpThroughputSample::new("e2", 999, 20, now));
    assert_eq!(reg.total_bytes_sent("e1"), 350);
    assert_eq!(reg.total_bytes_sent("e2"), 999);
}

// ── Framing (wire format) ────────────────────────────────────────────────────

#[test]
fn frame_prepends_little_endian_length() {
    let framed = TcpNetworkTransport::frame(&[0xAA, 0xBB, 0xCC]);
    // 3 as i32 LE = 03 00 00 00, then the data.
    assert_eq!(framed, vec![0x03, 0x00, 0x00, 0x00, 0xAA, 0xBB, 0xCC]);
}

#[test]
fn frame_deframe_roundtrip() {
    let data = vec![1, 2, 3, 4, 5];
    let framed = TcpNetworkTransport::frame(&data);
    assert_eq!(TcpNetworkTransport::deframe(&framed), Some(data));
}

#[test]
fn deframe_rejects_malformed() {
    // Too short for the length prefix.
    assert_eq!(TcpNetworkTransport::deframe(&[1, 2]), None);
    // Length says 10 but only 2 bytes present.
    assert_eq!(
        TcpNetworkTransport::deframe(&[0x0A, 0x00, 0x00, 0x00, 0x01, 0x02]),
        None
    );
}

#[test]
fn frame_empty_payload() {
    let framed = TcpNetworkTransport::frame(&[]);
    assert_eq!(framed, vec![0x00, 0x00, 0x00, 0x00]);
    assert_eq!(TcpNetworkTransport::deframe(&framed), Some(vec![]));
}

// ── TcpNetworkTransport: client mode ─────────────────────────────────────────

#[test]
fn client_transport_kind_is_tcp() {
    let conn = Arc::new(InMemoryTcpConnection::new(true));
    let t = TcpNetworkTransport::client(Arc::clone(&conn) as Arc<_>);
    assert_eq!(t.kind(), TransportKind::Tcp);
}

#[test]
fn client_transport_available_only_when_started_and_connected() {
    let conn = Arc::new(InMemoryTcpConnection::new(true));
    let t = TcpNetworkTransport::client(Arc::clone(&conn) as Arc<_>);
    assert!(!t.is_available()); // not started
    t.start();
    assert!(t.is_available());
    conn.set_connected(false);
    assert!(!t.is_available());
}

#[test]
fn client_send_frames_payload() {
    let conn = Arc::new(InMemoryTcpConnection::new(true));
    let t = TcpNetworkTransport::client(Arc::clone(&conn) as Arc<_>);
    t.start();
    t.send(&NetworkPayload::of(vec![9, 8, 7])).unwrap();
    let frames = conn.written_frames();
    assert_eq!(frames.len(), 1);
    // The transport writes one framed buffer: [len LE][data].
    assert_eq!(frames[0], vec![0x03, 0x00, 0x00, 0x00, 9, 8, 7]);
}

#[test]
fn client_receive_loop_deframes_inbound() {
    let conn = Arc::new(InMemoryTcpConnection::new(true));
    let t = TcpNetworkTransport::client(Arc::clone(&conn) as Arc<_>);
    t.start();
    // The pump delivers deframed payload bytes.
    conn.simulate_inbound(vec![1, 2]);
    conn.simulate_inbound(vec![3]);
    let drained = t.drain();
    assert_eq!(drained.len(), 2);
    assert_eq!(drained[0].data, vec![1, 2]);
    assert_eq!(drained[1].data, vec![3]);
}

#[test]
fn client_send_when_disconnected_errors() {
    let conn = Arc::new(InMemoryTcpConnection::new(false));
    let t = TcpNetworkTransport::client(Arc::clone(&conn) as Arc<_>);
    t.start();
    assert_eq!(
        t.send(&NetworkPayload::of(vec![1])),
        Err(TransportError::NotAvailable(TransportKind::Tcp))
    );
}

// ── TcpNetworkTransport: listener mode ───────────────────────────────────────

#[test]
fn listener_transport_has_no_send_path() {
    let t = TcpNetworkTransport::listener(9000);
    assert_eq!(t.kind(), TransportKind::Tcp);
    assert_eq!(t.listen_port(), Some(9000));
    // C#: bare listener has `_stream == null` → send fails; IsAvailable is false.
    t.start();
    assert!(!t.is_available());
    assert_eq!(
        t.send(&NetworkPayload::of(vec![1])),
        Err(TransportError::NotAvailable(TransportKind::Tcp))
    );
}

#[test]
fn client_stop_drops_further_inbound() {
    let conn = Arc::new(InMemoryTcpConnection::new(true));
    let t = TcpNetworkTransport::client(Arc::clone(&conn) as Arc<_>);
    t.start();
    t.stop();
    conn.simulate_inbound(vec![1]);
    assert!(t.drain().is_empty());
}
