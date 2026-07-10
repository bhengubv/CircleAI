//! networking_dtn_test.rs
//!
//! Ports the `CircleAI.Networking.Dtn` surface: `DtnPriority`, `DtnBundle`,
//! `DtnCustodyRecord`, `InMemoryDtnBundleStore`, and `DtnSyncChannel`
//! (store-and-forward push over injected transports).

use std::sync::Arc;

use chrono::{Duration as ChronoDuration, Utc};
use circle_ai::networking::{
    INetworkTransport, ISyncChannel, InMemoryNetworkTransport, SyncDelta, SyncDeliveryMode,
    TransportKind,
};
use circle_ai::networking_transports::{
    DtnBundle, DtnCustodyRecord, DtnPriority, DtnSyncChannel, InMemoryDtnBundleStore,
};

fn bundle(id: &str, dest: &str, expires_in_h: i64) -> DtnBundle {
    let now = Utc::now();
    DtnBundle::new(
        id,
        "src",
        dest,
        vec![1, 2, 3],
        now + ChronoDuration::hours(expires_in_h),
        true,
        0,
        now,
    )
}

fn delta(owner: &str, domain: &str, seq: i64, mode: SyncDeliveryMode) -> SyncDelta {
    SyncDelta::new(
        owner,
        "device-src",
        "device-dst",
        domain,
        vec![9, 9],
        seq,
        mode,
        None,
        Utc::now(),
        None,
    )
}

// ── DtnPriority ─────────────────────────────────────────────────────────────

#[test]
fn dtn_priority_orders_bulk_to_expedited() {
    assert!(DtnPriority::Bulk < DtnPriority::Normal);
    assert!(DtnPriority::Normal < DtnPriority::Expedited);
}

// ── InMemoryDtnBundleStore ──────────────────────────────────────────────────

#[test]
fn store_store_and_get() {
    let store = InMemoryDtnBundleStore::new();
    store.store(bundle("b1", "node-x", 72));
    assert_eq!(store.get("b1").unwrap().destination_node_id, "node-x");
    assert!(store.get("nope").is_none());
    assert_eq!(store.all().len(), 1);
}

#[test]
fn store_custody_roundtrips() {
    let store = InMemoryDtnBundleStore::new();
    let now = Utc::now();
    store.accept_custody(DtnCustodyRecord::new("b1", "custodian-A", now));
    assert_eq!(store.get_custody("b1").unwrap().custodian_node, "custodian-A");
    assert!(store.get_custody("b2").is_none());
}

#[test]
fn store_is_expired_true_for_unknown_bundle() {
    let store = InMemoryDtnBundleStore::new();
    assert!(store.is_expired("ghost", Utc::now()));
}

#[test]
fn store_is_expired_respects_expiry() {
    let store = InMemoryDtnBundleStore::new();
    store.store(bundle("b1", "x", 1)); // expires in 1h
    let now = Utc::now();
    assert!(!store.is_expired("b1", now));
    assert!(store.is_expired("b1", now + ChronoDuration::hours(2)));
}

#[test]
fn store_purge_removes_expired_and_returns_count() {
    let store = InMemoryDtnBundleStore::new();
    store.store(bundle("live", "x", 72));
    store.store(bundle("dead1", "x", -1)); // already expired
    store.store(bundle("dead2", "x", -5));
    store.accept_custody(DtnCustodyRecord::new("dead1", "c", Utc::now()));
    let removed = store.purge(Utc::now());
    assert_eq!(removed, 2);
    assert!(store.get("live").is_some());
    assert!(store.get("dead1").is_none());
    // Custody for a purged bundle is gone too.
    assert!(store.get_custody("dead1").is_none());
}

#[test]
fn store_in_flight_to_filters_destination() {
    let store = InMemoryDtnBundleStore::new();
    store.store(bundle("b1", "alice", 72));
    store.store(bundle("b2", "alice", 72));
    store.store(bundle("b3", "bob", 72));
    assert_eq!(store.in_flight_to("alice").len(), 2);
    assert_eq!(store.in_flight_to("bob").len(), 1);
    assert!(store.in_flight_to("carol").is_empty());
}

// ── DtnSyncChannel ──────────────────────────────────────────────────────────

