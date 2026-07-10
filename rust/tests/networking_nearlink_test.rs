//! networking_nearlink_test.rs
//!
//! Ports the `CircleAI.Networking.NearLink` surface: `NearLinkPairingState` /
//! `NearLinkPowerProfile`, `NearLinkDevice` / `NearLinkSession` /
//! `NearLinkThroughputSample`, `InMemoryNearLinkRegistry`, `INearLinkAdapter` /
//! `InMemoryNearLinkAdapter`, and `NearLinkTransport`.

use std::sync::Arc;

use chrono::Utc;
use circle_ai::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};
use circle_ai::networking_transports::{
    INearLinkAdapter, InMemoryNearLinkAdapter, InMemoryNearLinkRegistry, NearLinkDevice,
    NearLinkPairingState, NearLinkPowerProfile, NearLinkSession, NearLinkThroughputSample,
    NearLinkTransport,
};

fn device(id: &str, name: &str) -> NearLinkDevice {
    NearLinkDevice::new(id, name, "huawei", "1.0.0")
}

// ── InMemoryNearLinkRegistry ─────────────────────────────────────────────────

#[test]
fn registry_register_and_get_device() {
    let reg = InMemoryNearLinkRegistry::new();
    reg.register(device("d1", "Band"));
    assert_eq!(reg.get_device("d1").unwrap().friendly_name, "Band");
    assert!(reg.get_device("nope").is_none());
}

#[test]
fn registry_devices_ordered_by_friendly_name() {
    let reg = InMemoryNearLinkRegistry::new();
    reg.register(device("d1", "Zulu"));
    reg.register(device("d2", "Alpha"));
    reg.register(device("d3", "Mike"));
    let names: Vec<String> = reg.devices().iter().map(|d| d.friendly_name.clone()).collect();
    assert_eq!(names, vec!["Alpha", "Mike", "Zulu"]);
}

#[test]
fn registry_pairing_state_defaults_to_unpaired() {
    let reg = InMemoryNearLinkRegistry::new();
    assert_eq!(reg.pairing_state("x"), NearLinkPairingState::Unpaired);
    reg.set_pairing_state("d1", NearLinkPairingState::Paired);
    assert_eq!(reg.pairing_state("d1"), NearLinkPairingState::Paired);
}

#[test]
fn registry_session_open_get_close() {
    let reg = InMemoryNearLinkRegistry::new();
    let s = NearLinkSession::new("s1", "d1", NearLinkPowerProfile::Balanced, Utc::now());
    reg.open_session(s);
    assert_eq!(reg.get_session("s1").unwrap().device_id, "d1");
    assert_eq!(reg.active_sessions().len(), 1);
    reg.close_session("s1");
    assert!(reg.get_session("s1").is_none());
    assert!(reg.active_sessions().is_empty());
}

#[test]
fn registry_avg_rssi_defaults_to_minus_127() {
    let reg = InMemoryNearLinkRegistry::new();
    // No samples → -127 (the C# DefaultIfEmpty(-127)).
    assert_eq!(reg.avg_rssi("d1"), -127.0);
    let now = Utc::now();
    reg.record_throughput(NearLinkThroughputSample::new("d1", 100.0, 50.0, -40, now));
    reg.record_throughput(NearLinkThroughputSample::new("d1", 100.0, 50.0, -60, now));
    reg.record_throughput(NearLinkThroughputSample::new("d2", 100.0, 50.0, -10, now));
    assert_eq!(reg.avg_rssi("d1"), -50.0);
    assert_eq!(reg.avg_rssi("d2"), -10.0);
}

// ── InMemoryNearLinkAdapter ──────────────────────────────────────────────────

#[test]
fn adapter_send_records_and_availability_gates() {
    let adapter = InMemoryNearLinkAdapter::new(true);
    adapter.send(&NetworkPayload::of(vec![1, 2])).unwrap();
    assert_eq!(adapter.sent().len(), 1);

    adapter.set_available(false);
    assert_eq!(
        adapter.send(&NetworkPayload::of(vec![3])),
        Err(TransportError::NotAvailable(TransportKind::NearLink))
    );
    assert_eq!(adapter.sent().len(), 1);
}

// ── NearLinkTransport ────────────────────────────────────────────────────────

#[test]
fn transport_kind_and_availability_delegate_to_adapter() {
    let adapter = Arc::new(InMemoryNearLinkAdapter::new(true));
    let t = NearLinkTransport::new(Arc::clone(&adapter) as Arc<_>);
    assert_eq!(t.kind(), TransportKind::NearLink);
    assert!(t.is_available());
    adapter.set_available(false);
    assert!(!t.is_available());
}

#[test]
fn transport_send_delegates_to_adapter() {
    let adapter = Arc::new(InMemoryNearLinkAdapter::new(true));
    let t = NearLinkTransport::new(Arc::clone(&adapter) as Arc<_>);
    t.start();
    t.send(&NetworkPayload::of(vec![5, 6])).unwrap();
    assert_eq!(adapter.sent().len(), 1);
    assert_eq!(adapter.sent()[0].data, vec![5, 6]);
}

#[test]
fn transport_receive_loop_buffers_inbound() {
    let adapter = Arc::new(InMemoryNearLinkAdapter::new(true));
    let t = NearLinkTransport::new(Arc::clone(&adapter) as Arc<_>);
    t.start();
    adapter.simulate_inbound(NetworkPayload::of(vec![1]));
    adapter.simulate_inbound(NetworkPayload::of(vec![2]));
    let drained = t.drain();
    assert_eq!(drained.len(), 2);
    assert_eq!(drained[0].data, vec![1]);
    assert!(t.drain().is_empty());
}

#[test]
fn transport_inbound_before_start_is_dropped() {
    let adapter = Arc::new(InMemoryNearLinkAdapter::new(true));
    let t = NearLinkTransport::new(Arc::clone(&adapter) as Arc<_>);
    adapter.simulate_inbound(NetworkPayload::of(vec![1]));
    assert!(t.drain().is_empty());
}

#[test]
fn transport_stop_stops_adapter_and_completes_buffer() {
    let adapter = Arc::new(InMemoryNearLinkAdapter::new(true));
    let t = NearLinkTransport::new(Arc::clone(&adapter) as Arc<_>);
    t.start();
    t.stop();
    adapter.simulate_inbound(NetworkPayload::of(vec![1]));
    assert!(t.drain().is_empty());
}
