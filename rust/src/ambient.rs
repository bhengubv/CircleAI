//! ambient — CircleAI ambient-board primitives.
//!
//! Full Rust port of `src/CircleAI.Ambient/AmbientPrimitives.cs`:
//!
//! - Records [`AmbientReading`] / [`AmbientPreference`], the [`IAmbientBoard`]
//!   contract, and the deterministic in-memory [`InMemoryAmbientBoard`] (ambient
//!   readings + per-location preferences + comfort check).
//!
//! Sync-only; `DateTimeOffset` → [`chrono::DateTime<Utc>`]. Comfort tolerances
//! match the source exactly: temperature within ±2 °C, humidity within ±10 %,
//! noise at/below the preference ceiling.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// Default `limit` for [`IAmbientBoard::history`] (C# `limit = 50`).
pub const DEFAULT_HISTORY_LIMIT: i32 = 50;

/// (Ambient) An ambient sensor reading.
///
/// Mirrors `sealed record AmbientReading(string DeviceId, double TemperatureC,
/// double Humidity, double LuxLight, double DbNoise, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct AmbientReading {
    pub device_id: String,
    pub temperature_c: f64,
    pub humidity: f64,
    pub lux_light: f64,
    pub db_noise: f64,
    pub at_utc: DateTime<Utc>,
}

impl AmbientReading {
    /// Constructs a reading, mirroring the positional C# record constructor.
    pub fn new(
        device_id: impl Into<String>,
        temperature_c: f64,
        humidity: f64,
        lux_light: f64,
        db_noise: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            device_id: device_id.into(),
            temperature_c,
            humidity,
            lux_light,
            db_noise,
            at_utc,
        }
    }
}

/// (Ambient) A per-location comfort preference.
///
/// Mirrors `sealed record AmbientPreference(string Location, double TargetTempC,
/// double TargetHumidity, double MaxNoiseDb)`.
#[derive(Debug, Clone, PartialEq)]
pub struct AmbientPreference {
    pub location: String,
    pub target_temp_c: f64,
    pub target_humidity: f64,
    pub max_noise_db: f64,
}

impl AmbientPreference {
    /// Constructs a preference, mirroring the positional C# record constructor.
    pub fn new(location: impl Into<String>, target_temp_c: f64, target_humidity: f64, max_noise_db: f64) -> Self {
        Self {
            location: location.into(),
            target_temp_c,
            target_humidity,
            max_noise_db,
        }
    }
}

/// (Ambient) The ambient-board contract.
///
/// Mirrors `interface IAmbientBoard`.
pub trait IAmbientBoard {
    /// Records a reading.
    fn record(&self, r: AmbientReading);
    /// The latest reading for a device, if any.
    fn latest(&self, device_id: &str) -> Option<AmbientReading>;
    /// A device's reading history, newest first (default [`DEFAULT_HISTORY_LIMIT`]).
    fn history(&self, device_id: &str, limit: i32) -> Vec<AmbientReading>;
    /// Sets (or overwrites) a location preference.
    fn set_preference(&self, p: AmbientPreference);
    /// A location's preference, if any.
    fn get_preference(&self, location: &str) -> Option<AmbientPreference>;
    /// `true` when the device's latest reading is within the location's comfort
    /// tolerances. `false` when either the preference or a reading is missing.
    fn is_comfortable(&self, device_id: &str, location: &str) -> bool;
}

/// (Ambient) In-memory [`IAmbientBoard`].
///
/// Mirrors `sealed class InMemoryAmbientBoard`.
pub struct InMemoryAmbientBoard {
    readings: Mutex<Vec<AmbientReading>>,
    prefs: Mutex<HashMap<String, AmbientPreference>>,
}

impl InMemoryAmbientBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            readings: Mutex::new(Vec::new()),
            prefs: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryAmbientBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IAmbientBoard for InMemoryAmbientBoard {
    fn record(&self, r: AmbientReading) {
        self.readings.lock().unwrap().push(r);
    }

    fn latest(&self, device_id: &str) -> Option<AmbientReading> {
        let readings = self.readings.lock().unwrap();
        let mut best: Option<&AmbientReading> = None;
        for r in readings.iter().filter(|r| r.device_id == device_id) {
            match best {
                Some(b) if r.at_utc > b.at_utc => best = Some(r),
                None => best = Some(r),
                _ => {}
            }
        }
        best.cloned()
    }

    fn history(&self, device_id: &str, limit: i32) -> Vec<AmbientReading> {
        let mut hits: Vec<AmbientReading> = self
            .readings
            .lock()
            .unwrap()
            .iter()
            .filter(|r| r.device_id == device_id)
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        if limit >= 0 {
            hits.truncate(limit as usize);
        }
        hits
    }

    fn set_preference(&self, p: AmbientPreference) {
        self.prefs.lock().unwrap().insert(p.location.clone(), p);
    }

    fn get_preference(&self, location: &str) -> Option<AmbientPreference> {
        self.prefs.lock().unwrap().get(location).cloned()
    }

    fn is_comfortable(&self, device_id: &str, location: &str) -> bool {
        let pref = match self.get_preference(location) {
            Some(p) => p,
            None => return false,
        };
        let last = match self.latest(device_id) {
            Some(r) => r,
            None => return false,
        };
        (last.temperature_c - pref.target_temp_c).abs() <= 2.0
            && (last.humidity - pref.target_humidity).abs() <= 10.0
            && last.db_noise <= pref.max_noise_db
    }
}
