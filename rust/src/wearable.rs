//! wearable — CircleAI wearable-board primitives.
//!
//! Full Rust port of `src/CircleAI.Wearable/WearablePrimitives.cs`:
//!
//! - [`WearableKind`] / [`WearableTelemetryKind`] enums, records
//!   [`WearableDevice`] / [`WearableSample`], the [`IWearableBoard`] contract,
//!   and the deterministic in-memory [`InMemoryWearableBoard`] (devices +
//!   telemetry samples + latest/average lookups).
//!
//! Sync-only; `DateTimeOffset` → [`chrono::DateTime<Utc>`]. `AverageValue`
//! returns `f64::NAN` on an empty window, matching the C# `double.NaN`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Wearable) Physical form-factor of a wearable device.
///
/// Mirrors `enum WearableKind { Smartwatch, FitnessBand, ChestStrap, Patch,
/// Headset }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum WearableKind {
    Smartwatch,
    FitnessBand,
    ChestStrap,
    Patch,
    Headset,
}

/// (Wearable) Kind of telemetry a wearable reports.
///
/// Mirrors `enum WearableTelemetryKind { HeartRate, Steps, Calories, SleepStage,
/// SkinTempC, Stress, OxygenPct }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum WearableTelemetryKind {
    HeartRate,
    Steps,
    Calories,
    SleepStage,
    SkinTempC,
    Stress,
    OxygenPct,
}

/// (Wearable) A wearable device descriptor.
///
/// Mirrors `sealed record WearableDevice(string DeviceId, WearableKind Kind,
/// string Vendor, string FirmwareVersion, double BatteryPct)`.
#[derive(Debug, Clone, PartialEq)]
pub struct WearableDevice {
    pub device_id: String,
    pub kind: WearableKind,
    pub vendor: String,
    pub firmware_version: String,
    pub battery_pct: f64,
}

impl WearableDevice {
    /// Constructs a device, mirroring the positional C# record constructor.
    pub fn new(
        device_id: impl Into<String>,
        kind: WearableKind,
        vendor: impl Into<String>,
        firmware_version: impl Into<String>,
        battery_pct: f64,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            kind,
            vendor: vendor.into(),
            firmware_version: firmware_version.into(),
            battery_pct,
        }
    }
}

/// (Wearable) A telemetry sample.
///
/// Mirrors `sealed record WearableSample(string DeviceId,
/// WearableTelemetryKind Kind, double Value, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct WearableSample {
    pub device_id: String,
    pub kind: WearableTelemetryKind,
    pub value: f64,
    pub at_utc: DateTime<Utc>,
}

impl WearableSample {
    /// Constructs a sample, mirroring the positional C# record constructor.
    pub fn new(device_id: impl Into<String>, kind: WearableTelemetryKind, value: f64, at_utc: DateTime<Utc>) -> Self {
        Self {
            device_id: device_id.into(),
            kind,
            value,
            at_utc,
        }
    }
}

/// (Wearable) The wearable-board contract.
///
/// Mirrors `interface IWearableBoard`.
pub trait IWearableBoard {
    /// Adds (or overwrites) a device.
    fn add(&self, d: WearableDevice);
    /// A device by id, if any.
    fn get_device(&self, id: &str) -> Option<WearableDevice>;
    /// All devices, by vendor (mirrors the C# `Devices` property).
    fn devices(&self) -> Vec<WearableDevice>;
    /// Records a sample. Panics on an unknown device id (mirrors the C#
    /// `InvalidOperationException`).
    fn record(&self, s: WearableSample);
    /// A device's samples of `kind` since `since`, earliest first.
    fn read_since(&self, device_id: &str, kind: WearableTelemetryKind, since: DateTime<Utc>) -> Vec<WearableSample>;
    /// The latest value of `kind` for a device, if any.
    fn latest_value(&self, device_id: &str, kind: WearableTelemetryKind) -> Option<f64>;
    /// The mean value of `kind` since `since`; `f64::NAN` when there are none.
    fn average_value(&self, device_id: &str, kind: WearableTelemetryKind, since: DateTime<Utc>) -> f64;
}

/// (Wearable) In-memory [`IWearableBoard`].
///
/// Mirrors `sealed class InMemoryWearableBoard`.
pub struct InMemoryWearableBoard {
    devices: Mutex<HashMap<String, WearableDevice>>,
    samples: Mutex<Vec<WearableSample>>,
}

impl InMemoryWearableBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            devices: Mutex::new(HashMap::new()),
            samples: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryWearableBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IWearableBoard for InMemoryWearableBoard {
    fn add(&self, d: WearableDevice) {
        self.devices.lock().unwrap().insert(d.device_id.clone(), d);
    }

    fn get_device(&self, id: &str) -> Option<WearableDevice> {
        self.devices.lock().unwrap().get(id).cloned()
    }

    fn devices(&self) -> Vec<WearableDevice> {
        let mut hits: Vec<WearableDevice> = self.devices.lock().unwrap().values().cloned().collect();
        hits.sort_by(|a, b| a.vendor.cmp(&b.vendor));
        hits
    }

    fn record(&self, s: WearableSample) {
        if !self.devices.lock().unwrap().contains_key(&s.device_id) {
            panic!("Unknown device {}", s.device_id);
        }
        self.samples.lock().unwrap().push(s);
    }

    fn read_since(&self, device_id: &str, kind: WearableTelemetryKind, since: DateTime<Utc>) -> Vec<WearableSample> {
        let mut hits: Vec<WearableSample> = self
            .samples
            .lock()
            .unwrap()
            .iter()
            .filter(|s| s.device_id == device_id && s.kind == kind && s.at_utc >= since)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        hits
    }

    fn latest_value(&self, device_id: &str, kind: WearableTelemetryKind) -> Option<f64> {
        let samples = self.samples.lock().unwrap();
        let mut best: Option<&WearableSample> = None;
        for s in samples.iter().filter(|s| s.device_id == device_id && s.kind == kind) {
            match best {
                Some(b) if s.at_utc > b.at_utc => best = Some(s),
                None => best = Some(s),
                _ => {}
            }
        }
        best.map(|s| s.value)
    }

    fn average_value(&self, device_id: &str, kind: WearableTelemetryKind, since: DateTime<Utc>) -> f64 {
        let items = self.read_since(device_id, kind, since);
        if items.is_empty() {
            f64::NAN
        } else {
            items.iter().map(|s| s.value).sum::<f64>() / items.len() as f64
        }
    }
}
