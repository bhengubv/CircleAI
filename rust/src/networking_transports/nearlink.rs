//! networking_transports::nearlink — Rust port of `CircleAI.Networking.NearLink`
//! (`src/CircleAI.Networking.NearLink/*.cs`).
//!
//! Huawei SLE / NearLink binding of the [`crate::networking::INetworkTransport`]
//! contract. Faithful ports:
//!
//!   * [`NearLinkPairingState`] / [`NearLinkPowerProfile`] — port of the C# enums.
//!   * [`NearLinkDevice`] / [`NearLinkSession`] / [`NearLinkThroughputSample`] —
//!     the C# `record`s.
//!   * [`InMemoryNearLinkRegistry`]        — device table + pairing state + session
//!     table + throughput log, matching the C# ordering / aggregation
//!     (`Devices` ordered by friendly name; `AvgRssi` defaults to `-127`).
//!   * [`INearLinkAdapter`]                — the platform NearLink dependency
//!     (trait), port of the C# interface, with a working [`InMemoryNearLinkAdapter`].
//!   * [`NearLinkTransport`]               — `INetworkTransport` delegating start /
//!     stop / send to the adapter and buffering inbound payloads (port of the C#
//!     transport).
//!
//! `ReadOnlyMemory<byte>` → `Vec<u8>`; `DateTimeOffset` → `chrono::DateTime<Utc>`.

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// Enums — port of the C# enums
// ─────────────────────────────────────────────────────────────────────────────

/// Pairing lifecycle of a NearLink device. 1:1 with the C# `NearLinkPairingState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum NearLinkPairingState {
    Unpaired,
    Pairing,
    Paired,
    PairingFailed,
}

/// Power/throughput profile of a NearLink link. 1:1 with the C#
/// `NearLinkPowerProfile`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum NearLinkPowerProfile {
    LowEnergy,
    Balanced,
    HighThroughput,
}

// ─────────────────────────────────────────────────────────────────────────────
// Value records
// ─────────────────────────────────────────────────────────────────────────────

/// A discoverable NearLink device. Port of the C# `NearLinkDevice`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NearLinkDevice {
    pub device_id: String,
    pub friendly_name: String,
    pub manufacturer_id: String,
    pub firmware_version: String,
}

impl NearLinkDevice {
    pub fn new(
        device_id: impl Into<String>,
        friendly_name: impl Into<String>,
        manufacturer_id: impl Into<String>,
        firmware_version: impl Into<String>,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            friendly_name: friendly_name.into(),
            manufacturer_id: manufacturer_id.into(),
            firmware_version: firmware_version.into(),
        }
    }
}

/// An active NearLink session. Port of the C# `NearLinkSession`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NearLinkSession {
    pub session_id: String,
    pub device_id: String,
    pub power_profile: NearLinkPowerProfile,
    pub started_utc: DateTime<Utc>,
}

impl NearLinkSession {
    pub fn new(
        session_id: impl Into<String>,
        device_id: impl Into<String>,
        power_profile: NearLinkPowerProfile,
        started_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            session_id: session_id.into(),
            device_id: device_id.into(),
            power_profile,
            started_utc,
        }
    }
}

/// A read/write throughput + RSSI observation. Port of the C#
/// `NearLinkThroughputSample`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct NearLinkThroughputSample {
    pub device_id: String,
    pub kbps_read: f64,
    pub kbps_write: f64,
    pub rssi_dbm: i32,
    pub at_utc: DateTime<Utc>,
}

