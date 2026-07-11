//! wearable_test.rs
//!
//! Ports the behaviour of `CircleAI.Wearable`: device registry (by vendor) +
//! telemetry samples (unknown-device guard) + latest/average value lookups.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::wearable::{
    IWearableBoard, InMemoryWearableBoard, WearableDevice, WearableKind, WearableSample,
    WearableTelemetryKind,
};

#[test]
fn devices_sorted_by_vendor() {
    let board = InMemoryWearableBoard::new();
    board.add(WearableDevice::new("d1", WearableKind::Smartwatch, "Zenith", "1.0", 80.0));
    board.add(WearableDevice::new("d2", WearableKind::FitnessBand, "Apex", "2.0", 55.0));

    let ds = board.devices();
    assert_eq!(ds.len(), 2);
    assert_eq!(ds[0].vendor, "Apex"); // alphabetical by vendor
    assert_eq!(board.get_device("d1").unwrap().battery_pct, 80.0);
}

#[test]
#[should_panic(expected = "Unknown device")]
fn record_unknown_device_panics() {
    let board = InMemoryWearableBoard::new();
    board.record(WearableSample::new("ghost", WearableTelemetryKind::HeartRate, 70.0, Utc::now()));
}

#[test]
fn read_latest_and_average() {
    let board = InMemoryWearableBoard::new();
    board.add(WearableDevice::new("d1", WearableKind::ChestStrap, "Apex", "1.0", 90.0));
    let base = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.record(WearableSample::new("d1", WearableTelemetryKind::HeartRate, 60.0, base));
    board.record(WearableSample::new("d1", WearableTelemetryKind::HeartRate, 80.0, base + Duration::minutes(1)));
    board.record(WearableSample::new("d1", WearableTelemetryKind::Steps, 1000.0, base));

    let hr = board.read_since("d1", WearableTelemetryKind::HeartRate, base);
    assert_eq!(hr.len(), 2);
    assert_eq!(hr[0].value, 60.0); // earliest first

    assert_eq!(board.latest_value("d1", WearableTelemetryKind::HeartRate), Some(80.0));
    assert!((board.average_value("d1", WearableTelemetryKind::HeartRate, base) - 70.0).abs() < 1e-9);
}

#[test]
fn average_of_empty_window_is_nan() {
    let board = InMemoryWearableBoard::new();
    board.add(WearableDevice::new("d1", WearableKind::Patch, "Apex", "1.0", 50.0));
    let avg = board.average_value("d1", WearableTelemetryKind::OxygenPct, Utc::now());
    assert!(avg.is_nan());
    assert!(board.latest_value("d1", WearableTelemetryKind::OxygenPct).is_none());
}
