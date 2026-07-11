//! energy — CircleAI energy-board primitives.
//!
//! Full Rust port of `src/CircleAI.Energy/EnergyPrimitives.cs`:
//!
//! - Records [`MeterReading`] / [`EnergyTariff`] / [`Outage`], the
//!   [`IEnergyBoard`] contract, and the deterministic in-memory
//!   [`InMemoryEnergyBoard`] (meter readings + consumption delta + tariffs +
//!   cost estimate + outage log).
//!
//! Sync-only; `DateTimeOffset`/`DateTimeOffset?` → [`chrono::DateTime<Utc>`] /
//! `Option<DateTime<Utc>>`; `decimal EstimateCost` result → `f64`.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// (Energy) A meter reading.
///
/// Mirrors `sealed record MeterReading(string MeterId, double Kwh,
/// DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct MeterReading {
    pub meter_id: String,
    pub kwh: f64,
    pub at_utc: DateTime<Utc>,
}

impl MeterReading {
    /// Constructs a reading, mirroring the positional C# record constructor.
    pub fn new(meter_id: impl Into<String>, kwh: f64, at_utc: DateTime<Utc>) -> Self {
        Self {
            meter_id: meter_id.into(),
            kwh,
            at_utc,
        }
    }
}

/// (Energy) A tariff.
///
/// Mirrors `sealed record EnergyTariff(string TariffId, string Name,
/// double PeakKwhRate, double OffPeakKwhRate, string Currency)`.
#[derive(Debug, Clone, PartialEq)]
pub struct EnergyTariff {
    pub tariff_id: String,
    pub name: String,
    pub peak_kwh_rate: f64,
    pub off_peak_kwh_rate: f64,
    pub currency: String,
}

impl EnergyTariff {
    /// Constructs a tariff, mirroring the positional C# record constructor.
    pub fn new(
        tariff_id: impl Into<String>,
        name: impl Into<String>,
        peak_kwh_rate: f64,
        off_peak_kwh_rate: f64,
        currency: impl Into<String>,
    ) -> Self {
        Self {
            tariff_id: tariff_id.into(),
            name: name.into(),
            peak_kwh_rate,
            off_peak_kwh_rate,
            currency: currency.into(),
        }
    }
}

/// (Energy) An outage record.
///
/// Mirrors `sealed record Outage(string OutageId, string Area,
/// DateTimeOffset StartUtc, DateTimeOffset? EndUtc, string? Reason)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Outage {
    pub outage_id: String,
    pub area: String,
    pub start_utc: DateTime<Utc>,
    pub end_utc: Option<DateTime<Utc>>,
    pub reason: Option<String>,
}

impl Outage {
    /// Constructs an outage, mirroring the positional C# record constructor.
    pub fn new(
        outage_id: impl Into<String>,
        area: impl Into<String>,
        start_utc: DateTime<Utc>,
        end_utc: Option<DateTime<Utc>>,
        reason: Option<String>,
    ) -> Self {
        Self {
            outage_id: outage_id.into(),
            area: area.into(),
            start_utc,
            end_utc,
            reason,
        }
    }
}

/// (Energy) The energy-board contract.
///
/// Mirrors `interface IEnergyBoard`.
pub trait IEnergyBoard {
    /// Records a meter reading.
    fn record(&self, r: MeterReading);
    /// A meter's readings since `since`, earliest first.
    fn readings_for(&self, meter_id: &str, since: DateTime<Utc>) -> Vec<MeterReading>;
    /// Consumption delta (last kWh minus first) over readings since `since`;
    /// `0.0` with fewer than two readings.
    fn total_kwh_since(&self, meter_id: &str, since: DateTime<Utc>) -> f64;
    /// Sets (or overwrites) a tariff.
    fn set_tariff(&self, t: EnergyTariff);
    /// A tariff by id, if any.
    fn get_tariff(&self, id: &str) -> Option<EnergyTariff>;
    /// Estimated cost = consumption × peak rate. Panics on an unknown tariff id
    /// (mirrors the C# `InvalidOperationException`).
    fn estimate_cost(&self, meter_id: &str, tariff_id: &str, since: DateTime<Utc>) -> f64;
    /// Logs (or overwrites) an outage.
    fn log_outage(&self, o: Outage);
    /// Outages with no recorded end time.
    fn active_outages(&self) -> Vec<Outage>;
}

/// (Energy) In-memory [`IEnergyBoard`].
///
/// Mirrors `sealed class InMemoryEnergyBoard`.
pub struct InMemoryEnergyBoard {
    readings: Mutex<Vec<MeterReading>>,
    tariffs: Mutex<HashMap<String, EnergyTariff>>,
    outages: Mutex<HashMap<String, Outage>>,
}

impl InMemoryEnergyBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            readings: Mutex::new(Vec::new()),
            tariffs: Mutex::new(HashMap::new()),
            outages: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryEnergyBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl IEnergyBoard for InMemoryEnergyBoard {
    fn record(&self, r: MeterReading) {
        self.readings.lock().unwrap().push(r);
    }

    fn readings_for(&self, meter_id: &str, since: DateTime<Utc>) -> Vec<MeterReading> {
        let mut hits: Vec<MeterReading> = self
            .readings
            .lock()
            .unwrap()
            .iter()
            .filter(|r| r.meter_id == meter_id && r.at_utc >= since)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.at_utc.cmp(&b.at_utc));
        hits
    }

    fn total_kwh_since(&self, meter_id: &str, since: DateTime<Utc>) -> f64 {
        let rows = self.readings_for(meter_id, since);
        if rows.len() < 2 {
            return 0.0;
        }
        rows[rows.len() - 1].kwh - rows[0].kwh
    }

    fn set_tariff(&self, t: EnergyTariff) {
        self.tariffs.lock().unwrap().insert(t.tariff_id.clone(), t);
    }

    fn get_tariff(&self, id: &str) -> Option<EnergyTariff> {
        self.tariffs.lock().unwrap().get(id).cloned()
    }

    fn estimate_cost(&self, meter_id: &str, tariff_id: &str, since: DateTime<Utc>) -> f64 {
        let rate = {
            let tariffs = self.tariffs.lock().unwrap();
            match tariffs.get(tariff_id) {
                Some(t) => t.peak_kwh_rate,
                None => panic!("Unknown tariff {tariff_id}"),
            }
        };
        let kwh = self.total_kwh_since(meter_id, since);
        kwh * rate
    }

    fn log_outage(&self, o: Outage) {
        self.outages.lock().unwrap().insert(o.outage_id.clone(), o);
    }

    fn active_outages(&self) -> Vec<Outage> {
        self.outages
            .lock()
            .unwrap()
            .values()
            .filter(|o| o.end_utc.is_none())
            .cloned()
            .collect()
    }
}