impl NearLinkThroughputSample {
    pub fn new(
        device_id: impl Into<String>,
        kbps_read: f64,
        kbps_write: f64,
        rssi_dbm: i32,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            kbps_read,
            kbps_write,
            rssi_dbm,
            at_utc,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryNearLinkRegistry — port of the C# registry
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory device table + pairing state + session table + throughput log. Port
/// of the C# `InMemoryNearLinkRegistry`.
///
/// Matches the C#:
///   * [`devices`](Self::devices) ordered by `friendly_name` (ordinal).
///   * [`pairing_state`](Self::pairing_state) defaults to `Unpaired`.
///   * [`avg_rssi`](Self::avg_rssi) averages a device's samples, defaulting to
///     `-127` when none (the C# `DefaultIfEmpty(-127).Average()`).
#[derive(Default)]
pub struct InMemoryNearLinkRegistry {
    devices: Mutex<HashMap<String, NearLinkDevice>>,
    states: Mutex<HashMap<String, NearLinkPairingState>>,
    sessions: Mutex<HashMap<String, NearLinkSession>>,
    throughput: Mutex<Vec<NearLinkThroughputSample>>,
}

impl InMemoryNearLinkRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) a device keyed by `device_id`. Port of `Register`.
    pub fn register(&self, d: NearLinkDevice) {
        self.devices.lock().unwrap().insert(d.device_id.clone(), d);
    }

    /// The device with `id`, if registered. Port of `GetDevice`.
    pub fn get_device(&self, id: &str) -> Option<NearLinkDevice> {
        self.devices.lock().unwrap().get(id).cloned()
    }

    /// All devices, ordered by `friendly_name` (ordinal). Mirrors `Devices`.
    pub fn devices(&self) -> Vec<NearLinkDevice> {
        let mut v: Vec<NearLinkDevice> = self.devices.lock().unwrap().values().cloned().collect();
        v.sort_by(|a, b| a.friendly_name.cmp(&b.friendly_name));
        v
    }

    /// Sets the pairing state for `device_id`. Port of `SetPairingState`.
    pub fn set_pairing_state(&self, device_id: &str, s: NearLinkPairingState) {
        self.states.lock().unwrap().insert(device_id.to_string(), s);
    }

    /// The pairing state for `device_id`; `Unpaired` if unknown. Mirrors
    /// `PairingState`.
    pub fn pairing_state(&self, device_id: &str) -> NearLinkPairingState {
        self.states
            .lock()
            .unwrap()
            .get(device_id)
            .copied()
            .unwrap_or(NearLinkPairingState::Unpaired)
    }

    /// Opens (records) a session keyed by `session_id`. Port of `OpenSession`.
    pub fn open_session(&self, s: NearLinkSession) {
        self.sessions
            .lock()
            .unwrap()
            .insert(s.session_id.clone(), s);
    }

    /// The session with `id`, if open. Port of `GetSession`.
    pub fn get_session(&self, id: &str) -> Option<NearLinkSession> {
        self.sessions.lock().unwrap().get(id).cloned()
    }

    /// Closes (removes) a session by id. Port of `CloseSession`.
    pub fn close_session(&self, id: &str) {
        self.sessions.lock().unwrap().remove(id);
    }

    /// The active sessions (unordered, like the C# `_sessions.Values.ToArray()`).
    /// Mirrors `ActiveSessions`.
    pub fn active_sessions(&self) -> Vec<NearLinkSession> {
        self.sessions.lock().unwrap().values().cloned().collect()
    }

    /// Records a throughput sample. Port of `RecordThroughput`.
    pub fn record_throughput(&self, s: NearLinkThroughputSample) {
        self.throughput.lock().unwrap().push(s);
    }

