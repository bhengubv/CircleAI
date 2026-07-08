//! thermal.rs
//!
//! Cross-platform thermal state monitor. Ported from `IThermalThrottleService.cs`
//! + `ThermalThrottleService.cs`. The C# implementation polls OS temperature
//! APIs on a background `PeriodicTimer` (10 s). The sync port injects the OS
//! reading behind [`IThermalSampler`] (a deterministic sampler ships for tests),
//! exposes the same five-level [`ThermalState`] state-machine, and drives one
//! sample+transition per [`ThermalThrottleService::poll_once`] — the background
//! loop itself is a host concern.
//!
//! Threshold constants and the state-transition/`StateChanged` semantics are
//! ported 1:1.

use std::sync::{Arc, Mutex};

// Kelvin thresholds used for WMI readings (Windows path).
const KELVIN_SERIOUS_THRESHOLD: f64 = 348.0; // 75 °C
const KELVIN_CRITICAL_THRESHOLD: f64 = 363.0; // 90 °C

// Millidegrees-Celsius thresholds used for Linux sysfs readings.
const MILLI_CELSIUS_SERIOUS: i64 = 75_000;
const MILLI_CELSIUS_CRITICAL: i64 = 90_000;

/// Coarse thermal state, ordered from coolest to hottest so numeric comparisons
/// (e.g. `>= Serious`) are meaningful. 1:1 with the C# `ThermalState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
#[repr(i32)]
pub enum ThermalState {
    /// State could not be determined (API unavailable or error).
    Unknown = 0,
    /// Device is within normal operating temperature.
    Normal = 1,
    /// Device is slightly warm; performance may be lightly throttled.
    Fair = 2,
    /// Device is hot; OS may have begun throttling CPU/GPU.
    Serious = 3,
    /// Device is critically hot; aggressive throttling or shutdown imminent.
    Critical = 4,
}

/// A `StateChanged` handler — receives the new state.
pub type ThermalStateHandler = Arc<dyn Fn(ThermalState) + Send + Sync>;

/// Polls platform thermal APIs and exposes the current device temperature state.
/// 1:1 with the C# `IThermalThrottleService` (minus `IDisposable`, which Rust's
/// `Drop` covers).
pub trait IThermalThrottleService: Send + Sync {
    /// Most-recently sampled thermal state.
    fn current_state(&self) -> ThermalState;

    /// `true` when [`current_state`](Self::current_state) is `Serious` or
    /// `Critical`. Inference workers should pause when this returns `true`.
    fn should_pause_inference(&self) -> bool {
        self.current_state() >= ThermalState::Serious
    }

    /// Registers a handler fired whenever the state changes.
    fn on_state_changed(&self, handler: ThermalStateHandler);

    /// Starts monitoring: samples immediately so callers get a valid state
    /// before the first interval. Safe to call multiple times.
    fn start_monitoring(&self);

    /// Stops monitoring. The current state is retained.
    fn stop_monitoring(&self);
}

/// Injected OS temperature reading. Returns a raw [`ThermalState`] (already
/// classified against the platform thresholds). Real hosts implement one per OS
/// (Windows WMI / Linux sysfs / Android PowerManager / iOS NSProcessInfo).
pub trait IThermalSampler: Send + Sync {
    /// Sample the current thermal state. Return `Unknown` on any error.
    fn sample(&self) -> ThermalState;
}

/// Convenience sampler that classifies a Linux-style millidegrees-Celsius
/// reading using the same thresholds as the C# Linux path.
pub fn classify_milli_celsius(milli_celsius: i64) -> ThermalState {
    if milli_celsius > MILLI_CELSIUS_CRITICAL {
        ThermalState::Critical
    } else if milli_celsius > MILLI_CELSIUS_SERIOUS {
        ThermalState::Serious
    } else {
        ThermalState::Normal
    }
}

/// Convenience sampler that classifies a Kelvin reading using the same
/// thresholds as the C# Windows (WMI) path. `max_kelvin <= 0` → `Unknown`.
pub fn classify_kelvin(max_kelvin: f64) -> ThermalState {
    if max_kelvin <= 0.0 {
        ThermalState::Unknown
    } else if max_kelvin > KELVIN_CRITICAL_THRESHOLD {
        ThermalState::Critical
    } else if max_kelvin > KELVIN_SERIOUS_THRESHOLD {
        ThermalState::Serious
    } else {
        ThermalState::Normal
    }
}

/// Deterministic in-memory sampler for tests: returns whatever value the test
/// last set via [`ManualThermalSampler::set`].
#[derive(Debug)]
pub struct ManualThermalSampler {
    value: Mutex<ThermalState>,
}

impl ManualThermalSampler {
    pub fn new(initial: ThermalState) -> Self {
        Self {
            value: Mutex::new(initial),
        }
    }

    /// Set the value the next sample returns.
    pub fn set(&self, state: ThermalState) {
        *self.value.lock().unwrap() = state;
    }
}

impl IThermalSampler for ManualThermalSampler {
    fn sample(&self) -> ThermalState {
        *self.value.lock().unwrap()
    }
}

struct ThermalInner {
    current: ThermalState,
    running: bool,
    handlers: Vec<ThermalStateHandler>,
}

/// Cross-platform thermal state poller. Wraps an [`IThermalSampler`] and drives
/// the five-level state machine + `StateChanged` transitions. 1:1 with the C#
/// `ThermalThrottleService` (the OS reading is the injected sampler).
pub struct ThermalThrottleService<S: IThermalSampler> {
    sampler: S,
    inner: Mutex<ThermalInner>,
}

impl<S: IThermalSampler> ThermalThrottleService<S> {
    /// Creates the service over the given sampler. State starts `Unknown`.
    pub fn new(sampler: S) -> Self {
        Self {
            sampler,
            inner: Mutex::new(ThermalInner {
                current: ThermalState::Unknown,
                running: false,
                handlers: Vec::new(),
            }),
        }
    }

    /// Drives one sample + transition (the host's background loop calls this on
    /// its own interval, standing in for the C# `PeriodicTimer` tick). No-op
    /// when not monitoring.
    pub fn poll_once(&self) {
        {
            let inner = self.inner.lock().unwrap();
            if !inner.running {
                return;
            }
        }
        let sampled = self.sampler.sample();
        self.apply_new_state(sampled);
    }

    fn apply_new_state(&self, new_state: ThermalState) {
        let (changed, snapshot) = {
            let mut inner = self.inner.lock().unwrap();
            if inner.current == new_state {
                (false, Vec::new())
            } else {
                inner.current = new_state;
                (true, inner.handlers.clone())
            }
        };
        if changed {
            for h in snapshot {
                let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| h(new_state)));
            }
        }
    }
}

impl<S: IThermalSampler> IThermalThrottleService for ThermalThrottleService<S> {
    fn current_state(&self) -> ThermalState {
        self.inner.lock().unwrap().current
    }

    fn on_state_changed(&self, handler: ThermalStateHandler) {
        self.inner.lock().unwrap().handlers.push(handler);
    }

    fn start_monitoring(&self) {
        {
            let mut inner = self.inner.lock().unwrap();
            if inner.running {
                return;
            }
            inner.running = true;
        }
        // Sample immediately so callers get a valid state before the first
        // interval elapses (matches the C# PollLoopAsync prologue).
        let sampled = self.sampler.sample();
        self.apply_new_state(sampled);
    }

    fn stop_monitoring(&self) {
        self.inner.lock().unwrap().running = false;
    }
}
