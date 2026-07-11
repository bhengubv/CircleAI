//! wearable_biosignals — CircleAI wearable biosignal layer.
//!
//! Full Rust port of `src/CircleAI.Wearable.Biosignals/`:
//!
//! - [`BiosignalKind`] (stable integer taxonomy 0–8), [`BiosignalSample`]
//!   (+ [`BiosignalSample::create`] factory), the [`IBiosignalSource`] contract,
//!   the [`NullBiosignalSource`] and [`RecordedBiosignalSource`] backends, the
//!   sliding-window [`BiosignalAggregator`] (+ [`BiosignalStats`] /
//!   [`BiosignalSnapshot`]), and the deterministic [`BiosignalAffectMapper`].
//!
//! The C# async surface is projected sync-only, mirroring the existing
//! `companion::IBioSignalStream` convention in this crate: `StreamAsync`
//! (`IAsyncEnumerable`) becomes [`IBiosignalSource::stream`] returning a
//! materialised `Vec`, and `IsSupportedAsync` (`Task<bool>`) becomes
//! [`IBiosignalSource::is_supported`]. `RecordedBiosignalSource` keeps its
//! `replay_delay` for parity but replays synchronously without sleeping.
//! `BiosignalAggregator::snapshot` drains the whole source once and aggregates
//! samples at/after `now - window` (there is no wall-clock deadline in the sync
//! projection — a finite source always terminates).

use std::collections::{HashMap, HashSet};

use chrono::{DateTime, Duration, Utc};
use uuid::Uuid;

use crate::memory::AffectState;

/// (Biosignals) Canonical kinds of biosignal sample. Integer values are stable
/// across language ports — do not renumber.
///
/// Mirrors `enum BiosignalKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum BiosignalKind {
    /// Heart rate, beats per minute.
    HeartRate = 0,
    /// Heart rate variability, RMSSD in milliseconds.
    HeartRateVariability = 1,
    /// Peripheral oxygen saturation, percent (0-100).
    OxygenSaturation = 2,
    /// Accelerometer magnitude, m/s^2.
    Accelerometer = 3,
    /// Body temperature, degrees Celsius.
    BodyTemperature = 4,
    /// Sleep stage encoded as a float: 0=awake, 1=light, 2=deep, 3=REM.
    SleepStage = 5,
    /// Step count (cumulative or delta — see [`BiosignalSample::is_cumulative`]).
    Steps = 6,
    /// Galvanic skin response, microsiemens.
    GalvanicSkinResponse = 7,
    /// Catch-all for vendor-specific or future signals.
    Unknown = 8,
}

/// (Biosignals) A single biosignal measurement.
///
/// Mirrors `sealed record BiosignalSample(Guid Id, BiosignalKind Kind,
/// float Value, string Unit, float Confidence, bool IsCumulative,
/// DateTimeOffset MeasuredAt)`.
#[derive(Debug, Clone, PartialEq)]
pub struct BiosignalSample {
    pub id: Uuid,
    pub kind: BiosignalKind,
    pub value: f32,
    pub unit: String,
    pub confidence: f32,
    pub is_cumulative: bool,
    pub measured_at: DateTime<Utc>,
}

impl BiosignalSample {
    /// Constructs a sample, mirroring the positional C# record constructor.
    pub fn new(
        id: Uuid,
        kind: BiosignalKind,
        value: f32,
        unit: impl Into<String>,
        confidence: f32,
        is_cumulative: bool,
        measured_at: DateTime<Utc>,
    ) -> Self {
        Self {
            id,
            kind,
            value,
            unit: unit.into(),
            confidence,
            is_cumulative,
            measured_at,
        }
    }

    /// Creates a fresh sample with a new UUID, current UTC timestamp, and
    /// confidence clamped to `[0, 1]`.
    ///
    /// Mirrors `BiosignalSample.Create(kind, value, unit, confidence = 1.0f,
    /// isCumulative = false)`.
    pub fn create(kind: BiosignalKind, value: f32, unit: impl Into<String>, confidence: f32, is_cumulative: bool) -> Self {
        Self {
            id: Uuid::new_v4(),
            kind,
            value,
            unit: unit.into(),
            confidence: confidence.clamp(0.0, 1.0),
            is_cumulative,
            measured_at: Utc::now(),
        }
    }
}

/// (Biosignals) A streaming source of biosignal samples.
///
/// Mirrors `interface IBiosignalSource` (sync-projected).
pub trait IBiosignalSource {
    /// The kinds this source can emit. May be empty for the null source.
    fn supported_kinds(&self) -> Vec<BiosignalKind>;
    /// The source's samples, in order (the sync analogue of `StreamAsync`).
    fn stream(&self) -> Vec<BiosignalSample>;
    /// Whether this source can produce samples of `kind`.
    fn is_supported(&self, kind: BiosignalKind) -> bool;
}