    /// Average RSSI (dBm) for `device_id`; `-127` if none. Mirrors `AvgRssi`.
    pub fn avg_rssi(&self, device_id: &str) -> f64 {
        let guard = self.throughput.lock().unwrap();
        let vals: Vec<f64> = guard
            .iter()
            .filter(|t| t.device_id == device_id)
            .map(|t| t.rssi_dbm as f64)
            .collect();
        if vals.is_empty() {
            -127.0
        } else {
            vals.iter().sum::<f64>() / vals.len() as f64
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// INearLinkAdapter — port of the C# platform interface
// ─────────────────────────────────────────────────────────────────────────────

/// Platform-level NearLink / SLE operations. Port of the C# `INearLinkAdapter`
/// (implemented via the Huawei DevEco NearLink SDK on HarmonyOS, or the NearLink
/// HAL on compatible Android devices).
///
/// The C# `StartAsync(ChannelWriter<NetworkPayload>, ct)` hands the adapter a sink
/// to push inbound payloads into. Here the sink is the injected [`NearLinkInboundSink`]
/// closure — the adapter calls it for each received payload.
pub trait INearLinkAdapter: Send + Sync {
    /// Whether NearLink is powered and permission-granted.
    fn is_available(&self) -> bool;

    /// Begin the receive loop, pushing inbound payloads into `inbound`.
    fn start(&self, inbound: NearLinkInboundSink);

    /// Stop the receive loop.
    fn stop(&self);

    /// Send `payload` over the NearLink link.
    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError>;
}

/// The sink an [`INearLinkAdapter`] pushes inbound payloads into (the analogue of
/// the C# `ChannelWriter<NetworkPayload>`).
pub type NearLinkInboundSink = Arc<dyn Fn(NetworkPayload) + Send + Sync>;

/// A working, deterministic in-memory [`INearLinkAdapter`]. `send` records the
/// payload; [`InMemoryNearLinkAdapter::simulate_inbound`] injects a payload as if
/// received (delivered to the sink while started). Availability is settable.
pub struct InMemoryNearLinkAdapter {
    available: AtomicBool,
    started: AtomicBool,
    sent: Mutex<Vec<NetworkPayload>>,
    sink: Mutex<Option<NearLinkInboundSink>>,
}

impl Default for InMemoryNearLinkAdapter {
    fn default() -> Self {
        Self::new(true)
    }
}

impl InMemoryNearLinkAdapter {
    /// A new adapter with the given initial availability, not started.
    pub fn new(available: bool) -> Self {
        Self {
            available: AtomicBool::new(available),
            started: AtomicBool::new(false),
            sent: Mutex::new(Vec::new()),
            sink: Mutex::new(None),
        }
    }

    /// Sets availability (e.g. NearLink toggled off).
    pub fn set_available(&self, value: bool) {
        self.available.store(value, Ordering::SeqCst);
    }

    /// Every payload sent via [`INearLinkAdapter::send`], in order.
    pub fn sent(&self) -> Vec<NetworkPayload> {
        self.sent.lock().unwrap().clone()
    }

    /// Injects `payload` as if the NearLink peer sent it: forwarded to the inbound
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

impl INearLinkAdapter for InMemoryNearLinkAdapter {
    fn is_available(&self) -> bool {
        self.available.load(Ordering::SeqCst)
    }

    fn start(&self, inbound: NearLinkInboundSink) {
        *self.sink.lock().unwrap() = Some(inbound);
        self.started.store(true, Ordering::SeqCst);
    }

    fn stop(&self) {
        self.started.store(false, Ordering::SeqCst);
        *self.sink.lock().unwrap() = None;
    }

    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        if !self.is_available() {
            return Err(TransportError::NotAvailable(TransportKind::NearLink));
        }
        self.sent.lock().unwrap().push(payload.clone());
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// NearLinkTransport — port of NearLinkTransport.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`INetworkTransport`] for Huawei SLE / NearLink. Port of the C#
/// `NearLinkTransport`.
///
/// Delegates `is_available` / `send` to the injected [`INearLinkAdapter`]; `start`
/// hands the adapter an inbound sink that buffers payloads into an unbounded inbox
/// for [`drain`]; `stop` stops the adapter and completes the inbound buffer (the
/// C# `Channel.CreateUnbounded` + `_inbound.Writer.TryComplete()`).
pub struct NearLinkTransport {
    adapter: Arc<dyn INearLinkAdapter>,
    /// Unbounded inbound buffer — the analogue of the C#
    /// `Channel.CreateUnbounded<NetworkPayload>()`.
    inbound: Arc<Mutex<VecDeque<NetworkPayload>>>,
    completed: Arc<AtomicBool>,
}

impl NearLinkTransport {
    /// Builds a transport over the given NearLink adapter.
    pub fn new(adapter: Arc<dyn INearLinkAdapter>) -> Self {
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

impl INetworkTransport for NearLinkTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::NearLink
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
        let sink: NearLinkInboundSink = Arc::new(move |payload: NetworkPayload| {
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
        self.adapter.send(payload)
    }
}
