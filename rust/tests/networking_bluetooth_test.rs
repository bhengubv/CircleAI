//! networking_bluetooth_test.rs
//!
//! Ports the `CircleAI.Networking.Bluetooth` surface: `BluetoothConnectionState`,
//! `BluetoothEndpointDescriptor` / `BluetoothCapabilityProfile` /
//! `BluetoothThroughputSample`, `BluetoothCapabilityProfiles`,
//! `InMemoryBluetoothTransportRegistry`, `IBleGattAdapter` /
//! `InMemoryBleGattAdapter`, and `BluetoothNetworkTransport`.

use std::sync::Arc;

use chrono::Utc;
use circle_ai::networking::{
    INetworkTransport, NetworkPayload, TransportError, TransportKind,
};
use circle_ai::networking_transports::{
    BluetoothCapabilityProfiles, BluetoothConnectionState, BluetoothEndpointDescriptor,
    BluetoothNetworkTransport, BluetoothThroughputSample, IBleGattAdapter, InMemoryBleGattAdapter,
    InMemoryBluetoothTransportRegistry,
};

fn endpoint(id: &str, name: &str) -> BluetoothEndpointDescriptor {
    BluetoothEndpointDescriptor::new(
        id,
        name,
        "AA:BB:CC:DD:EE:FF",
        vec!["GATT".into()],
    )
}

// ── BluetoothCapabilityProfiles (static table) ──────────────────────────────

#[test]
fn capability_profiles_match_csharp_values() {
    let le5 = BluetoothCapabilityProfiles::le5();
    assert_eq!(le5.max_mtu_bytes, 247);
    assert!(le5.supports_secure_connections);
    assert!(le5.supports_high_speed);
    assert_eq!(le5.compatible_profiles, vec!["GATT", "L2CAP"]);

    let le4 = BluetoothCapabilityProfiles::le4();
    assert_eq!(le4.max_mtu_bytes, 23);
    assert!(le4.supports_secure_connections);
    assert!(!le4.supports_high_speed);
    assert_eq!(le4.compatible_profiles, vec!["GATT"]);

    let classic = BluetoothCapabilityProfiles::classic();
    assert_eq!(classic.max_mtu_bytes, 1024);
    assert!(classic.supports_secure_connections);
    assert!(!classic.supports_high_speed);
    assert_eq!(classic.compatible_profiles, vec!["SPP", "RFCOMM"]);
}

// ── InMemoryBluetoothTransportRegistry ──────────────────────────────────────

#[test]
fn registry_register_and_get_endpoint() {
    let reg = InMemoryBluetoothTransportRegistry::new();
    reg.register(endpoint("d1", "Watch"));
    assert_eq!(reg.get_endpoint("d1").unwrap().name, "Watch");
    assert!(reg.get_endpoint("nope").is_none());
}

#[test]
fn registry_all_endpoints_ordered_by_name() {
    let reg = InMemoryBluetoothTransportRegistry::new();
    reg.register(endpoint("d1", "Zebra"));
    reg.register(endpoint("d2", "Apple"));
    reg.register(endpoint("d3", "Mango"));
    let names: Vec<String> = reg.all_endpoints().iter().map(|e| e.name.clone()).collect();
    assert_eq!(names, vec!["Apple", "Mango", "Zebra"]);
}

#[test]
fn registry_state_defaults_to_disconnected() {
    let reg = InMemoryBluetoothTransportRegistry::new();
    assert_eq!(reg.state("unknown"), BluetoothConnectionState::Disconnected);
    reg.set_state("d1", BluetoothConnectionState::Connected);
    assert_eq!(reg.state("d1"), BluetoothConnectionState::Connected);
}

#[test]
fn registry_avg_kbps_read_zero_then_averaged() {
    let reg = InMemoryBluetoothTransportRegistry::new();
    assert_eq!(reg.avg_kbps_read("d1"), 0.0);
    let now = Utc::now();
    reg.record_throughput(BluetoothThroughputSample::new("d1", 100.0, 50.0, now));
    reg.record_throughput(BluetoothThroughputSample::new("d1", 300.0, 60.0, now));
    reg.record_throughput(BluetoothThroughputSample::new("d2", 999.0, 60.0, now));
    assert_eq!(reg.avg_kbps_read("d1"), 200.0);
    assert_eq!(reg.avg_kbps_read("d2"), 999.0);
}