/// (Biosignals) A source that supports nothing and emits nothing.
///
/// Mirrors `sealed class NullBiosignalSource` — the "no wearable connected" case.
pub struct NullBiosignalSource;

impl NullBiosignalSource {
    /// Creates the null source.
    pub fn new() -> Self {
        Self
    }
}

impl Default for NullBiosignalSource {
    fn default() -> Self {
        Self::new()
    }
}

impl IBiosignalSource for NullBiosignalSource {
    fn supported_kinds(&self) -> Vec<BiosignalKind> {
        Vec::new()
    }
    fn stream(&self) -> Vec<BiosignalSample> {
        Vec::new()
    }
    fn is_supported(&self, _kind: BiosignalKind) -> bool {
        false
    }
}

/// (Biosignals) Replays a recorded biosignal stream.
///
/// Mirrors `sealed class RecordedBiosignalSource`. `replay_delay` is retained for
/// parity with the C# constructor but is not used to pace the sync replay.
pub struct RecordedBiosignalSource {
    samples: Vec<BiosignalSample>,
    kinds: Vec<BiosignalKind>,
    replay_delay: Duration,
}

impl RecordedBiosignalSource {
    /// Creates a source replaying `samples`. `replay_delay` defaults to zero when
    /// `None` (mirrors the C# `TimeSpan? replayDelay = null`).
    pub fn new(samples: Vec<BiosignalSample>, replay_delay: Option<Duration>) -> Self {
        // Distinct kinds, first-seen order (matches the C# HashSet enumerate).
        let mut seen: HashSet<BiosignalKind> = HashSet::new();
        let mut kinds: Vec<BiosignalKind> = Vec::new();
        for s in &samples {
            if seen.insert(s.kind) {
                kinds.push(s.kind);
            }
        }
        Self {
            samples,
            kinds,
            replay_delay: replay_delay.unwrap_or_else(Duration::zero),
        }
    }

    /// The configured replay delay (parity accessor; zero unless set).
    pub fn replay_delay(&self) -> Duration {
        self.replay_delay
    }
}

impl IBiosignalSource for RecordedBiosignalSource {
    fn supported_kinds(&self) -> Vec<BiosignalKind> {
        self.kinds.clone()
    }
    fn stream(&self) -> Vec<BiosignalSample> {
        self.samples.clone()
    }
    fn is_supported(&self, kind: BiosignalKind) -> bool {
        self.kinds.contains(&kind)
    }
}

/// (Biosignals) Per-kind aggregate statistics over a window.
///
/// Mirrors `sealed record BiosignalStats(int SampleCount, float Min, float Max,
/// float Mean)`.
#[derive(Debug, Clone, PartialEq)]
pub struct BiosignalStats {
    pub sample_count: i32,
    pub min: f32,
    pub max: f32,
    pub mean: f32,
}

impl BiosignalStats {
    /// Constructs stats, mirroring the positional C# record constructor.
    pub fn new(sample_count: i32, min: f32, max: f32, mean: f32) -> Self {
        Self {
            sample_count,
            min,
            max,
            mean,
        }
    }
}

/// (Biosignals) A point-in-time snapshot of per-kind aggregates.
///
/// Mirrors `sealed record BiosignalSnapshot(
/// IReadOnlyDictionary<BiosignalKind, BiosignalStats> Stats,
/// DateTimeOffset GeneratedAt)`.
#[derive(Debug, Clone, PartialEq)]
pub struct BiosignalSnapshot {
    pub stats: HashMap<BiosignalKind, BiosignalStats>,
    pub generated_at: DateTime<Utc>,
}

impl BiosignalSnapshot {
    /// Constructs a snapshot, mirroring the positional C# record constructor.
    pub fn new(stats: HashMap<BiosignalKind, BiosignalStats>, generated_at: DateTime<Utc>) -> Self {
        Self { stats, generated_at }
    }
}

/// Running min/max/mean accumulator — mirrors the private C# `Accumulator`.
struct Accumulator {
    count: i32,
    min: f32,
    max: f32,
    sum: f64,
}

impl Accumulator {
    fn new() -> Self {
        Self {
            count: 0,
            min: f32::INFINITY,
            max: f32::NEG_INFINITY,
            sum: 0.0,
        }
    }

    fn add(&mut self, v: f32) {
        self.count += 1;
        if v < self.min {
            self.min = v;
        }
        if v > self.max {
            self.max = v;
        }
        self.sum += v as f64;
    }

