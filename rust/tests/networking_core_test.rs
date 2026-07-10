//! networking_core_test.rs
//!
//! Ports the `CircleAI.Networking` transport-abstraction surface: the enums and
//! value types, `INetworkPolicy` / `DefaultNetworkPolicy` / `NetworkPolicyBuilder`,
//! `ITransportSelector` / `CascadeTransportSelector`, `INetworkTransport`,
//! `IMessageChannel`, `IMeshNetwork`, `IConnectivityMonitor`, `IPeerDiscovery`,
//! `IPayloadOptimiser`, and the networking `ISyncChannel`.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use circle_ai::networking::{
    CascadeTransportSelector, ConnectivityState, DefaultNetworkPolicy, IConnectivityMonitor,
    IMeshNetwork, IMessageChannel, INetworkPolicy, INetworkTransport, IPayloadOptimiser,
    IPeerDiscovery, ISyncChannel, ITransportSelector, InMemoryMeshNetwork, InMemoryMessageBus,
    InMemoryMessageChannel, InMemoryNetworkTransport, InMemoryPeerDiscovery, InMemorySyncChannel,
    ManualConnectivityMonitor, MessagePriority, NetworkContext, NetworkPayload, NetworkPolicyBuilder,
    PeerInfo, PeerRole, RlePayloadOptimiser, SchedulingHint, SyncDeliveryMode, SyncDelta,
    TransportError, TransportKind, DEFAULT_CASCADE,
};

// ── Value types: NetworkPayload ─────────────────────────────────────────────

