//! warmup.rs
//!
//! (RT-07) Local-only request-timeline learner + predictive warmup controller.
//! Ported from `Warmup/IRequestPredictor.cs`, `Warmup/HistogramRequestPredictor.cs`,
//! `Warmup/PredictiveWarmupOptions.cs`, and `Warmup/PredictiveWarmupController.cs`.
//!
//! The predictor records arrival times and forecasts whether a spike is coming;
//! the controller polls it and pre-warms the generator before a predicted spike.
//! All counting is in-process — no telemetry, no upload. The C# controller runs
//! an async `PeriodicTimer` loop; the sync port exposes
//! [`PredictiveWarmupController::tick`] which the host calls on its own interval.

use std::sync::Mutex;

use chrono::{DateTime, Duration, Timelike, Utc};

use super::service::IAIService;

/// (RT-07) Forecast of inbound requests over a window. 1:1 with the C#
/// `ArrivalForecast` readonly record struct.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct ArrivalForecast {
    /// 0.0 .. 1.0 — probability of at least one arrival in the window.
    pub probability_of_arrival: f64,
    /// Best estimate of how many arrivals to expect.
    pub expected_count: f64,
    /// 0.0 .. 1.0 — how trustworthy the estimate is given sample size.
    pub confidence: f64,
}

impl ArrivalForecast {
    pub fn new(probability_of_arrival: f64, expected_count: f64, confidence: f64) -> Self {
        Self {
            probability_of_arrival,
            expected_count,
            confidence,
        }
    }
}

/// (RT-07) Local-only predictor that learns request arrival timing. 1:1 with the
/// C# `IRequestPredictor`.
pub trait IRequestPredictor: Send + Sync {
    /// Record one arrival at `utc`.
    fn record_arrival(&self, utc: DateTime<Utc>);

    /// Forecast arrivals in `forecast_window` starting at `utc_now`.
    fn predict(&self, utc_now: DateTime<Utc>, forecast_window: Duration) -> ArrivalForecast;

    /// Total arrivals observed since construction.
    fn observed_arrivals(&self) -> i64;
}

const MINUTES_PER_DAY: usize = 24 * 60;
const WARM_CONFIDENCE: f64 = 1.0;
const MIN_SAMPLES_FOR_FULL_CONFIDENCE: i64 = 25;

struct HistogramInner {
    /// index = minute-of-day; value = avg arrivals/minute observed (EWMA).
    per_minute_rate: Vec<f64>,
    per_minute_count: Vec<i32>,
}

/// (RT-07) Default [`IRequestPredictor`] — keeps a histogram of per-minute
/// arrival rates over a rolling window of recent days. 1:1 with the C#
/// `HistogramRequestPredictor` (same EWMA, same Poisson-tail probability, same
/// confidence formula).
pub struct HistogramRequestPredictor {
    history_days: i64,
    inner: Mutex<HistogramInner>,
    observed: Mutex<i64>,
}

impl HistogramRequestPredictor {
    /// Construct with a rolling history of `history_days` days. Default 7.
    /// Panics on `history_days <= 0` (matches the C#
    /// `ArgumentOutOfRangeException`).
    pub fn new(history_days: i64) -> Self {
        assert!(history_days > 0, "historyDays must be positive");
        Self {
            history_days,
            inner: Mutex::new(HistogramInner {
                per_minute_rate: vec![0.0; MINUTES_PER_DAY],
                per_minute_count: vec![0; MINUTES_PER_DAY],
            }),
            observed: Mutex::new(0),
        }
    }

    /// Default 7-day history (one calendar week).
    pub fn with_default_history() -> Self {
        Self::new(7)
    }

    /// Test-only — wipe state. 1:1 with the C# `ResetForTests`.
    pub fn reset_for_tests(&self) {
        let mut inner = self.inner.lock().unwrap();
        inner.per_minute_rate.iter_mut().for_each(|r| *r = 0.0);
        inner.per_minute_count.iter_mut().for_each(|c| *c = 0);
        *self.observed.lock().unwrap() = 0;
    }
}

impl IRequestPredictor for HistogramRequestPredictor {
    fn record_arrival(&self, utc: DateTime<Utc>) {
        let minute = (utc.hour() as usize * 60) + utc.minute() as usize;
        {
            let mut inner = self.inner.lock().unwrap();
            inner.per_minute_count[minute] += 1;
            let cnt = inner.per_minute_count[minute] as i64;
            // EWMA over the last `history_days` of observations at this slot.
            let alpha = 2.0 / (cnt.min(self.history_days) as f64 + 1.0);
            inner.per_minute_rate[minute] =
                (alpha * 1.0) + ((1.0 - alpha) * inner.per_minute_rate[minute]);
        }
        *self.observed.lock().unwrap() += 1;
    }