    fn to_stats(&self) -> BiosignalStats {
        let mean = if self.count == 0 {
            0.0
        } else {
            (self.sum / self.count as f64) as f32
        };
        BiosignalStats::new(self.count, self.min, self.max, mean)
    }
}

/// (Biosignals) Sliding-window aggregator over an [`IBiosignalSource`].
///
/// Mirrors `sealed class BiosignalAggregator`.
pub struct BiosignalAggregator<'a> {
    source: &'a dyn IBiosignalSource,
}

impl<'a> BiosignalAggregator<'a> {
    /// Wraps `source`.
    pub fn new(source: &'a dyn IBiosignalSource) -> Self {
        Self { source }
    }

    /// Drains the source once and returns a snapshot over the samples measured at
    /// or after `now - window`. Panics when `window <= 0` (mirrors the C#
    /// `ArgumentOutOfRangeException`).
    pub fn snapshot(&self, window: Duration) -> BiosignalSnapshot {
        if window <= Duration::zero() {
            panic!("Window must be positive.");
        }
        let generated_at = Utc::now();
        let cutoff = generated_at - window;
        let mut accumulator: HashMap<BiosignalKind, Accumulator> = HashMap::new();
        for sample in self.source.stream() {
            if sample.measured_at < cutoff {
                continue;
            }
            accumulator
                .entry(sample.kind)
                .or_insert_with(Accumulator::new)
                .add(sample.value);
        }
        let stats: HashMap<BiosignalKind, BiosignalStats> =
            accumulator.iter().map(|(k, acc)| (*k, acc.to_stats())).collect();
        BiosignalSnapshot::new(stats, generated_at)
    }
}

/// (Biosignals) Deterministic projection of biosignal samples onto
/// [`AffectState`] mutations.
///
/// Mirrors `static class BiosignalAffectMapper`. Rule sheet (all mutations
/// clamped to `[0, 1]`; confidence `< 0.5` never mutates):
///
/// - HeartRate `> 130` bpm: energy += 0.10, uncertainty += 0.05.
/// - HeartRate `> 100` bpm: energy += 0.05.
/// - HeartRate `< 50` bpm: energy -= 0.05.
/// - HRV `< 20` ms: uncertainty += 0.05, rapport -= 0.02.
/// - HRV `> 60` ms: engagement += 0.02.
/// - SpO2 `< 90` %: uncertainty += 0.10.
/// - SleepStage / other kinds: no mutation.
pub struct BiosignalAffectMapper;

impl BiosignalAffectMapper {
    const MIN_CONFIDENCE: f32 = 0.5;

    /// Applies the rule for `sample` to `affect`, mutating it in place. Stamps
    /// `last_updated_at` on any applicable (confidence-passing) sample, matching
    /// the C# which sets `LastUpdatedUtc` after the switch.
    pub fn apply(sample: &BiosignalSample, affect: &mut AffectState) {
        // Confidence gate — low-confidence samples never mutate state.
        if sample.confidence < Self::MIN_CONFIDENCE {
            return;
        }

        match sample.kind {
            BiosignalKind::HeartRate => Self::apply_heart_rate(sample.value, affect),
            BiosignalKind::HeartRateVariability => Self::apply_hrv(sample.value, affect),
            BiosignalKind::OxygenSaturation => Self::apply_spo2(sample.value, affect),
            // SleepStage and the remaining kinds do not drive affect.
            _ => {}
        }

        affect.last_updated_at = Utc::now();
    }

    fn apply_heart_rate(bpm: f32, a: &mut AffectState) {
        if bpm > 130.0 {
            a.energy = Self::clamp01(a.energy + 0.10);
            a.uncertainty = Self::clamp01(a.uncertainty + 0.05);
        } else if bpm > 100.0 {
            a.energy = Self::clamp01(a.energy + 0.05);
        } else if bpm < 50.0 {
            a.energy = Self::clamp01(a.energy - 0.05);
        }
    }

    fn apply_hrv(rmssd_ms: f32, a: &mut AffectState) {
        if rmssd_ms < 20.0 {
            a.uncertainty = Self::clamp01(a.uncertainty + 0.05);
            a.rapport = Self::clamp01(a.rapport - 0.02);
        } else if rmssd_ms > 60.0 {
            a.engagement = Self::clamp01(a.engagement + 0.02);
        }
    }

    fn apply_spo2(percent: f32, a: &mut AffectState) {
        if percent < 90.0 {
            a.uncertainty = Self::clamp01(a.uncertainty + 0.10);
        }
    }

    fn clamp01(v: f32) -> f32 {
        v.clamp(0.0, 1.0)
    }
}