#[test]
fn payload_create_assigns_hyphenless_id_and_defaults() {
    let p = NetworkPayload::create(
        vec![1, 2, 3],
        Some("dest".into()),
        MessagePriority::High,
        "text/plain",
        None,
    );
    // Guid.ToString("N") => 32 lowercase hex chars, no dashes.
    assert_eq!(p.id.len(), 32);
    assert!(!p.id.contains('-'));
    assert!(p.id.chars().all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
    assert_eq!(p.data, vec![1, 2, 3]);
    assert_eq!(p.destination_id.as_deref(), Some("dest"));
    assert_eq!(p.priority, MessagePriority::High);
    assert_eq!(p.content_type, "text/plain");
    assert!(p.source_id.is_none());
    assert!(p.metadata.is_empty());
    assert!(p.ttl.is_none());
}

#[test]
fn payload_of_uses_csharp_defaults() {
    let p = NetworkPayload::of(b"hello".to_vec());
    assert_eq!(p.priority, MessagePriority::Normal);
    assert_eq!(p.content_type, "application/octet-stream");
    assert!(p.destination_id.is_none());
    assert!(p.ttl.is_none());
}

#[test]
fn payload_ids_are_unique() {
    let a = NetworkPayload::of(vec![0]);
    let b = NetworkPayload::of(vec![0]);
    assert_ne!(a.id, b.id);
}

#[test]
fn payload_with_source_does_not_mutate_original() {
    let p = NetworkPayload::of(vec![9]);
    let stamped = p.with_source("node-A");
    assert!(p.source_id.is_none());
    assert_eq!(stamped.source_id.as_deref(), Some("node-A"));
    assert_eq!(stamped.id, p.id); // identity preserved
}

// ── Value types: NetworkContext ─────────────────────────────────────────────

#[test]
fn network_context_offline_matches_csharp_static() {
    let ctx = NetworkContext::offline();
    assert_eq!(ctx.state, ConnectivityState::Offline);
    assert_eq!(ctx.preferred_transport, TransportKind::LocalStore);
    assert!(ctx.available_transports.is_empty());
    assert!(ctx.signal_strength_dbm.is_none());
    assert!(ctx.estimated_bandwidth_bps.is_none());
    assert!(ctx.latency_ms.is_none());
    assert_eq!(ctx.nearby_peer_count, 0);
}

// ── Enums ordering (used by the cascade) ────────────────────────────────────

#[test]
fn transport_kind_declaration_order_is_preserved() {
    // Http first, LocalStore last — the C# enum order.
    assert!(TransportKind::Http < TransportKind::WebSocket);
    assert!(TransportKind::Aether < TransportKind::Dtn);
    assert!(TransportKind::Dtn < TransportKind::LocalStore);
}

#[test]
fn message_priority_orders_low_to_emergency() {
    assert!(MessagePriority::Low < MessagePriority::Normal);
    assert!(MessagePriority::Urgent < MessagePriority::Emergency);
}

// ── INetworkPolicy / DefaultNetworkPolicy ───────────────────────────────────

#[test]
fn default_policy_is_fully_permissive() {
    let p = DefaultNetworkPolicy::INSTANCE;
    let payload = NetworkPayload::of(vec![1]);
    for t in DEFAULT_CASCADE {
        assert!(p.permits(t, &payload));
    }
    assert!(p.force_transport().is_none());
    assert!(!p.mesh_first());
    assert!(p.offline_queue_enabled());
    assert!(p.allow_cloud_transports());
}

// ── NetworkPolicyBuilder ────────────────────────────────────────────────────

#[test]
fn builder_no_cloud_blocks_cloud_transports() {
    let policy = NetworkPolicyBuilder::new().no_cloud().build();
    let payload = NetworkPayload::of(vec![1]);
    // Cloud four are blocked.
    for t in [
        TransportKind::Http,
        TransportKind::WebSocket,
        TransportKind::Grpc,
        TransportKind::Mqtt,
    ] {
        assert!(!policy.permits(t, &payload), "{t:?} should be blocked");
    }
    // Non-cloud allowed (empty allow-list => all non-cloud pass).
    assert!(policy.permits(TransportKind::Aether, &payload));
    assert!(policy.permits(TransportKind::Bluetooth, &payload));
    assert!(!policy.allow_cloud_transports());
}

#[test]
fn builder_allow_list_restricts_to_named_transports() {
    let policy = NetworkPolicyBuilder::new()
        .allow(&[TransportKind::Aether, TransportKind::WiFi])
        .build();
    let payload = NetworkPayload::of(vec![1]);
    assert!(policy.permits(TransportKind::Aether, &payload));
    assert!(policy.permits(TransportKind::WiFi, &payload));
    assert!(!policy.permits(TransportKind::Bluetooth, &payload));
    assert!(!policy.permits(TransportKind::Http, &payload));
}

#[test]
fn builder_flags_roundtrip() {
    let policy = NetworkPolicyBuilder::new()
        .mesh_first()
        .disable_queue()
        .force(TransportKind::Aether)
        .build();
    assert!(policy.mesh_first());
    assert!(!policy.offline_queue_enabled());
    assert_eq!(policy.force_transport(), Some(TransportKind::Aether));
}

#[test]
fn builder_default_queue_is_enabled() {
    let policy = NetworkPolicyBuilder::new().build();
    assert!(policy.offline_queue_enabled());
}

// ── ITransportSelector / CascadeTransportSelector ───────────────────────────

fn ctx_with(available: Vec<TransportKind>) -> NetworkContext {
    NetworkContext::new(
        ConnectivityState::Online,
        TransportKind::Grpc,
        available,
        None,
        None,
        None,
        0,
        chrono::Utc::now(),
    )
}

#[test]
fn selector_default_cascade_prefers_grpc_first() {
    let sel = CascadeTransportSelector::with_default_policy();
    let payload = NetworkPayload::of(vec![1]);
    // Empty available => availability-unconstrained => full cascade order.
    let ctx = ctx_with(vec![]);
    let cascade = sel.get_cascade(&payload, &ctx);
    assert_eq!(cascade, DEFAULT_CASCADE.to_vec());
    assert_eq!(sel.select_best(&payload, &ctx), TransportKind::Grpc);
}

#[test]
fn selector_filters_by_context_availability() {
    let sel = CascadeTransportSelector::with_default_policy();
    let payload = NetworkPayload::of(vec![1]);
    let ctx = ctx_with(vec![TransportKind::Bluetooth, TransportKind::WiFi]);
    let cascade = sel.get_cascade(&payload, &ctx);
    // Only WiFi + Bluetooth available, LocalStore always appended.
    assert_eq!(
        cascade,
        vec![
            TransportKind::WiFi,
            TransportKind::Bluetooth,
            TransportKind::LocalStore
        ]
    );
    assert_eq!(sel.select_best(&payload, &ctx), TransportKind::WiFi);
}

#[test]
fn selector_honours_force_transport() {
    let policy = NetworkPolicyBuilder::new().force(TransportKind::Dtn).build();
    let sel = CascadeTransportSelector::new(Arc::new(policy));
    let payload = NetworkPayload::of(vec![1]);
    let ctx = ctx_with(vec![TransportKind::Grpc]);
    let cascade = sel.get_cascade(&payload, &ctx);
    assert_eq!(cascade, vec![TransportKind::Dtn, TransportKind::LocalStore]);
    assert_eq!(sel.select_best(&payload, &ctx), TransportKind::Dtn);
}

#[test]
fn selector_mesh_first_floats_mesh_transports() {
    let policy = NetworkPolicyBuilder::new().mesh_first().build();
    let sel = CascadeTransportSelector::new(Arc::new(policy));
    let payload = NetworkPayload::of(vec![1]);
    let ctx = ctx_with(vec![]); // unconstrained
    let cascade = sel.get_cascade(&payload, &ctx);
    // Mesh transports (WiFi, Bluetooth, NearLink, Aether) lead, in their
    // relative order, ahead of cloud/tcp.
    let first_four = &cascade[..4];
    assert_eq!(
        first_four,
        &[
            TransportKind::WiFi,
            TransportKind::Bluetooth,
            TransportKind::NearLink,
            TransportKind::Aether
        ]
    );
    // Cloud transports still present, just after mesh.
    assert!(cascade.contains(&TransportKind::Grpc));
}

#[test]
fn selector_no_cloud_policy_drops_cloud_from_cascade() {
    let policy = NetworkPolicyBuilder::new().no_cloud().build();
    let sel = CascadeTransportSelector::new(Arc::new(policy));
    let payload = NetworkPayload::of(vec![1]);
    let ctx = ctx_with(vec![]);
    let cascade = sel.get_cascade(&payload, &ctx);
    for cloud in [
        TransportKind::Http,
        TransportKind::WebSocket,
        TransportKind::Grpc,
        TransportKind::Mqtt,
    ] {
        assert!(!cascade.contains(&cloud), "{cloud:?} should be filtered");
    }
    // First survivor is TCP (next in cascade after the cloud four).
    assert_eq!(sel.select_best(&payload, &ctx), TransportKind::Tcp);
}

// ── INetworkTransport / InMemoryNetworkTransport ────────────────────────────

#[test]
fn transport_send_before_start_errors() {
    let t = InMemoryNetworkTransport::new(TransportKind::Grpc, "node-A");
    assert!(!t.is_available());
    let payload = NetworkPayload::of(vec![1]);
    assert_eq!(
        t.send(&payload),
        Err(TransportError::NotAvailable(TransportKind::Grpc))
    );
}

#[test]
fn transport_send_stamps_source_and_records() {
    let t = InMemoryNetworkTransport::new(TransportKind::WiFi, "node-A");
    t.start();
    assert!(t.is_available());
    let payload = NetworkPayload::of(vec![7, 8]);
    t.send(&payload).unwrap();
    let sent = t.sent();
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0].source_id.as_deref(), Some("node-A"));
    assert_eq!(sent[0].data, vec![7, 8]);
    assert_eq!(t.kind(), TransportKind::WiFi);
}

