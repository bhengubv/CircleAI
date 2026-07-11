//! energy_test.rs
//!
//! Ports the behaviour of `CircleAI.Energy`: meter readings + consumption delta
//! + tariff cost estimate + active outages.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::energy::{EnergyTariff, IEnergyBoard, InMemoryEnergyBoard, MeterReading, Outage};

#[test]
fn total_kwh_since_is_last_minus_first() {
    let board = InMemoryEnergyBoard::new();
    let base = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.record(MeterReading::new("m1", 100.0, base));
    board.record(MeterReading::new("m1", 140.0, base + Duration::hours(24)));
    board.record(MeterReading::new("m1", 175.0, base + Duration::hours(48)));

    // 175 - 100 = 75.
    assert!((board.total_kwh_since("m1", base) - 75.0).abs() < 1e-9);
    // Fewer than two readings in-window → 0.
    assert!((board.total_kwh_since("m1", base + Duration::hours(30)) - 0.0).abs() < 1e-9);
}

#[test]
fn estimate_cost_uses_peak_rate() {
    let board = InMemoryEnergyBoard::new();
    let base = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.record(MeterReading::new("m1", 100.0, base));
    board.record(MeterReading::new("m1", 150.0, base + Duration::hours(24)));
    board.set_tariff(EnergyTariff::new("tf1", "Home", 2.5, 1.5, "ZAR"));

    // 50 kWh * 2.5 = 125.
    assert!((board.estimate_cost("m1", "tf1", base) - 125.0).abs() < 1e-9);
}

#[test]
#[should_panic(expected = "Unknown tariff")]
fn estimate_cost_unknown_tariff_panics() {
    let board = InMemoryEnergyBoard::new();
    board.estimate_cost("m1", "nope", Utc::now());
}

#[test]
fn active_outages_are_open_ended() {
    let board = InMemoryEnergyBoard::new();
    let t = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.log_outage(Outage::new("o1", "Sector A", t, None, Some("Cable fault".into())));
    board.log_outage(Outage::new("o2", "Sector B", t, Some(t + Duration::hours(2)), None));

    let active = board.active_outages();
    assert_eq!(active.len(), 1);
    assert_eq!(active[0].outage_id, "o1");
}
