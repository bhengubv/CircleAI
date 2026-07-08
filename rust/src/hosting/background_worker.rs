//! background_worker.rs
//!
//! Hosted-service adapter that wraps [`IAIService`] in a start/stop lifecycle.
//! Ported from `BackgroundInferenceWorker.cs`. The C# type implements
//! `IHostedService` (`.NET` Generic Host); the sync port exposes [`start`] /
//! [`stop`] the host calls directly. When a [`IThermalThrottleService`] is
//! supplied, the worker subscribes to its state changes and flips
//! [`Self::is_paused`] to `true` while the device is in `Serious` / `Critical`
//! thermal state — callers driving inference check [`Self::is_paused`] before
//! submitting work. The pause state-machine (Serious/Critical → paused, cooler →
//! resumed) is ported 1:1.

use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

use super::service::{HostingError, IAIService};
use super::thermal::{IThermalThrottleService, ThermalState};

/// Wraps an [`IAIService`] in a start/stop lifecycle and honours a thermal
/// throttle service when one is provided. 1:1 with the C#
/// `BackgroundInferenceWorker`.
pub struct BackgroundInferenceWorker<'a> {
    butler: &'a dyn IAIService,
    thermal: Option<Arc<dyn IThermalThrottleService>>,
    /// `true` while the device is in Serious / Critical thermal state.
    paused: Arc<AtomicBool>,
    /// `0` = running, `1` = stopped (guards against double-stop).
    stopped: AtomicBool,
}

impl<'a> BackgroundInferenceWorker<'a> {
    /// Initialises the worker. `thermal` `None` skips thermal monitoring and
    /// [`Self::is_paused`] is always `false`.
    pub fn new(
        butler: &'a dyn IAIService,
        thermal: Option<Arc<dyn IThermalThrottleService>>,
    ) -> Self {
        Self {
            butler,
            thermal,
            paused: Arc::new(AtomicBool::new(false)),
            stopped: AtomicBool::new(false),
        }
    }

    /// `true` while the device is thermally throttled
    /// ([`ThermalState::Serious`] or [`ThermalState::Critical`]). Callers that
    /// queue inference work should check this before submitting. 1:1 with the C#
    /// `IsPaused`.
    pub fn is_paused(&self) -> bool {
        self.paused.load(Ordering::SeqCst)
    }

    /// Starts the butler (model load + optional warm-up) and, when a thermal
    /// service is available, begins monitoring device temperature. 1:1 with the
    /// C# `StartAsync`.
    pub fn start(&self) -> Result<(), HostingError> {
        if let Some(thermal) = self.thermal.as_ref() {
            let paused = Arc::clone(&self.paused);
            thermal.on_state_changed(Arc::new(move |new_state: ThermalState| {
                let should_pause = new_state >= ThermalState::Serious;
                // Mirror the C# transition logic: only toggle on an actual edge.
                if should_pause && !paused.load(Ordering::SeqCst) {
                    paused.store(true, Ordering::SeqCst);
                } else if !should_pause && paused.load(Ordering::SeqCst) {
                    paused.store(false, Ordering::SeqCst);
                }
            }));
            thermal.start_monitoring();
        }

        self.butler.start()
    }

    /// Stops thermal monitoring and the butler in order. Safe to call multiple
    /// times — subsequent calls are no-ops. 1:1 with the C# `StopAsync`
    /// (which uses `Interlocked.CompareExchange` to guard double-stop).
    pub fn stop(&self) -> Result<(), HostingError> {
        // Compare-and-swap running(false) → stopped(true); bail if already set.
        if self
            .stopped
            .compare_exchange(false, true, Ordering::SeqCst, Ordering::SeqCst)
            .is_err()
        {
            return Ok(());
        }

        if let Some(thermal) = self.thermal.as_ref() {
            thermal.stop_monitoring();
        }

        self.butler.stop()
    }
}
