//! networking_wifi_test.rs
//!
//! Ports the `CircleAI.Networking.WiFi` surface: `WiFiNetworkTransport` (UDP
//! broadcast/unicast + data-port framing), `IWiFiDatagramSocket` /
//! `InMemoryWiFiDatagramSocket`, and `WiFiPeerDiscovery` (beacon magic, projection,
//! announce).

use std::sync::Arc;

use circle_ai::networking::{
    INetworkTransport, IPeerDiscovery, MessagePriority, NetworkPayload, PeerInfo, PeerRole,
    TransportKind,
};
use circle_ai::networking_transports::{
    InMemoryWiFiDatagramSocket, WiFiNetworkTransport, WiFiPeerDiscovery, BEACON_MAGIC,
    BROADCAST_ADDR, DATA_PORT, DISCOVERY_PORT,
};

fn payload_to(dest: Option<&str>, data: Vec<u8>) -> NetworkPayload {
    NetworkPayload::create(
        data,
        dest.map(|s| s.to_string()),
        MessagePriority::Normal,
        "application/octet-stream",
        None,
    )
}

// ── Ports ────────────────────────────────────────────────────────────────────

#[test]
fn ports_match_csharp() {
    assert_eq!(DISCOVERY_PORT, 47890);
    assert_eq!(DATA_PORT, 47891);
    assert_eq!(BEACON_MAGIC, "CIRCLEAI:BEACON:");
}

// ── WiFiNetworkTransport: datagram build (unicast vs broadcast) ───────────────

#[test]
fn build_datagram_unicasts_to_parseable_ip() {
    let dg = WiFiNetworkTransport::build_datagram(&payload_to(Some("192.168.1.50"), vec![1, 2]));
    assert_eq!(dg.dest_host, "192.168.1.50");
    assert_eq!(dg.dest_port, DATA_PORT);
    assert_eq!(dg.data, vec![1, 2]);
}

#[test]
fn build_datagram_broadcasts_for_non_ip_destination() {
    // A non-IP destination id → broadcast.
    let dg = WiFiNetworkTransport::build_datagram(&payload_to(Some("some-node-name"), vec![7]));
    assert_eq!(dg.dest_host, BROADCAST_ADDR);
    assert_eq!(dg.dest_port, DATA_PORT);
}

#[test]
fn build_datagram_broadcasts_for_no_destination() {
    let dg = WiFiNetworkTransport::build_datagram(&payload_to(None, vec![7]));
    assert_eq!(dg.dest_host, BROADCAST_ADDR);
}

// ── WiFiNetworkTransport: transport behaviour ────────────────────────────────

#[test]
fn transport_kind_and_availability() {
    let sock = Arc::new(InMemoryWiFiDatagramSocket::new());
    let t = WiFiNetworkTransport::new(Arc::clone(&sock) as Arc<_>);
    assert_eq!(t.kind(), TransportKind::WiFi);
    assert!(!t.is_available());
    t.start();
    assert!(t.is_available());
    t.stop();
    assert!(!t.is_available());
}

#[test]
fn transport_send_emits_datagram() {
    let sock = Arc::new(InMemoryWiFiDatagramSocket::new());
    let t = WiFiNetworkTransport::new(Arc::clone(&sock) as Arc<_>);
    t.start();
    t.send(&payload_to(Some("10.0.0.1"), vec![9])).unwrap();
    let sent = sock.sent();
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0].dest_host, "10.0.0.1");
    assert_eq!(sent[0].dest_port, DATA_PORT);
    assert_eq!(sent[0].data, vec![9]);
}

#[test]
fn transport_receive_loop_buffers_datagrams() {
    let sock = Arc::new(InMemoryWiFiDatagramSocket::new());
    let t = WiFiNetworkTransport::new(Arc::clone(&sock) as Arc<_>);
    t.start();
    sock.simulate_inbound("10.0.0.2", vec![1, 2]);
    sock.simulate_inbound("10.0.0.3", vec![3]);
    let drained = t.drain();
    assert_eq!(drained.len(), 2);
    assert_eq!(drained[0].data, vec![1, 2]);
    assert!(t.drain().is_empty());
}