#[test]
fn sync_channel_pushes_via_first_available_transport() {
    let t1 = Arc::new(InMemoryNetworkTransport::new(TransportKind::WiFi, "n1"));
    let t2 = Arc::new(InMemoryNetworkTransport::new(TransportKind::Bluetooth, "n2"));
    t1.start(); // available
    // t2 left stopped.
    let ch = DtnSyncChannel::new(vec![
        Arc::clone(&t1) as Arc<dyn INetworkTransport>,
        Arc::clone(&t2) as Arc<dyn INetworkTransport>,
    ]);
    ch.push_delta(&delta("owner-1", "d", 1, SyncDeliveryMode::Guaranteed));
    // First available transport (t1) got the send; nothing queued.
    assert_eq!(t1.sent().len(), 1);
    assert_eq!(t1.sent()[0].content_type, "application/dtn-bundle");
    assert!(ch.queued().is_empty());
}

#[test]
fn sync_channel_queues_when_no_transport_available() {
    let t1 = Arc::new(InMemoryNetworkTransport::new(TransportKind::WiFi, "n1"));
    // Not started → unavailable.
    let ch = DtnSyncChannel::new(vec![Arc::clone(&t1) as Arc<dyn INetworkTransport>]);
    ch.push_delta(&delta("owner-1", "d", 1, SyncDeliveryMode::Guaranteed));
    assert_eq!(t1.sent().len(), 0);
    // Bundle queued locally for later delivery.
    assert_eq!(ch.queued().len(), 1);
    assert_eq!(ch.queued()[0].destination_node_id, "device-dst");
}

#[test]
fn sync_channel_urgent_delivery_uses_urgent_priority() {
    let t1 = Arc::new(InMemoryNetworkTransport::new(TransportKind::WiFi, "n1"));
    t1.start();
    let ch = DtnSyncChannel::new(vec![Arc::clone(&t1) as Arc<dyn INetworkTransport>]);
    ch.push_delta(&delta("owner-1", "d", 1, SyncDeliveryMode::Urgent));
    assert_eq!(
        t1.sent()[0].priority,
        circle_ai::networking::MessagePriority::Urgent
    );
}

#[test]
fn sync_channel_bundle_custody_matches_delivery_mode() {
    // Guaranteed => custody required; other modes => not.
    let t1 = Arc::new(InMemoryNetworkTransport::new(TransportKind::WiFi, "n1"));
    // Unavailable so the bundle is queued and inspectable.
    let ch = DtnSyncChannel::new(vec![Arc::clone(&t1) as Arc<dyn INetworkTransport>]);
    ch.push_delta(&delta("o", "d", 1, SyncDeliveryMode::Guaranteed));
    assert!(ch.queued()[0].custody_required);

    let t2 = Arc::new(InMemoryNetworkTransport::new(TransportKind::WiFi, "n2"));
    let ch2 = DtnSyncChannel::new(vec![Arc::clone(&t2) as Arc<dyn INetworkTransport>]);
    ch2.push_delta(&delta("o", "d", 1, SyncDeliveryMode::BestEffort));
    assert!(!ch2.queued()[0].custody_required);
}

#[test]
fn sync_channel_tracks_monotonic_sequence() {
    let ch = DtnSyncChannel::new(vec![]);
    assert_eq!(ch.get_last_sequence("o", "d"), 0);
    ch.push_delta(&delta("o", "d", 7, SyncDeliveryMode::Guaranteed));
    assert_eq!(ch.get_last_sequence("o", "d"), 7);
    ch.push_delta(&delta("o", "d", 4, SyncDeliveryMode::Guaranteed));
    assert_eq!(ch.get_last_sequence("o", "d"), 7); // no backward move
}

#[test]
fn sync_channel_deliver_then_receive() {
    let ch = DtnSyncChannel::new(vec![]);
    ch.deliver("owner-1", delta("owner-1", "d", 1, SyncDeliveryMode::Guaranteed));
    ch.deliver("owner-1", delta("owner-1", "d", 2, SyncDeliveryMode::Guaranteed));
    let got = ch.receive_deltas("owner-1");
    assert_eq!(got.len(), 2);
    assert!(ch.receive_deltas("owner-1").is_empty());
    assert!(ch.receive_deltas("owner-2").is_empty());
}

#[test]
fn sync_channel_default_ttl_is_72h() {
    // A pushed bundle (queued when no transport) expires ~72h out by default.
    let ch = DtnSyncChannel::new(vec![]);
    let before = Utc::now();
    ch.push_delta(&delta("o", "d", 1, SyncDeliveryMode::Guaranteed));
    let q = ch.queued();
    assert_eq!(q.len(), 1);
    let expiry = q[0].expires_at;
    // Between 71h and 73h from now (allowing scheduling slack).
    assert!(expiry > before + ChronoDuration::hours(71));
    assert!(expiry < before + ChronoDuration::hours(73));
}