#[test]
fn transport_receive_buffers_before_subscription_and_pull_drains() {
    // Concurrency-safety: a payload received before any subscriber attaches is
    // retained (unbounded buffer) and returned by the first drain.
    let t = InMemoryNetworkTransport::new(TransportKind::Aether, "node-A");
    t.start();
    t.receive(NetworkPayload::of(vec![1]));
    t.receive(NetworkPayload::of(vec![2]));
    let drained = t.drain();
    assert_eq!(drained.len(), 2);
    assert_eq!(drained[0].data, vec![1]);
    assert_eq!(drained[1].data, vec![2]);
    // Second drain is empty.
    assert!(t.drain().is_empty());
}

#[test]
fn transport_push_subscriber_receives_after_subscribe() {
    let t = InMemoryNetworkTransport::new(TransportKind::Aether, "node-A");
    t.start();
    let hits = Arc::new(AtomicUsize::new(0));
    let hits_c = Arc::clone(&hits);
    // Subscribe SYNCHRONOUSLY before driving traffic.
    let _sub = t.subscribe(Arc::new(move |_p: &NetworkPayload| {
        hits_c.fetch_add(1, Ordering::SeqCst);
    }));
    t.receive(NetworkPayload::of(vec![1]));
    t.receive(NetworkPayload::of(vec![2]));
    assert_eq!(hits.load(Ordering::SeqCst), 2);
    assert_eq!(t.subscriber_count(), 1);
}