#[test]
fn transport_stop_drops_further_inbound() {
    let sock = Arc::new(InMemoryWiFiDatagramSocket::new());
    let t = WiFiNetworkTransport::new(Arc::clone(&sock) as Arc<_>);
    t.start();
    t.stop();
    sock.simulate_inbound("10.0.0.2", vec![1]);
    assert!(t.drain().is_empty());
}

// ── WiFiPeerDiscovery ────────────────────────────────────────────────────────

#[test]
fn discovery_parse_beacon_projects_peer() {
    let data = format!("{BEACON_MAGIC}node-xyz").into_bytes();
    let peer = WiFiPeerDiscovery::parse_beacon("10.0.0.9", &data).unwrap();
    assert_eq!(peer.node_id, "node-xyz");
    assert_eq!(peer.display_name.as_deref(), Some("WiFi/10.0.0.9"));
    assert_eq!(peer.supported_transports, vec![TransportKind::WiFi]);
    assert_eq!(peer.role, PeerRole::Peer);
}

#[test]
fn discovery_parse_beacon_ignores_non_beacon() {
    assert!(WiFiPeerDiscovery::parse_beacon("10.0.0.9", b"HELLO:whatever").is_none());
    assert!(WiFiPeerDiscovery::parse_beacon("10.0.0.9", &[0xFF, 0xFE]).is_none());
}

#[test]
fn discovery_build_beacon_frames_node_id() {
    let bytes = WiFiPeerDiscovery::build_beacon("abc");
    assert_eq!(bytes, b"CIRCLEAI:BEACON:abc".to_vec());
}

#[test]
fn discovery_receives_beacons_via_socket() {
    let sock = Arc::new(InMemoryWiFiDatagramSocket::new());
    let disco = WiFiPeerDiscovery::new(Arc::clone(&sock) as Arc<_>);
    // A beacon arrives on the wire.
    sock.simulate_inbound("10.0.0.5", WiFiPeerDiscovery::build_beacon("peerA"));
    sock.simulate_inbound("10.0.0.6", WiFiPeerDiscovery::build_beacon("peerB"));
    // A non-beacon datagram is ignored.
    sock.simulate_inbound("10.0.0.7", b"garbage".to_vec());

    let peers = disco.discover();
    assert_eq!(peers.len(), 2);
    let ids: Vec<String> = peers.iter().map(|p| p.node_id.clone()).collect();
    assert!(ids.contains(&"peerA".to_string()));
    assert!(ids.contains(&"peerB".to_string()));
}

#[test]
fn discovery_freshest_beacon_wins_per_node() {
    let sock = Arc::new(InMemoryWiFiDatagramSocket::new());
    let disco = WiFiPeerDiscovery::new(Arc::clone(&sock) as Arc<_>);
    sock.simulate_inbound("10.0.0.5", WiFiPeerDiscovery::build_beacon("peerA"));
    // Same node id, different address → replaces, not duplicates.
    sock.simulate_inbound("10.0.0.99", WiFiPeerDiscovery::build_beacon("peerA"));
    let peers = disco.discover();
    assert_eq!(peers.len(), 1);
    assert_eq!(peers[0].display_name.as_deref(), Some("WiFi/10.0.0.99"));
}

#[test]
fn discovery_announce_broadcasts_beacon_and_records() {
    let sock = Arc::new(InMemoryWiFiDatagramSocket::new());
    let disco = WiFiPeerDiscovery::new(Arc::clone(&sock) as Arc<_>);
    let local = PeerInfo::new(
        "me",
        Some("Local".into()),
        vec![TransportKind::WiFi],
        PeerRole::Peer,
        None,
        chrono::Utc::now(),
    );
    disco.announce(local);
    let sent = sock.sent();
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0].dest_host, BROADCAST_ADDR);
    assert_eq!(sent[0].dest_port, DISCOVERY_PORT);
    assert_eq!(sent[0].data, b"CIRCLEAI:BEACON:me".to_vec());
    assert_eq!(disco.announcements().len(), 1);
}
