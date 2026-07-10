//! networking_aethernet_test.rs
//!
//! Ports the `CircleAI.Networking.AetherNet` surface: `AetherPeerKind`,
//! `AetherPeer` / `AetherHopTelemetry` / `AetherPacketSummary`,
//! `InMemoryAetherNetRegistry`, `AetherNetworkTransport` (+ injected
//! `IAetherRouter`), `AetherPeerDiscovery`, and `AetherSyncChannel`.

use std::sync::Arc;

use chrono::{Duration as ChronoDuration, Utc};
use circle_ai::networking::{
    INetworkTransport, IPeerDiscovery, ISyncChannel, MessagePriority, NetworkPayload, PeerInfo,
    PeerRole, SyncDelta, SyncDeliveryMode, TransportError, TransportKind,
};
use circle_ai::networking_transports::{
    AetherHopTelemetry, AetherNetworkTransport, AetherPacketSummary, AetherPeer, AetherPeerDiscovery,
    AetherPeerKind, AetherSyncChannel, FixedAetherAvailability, InMemoryAetherNetRegistry,
    InMemoryAetherRouter,
};

fn peer(id: &str) -> AetherPeer {
    AetherPeer::new(
        id,
        AetherPeerKind::Phone,
        Some(format!("Device {id}")),
        vec!["chat".into(), "sync".into()],
    )
}

fn sync_delta(owner: &str, domain: &str, seq: i64, mode: SyncDeliveryMode) -> SyncDelta {
    SyncDelta::new(
        owner,
        "device-src",
        "device-dst",
        domain,
        vec![1, 2, 3],
        seq,
        mode,
        None,
        Utc::now(),
        None,
    )
}

// ── InMemoryAetherNetRegistry ───────────────────────────────────────────────

#[test]
fn registry_register_and_get_peer() {
    let reg = InMemoryAetherNetRegistry::new();
    reg.register(peer("p1"));
    let got = reg.get_peer("p1").unwrap();
    assert_eq!(got.peer_id, "p1");
    assert_eq!(got.kind, AetherPeerKind::Phone);
    assert!(reg.get_peer("nope").is_none());
}

#[test]
fn registry_peers_are_ordered_by_id() {
    let reg = InMemoryAetherNetRegistry::new();
    reg.register(peer("c"));
    reg.register(peer("a"));
    reg.register(peer("b"));
    let ids: Vec<String> = reg.peers().iter().map(|p| p.peer_id.clone()).collect();
    assert_eq!(ids, vec!["a", "b", "c"]);
}

#[test]
fn registry_register_replaces_same_id() {
    let reg = InMemoryAetherNetRegistry::new();
    reg.register(peer("p1"));
    reg.register(AetherPeer::new(
        "p1",
        AetherPeerKind::Vehicle,
        None,
        vec![],
    ));
    assert_eq!(reg.peers().len(), 1);
    assert_eq!(reg.get_peer("p1").unwrap().kind, AetherPeerKind::Vehicle);
}

#[test]
fn registry_avg_round_trip_zero_when_none() {
    let reg = InMemoryAetherNetRegistry::new();
    assert_eq!(reg.avg_round_trip_ms("p1"), 0.0);
}

#[test]
fn registry_avg_round_trip_averages_peer_samples() {
    let reg = InMemoryAetherNetRegistry::new();
    let now = Utc::now();
    reg.record_hop(AetherHopTelemetry::new("p1", 1, 10.0, now));
    reg.record_hop(AetherHopTelemetry::new("p1", 2, 30.0, now));
    reg.record_hop(AetherHopTelemetry::new("p2", 1, 99.0, now));
    assert_eq!(reg.avg_round_trip_ms("p1"), 20.0);
    assert_eq!(reg.avg_round_trip_ms("p2"), 99.0);
}

