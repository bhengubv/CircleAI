//! cron_models.rs
//!
//! Domain models for B! scheduled tasks (Track 3). Ported 1:1 from
//! `CronJobModels.cs`. These types are intentionally free of any external
//! dependencies.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

/// Delivery channel for a scheduled job's output. Mirrors `DeliveryTarget`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum DeliveryTarget {
    /// Deliver via in-process `IAIObserver` callback.
    Local,
    /// Deliver via push notification (requires `IPushNotificationSender`).
    Push,
    /// Deliver as a Telegram message (requires webhook config).
    Telegram,
    /// Deliver via email (requires SMTP config).
    Email,
    /// Caller handles delivery via custom callback.
    Custom,
}

/// State of a scheduled job's last execution. Mirrors `CronJobState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum CronJobState {
    /// Job has never run.
    Pending,
    /// Job is currently executing.
    Running,
    /// Last run completed without error.
    Succeeded,
    /// Last run threw an exception or the model returned an error.
    Failed,
    /// Job has been manually paused and will not fire until re-enabled.
    Paused,
}

/// A named, recurring B! task with a cron schedule. 1:1 with the C# `CronJob`
/// record (immutable; use [`CronJob::with_*`] helpers for record-style
/// `with { ... }` updates).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CronJob {
    /// Unique job identifier.
    pub id: String,
    /// Human-readable name.
    pub name: String,
    /// The prompt B! will process on schedule.
    pub prompt: String,
    /// Cron expression (5-field: min hour dom month dow).
    pub cron_expression: String,
    /// Where to deliver the AI response.
    pub delivery: DeliveryTarget,
    /// UTC time of last run. `None` = never run.
    pub last_run_utc: Option<DateTime<Utc>>,
    /// UTC time of next scheduled run.
    pub next_run_utc: Option<DateTime<Utc>>,
    /// Current execution state.
    pub state: CronJobState,
    /// Whether this job is active.
    pub is_enabled: bool,
}

impl CronJob {
    /// Constructs a job with the record's default field values
    /// (`last_run_utc = None`, `next_run_utc = None`, `state = Pending`,
    /// `is_enabled = true`).
    pub fn new(
        id: impl Into<String>,
        name: impl Into<String>,
        prompt: impl Into<String>,
        cron_expression: impl Into<String>,
        delivery: DeliveryTarget,
    ) -> Self {
        Self {
            id: id.into(),
            name: name.into(),
            prompt: prompt.into(),
            cron_expression: cron_expression.into(),
            delivery,
            last_run_utc: None,
            next_run_utc: None,
            state: CronJobState::Pending,
            is_enabled: true,
        }
    }

    /// Record-style copy with a new `state`.
    pub fn with_state(&self, state: CronJobState) -> Self {
        let mut c = self.clone();
        c.state = state;
        c
    }

    /// Record-style copy with a new `last_run_utc`.
    pub fn with_last_run_utc(&self, last_run_utc: Option<DateTime<Utc>>) -> Self {
        let mut c = self.clone();
        c.last_run_utc = last_run_utc;
        c
    }

    /// Record-style copy with a new `next_run_utc`.
    pub fn with_next_run_utc(&self, next_run_utc: Option<DateTime<Utc>>) -> Self {
        let mut c = self.clone();
        c.next_run_utc = next_run_utc;
        c
    }

    /// Record-style copy with a new `is_enabled`.
    pub fn with_is_enabled(&self, is_enabled: bool) -> Self {
        let mut c = self.clone();
        c.is_enabled = is_enabled;
        c
    }
}