    fn predict(&self, utc_now: DateTime<Utc>, forecast_window: Duration) -> ArrivalForecast {
        if forecast_window <= Duration::zero() {
            return ArrivalForecast::new(0.0, 0.0, 0.0);
        }
        let observed = self.observed_arrivals();
        if observed == 0 {
            return ArrivalForecast::new(0.0, 0.0, 0.0);
        }

        let minute = (utc_now.hour() as usize * 60) + utc_now.minute() as usize;
        // Ceiling of window in minutes, at least 1.
        let window_secs = forecast_window.num_seconds() as f64;
        let minutes = ((window_secs / 60.0).ceil() as i64).max(1) as usize;

        let mut expected = 0.0_f64;
        let mut covered_samples = 0_i64;
        {
            let inner = self.inner.lock().unwrap();
            for i in 0..minutes {
                let idx = (minute + i) % MINUTES_PER_DAY;
                expected += inner.per_minute_rate[idx];
                covered_samples += inner.per_minute_count[idx] as i64;
            }
        }

        // Poisson tail: P(>=1 arrival) = 1 - exp(-lambda).
        let probability = 1.0 - (-expected).exp();
        let confidence = WARM_CONFIDENCE.min(
            covered_samples as f64 / (MIN_SAMPLES_FOR_FULL_CONFIDENCE * minutes as i64) as f64,
        );
        ArrivalForecast::new(probability, expected, confidence)
    }

    fn observed_arrivals(&self) -> i64 {
        *self.observed.lock().unwrap()
    }
}

/// (RT-07) Configuration for [`PredictiveWarmupController`]. 1:1 with the C#
/// `PredictiveWarmupOptions`.
#[derive(Debug, Clone, Copy)]
pub struct PredictiveWarmupOptions {
    /// When `false` (default), the controller does not pre-warm.
    pub enabled: bool,
    /// How often the controller polls the predictor. Default 30 s.
    pub poll_interval: Duration,
    /// How far ahead to forecast. Default 60 s.
    pub forecast_window: Duration,
    /// Pre-warm when `probability × confidence >= threshold`. Default 0.5.
    pub warmup_threshold: f64,
    /// Minimum delay between consecutive pre-warm calls. Default 5 minutes.
    pub min_time_between_warmups: Duration,
}

impl Default for PredictiveWarmupOptions {
    fn default() -> Self {
        Self {
            enabled: false,
            poll_interval: Duration::seconds(30),
            forecast_window: Duration::seconds(60),
            warmup_threshold: 0.5,
            min_time_between_warmups: Duration::minutes(5),
        }
    }
}

/// A monotonic clock provider (mirrors the C# `Func<DateTimeOffset>`).
pub type Clock = Box<dyn Fn() -> DateTime<Utc> + Send + Sync>;

/// (RT-07) Polls an [`IRequestPredictor`] and triggers [`IAIService`] pre-warm
/// before predicted spikes. 1:1 with the C# `PredictiveWarmupController` (the
/// async `PeriodicTimer` loop is replaced by the host calling [`Self::tick`]).
pub struct PredictiveWarmupController<'a, P: IRequestPredictor> {
    service: &'a dyn IAIService,
    predictor: &'a P,
    options: PredictiveWarmupOptions,
    clock: Clock,
    last_warmup: Mutex<DateTime<Utc>>,
}

impl<'a, P: IRequestPredictor> PredictiveWarmupController<'a, P> {
    /// Constructs the controller. `clock` defaults to `Utc::now` when `None`.
    pub fn new(
        service: &'a dyn IAIService,
        predictor: &'a P,
        options: PredictiveWarmupOptions,
        clock: Option<Clock>,
    ) -> Self {
        Self {
            service,
            predictor,
            options,
            clock: clock.unwrap_or_else(|| Box::new(Utc::now)),
            last_warmup: Mutex::new(DateTime::<Utc>::MIN_UTC),
        }
    }

    /// Record a request arrival on the underlying predictor at "now". 1:1 with
    /// the C# `NotifyArrival`.
    pub fn notify_arrival(&self) {
        self.predictor.record_arrival((self.clock)());
    }

    /// Run one prediction + decide-and-maybe-warm cycle. Returns `true` when
    /// warmup was triggered. 1:1 with the C# `TickAsync`.
    pub fn tick(&self) -> bool {
        let now = (self.clock)();
        let forecast = self.predictor.predict(now, self.options.forecast_window);
        let score = forecast.probability_of_arrival * forecast.confidence;
        if score < self.options.warmup_threshold {
            return false;
        }
        {
            let last = *self.last_warmup.lock().unwrap();
            if now - last < self.options.min_time_between_warmups {
                return false;
            }
        }

        *self.last_warmup.lock().unwrap() = now;
        match self.service.prewarm() {
            Ok(()) => true,
            Err(_) => false,
        }
    }

    /// Whether the controller is enabled (host loop should skip ticking when
    /// false). Mirrors the C# `StartAsync` early-out on `!Enabled`.
    pub fn is_enabled(&self) -> bool {
        self.options.enabled
    }
}