#[test]
fn registry_recent_packets_newest_first_and_limited() {
    let reg = InMemoryAetherNetRegistry::new();
    let base = Utc::now();
    for i in 0..5 {
        reg.record_packet(AetherPacketSummary::new(
            format!("pkt-{i}"),
            "a",
            "b",
            100,
            "data",
            base + ChronoDuration::seconds(i),
        ));
    }
    let recent = reg.recent_packets(3);
    assert_eq!(recent.len(), 3);
    // Newest (i=4) first.
    assert_eq!(recent[0].packet_id, "pkt-4");
    assert_eq!(recent[2].packet_id, "pkt-2");
}

#[test]
fn registry_total_bytes_between_directed_pair() {
    let reg = InMemoryAetherNetRegistry::new();
    let now = Utc::now();
    reg.record_packet(AetherPacketSummary::new("1", "a", "b", 100, "d", now));
    reg.record_packet(AetherPacketSummary::new("2", "a", "b", 250, "d", now));
    reg.record_packet(AetherPacketSummary::new("3", "b", "a", 999, "d", now)); // reverse dir
    assert_eq!(reg.total_bytes_between("a", "b"), 350);
    assert_eq!(reg.total_bytes_between("b", "a"), 999);
    assert_eq!(reg.total_bytes_between("a", "c"), 0);
}

// ── AetherNetworkTransport ──────────────────────────────────────────────────

#[test]
fn transport_kind_is_aether() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let t = AetherNetworkTransport::new(Arc::new(FixedAetherAvailability(true)), router);
    assert_eq!(t.kind(), TransportKind::Aether);
}

#[test]
fn transport_availability_follows_probe() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let up = AetherNetworkTransport::new(
        Arc::new(FixedAetherAvailability(true)),
        Arc::clone(&router) as Arc<_>,
    );
    let down = AetherNetworkTransport::new(Arc::new(FixedAetherAvailability(false)), router);
    assert!(up.is_available());
    assert!(!down.is_available());
}

#[test]
fn transport_send_when_unavailable_errors() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let t = AetherNetworkTransport::new(Arc::new(FixedAetherAvailability(false)), router);
    let payload = NetworkPayload::of(vec![1]);
    assert_eq!(
        t.send(&payload),
        Err(TransportError::NotAvailable(TransportKind::Aether))
    );
}

#[test]
fn transport_send_routes_via_engine() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let t = AetherNetworkTransport::new(
        Arc::new(FixedAetherAvailability(true)),
        Arc::clone(&router) as Arc<_>,
    );
    t.send(&NetworkPayload::of(vec![7])).unwrap();
    let routed = router.routed();
    assert_eq!(routed.len(), 1);
    assert_eq!(routed[0].0.data, vec![7]);
    assert!(!routed[0].1); // not emergency
}

#[test]
fn transport_emergency_triggers_sos_flood() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let t = AetherNetworkTransport::new(
        Arc::new(FixedAetherAvailability(true)),
        Arc::clone(&router) as Arc<_>,
    );
    let payload = NetworkPayload::create(
        vec![9],
        None,
        MessagePriority::Emergency,
        "text/plain",
        None,
    );
    t.send(&payload).unwrap();
    assert!(router.routed()[0].1); // SOS flood flag set
}

#[test]
fn transport_receive_buffers_and_drain_clears() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let t = AetherNetworkTransport::new(Arc::new(FixedAetherAvailability(true)), router);
    t.start();
    t.receive(NetworkPayload::of(vec![1]));
    t.receive(NetworkPayload::of(vec![2]));
    let drained = t.drain();
    assert_eq!(drained.len(), 2);
    assert!(t.drain().is_empty());
}

#[test]
fn transport_stop_completes_inbound() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let t = AetherNetworkTransport::new(Arc::new(FixedAetherAvailability(true)), router);
    t.start();
    t.stop();
    // After stop the writer is completed: further receives are dropped.
    t.receive(NetworkPayload::of(vec![1]));
    assert!(t.drain().is_empty());
    // Restart re-opens it.
    t.start();
    t.receive(NetworkPayload::of(vec![2]));
    assert_eq!(t.drain().len(), 1);
}