#[test]
fn transport_subscription_drops_unsubscribe() {
    let t = InMemoryNetworkTransport::new(TransportKind::Aether, "node-A");
    t.start();
    let hits = Arc::new(AtomicUsize::new(0));
    let hits_c = Arc::clone(&hits);
    let sub = t.subscribe(Arc::new(move |_p: &NetworkPayload| {
        hits_c.fetch_add(1, Ordering::SeqCst);
    }));
    drop(sub);
    assert_eq!(t.subscriber_count(), 0);
    t.receive(NetworkPayload::of(vec![1]));
    assert_eq!(hits.load(Ordering::SeqCst), 0);
}

#[test]
fn transport_handler_can_reenter_without_deadlock() {
    // A handler that re-enters the transport (calls drain / subscriber_count)
    // must not self-deadlock, because delivery snapshots subscribers and fires
    // outside the lock.
    let t = Arc::new(InMemoryNetworkTransport::new(TransportKind::Aether, "node-A"));
    t.start();
    let seen = Arc::new(Mutex::new(Vec::<usize>::new()));
    let t_inner = Arc::clone(&t);
    let seen_c = Arc::clone(&seen);
    let _sub = t.subscribe(Arc::new(move |_p: &NetworkPayload| {
        // Re-enter: read subscriber_count (takes the subscribers lock again).
        let n = t_inner.subscriber_count();
        seen_c.lock().unwrap().push(n);
    }));
    t.receive(NetworkPayload::of(vec![1]));
    assert_eq!(&*seen.lock().unwrap(), &[1]);
}

#[test]
fn transport_loopback_delivers_sends_inbound() {
    let t = InMemoryNetworkTransport::new_loopback(TransportKind::Grpc, "node-A");
    t.start();
    t.send(&NetworkPayload::of(vec![5])).unwrap();
    let drained = t.drain();
    assert_eq!(drained.len(), 1);
    assert_eq!(drained[0].data, vec![5]);
    assert_eq!(drained[0].source_id.as_deref(), Some("node-A"));
}

#[test]
fn transport_stopped_drops_inbound() {
    let t = InMemoryNetworkTransport::new(TransportKind::Aether, "node-A");
    // Not started.
    t.receive(NetworkPayload::of(vec![1]));
    assert!(t.drain().is_empty());
}

// ── IMessageChannel / InMemoryMessageChannel ────────────────────────────────

#[derive(serde::Serialize, serde::Deserialize, Debug, PartialEq)]
struct Ping {
    seq: u32,
    note: String,
}

#[test]
fn message_channel_loopback_roundtrips_typed_message() {
    let ch = InMemoryMessageChannel::new("node-A");
    let msg = Ping {
        seq: 7,
        note: "hi".into(),
    };
    ch.send("node-A", "ping", &msg).unwrap();
    let got: Vec<Ping> = ch.receive("ping").unwrap();
    assert_eq!(got.len(), 1);
    assert_eq!(got[0], msg);
    // Buffer now empty.
    assert_eq!(ch.pending(), 0);
}

