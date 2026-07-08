//! runtime.rs
//!
//! Port of `CircleAI.Memory/Runtime/` — [`CompanionRuntime`] +
//! [`CompanionRuntimeOptions`].
//!
//! `CompanionRuntime` owns the lifecycle of the memory pipeline: it starts the
//! sync engine, runs a catch-up consolidation pass on start, and exposes a single
//! ingestion entry point for multimodal artefacts.
//!
//! The C# type is an `IHostedService` with background `Task.Delay` loops that
//! periodically tick the consolidator and broadcast the sync state vector. Those
//! loops are a host concern (exactly as the proactive-scheduler port leaves its
//! `BackgroundService` loop to the host); this port keeps the tunable cadences on
//! [`CompanionRuntimeOptions`] and exposes the schedulable core —
//! [`CompanionRuntime::consolidate_now`], [`sync_now`](CompanionRuntime::sync_now),
//! and [`tick`](CompanionRuntime::tick) — for a host to drive on its own timer.

use std::sync::Arc;
use std::time::Duration;

use super::consolidation::{ConsolidationOutcome, IMemoryConsolidator, SleepKind};
use super::multimodal::{IngestOptions, IngestionResult, MediaModality, MultimodalMemoryIngester};
use crate::brain::BrainError;

use super::companion_sync::ICompanionStateSyncEngine;

// ─────────────────────────────────────────────────────────────────────────────
// CompanionRuntimeOptions
// ─────────────────────────────────────────────────────────────────────────────

/// Configuration for [`CompanionRuntime`]. All fields carry the C# defaults so a
/// host gets a working pipeline out of the box.
#[derive(Debug, Clone)]
pub struct CompanionRuntimeOptions {
    /// Cadence for the daily-tier consolidation pass. Default: every 6 hours.
    /// `Duration::ZERO` disables automatic daily ticks.
    pub daily_tick_interval: Duration,
    /// Cadence for the weekly-tier consolidation pass. Default: every 24 hours.
    pub weekly_tick_interval: Duration,
    /// Cadence for the monthly-tier (persona-delta) pass. Default: every 48 hours.
    pub monthly_tick_interval: Duration,
    /// Cadence at which the runtime broadcasts its sync state vector to peers.
    /// Default: every 5 minutes. `Duration::ZERO` disables periodic sync.
    pub sync_broadcast_interval: Duration,
    /// Initial delay before the first consolidator tick after start. Default: 30s.
    pub initial_delay: Duration,
    /// When true, `start` runs an OnDemand consolidation pass to catch up
    /// anything pending before the timer cadence kicks in. Default: true.
    pub catch_up_on_start: bool,
}

impl Default for CompanionRuntimeOptions {
    fn default() -> Self {
        Self {
            daily_tick_interval: Duration::from_secs(6 * 3600),
            weekly_tick_interval: Duration::from_secs(24 * 3600),
            monthly_tick_interval: Duration::from_secs(48 * 3600),
            sync_broadcast_interval: Duration::from_secs(5 * 60),
            initial_delay: Duration::from_secs(30),
            catch_up_on_start: true,
        }
    }
}

impl CompanionRuntimeOptions {
    pub fn new() -> Self {
        Self::default()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionRuntime
// ─────────────────────────────────────────────────────────────────────────────

/// Owns the lifecycle of the memory pipeline (consolidator, sync engine,
/// multimodal ingester) and drives the consolidation passes.
///
/// 1:1 with the C# `CompanionRuntime`, minus the host-owned background loops.
pub struct CompanionRuntime {
    consolidator: Arc<dyn IMemoryConsolidator>,
    sync_engine: Option<Arc<dyn ICompanionStateSyncEngine>>,
    ingester: Option<MultimodalMemoryIngester>,
    options: CompanionRuntimeOptions,
}

impl CompanionRuntime {
    /// Creates a runtime over a consolidator, with optional sync engine and
    /// ingester and the given options (`None` uses the C# defaults).
    pub fn new(
        consolidator: Arc<dyn IMemoryConsolidator>,
        options: Option<CompanionRuntimeOptions>,
        sync_engine: Option<Arc<dyn ICompanionStateSyncEngine>>,
        ingester: Option<MultimodalMemoryIngester>,
    ) -> Self {
        Self {
            consolidator,
            sync_engine,
            ingester,
            options: options.unwrap_or_default(),
        }
    }

    /// The active options.
    pub fn options(&self) -> &CompanionRuntimeOptions {
        &self.options
    }

    /// Starts the pipeline: subscribes the sync engine (if wired) and, when
    /// [`CompanionRuntimeOptions::catch_up_on_start`] is set, runs an OnDemand
    /// consolidation pass.
    ///
    /// Returns the catch-up outcome when one ran. A failed catch-up pass is
    /// non-fatal (the C# code logs and swallows it) and surfaces here as
    /// `Ok(None)`.
    pub fn start(&self) -> Option<ConsolidationOutcome> {
        if let Some(engine) = &self.sync_engine {
            engine.start();
        }

        if self.options.catch_up_on_start {
            match self.consolidator.tick(SleepKind::OnDemand) {
                Ok(outcome) => return Some(outcome),
                Err(_) => return None, // non-fatal, mirrors the C# catch
            }
        }
        None
    }

    /// Stops the pipeline. The sync engine unsubscribes when the owning
    /// [`CompanionStateSyncEngine`](super::companion_sync::CompanionStateSyncEngine)
    /// is dropped; nothing else to tear down for the in-memory port.
    pub fn stop(&self) {
        // No-op for the in-memory port: there are no background loops to cancel.
        // The sync engine's subscription is released on drop.
    }

    /// Triggers an OnDemand consolidation pass. Hosts call this after large
    /// chunks of new activity when they don't want to wait for the timer.
    pub fn consolidate_now(&self) -> Result<ConsolidationOutcome, BrainError> {
        self.consolidator.tick(SleepKind::OnDemand)
    }

    /// Runs a consolidation tick of a specific [`SleepKind`] — the schedulable
    /// core a host drives on its own timer in place of the C# background loops.
    pub fn tick(&self, kind: SleepKind) -> Result<ConsolidationOutcome, BrainError> {
        self.consolidator.tick(kind)
    }

    /// Forwards multimodal ingestion to the registered ingester.
    ///
    /// Returns `Err` when no ingester was wired (the runtime can be built without
    /// one for text-only hosts) — mirroring the C# `InvalidOperationException`.
    pub fn ingest_media(
        &self,
        modality: MediaModality,
        source_bytes: &[u8],
        options: IngestOptions,
    ) -> Result<IngestionResult, BrainError> {
        match &self.ingester {
            Some(ingester) => ingester.ingest(modality, source_bytes, options),
            None => Err(BrainError::new(
                "CompanionRuntime was constructed without a MultimodalMemoryIngester.",
            )),
        }
    }

    /// Forces an immediate sync broadcast. No-op when sync isn't wired.
    pub fn sync_now(&self) {
        if let Some(engine) = &self.sync_engine {
            engine.sync_now();
        }
    }
}