// ── AetherPeerDiscovery ─────────────────────────────────────────────────────

#[test]
fn discovery_projects_registry_peers_to_peerinfo() {
    let reg = Arc::new(InMemoryAetherNetRegistry::new());
    reg.register(peer("p1"));
    reg.register(peer("p2"));
    let disc = AetherPeerDiscovery::new(Arc::clone(&reg));
    let found = disc.discover();
    assert_eq!(found.len(), 2);
    assert!(found.iter().all(|p| p.supported_transports == vec![TransportKind::Aether]));
    assert!(found.iter().all(|p| p.role == PeerRole::Peer));
    let names: Vec<Option<String>> = found.iter().map(|p| p.display_name.clone()).collect();
    assert!(names.contains(&Some("Device p1".to_string())));
}

#[test]
fn discovery_announce_records_local_info() {
    let reg = Arc::new(InMemoryAetherNetRegistry::new());
    let disc = AetherPeerDiscovery::new(reg);
    let me = PeerInfo::new(
        "me",
        Some("My Phone".into()),
        vec![TransportKind::Aether],
        PeerRole::Peer,
        None,
        Utc::now(),
    );
    disc.announce(me);
    assert_eq!(disc.announcements().len(), 1);
    assert_eq!(disc.announcements()[0].node_id, "me");
}

// ── AetherSyncChannel ───────────────────────────────────────────────────────

#[test]
fn sync_channel_push_routes_dtn_bundle() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let ch = AetherSyncChannel::new(Arc::clone(&router) as Arc<_>);
    ch.push_delta(&sync_delta("owner-1", "memory.episodic", 1, SyncDeliveryMode::Guaranteed));
    // The delta was serialised into a DTN bundle handed to the engine.
    let routed = router.routed();
    assert_eq!(routed.len(), 1);
    assert_eq!(routed[0].0.content_type, "application/dtn-bundle");
    assert_eq!(routed[0].0.destination_id.as_deref(), Some("device-dst"));
    // 72h TTL by default.
    assert!(routed[0].0.ttl.is_some());
}

#[test]
fn sync_channel_tracks_monotonic_sequence() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let ch = AetherSyncChannel::new(router);
    assert_eq!(ch.get_last_sequence("owner-1", "persona"), 0);
    ch.push_delta(&sync_delta("owner-1", "persona", 5, SyncDeliveryMode::Guaranteed));
    assert_eq!(ch.get_last_sequence("owner-1", "persona"), 5);
    ch.push_delta(&sync_delta("owner-1", "persona", 3, SyncDeliveryMode::Guaranteed)); // lower
    assert_eq!(ch.get_last_sequence("owner-1", "persona"), 5); // no backward move
    ch.push_delta(&sync_delta("owner-1", "persona", 9, SyncDeliveryMode::Guaranteed));
    assert_eq!(ch.get_last_sequence("owner-1", "persona"), 9);
    // Independent domain.
    assert_eq!(ch.get_last_sequence("owner-1", "affect"), 0);
}

#[test]
fn sync_channel_deliver_then_receive_drains_per_owner() {
    let router = Arc::new(InMemoryAetherRouter::new());
    let ch = AetherSyncChannel::new(router);
    ch.deliver("owner-1", sync_delta("owner-1", "d", 1, SyncDeliveryMode::Guaranteed));
    ch.deliver("owner-1", sync_delta("owner-1", "d", 2, SyncDeliveryMode::Guaranteed));
    ch.deliver("owner-2", sync_delta("owner-2", "d", 1, SyncDeliveryMode::Guaranteed));
    let o1 = ch.receive_deltas("owner-1");
    assert_eq!(o1.len(), 2);
    assert_eq!(ch.receive_deltas("owner-2").len(), 1);
    // Drained.
    assert!(ch.receive_deltas("owner-1").is_empty());
    assert!(ch.receive_deltas("owner-3").is_empty());
}
