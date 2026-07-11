//! ambient_test.rs
//!
//! Ports the behaviour of `CircleAI.Ambient`: readings + latest + history
//! (newest first, limited) + per-location preference + comfort tolerances.

use chrono::{Duration, TimeZone, Utc};
use circle_ai::ambient::{AmbientPreference, AmbientReading, IAmbientBoard, InMemoryAmbientBoard};

#[test]
fn latest_and_history() {
    let board = InMemoryAmbientBoard::new();
    let base = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    board.record(AmbientReading::new("dev1", 21.0, 45.0, 300.0, 35.0, base));
    board.record(AmbientReading::new("dev1", 22.0, 44.0, 320.0, 34.0, base + Duration::hours(1)));

    assert_eq!(board.latest("dev1").unwrap().temperature_c, 22.0);
    let hist = board.history("dev1", 50);
    assert_eq!(hist.len(), 2);
    assert_eq!(hist[0].temperature_c, 22.0); // newest first
    assert_eq!(board.history("dev1", 1).len(), 1);
}

#[test]
fn is_comfortable_respects_tolerances() {
    let board = InMemoryAmbientBoard::new();
    board.set_preference(AmbientPreference::new("Lounge", 22.0, 45.0, 40.0));
    let base = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();

    // Within: |21-22|<=2, |50-45|<=10, 30<=40.
    board.record(AmbientReading::new("dev1", 21.0, 50.0, 300.0, 30.0, base));
    assert!(board.is_comfortable("dev1", "Lounge"));

    // Too loud: 60 > 40.
    board.record(AmbientReading::new("dev1", 21.0, 50.0, 300.0, 60.0, base + Duration::hours(1)));
    assert!(!board.is_comfortable("dev1", "Lounge"));
}

#[test]
fn comfort_false_when_missing_data() {
    let board = InMemoryAmbientBoard::new();
    // No preference, no reading.
    assert!(!board.is_comfortable("dev1", "Nowhere"));
    board.set_preference(AmbientPreference::new("Lounge", 22.0, 45.0, 40.0));
    // Preference exists but no reading for the device.
    assert!(!board.is_comfortable("dev1", "Lounge"));
}
