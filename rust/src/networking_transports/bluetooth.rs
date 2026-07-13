//! networking_transports::bluetooth — Rust port of `CircleAI.Networking.Bluetooth`
//! (`src/CircleAI.Networking.Bluetooth/*.cs`).
//!
//! BLE GATT binding of the [`crate::networking::INetworkTransport`] contract.
//! Faithful ports:
//!
//!   * [`BluetoothConnectionState`]        — port of the C# enum.
//!   * [`BluetoothEndpointDescriptor`] / [`BluetoothCapabilityProfile`] /
//!     [`BluetoothThroughputSample`] — the C# `record`s.
//!   * [`BluetoothCapabilityProfiles`]     — the static LE5 / LE4 / Classic
//!     profile table (port of `BluetoothCapabilityProfiles`).
//!   * [`InMemoryBluetoothTransportRegistry`] — endpoint table + per-device state
//!     + throughput log, matching the C# ordering / aggregation.
//!   * [`IBleGattAdapter`]                 — the platform BLE dependency (trait),
//!     port of the C# interface, with a working [`InMemoryBleGattAdapter`].
//!   * [`BluetoothNetworkTransport`]       — `INetworkTransport` wiring the adapter
//!     to a channel-based receive loop (port of the C# transport).

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// BluetoothConnectionState — port of the C# enum
// ─────────────────────────────────────────────────────────────────────────────

/// Lifecycle state of a BLE connection. 1:1 with the C# `BluetoothConnectionState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum BluetoothConnectionState {
    Disconnected,
    Discovering,
    Connecting,
    Connected,
    Failed,
}

// ─────────────────────────────────────────────────────────────────────────────
// Value records
// ─────────────────────────────────────────────────────────────────────────────

/// A discoverable BLE endpoint. Port of the C# `BluetoothEndpointDescriptor`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BluetoothEndpointDescriptor {
    pub device_id: String,
    pub name: String,
    pub mac_address: String,
    pub advertised_services: Vec<String>,
}

impl BluetoothEndpointDescriptor {
    pub fn new(
        device_id: impl Into<String>,
        name: impl Into<String>,
        mac_address: impl Into<String>,
        advertised_services: Vec<String>,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            name: name.into(),
            mac_address: mac_address.into(),
            advertised_services,
        }
    }
}

/// A BLE stack's capability profile. Port of the C# `BluetoothCapabilityProfile`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct BluetoothCapabilityProfile {
    pub max_mtu_bytes: i32,
    pub supports_secure_connections: bool,
    pub supports_high_speed: bool,
    pub compatible_profiles: Vec<String>,
}

impl BluetoothCapabilityProfile {
    pub fn new(
        max_mtu_bytes: i32,
        supports_secure_connections: bool,
        supports_high_speed: bool,
        compatible_profiles: Vec<String>,
    ) -> Self {
        Self {
            max_mtu_bytes,
            supports_secure_connections,
            supports_high_speed,
            compatible_profiles,
        }
    }
}

/// A read/write throughput observation for a device. Port of the C#
/// `BluetoothThroughputSample`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct BluetoothThroughputSample {
    pub device_id: String,
    pub kbps_read: f64,
    pub kbps_write: f64,
    pub at_utc: DateTime<Utc>,
}

