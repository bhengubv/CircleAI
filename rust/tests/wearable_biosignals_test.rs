//! wearable_biosignals_test.rs
//!
//! Ports the behaviour of `CircleAI.Wearable.Biosignals`: sample factory
//! (confidence clamp + fresh id), recorded/null sources, sliding-window
//! aggregator stats, and the deterministic affect mapper rule sheet.

use chrono::{Duration, Utc};
use circle_ai::memory::AffectState;
use circle_ai::wearable_biosignals::{
    BiosignalAffectMapper, BiosignalAggregator, BiosignalKind, BiosignalSample, IBiosignalSource,
    NullBiosignalSource, RecordedBiosignalSource,
};

#[test]
fn kind_integer_values_are_stable() {
    assert_eq!(BiosignalKind::HeartRate as i32, 0);
    assert_eq!(BiosignalKind::HeartRateVariability as i32, 1);
    assert_eq!(BiosignalKind::OxygenSaturation as i32, 2);
    assert_eq!(BiosignalKind::Accelerometer as i32, 3);
    assert_eq!(BiosignalKind::BodyTemperature as i32, 4);
    assert_eq!(BiosignalKind::SleepStage as i32, 5);
    assert_eq!(BiosignalKind::Steps as i32, 6);
    assert_eq!(BiosignalKind::GalvanicSkinResponse as i32, 7);
    assert_eq!(BiosignalKind::Unknown as i32, 8);
}

#[test]
fn create_clamps_confidence_and_assigns_id() {
    let s = BiosignalSample::create(BiosignalKind::HeartRate, 72.0, "bpm", 2.5, false);
    assert_eq!(s.confidence, 1.0); // clamped to [0,1]
    assert!(!s.id.is_nil());
    let s2 = BiosignalSample::create(BiosignalKind::HeartRate, 72.0, "bpm", -1.0, false);
    assert_eq!(s2.confidence, 0.0);
    assert_ne!(s.id, s2.id); // fresh id each time
}

#[test]
fn null_source_emits_nothing() {
    let src = NullBiosignalSource::new();
    assert!(src.supported_kinds().is_empty());
    assert!(src.stream().is_empty());
    assert!(!src.is_supported(BiosignalKind::HeartRate));
}

#[test]
fn recorded_source_reports_distinct_kinds_and_replays() {
    let now = Utc::now();
    let samples = vec![
        BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::HeartRate, 70.0, "bpm", 1.0, false, now),
        BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::HeartRate, 72.0, "bpm", 1.0, false, now),
        BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::Steps, 100.0, "count", 1.0, true, now),
    ];
    let src = RecordedBiosignalSource::new(samples, None);
    assert_eq!(src.stream().len(), 3);
    assert_eq!(src.supported_kinds().len(), 2); // HeartRate + Steps
    assert!(src.is_supported(BiosignalKind::Steps));
    assert!(!src.is_supported(BiosignalKind::OxygenSaturation));
    assert_eq!(src.replay_delay(), Duration::zero());
}

#[test]
fn aggregator_computes_min_max_mean_within_window() {
    let now = Utc::now();
    let samples = vec![
        BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::HeartRate, 60.0, "bpm", 1.0, false, now),
        BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::HeartRate, 80.0, "bpm", 1.0, false, now),
        BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::HeartRate, 100.0, "bpm", 1.0, false, now),
        // Outside the window (measured well before cutoff) — excluded.
        BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::HeartRate, 999.0, "bpm", 1.0, false, now - Duration::hours(2)),
    ];
    let src = RecordedBiosignalSource::new(samples, None);
    let agg = BiosignalAggregator::new(&src);
    let snap = agg.snapshot(Duration::minutes(5));
    let stats = snap.stats.get(&BiosignalKind::HeartRate).unwrap();
    assert_eq!(stats.sample_count, 3);
    assert_eq!(stats.min, 60.0);
    assert_eq!(stats.max, 100.0);
    assert!((stats.mean - 80.0).abs() < 1e-4);
}

#[test]
#[should_panic(expected = "Window must be positive.")]
fn aggregator_zero_window_panics() {
    let src = NullBiosignalSource::new();
    let agg = BiosignalAggregator::new(&src);
    agg.snapshot(Duration::zero());
}

#[test]
fn affect_mapper_high_heart_rate_raises_energy_and_uncertainty() {
    let mut affect = AffectState::new("u");
    affect.energy = 0.5;
    affect.uncertainty = 0.2;
    let sample = BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::HeartRate, 140.0, "bpm", 1.0, false, Utc::now());
    BiosignalAffectMapper::apply(&sample, &mut affect);
    assert!((affect.energy - 0.60).abs() < 1e-4);
    assert!((affect.uncertainty - 0.25).abs() < 1e-4);
}

#[test]
fn affect_mapper_low_confidence_is_ignored() {
    let mut affect = AffectState::new("u");
    affect.energy = 0.5;
    let sample = BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::HeartRate, 140.0, "bpm", 0.4, false, Utc::now());
    BiosignalAffectMapper::apply(&sample, &mut affect);
    assert!((affect.energy - 0.5).abs() < 1e-9); // unchanged
}

#[test]
fn affect_mapper_low_spo2_raises_uncertainty() {
    let mut affect = AffectState::new("u");
    affect.uncertainty = 0.2;
    let sample = BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::OxygenSaturation, 88.0, "%", 1.0, false, Utc::now());
    BiosignalAffectMapper::apply(&sample, &mut affect);
    assert!((affect.uncertainty - 0.30).abs() < 1e-4);
}

#[test]
fn affect_mapper_sleep_stage_no_mutation() {
    let mut affect = AffectState::new("u");
    affect.energy = 0.5;
    affect.uncertainty = 0.2;
    let sample = BiosignalSample::new(uuid::Uuid::new_v4(), BiosignalKind::SleepStage, 2.0, "stage", 1.0, false, Utc::now());
    BiosignalAffectMapper::apply(&sample, &mut affect);
    assert!((affect.energy - 0.5).abs() < 1e-9);
    assert!((affect.uncertainty - 0.2).abs() < 1e-9);
}
