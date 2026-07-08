//! triggers.rs
//!
//! Proactive reasoning trigger conditions. Ported from `ITriggerCondition.cs`,
//! `ScheduleTrigger.cs`, and `IdleTrigger.cs`. Each condition evaluates a
//! [`ProactiveContext`] snapshot and signals when B! should initiate a check-in.
//!
//! The C# `IsMetAsync` is async (`ValueTask<bool>`); the sync port returns the
//! boolean directly. `ScheduleTrigger` carries interior "last fired" state so it
//! fires at most once per calendar day — modelled with a `Mutex`.

use std::sync::Mutex;

use chrono::{DateTime, Datelike, Duration, NaiveDate, NaiveTime, Timelike, Utc};

use crate::memory::{AffectState, Goal};

/// Context snapshot passed to trigger conditions. 1:1 with the C#
/// `ProactiveContext` record. `now_utc` here is the UTC instant; triggers that
/// need local wall-clock convert it (the C# `NowUtc.LocalDateTime`); the port
/// treats "local" as UTC since the crate has no host time-zone dependency.
#[derive(Debug, Clone)]
pub struct ProactiveContext {
    /// User being evaluated.
    pub user_id: String,
    /// Current UTC time.
    pub now_utc: DateTime<Utc>,
    /// How long since the user last interacted.
    pub time_since_last_interaction: Duration,
    /// Current affect state (may be `None` if no store is configured).
    pub affect_state: Option<AffectState>,
    /// User's currently active goals.
    pub active_goals: Vec<Goal>,
}

impl ProactiveContext {
    pub fn new(
        user_id: impl Into<String>,
        now_utc: DateTime<Utc>,
        time_since_last_interaction: Duration,
        affect_state: Option<AffectState>,
        active_goals: Vec<Goal>,
    ) -> Self {
        Self {
            user_id: user_id.into(),
            now_utc,
            time_since_last_interaction,
            affect_state,
            active_goals,
        }
    }
}

/// A condition that, when true, signals B! should check in proactively.
pub trait ITriggerCondition: Send + Sync {
    /// Stable name used for logging and deduplication.
    fn name(&self) -> &str;

    /// Returns true when the condition is currently met.
    fn is_met(&self, context: &ProactiveContext) -> bool;
}

/// Fires at a specific time of day. The trigger is active for a 5-minute window
/// starting at `trigger_time` and fires at most once per calendar day. 1:1 with
/// the C# `ScheduleTrigger`.
pub struct ScheduleTrigger {
    trigger_time: NaiveTime,
    name: String,
    last_fire_date: Mutex<Option<NaiveDate>>,
}

impl ScheduleTrigger {
    /// Constructs a `ScheduleTrigger`. `name` defaults to `"schedule"`.
    pub fn new(trigger_time: NaiveTime, name: impl Into<String>) -> Self {
        Self {
            trigger_time,
            name: name.into(),
            last_fire_date: Mutex::new(None),
        }
    }

    /// Constructs with the default name `"schedule"`.
    pub fn at(trigger_time: NaiveTime) -> Self {
        Self::new(trigger_time, "schedule")
    }

    /// Time of day at which this trigger fires.
    pub fn trigger_time(&self) -> NaiveTime {
        self.trigger_time
    }
}

impl ITriggerCondition for ScheduleTrigger {
    fn name(&self) -> &str {
        &self.name
    }

    fn is_met(&self, context: &ProactiveContext) -> bool {
        // Convert NowUtc to local time for comparison — the port treats local
        // as UTC (no host TZ dependency in the crate).
        let local_now = context.now_utc;
        let local_date = local_now.date_naive();
        let local_time = local_now.time();

        let mut last_fire = self.last_fire_date.lock().unwrap();

        // Already fired today — don't fire again.
        if let Some(d) = *last_fire {
            if d == local_date {
                return false;
            }
        }

        // Check whether we are within the 5-minute window after triggerTime.
        let window_start = self.trigger_time;
        let window_end = self
            .trigger_time
            .overflowing_add_signed(Duration::minutes(5))
            .0;

        let in_window = if window_end >= window_start {
            // Normal case — window doesn't wrap midnight.
            local_time >= window_start && local_time < window_end
        } else {
            // Window wraps midnight (e.g. 23:58 + 5 min = 00:03).
            local_time >= window_start || local_time < window_end
        };

        if !in_window {
            return false;
        }

        // We are in the window — mark as fired for today and return true.
        *last_fire = Some(local_date);
        true
    }
}

/// Fires when [`ProactiveContext::time_since_last_interaction`] exceeds
/// `idle_threshold`. Useful for a warm check-in after the user has been away.
/// 1:1 with the C# `IdleTrigger` (default threshold 4 hours).
pub struct IdleTrigger {
    idle_threshold: Duration,
}

impl IdleTrigger {
    /// Constructs an `IdleTrigger`. `None` → default 4 hours.
    pub fn new(idle_threshold: Option<Duration>) -> Self {
        Self {
            idle_threshold: idle_threshold.unwrap_or_else(|| Duration::hours(4)),
        }
    }

    /// Idle threshold used by this trigger.
    pub fn idle_threshold(&self) -> Duration {
        self.idle_threshold
    }
}

impl Default for IdleTrigger {
    fn default() -> Self {
        Self::new(None)
    }
}

impl ITriggerCondition for IdleTrigger {
    fn name(&self) -> &str {
        "idle"
    }

    fn is_met(&self, context: &ProactiveContext) -> bool {
        context.time_since_last_interaction > self.idle_threshold
    }
}

// Helper used by tests + callers constructing a wall-clock trigger time.
/// Build a `NaiveTime` from hour/minute (panics on out-of-range like the C#
/// `TimeOnly` constructor throws).
pub fn time_of_day(hour: u32, minute: u32) -> NaiveTime {
    NaiveTime::from_hms_opt(hour, minute, 0).expect("valid hour/minute")
}

// Suppress unused-import warning when the crate compiles without touching
// `Timelike`/`Datelike` directly at call sites (they drive `.time()`/`.date_naive()`).
const _: fn() = || {
    let _ = |t: NaiveTime| t.hour();
    let _ = |d: DateTime<Utc>| d.day();
};