#[test]
fn message_channel_receive_filters_by_type_key() {
    let ch = InMemoryMessageChannel::new("node-A");
    ch.send("node-A", "ping", &Ping { seq: 1, note: "a".into() })
        .unwrap();
    ch.send("node-A", "other", &Ping { seq: 2, note: "b".into() })
        .unwrap();
    let pings: Vec<Ping> = ch.receive("ping").unwrap();
    assert_eq!(pings.len(), 1);
    assert_eq!(pings[0].seq, 1);
    // The "other"-typed message is untouched and still retrievable.
    let others: Vec<Ping> = ch.receive("other").unwrap();
    assert_eq!(others.len(), 1);
    assert_eq!(others[0].seq, 2);
}

#[test]
fn message_channel_buffers_before_receiver_reads() {
    // Unbounded buffering: messages sent before any receive() call are retained.
    let ch = InMemoryMessageChannel::new("node-A");
    for i in 0..5 {
        ch.send("node-A", "ping", &Ping { seq: i, note: "x".into() })
            .unwrap();
    }
    assert_eq!(ch.pending(), 5);
    let got: Vec<Ping> = ch.receive("ping").unwrap();
    assert_eq!(got.len(), 5);
}

#[test]
fn message_channel_routes_across_bus_to_peer() {
    let bus = Arc::new(InMemoryMessageBus::new());
    let a = InMemoryMessageChannel::on_bus("node-A", Arc::clone(&bus));
    let b = InMemoryMessageChannel::on_bus("node-B", Arc::clone(&bus));
    a.send("node-B", "ping", &Ping { seq: 42, note: "cross".into() })
        .unwrap();
    // A's own inbox is empty; B received it.
    let a_got: Vec<Ping> = a.receive("ping").unwrap();
    assert!(a_got.is_empty());
    let b_got: Vec<Ping> = b.receive("ping").unwrap();
    assert_eq!(b_got.len(), 1);
    assert_eq!(b_got[0].seq, 42);
}

#[test]
fn message_channel_push_subscriber_fires_synchronously() {
    let bus = Arc::new(InMemoryMessageBus::new());
    let a = InMemoryMessageChannel::on_bus("node-A", Arc::clone(&bus));
    let b = InMemoryMessageChannel::on_bus("node-B", Arc::clone(&bus));
    let hits = Arc::new(AtomicUsize::new(0));
    let hits_c = Arc::clone(&hits);
    // Subscribe before traffic.
    let _sub = b.subscribe("ping", Arc::new(move |_raw: &[u8]| {
        hits_c.fetch_add(1, Ordering::SeqCst);
    }));
    a.send("node-B", "ping", &Ping { seq: 1, note: "n".into() })
        .unwrap();
    assert_eq!(hits.load(Ordering::SeqCst), 1);
}

// ── IMeshNetwork / InMemoryMeshNetwork ──────────────────────────────────────

#[test]
fn mesh_network_reports_identity_and_peers() {
    let mesh = InMemoryMeshNetwork::with_peers("me", ["p1".into(), "p2".into(), "p1".into()]);
    assert_eq!(mesh.local_node_id(), "me");
    let peers = mesh.get_peer_ids();
    assert_eq!(peers, vec!["p1".to_string(), "p2".to_string()]); // dedup
}

#[test]
fn mesh_network_health_reflects_peer_presence() {
    let mesh = InMemoryMeshNetwork::new("me");
    let h0 = mesh.get_mesh_health();
    assert_eq!(h0.state, ConnectivityState::Offline);
    assert_eq!(h0.nearby_peer_count, 0);
    assert!(h0.available_transports.is_empty());

    mesh.add_peer("p1");
    let h1 = mesh.get_mesh_health();
    assert_eq!(h1.state, ConnectivityState::MeshOnly);
    assert_eq!(h1.nearby_peer_count, 1);
    assert_eq!(h1.preferred_transport, TransportKind::Aether);
    assert_eq!(h1.available_transports, vec![TransportKind::Aether]);
}