impl BluetoothThroughputSample {
    pub fn new(
        device_id: impl Into<String>,
        kbps_read: f64,
        kbps_write: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            kbps_read,
            kbps_write,
            at_utc,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BluetoothCapabilityProfiles — port of the static profile table
// ─────────────────────────────────────────────────────────────────────────────

/// The canonical BLE capability profiles. Port of the C# static
/// `BluetoothCapabilityProfiles` (LE5 / LE4 / Classic), values byte-identical.
pub struct BluetoothCapabilityProfiles;

impl BluetoothCapabilityProfiles {
    /// Bluetooth LE 5.x: 247-byte MTU, secure connections + high speed, GATT/L2CAP.
    pub fn le5() -> BluetoothCapabilityProfile {
        BluetoothCapabilityProfile::new(
            247,
            true,
            true,
            vec!["GATT".to_string(), "L2CAP".to_string()],
        )
    }

    /// Bluetooth LE 4.x: 23-byte MTU, secure connections, no high speed, GATT.
    pub fn le4() -> BluetoothCapabilityProfile {
        BluetoothCapabilityProfile::new(23, true, false, vec!["GATT".to_string()])
    }

    /// Bluetooth Classic: 1024-byte MTU, secure connections, no high speed,
    /// SPP/RFCOMM.
    pub fn classic() -> BluetoothCapabilityProfile {
        BluetoothCapabilityProfile::new(
            1024,
            true,
            false,
            vec!["SPP".to_string(), "RFCOMM".to_string()],
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryBluetoothTransportRegistry — port of the C# registry
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory endpoint table + per-device connection state + throughput log. Port
/// of the C# `InMemoryBluetoothTransportRegistry`.
///
/// Matches the C#:
///   * [`all_endpoints`](Self::all_endpoints) ordered by `name` (ordinal).
///   * [`state`](Self::state) defaults to `Disconnected` for unknown devices.
///   * [`avg_kbps_read`](Self::avg_kbps_read) averages a device's read samples,
///     `0.0` when none.
#[derive(Default)]
pub struct InMemoryBluetoothTransportRegistry {
    endpoints: Mutex<HashMap<String, BluetoothEndpointDescriptor>>,
    states: Mutex<HashMap<String, BluetoothConnectionState>>,
    throughput: Mutex<Vec<BluetoothThroughputSample>>,
}

impl InMemoryBluetoothTransportRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) an endpoint keyed by `device_id`.
    pub fn register(&self, e: BluetoothEndpointDescriptor) {
        self.endpoints
            .lock()
            .unwrap()
            .insert(e.device_id.clone(), e);
    }

    /// The endpoint with `device_id`, if registered.
    pub fn get_endpoint(&self, device_id: &str) -> Option<BluetoothEndpointDescriptor> {
        self.endpoints.lock().unwrap().get(device_id).cloned()
    }

    /// All endpoints, ordered by `name` (ordinal). Mirrors `AllEndpoints`.
    pub fn all_endpoints(&self) -> Vec<BluetoothEndpointDescriptor> {
        let mut v: Vec<BluetoothEndpointDescriptor> =
            self.endpoints.lock().unwrap().values().cloned().collect();
        v.sort_by(|a, b| a.name.cmp(&b.name));
        v
    }

    /// Sets the connection state for `device_id`.
    pub fn set_state(&self, device_id: &str, s: BluetoothConnectionState) {
        self.states.lock().unwrap().insert(device_id.to_string(), s);
    }

    /// The connection state for `device_id`; `Disconnected` if unknown. Mirrors
    /// `State`.
    pub fn state(&self, device_id: &str) -> BluetoothConnectionState {
        self.states
            .lock()
            .unwrap()
            .get(device_id)
            .copied()
            .unwrap_or(BluetoothConnectionState::Disconnected)
    }

    /// Records a throughput sample.
    pub fn record_throughput(&self, s: BluetoothThroughputSample) {
        self.throughput.lock().unwrap().push(s);
    }

    /// Average read throughput for `device_id`; `0.0` if none. Mirrors
    /// `AvgKbpsRead`.
    pub fn avg_kbps_read(&self, device_id: &str) -> f64 {
        let guard = self.throughput.lock().unwrap();
        let vals: Vec<f64> = guard
            .iter()
            .filter(|t| t.device_id == device_id)
            .map(|t| t.kbps_read)
            .collect();
        if vals.is_empty() {
            0.0
        } else {
            vals.iter().sum::<f64>() / vals.len() as f64
        }
    }

    /// Average observed write throughput (kbps) for `device_id`; `0.0` when
    /// unsampled. Mirrors `AvgKbpsWrite`.
    pub fn avg_kbps_write(&self, device_id: &str) -> f64 {
        let guard = self.throughput.lock().unwrap();
        let vals: Vec<f64> = guard
            .iter()
            .filter(|t| t.device_id == device_id)
            .map(|t| t.kbps_write)
            .collect();
        if vals.is_empty() {
            0.0
        } else {
            vals.iter().sum::<f64>() / vals.len() as f64
        }
    }

    /// Drops a device from the registry: removes its endpoint descriptor and any
    /// tracked connection state. Returns `true` if an endpoint was actually
    /// removed. Mirrors `Unregister`.
    pub fn unregister(&self, device_id: &str) -> bool {
        if device_id.is_empty() {
            return false;
        }
        let removed = self.endpoints.lock().unwrap().remove(device_id).is_some();
        self.states.lock().unwrap().remove(device_id);
        removed
    }

    /// Endpoints advertising a given GATT/SPP service, matched case-insensitively
    /// and ordered by device name (ordinal) — the discovery view a service scanner
    /// needs. Mirrors `EndpointsWithService`.
    pub fn endpoints_with_service(&self, service: &str) -> Vec<BluetoothEndpointDescriptor> {
        if service.is_empty() {
            return Vec::new();
        }
        let mut v: Vec<BluetoothEndpointDescriptor> = self
            .endpoints
            .lock()
            .unwrap()
            .values()
            .filter(|e| {
                e.advertised_services
                    .iter()
                    .any(|s| s.eq_ignore_ascii_case(service))
            })
            .cloned()
            .collect();
        v.sort_by(|a, b| a.name.cmp(&b.name));
        v
    }

    /// Number of devices currently in the [`BluetoothConnectionState::Connected`]
    /// state. Mirrors `ConnectedCount`.
    pub fn connected_count(&self) -> usize {
        self.states
            .lock()
            .unwrap()
            .values()
            .filter(|s| **s == BluetoothConnectionState::Connected)
            .count()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IBleGattAdapter — port of the C# platform interface
// ─────────────────────────────────────────────────────────────────────────────

/// Platform-specific BLE GATT operations. Port of the C# `IBleGattAdapter`
/// (`Windows.Devices.Bluetooth` / `CoreBluetooth` / `BluetoothGatt` / BlueZ
/// implement it per platform).
///
/// The C# `StartAsync(ChannelWriter<NetworkPayload>, ct)` hands the adapter a
/// sink to push inbound payloads into. Here the sink is the injected
/// [`InboundSink`] closure — the adapter calls it for each received payload.
pub trait IBleGattAdapter: Send + Sync {
    /// Whether BLE is powered and permission-granted.
    fn is_available(&self) -> bool;

    /// Begin the receive loop, pushing inbound payloads into `inbound`.
    fn start(&self, inbound: InboundSink);

    /// Stop the receive loop.
    fn stop(&self);

    /// Write `payload` to the connected GATT characteristic.
    fn write(&self, payload: &NetworkPayload) -> Result<(), TransportError>;
}

/// The sink an [`IBleGattAdapter`] pushes inbound payloads into (the analogue of
/// the C# `ChannelWriter<NetworkPayload>`).
pub type InboundSink = Arc<dyn Fn(NetworkPayload) + Send + Sync>;

/// A working, deterministic in-memory [`IBleGattAdapter`]. `write` records the
/// payload; [`InMemoryBleGattAdapter::simulate_inbound`] injects a payload as if
/// received (delivered to the sink while started). Availability is settable.
pub struct InMemoryBleGattAdapter {
    available: AtomicBool,
    started: AtomicBool,
    written: Mutex<Vec<NetworkPayload>>,
    sink: Mutex<Option<InboundSink>>,
}

impl Default for InMemoryBleGattAdapter {
    fn default() -> Self {
        Self::new(true)
    }
}

impl InMemoryBleGattAdapter {
    /// A new adapter with the given initial availability, not started.
    pub fn new(available: bool) -> Self {
        Self {
            available: AtomicBool::new(available),
            started: AtomicBool::new(false),
            written: Mutex::new(Vec::new()),
            sink: Mutex::new(None),
        }
    }

    /// Sets availability (e.g. Bluetooth toggled off).
    pub fn set_available(&self, value: bool) {
        self.available.store(value, Ordering::SeqCst);
    }

    /// Every payload written via [`IBleGattAdapter::write`], in order.
    pub fn written(&self) -> Vec<NetworkPayload> {
        self.written.lock().unwrap().clone()
    }

    /// Injects `payload` as if the GATT peer sent it: forwarded to the inbound
    /// sink when started. No-op when stopped (no live receive loop).
    pub fn simulate_inbound(&self, payload: NetworkPayload) {
        if !self.started.load(Ordering::SeqCst) {
            return;
        }
        // Snapshot the sink under the lock, release, then fire outside it.
        let sink = self.sink.lock().unwrap().clone();
        if let Some(sink) = sink {
            sink(payload);
        }
    }
}

impl IBleGattAdapter for InMemoryBleGattAdapter {
    fn is_available(&self) -> bool {
        self.available.load(Ordering::SeqCst)
    }

    fn start(&self, inbound: InboundSink) {
        *self.sink.lock().unwrap() = Some(inbound);
        self.started.store(true, Ordering::SeqCst);
    }

    fn stop(&self) {
        self.started.store(false, Ordering::SeqCst);
        *self.sink.lock().unwrap() = None;
    }

    fn write(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        if !self.is_available() {
            return Err(TransportError::NotAvailable(TransportKind::Bluetooth));
        }
        self.written.lock().unwrap().push(payload.clone());
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BluetoothNetworkTransport — port of BluetoothNetworkTransport.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`INetworkTransport`] over BLE GATT. Port of the C# `BluetoothNetworkTransport`.
///
/// Wires the injected [`IBleGattAdapter`] to a channel-based receive loop: `start`
/// hands the adapter an inbound sink that buffers payloads for [`drain`]; `send`
/// delegates to the adapter's write; `stop` stops the adapter and completes the
/// inbound buffer.
pub struct BluetoothNetworkTransport {
    adapter: Arc<dyn IBleGattAdapter>,
    /// Unbounded inbound buffer — the analogue of the C#
    /// `Channel.CreateUnbounded<NetworkPayload>()`.
    inbound: Arc<Mutex<VecDeque<NetworkPayload>>>,
    completed: Arc<AtomicBool>,
}

impl BluetoothNetworkTransport {
    /// Builds a transport over the given BLE adapter.
    pub fn new(adapter: Arc<dyn IBleGattAdapter>) -> Self {
        Self {
            adapter,
            inbound: Arc::new(Mutex::new(VecDeque::new())),
            completed: Arc::new(AtomicBool::new(false)),
        }
    }

    /// Drains every buffered inbound payload in arrival order. Pull side of the C#
    /// `ReceiveAsync` enumerable.
    pub fn drain(&self) -> Vec<NetworkPayload> {
        self.inbound.lock().unwrap().drain(..).collect()
    }
}

impl INetworkTransport for BluetoothNetworkTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::Bluetooth
    }

    fn is_available(&self) -> bool {
        self.adapter.is_available()
    }

    fn start(&self) {
        self.completed.store(false, Ordering::SeqCst);
        // Hand the adapter an inbound sink that respects the completed flag and
        // buffers into the unbounded inbox.
        let inbox = Arc::clone(&self.inbound);
        let completed = Arc::clone(&self.completed);
        let sink: InboundSink = Arc::new(move |payload: NetworkPayload| {
            if completed.load(Ordering::SeqCst) {
                return;
            }
            inbox.lock().unwrap().push_back(payload);
        });
        self.adapter.start(sink);
    }

    fn stop(&self) {
        self.adapter.stop();
        self.completed.store(true, Ordering::SeqCst);
    }

    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        self.adapter.write(payload)
    }
}