// ── InMemoryBleGattAdapter ──────────────────────────────────────────────────

#[test]
fn adapter_write_records_and_availability_gates() {
    let adapter = InMemoryBleGattAdapter::new(true);
    adapter.write(&NetworkPayload::of(vec![1, 2])).unwrap();
    assert_eq!(adapter.written().len(), 1);

    adapter.set_available(false);
    assert_eq!(
        adapter.write(&NetworkPayload::of(vec![3])),
        Err(TransportError::NotAvailable(TransportKind::Bluetooth))
    );
    // Still only the first write recorded.
    assert_eq!(adapter.written().len(), 1);
}

// ── BluetoothNetworkTransport ───────────────────────────────────────────────

#[test]
fn transport_kind_and_availability_delegate_to_adapter() {
    let adapter = Arc::new(InMemoryBleGattAdapter::new(true));
    let t = BluetoothNetworkTransport::new(Arc::clone(&adapter) as Arc<_>);
    assert_eq!(t.kind(), TransportKind::Bluetooth);
    assert!(t.is_available());
    adapter.set_available(false);
    assert!(!t.is_available());
}

#[test]
fn transport_send_delegates_to_adapter_write() {
    let adapter = Arc::new(InMemoryBleGattAdapter::new(true));
    let t = BluetoothNetworkTransport::new(Arc::clone(&adapter) as Arc<_>);
    t.start();
    t.send(&NetworkPayload::of(vec![5, 6])).unwrap();
    assert_eq!(adapter.written().len(), 1);
    assert_eq!(adapter.written()[0].data, vec![5, 6]);
}

#[test]
fn transport_receive_loop_buffers_inbound_from_adapter() {
    let adapter = Arc::new(InMemoryBleGattAdapter::new(true));
    let t = BluetoothNetworkTransport::new(Arc::clone(&adapter) as Arc<_>);
    t.start();
    // The adapter (as if the GATT peer wrote) pushes inbound payloads.
    adapter.simulate_inbound(NetworkPayload::of(vec![1]));
    adapter.simulate_inbound(NetworkPayload::of(vec![2]));
    let drained = t.drain();
    assert_eq!(drained.len(), 2);
    assert_eq!(drained[0].data, vec![1]);
    assert!(t.drain().is_empty());
}

#[test]
fn transport_inbound_before_start_is_dropped() {
    let adapter = Arc::new(InMemoryBleGattAdapter::new(true));
    let t = BluetoothNetworkTransport::new(Arc::clone(&adapter) as Arc<_>);
    // Not started: adapter has no sink, inbound goes nowhere.
    adapter.simulate_inbound(NetworkPayload::of(vec![1]));
    assert!(t.drain().is_empty());
}

#[test]
fn transport_stop_stops_adapter_and_completes_buffer() {
    let adapter = Arc::new(InMemoryBleGattAdapter::new(true));
    let t = BluetoothNetworkTransport::new(Arc::clone(&adapter) as Arc<_>);
    t.start();
    t.stop();
    // After stop the adapter's sink is gone; inbound is dropped.
    adapter.simulate_inbound(NetworkPayload::of(vec![1]));
    assert!(t.drain().is_empty());
}

#[test]
fn transport_inbound_preserves_arrival_order() {
    // The transport's sink (handed to the adapter at start()) buffers inbound
    // payloads in arrival order; drain returns them in that order.
    let adapter = Arc::new(InMemoryBleGattAdapter::new(true));
    let t = BluetoothNetworkTransport::new(Arc::clone(&adapter) as Arc<_>);
    t.start();
    for i in 0..3u8 {
        adapter.simulate_inbound(NetworkPayload::of(vec![i]));
    }
    let drained = t.drain();
    assert_eq!(drained.len(), 3);
    let bytes: Vec<u8> = drained.iter().map(|p| p.data[0]).collect();
    assert_eq!(bytes, vec![0, 1, 2]);
}