#[test]
fn mesh_network_add_remove_peer_semantics() {
    let mesh = InMemoryMeshNetwork::new("me");
    assert!(mesh.add_peer("p1"));
    assert!(!mesh.add_peer("p1")); // duplicate
    assert!(!mesh.add_peer("   ")); // blank
    assert!(mesh.remove_peer("p1"));
    assert!(!mesh.remove_peer("p1")); // gone
}

// ── IConnectivityMonitor / ManualConnectivityMonitor ────────────────────────

#[test]
fn connectivity_monitor_starts_offline() {
    let mon = ManualConnectivityMonitor::new();
    assert_eq!(mon.current_state(), ConnectivityState::Offline);
    assert_eq!(mon.get_snapshot().state, ConnectivityState::Offline);
}

#[test]
fn connectivity_monitor_update_changes_state_and_history() {
    let mon = ManualConnectivityMonitor::new();
    let online = NetworkContext::new(
        ConnectivityState::Online,
        TransportKind::Grpc,
        vec![TransportKind::Grpc],
        Some(-50),
        Some(10_000_000),
        Some(20),
        3,
        chrono::Utc::now(),
    );
    mon.update(online.clone());
    assert_eq!(mon.current_state(), ConnectivityState::Online);
    assert_eq!(mon.get_snapshot().nearby_peer_count, 3);
    assert_eq!(mon.history().len(), 1);
}

#[test]
fn connectivity_monitor_watch_receives_updates() {
    let mon = ManualConnectivityMonitor::new();
    let states = Arc::new(Mutex::new(Vec::<ConnectivityState>::new()));
    let states_c = Arc::clone(&states);
    // Subscribe synchronously before driving updates.
    let _sub = mon.watch(Arc::new(move |ctx: &NetworkContext| {
        states_c.lock().unwrap().push(ctx.state);
    }));
    mon.update(NetworkContext::offline()); // Offline
    let mut mesh_ctx = NetworkContext::offline();
    mesh_ctx.state = ConnectivityState::MeshOnly;
    mon.update(mesh_ctx);
    assert_eq!(
        &*states.lock().unwrap(),
        &[ConnectivityState::Offline, ConnectivityState::MeshOnly]
    );
}

#[test]
fn connectivity_monitor_watch_unsubscribes_on_drop() {
    let mon = ManualConnectivityMonitor::new();
    let hits = Arc::new(AtomicUsize::new(0));
    let hits_c = Arc::clone(&hits);
    let sub = mon.watch(Arc::new(move |_ctx: &NetworkContext| {
        hits_c.fetch_add(1, Ordering::SeqCst);
    }));
    drop(sub);
    mon.update(NetworkContext::offline());
    assert_eq!(hits.load(Ordering::SeqCst), 0);
}

// ── IPeerDiscovery / InMemoryPeerDiscovery ──────────────────────────────────

fn peer(id: &str) -> PeerInfo {
    PeerInfo::new(
        id,
        Some(format!("Device {id}")),
        vec![TransportKind::Aether, TransportKind::Bluetooth],
        PeerRole::Peer,
        Some(-60),
        chrono::Utc::now(),
    )
}

#[test]
fn peer_discovery_discover_returns_added_peers() {
    let disc = InMemoryPeerDiscovery::new();
    disc.add_peer(peer("p1"));
    disc.add_peer(peer("p2"));
    let found = disc.discover();
    assert_eq!(found.len(), 2);
    let ids: Vec<&str> = found.iter().map(|p| p.node_id.as_str()).collect();
    assert!(ids.contains(&"p1"));
    assert!(ids.contains(&"p2"));
}

