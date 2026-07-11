//! iot — CircleAI IoT-board primitives.
//!
//! Full Rust port of `src/CircleAI.IoT/IoTPrimitives.cs`:
//!
//! - Records ([`IoTDevice`], [`IoTTelemetry`], [`IoTCommand`]) + [`IIoTBoard`]
//!   with the deterministic in-memory [`InMemoryIoTBoard`] (device registry,
//!   telemetry ingest + latest/history queries, command dispatch log).
//!
//! The C# `ConcurrentDictionary` collapses to a `Mutex`-guarded `HashMap`; the
//! telemetry / command lists to `Mutex<Vec<_>>`. Missing latest values return
//! [`f64::NAN`], mirroring the C# `double.NaN`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (IoT) A registered device.
///
/// Mirrors `sealed record IoTDevice(string DeviceId, string Name, string Kind,
/// string FirmwareVersion, DateTimeOffset LastSeenUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IoTDevice {
    pub device_id: String,
    pub name: String,
    pub kind: String,
    pub firmware_version: String,
    pub last_seen_utc: DateTime<Utc>,
}

impl IoTDevice {
    /// Constructs a device, mirroring the positional C# record constructor.
    pub fn new(
        device_id: impl Into<String>,
        name: impl Into<String>,
        kind: impl Into<String>,
        firmware_version: impl Into<String>,
        last_seen_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            name: name.into(),
            kind: kind.into(),
            firmware_version: firmware_version.into(),
            last_seen_utc,
        }
    }
}

/// (IoT) A telemetry sample.
///
/// Mirrors `sealed record IoTTelemetry(string DeviceId, string Metric,
/// double Value, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct IoTTelemetry {
    pub device_id: String,
    pub metric: String,
    pub value: f64,
    pub at_utc: DateTime<Utc>,
}

impl IoTTelemetry {
    /// Constructs a telemetry sample, mirroring the positional C# record constructor.
    pub fn new(
        device_id: impl Into<String>,
        metric: impl Into<String>,
        value: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            metric: metric.into(),
            value,
            at_utc,
        }
    }
}

/// (IoT) A command sent to a device.
///
/// Mirrors `sealed record IoTCommand(string CommandId, string DeviceId,
/// string Action, string ArgumentsJson, DateTimeOffset SentUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct IoTCommand {
    pub command_id: String,
    pub device_id: String,
    pub action: String,
    pub arguments_json: String,
    pub sent_utc: DateTime<Utc>,
}

impl IoTCommand {
    /// Constructs a command, mirroring the positional C# record constructor.
    pub fn new(
        command_id: impl Into<String>,
        device_id: impl Into<String>,
        action: impl Into<String>,
        arguments_json: impl Into<String>,
        sent_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            command_id: command_id.into(),
            device_id: device_id.into(),
            action: action.into(),
            arguments_json: arguments_json.into(),
            sent_utc,
        }
    }
}

/// (IoT) The IoT board contract.
///
/// Mirrors `interface IIoTBoard`.
pub trait IIoTBoard {
    /// Registers (or overwrites) a device.
    fn register(&self, d: IoTDevice);
    /// Looks up a device by id.
    fn get_device(&self, id: &str) -> Option<IoTDevice>;
    /// All devices, ordered by name ascending.
    fn devices(&self) -> Vec<IoTDevice>;
    /// Records a telemetry sample.
    fn record_telemetry(&self, t: IoTTelemetry);
    /// The most-recent value for `(device_id, metric)`, or [`f64::NAN`] if none.
    fn latest_value(&self, device_id: &str, metric: &str) -> f64;
    /// Up to `limit` samples for `(device_id, metric)`, newest-first.
    fn history(&self, device_id: &str, metric: &str, limit: usize) -> Vec<IoTTelemetry>;
    /// Logs a command send.
    fn send_command(&self, c: IoTCommand);
    /// All commands for a device, newest-first.
    fn commands_for(&self, device_id: &str) -> Vec<IoTCommand>;
}

/// (IoT) In-memory [`IIoTBoard`].
///
/// Mirrors `sealed class InMemoryIoTBoard`.
pub struct InMemoryIoTBoard {
    devices: Mutex<HashMap<String, IoTDevice>>,
    telemetry: Mutex<Vec<IoTTelemetry>>,
    commands: Mutex<Vec<IoTCommand>>,
}

impl InMemoryIoTBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            devices: Mutex::new(HashMap::new()),
            telemetry: Mutex::new(Vec::new()),
            commands: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemoryIoTBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IIoTBoard for InMemoryIoTBoard {
    fn register(&self, d: IoTDevice) {
        self.devices.lock().unwrap().insert(d.device_id.clone(), d);
    }

    fn get_device(&self, id: &str) -> Option<IoTDevice> {
        self.devices.lock().unwrap().get(id).cloned()
    }

    fn devices(&self) -> Vec<IoTDevice> {
        let mut out: Vec<IoTDevice> = self.devices.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }

    fn record_telemetry(&self, t: IoTTelemetry) {
        self.telemetry.lock().unwrap().push(t);
    }

    fn latest_value(&self, device_id: &str, metric: &str) -> f64 {
        let telemetry = self.telemetry.lock().unwrap();
        // C# `OrderByDescending(AtUtc).FirstOrDefault()` — stable, so among
        // equal timestamps the earliest-inserted wins.
        let mut best: Option<&IoTTelemetry> = None;
        for t in telemetry
            .iter()
            .filter(|t| t.device_id == device_id && t.metric == metric)
        {
            match best {
                Some(b) if t.at_utc > b.at_utc => best = Some(t),
                None => best = Some(t),
                _ => {}
            }
        }
        best.map(|t| t.value).unwrap_or(f64::NAN)
    }

    fn history(&self, device_id: &str, metric: &str, limit: usize) -> Vec<IoTTelemetry> {
        if limit == 0 {
            panic!("limit out of range");
        }
        let telemetry = self.telemetry.lock().unwrap();
        let mut out: Vec<IoTTelemetry> = telemetry
            .iter()
            .filter(|t| t.device_id == device_id && t.metric == metric)
            .cloned()
            .collect();
        // OrderByDescending(AtUtc).Take(limit).
        out.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        out.truncate(limit);
        out
    }

    fn send_command(&self, c: IoTCommand) {
        self.commands.lock().unwrap().push(c);
    }

    fn commands_for(&self, device_id: &str) -> Vec<IoTCommand> {
        let mut out: Vec<IoTCommand> = self
            .commands
            .lock()
            .unwrap()
            .iter()
            .filter(|c| c.device_id == device_id)
            .cloned()
            .collect();
        out.sort_by(|a, b| b.sent_utc.cmp(&a.sent_utc));
        out
    }
}