#[test]
fn peer_discovery_add_replaces_same_node_id() {
    let disc = InMemoryPeerDiscovery::new();
    disc.add_peer(peer("p1"));
    let mut fresher = peer("p1");
    fresher.signal_strength_dbm = Some(-30);
    disc.add_peer(fresher);
    let found = disc.discover();
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].signal_strength_dbm, Some(-30));
}

#[test]
fn peer_discovery_watch_fires_on_add() {
    let disc = InMemoryPeerDiscovery::new();
    let seen = Arc::new(Mutex::new(Vec::<String>::new()));
    let seen_c = Arc::clone(&seen);
    let _sub = disc.watch(Arc::new(move |p: &PeerInfo| {
        seen_c.lock().unwrap().push(p.node_id.clone());
    }));
    disc.add_peer(peer("p1"));
    disc.add_peer(peer("p2"));
    assert_eq!(&*seen.lock().unwrap(), &["p1".to_string(), "p2".to_string()]);
}

#[test]
fn peer_discovery_announce_records_local_info() {
    let disc = InMemoryPeerDiscovery::new();
    disc.announce(peer("me"));
    let announced = disc.announcements();
    assert_eq!(announced.len(), 1);
    assert_eq!(announced[0].node_id, "me");
}

// ── IPayloadOptimiser / RlePayloadOptimiser ─────────────────────────────────

#[test]
fn optimiser_compresses_for_low_bandwidth_and_roundtrips() {
    let opt = RlePayloadOptimiser::new();
    // Highly compressible payload.
    let data = vec![0xAAu8; 200];
    let payload = NetworkPayload::create(
        data.clone(),
        None,
        MessagePriority::Normal,
        "application/json",
        None,
    );
    let compressed = opt.optimise(&payload, TransportKind::Bluetooth);
    // RLE of 200 identical bytes => 2 bytes (count 255-capped runs): 200 -> one run.
    assert!(compressed.data.len() < payload.data.len());
    assert_ne!(compressed.content_type, "application/json");
    // Exact reversal, including restored content type.
    let restored = opt.decompress(&compressed);
    assert_eq!(restored.data, data);
    assert_eq!(restored.content_type, "application/json");
    // Metadata marker is gone after decompress.
    assert!(restored
        .metadata
        .keys()
        .all(|k| !k.contains("rle.original_content_type")));
}

#[test]
fn optimiser_passes_through_high_bandwidth_transports() {
    let opt = RlePayloadOptimiser::new();
    let payload = NetworkPayload::of(vec![1, 2, 3, 4]);
    let out = opt.optimise(&payload, TransportKind::Grpc);
    assert_eq!(out.data, payload.data);
    assert_eq!(out.content_type, payload.content_type);
}

#[test]
fn optimiser_decompress_is_noop_for_uncompressed() {
    let opt = RlePayloadOptimiser::new();
    let payload = NetworkPayload::of(vec![9, 9, 9]);
    let out = opt.decompress(&payload);
    assert_eq!(out.data, payload.data);
    assert_eq!(out.content_type, payload.content_type);
}

#[test]
fn optimiser_roundtrips_incompressible_data() {
    let opt = RlePayloadOptimiser::new();
    let data: Vec<u8> = (0..=255u8).collect(); // no runs
    let payload = NetworkPayload::create(
        data.clone(),
        None,
        MessagePriority::Normal,
        "application/octet-stream",
        None,
    );
    let compressed = opt.optimise(&payload, TransportKind::Dtn);
    let restored = opt.decompress(&compressed);
    assert_eq!(restored.data, data); // reversible regardless of ratio
}

#[test]
fn optimiser_double_optimise_is_idempotent() {
    let opt = RlePayloadOptimiser::new();
    let payload = NetworkPayload::of(vec![7u8; 10]);
    let once = opt.optimise(&payload, TransportKind::NearLink);
    let twice = opt.optimise(&once, TransportKind::NearLink);
    // Already-compressed payload passes through unchanged.
    assert_eq!(once.data, twice.data);
    assert_eq!(once.content_type, twice.content_type);
}

// ── ISyncChannel (networking variant) / InMemorySyncChannel ─────────────────

fn delta(owner: &str, domain: &str, seq: i64, hint: Option<SchedulingHint>) -> SyncDelta {
    SyncDelta::new(
        owner,
        "device-src",
        "", // broadcast
        domain,
        vec![1, 2, 3],
        seq,
        SyncDeliveryMode::Guaranteed,
        None,
        chrono::Utc::now(),
        hint,
    )
}

#[test]
fn sync_channel_push_then_receive_drains_queue() {
    let ch = InMemorySyncChannel::new();
    ch.push_delta(&delta("owner-1", "memory.episodic", 1, None));
    ch.push_delta(&delta("owner-1", "memory.episodic", 2, None));
    assert_eq!(ch.pending("owner-1"), 2);
    let got = ch.receive_deltas("owner-1");
    assert_eq!(got.len(), 2);
    assert_eq!(got[0].sequence, 1);
    assert_eq!(got[1].sequence, 2);
    // Drained.
    assert_eq!(ch.pending("owner-1"), 0);
    assert!(ch.receive_deltas("owner-1").is_empty());
}

#[test]
fn sync_channel_tracks_monotonic_sequence_per_owner_domain() {
    let ch = InMemorySyncChannel::new();
    assert_eq!(ch.get_last_sequence("owner-1", "persona"), 0);
    ch.push_delta(&delta("owner-1", "persona", 5, None));
    assert_eq!(ch.get_last_sequence("owner-1", "persona"), 5);
    // A lower sequence does not move the high-water mark backward.
    ch.push_delta(&delta("owner-1", "persona", 3, None));
    assert_eq!(ch.get_last_sequence("owner-1", "persona"), 5);
    ch.push_delta(&delta("owner-1", "persona", 9, None));
    assert_eq!(ch.get_last_sequence("owner-1", "persona"), 9);
    // Different domain is independent.
    assert_eq!(ch.get_last_sequence("owner-1", "affect.state"), 0);
}

#[test]
fn sync_channel_isolates_owners() {
    let ch = InMemorySyncChannel::new();
    ch.push_delta(&delta("owner-1", "d", 1, None));
    ch.push_delta(&delta("owner-2", "d", 1, None));
    assert_eq!(ch.receive_deltas("owner-1").len(), 1);
    assert_eq!(ch.receive_deltas("owner-2").len(), 1);
    assert!(ch.receive_deltas("owner-3").is_empty());
}

#[test]
fn sync_channel_preserves_scheduling_hint() {
    let ch = InMemorySyncChannel::new();
    let hint = SchedulingHint::new(
        vec!["peer-A".into(), "peer-B".into()],
        None,
        0.9,
    );
    ch.push_delta(&delta("owner-1", "d", 1, Some(hint.clone())));
    let got = ch.receive_deltas("owner-1");
    assert_eq!(got.len(), 1);
    let carried = got[0].scheduling_hint.as_ref().unwrap();
    assert_eq!(carried.preferred_peer_ids, vec!["peer-A", "peer-B"]);
    assert!((carried.confidence_score - 0.9).abs() < f32::EPSILON);
    assert!(got[0].is_broadcast());
}

#[test]
fn sync_channel_log_retains_all_pushes() {
    let ch = InMemorySyncChannel::new();
    ch.push_delta(&delta("owner-1", "d", 1, None));
    ch.push_delta(&delta("owner-1", "d", 2, None));
    // Draining the queue does not clear the log.
    let _ = ch.receive_deltas("owner-1");
    assert_eq!(ch.log().len(), 2);
}

// ── SchedulingHint value type ───────────────────────────────────────────────

#[test]
fn scheduling_hint_fields_roundtrip() {
    let h = SchedulingHint::new(vec!["x".into()], None, 0.42);
    assert_eq!(h.preferred_peer_ids, vec!["x"]);
    assert!(h.suggested_window_utc.is_none());
    assert!((h.confidence_score - 0.42).abs() < f32::EPSILON);
}
