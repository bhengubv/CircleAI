//! scheduler.rs
//!
//! The proactive scheduling contract surface + the default scheduler + the
//! null/in-memory/delegate implementations. Ported 1:1 from `Contracts.cs`,
//! `ProactiveScheduler.cs`, and `NullImplementations.cs`.
//!
//!   * [`IProactiveTaskSource`] — where tasks come from.
//!   * [`IProactiveTaskRunner`] — how one is executed.
//!   * [`IProactiveScheduler`] — when they fire (cron tick + last-run tracking +
//!     event dispatch), provided by [`ProactiveScheduler`].
//!
//! The C# runner methods are async `ValueTask`; the sync port returns the result
//! directly. The C# background `BackgroundService` loop is a host concern —
//! [`ProactiveSchedulerOptions`] carries its tunable knobs, and a host drives the
//! schedulable core by calling [`IProactiveScheduler::refresh`] then
//! [`IProactiveScheduler::tick`] on its own interval.

use std::collections::{HashMap, HashSet};
use std::sync::Mutex;

use chrono::{DateTime, Duration, Utc};

use super::cron::CronExpression;
use super::primitives::{
    ProactiveTask, ProactiveTaskLoadError, ProactiveTaskRunResult,
};

/// Trigger-time context variables (event payload, manual-invoke args, …).
pub type Variables = HashMap<String, String>;

// ─────────────────────────────────────────────────────────────────────────────
// Contracts.
// ─────────────────────────────────────────────────────────────────────────────

/// Where the active set of tasks comes from.
pub trait IProactiveTaskSource: Send + Sync {
    /// Backend self-identification — "vault-fs", "in-memory", "null".
    fn backend_id(&self) -> &str;
    /// Snapshot the current set of tasks.
    fn get_tasks(&self) -> Vec<ProactiveTask>;
    /// Any parse / load failures surfaced from the last refresh.
    fn get_errors(&self) -> Vec<ProactiveTaskLoadError>;
}

/// Executes one task.
pub trait IProactiveTaskRunner: Send + Sync {
    /// Backend self-identification — "workflow-engine", "delegate", "null".
    fn backend_id(&self) -> &str;
    /// Execute one task with optional trigger-time `variables`.
    fn run(&self, task: &ProactiveTask, variables: Option<&Variables>) -> ProactiveTaskRunResult;
}

/// The scheduling loop. Owns cron parsing + last-run tracking + event dispatch.
pub trait IProactiveScheduler: Send + Sync {
    /// Backend self-identification.
    fn backend_id(&self) -> &str;
    /// Current snapshot — populated by [`refresh`](Self::refresh).
    fn tasks(&self) -> Vec<ProactiveTask>;
    /// Any load errors from the source.
    fn load_errors(&self) -> Vec<ProactiveTaskLoadError>;
    /// Next cron firing for a task after `after`, or `None` for non-cron /
    /// unparseable triggers.
    fn get_next_run(&self, task: &ProactiveTask, after: DateTime<Utc>) -> Option<DateTime<Utc>>;
    /// Re-snapshot tasks from the source; drop state for vanished tasks.
    fn refresh(&self);
    /// Tick: run every cron task whose next-run is at-or-before `now` and that
    /// hasn't already fired for the matching minute.
    fn tick(&self, now: DateTime<Utc>);
    /// Fire every event-triggered task matching `event_name`.
    fn dispatch_event(&self, event_name: &str, variables: Option<&Variables>);
    /// One-shot manual run by task id.
    fn run_by_id(&self, id: &str, variables: Option<&Variables>) -> ProactiveTaskRunResult;
}

// ─────────────────────────────────────────────────────────────────────────────
// Default scheduler.
// ─────────────────────────────────────────────────────────────────────────────

/// Default [`IProactiveScheduler`]. Per-`(context, task-id)` last-run tracking
/// keeps multi-tenant hosts' schedules separate. 1:1 with the C#
/// `ProactiveScheduler`.
pub struct ProactiveScheduler<S: IProactiveTaskSource, R: IProactiveTaskRunner> {
    source: S,
    runner: R,
    state: Mutex<SchedulerState>,
}

#[derive(Default)]
struct SchedulerState {
    tasks: Vec<ProactiveTask>,
    errors: Vec<ProactiveTaskLoadError>,
    /// context (lower-cased) -> { task-id (lower-cased) -> last-run }.
    last_runs: HashMap<String, HashMap<String, DateTime<Utc>>>,
}

impl<S: IProactiveTaskSource, R: IProactiveTaskRunner> ProactiveScheduler<S, R> {
    /// Creates a scheduler over the given source + runner.
    pub fn new(source: S, runner: R) -> Self {
        Self {
            source,
            runner,
            state: Mutex::new(SchedulerState::default()),
        }
    }

    fn context_key(source_context: &Option<String>) -> String {
        source_context.clone().unwrap_or_default().to_lowercase()
    }

    fn mark_run(&self, task: &ProactiveTask, when: DateTime<Utc>) {
        let ctx = Self::context_key(&task.source_context);
        let mut state = self.state.lock().unwrap();
        state
            .last_runs
            .entry(ctx)
            .or_default()
            .insert(task.id.to_lowercase(), when);
    }
}

impl<S: IProactiveTaskSource, R: IProactiveTaskRunner> IProactiveScheduler
    for ProactiveScheduler<S, R>
{
    fn backend_id(&self) -> &str {
        "default"
    }

    fn tasks(&self) -> Vec<ProactiveTask> {
        self.state.lock().unwrap().tasks.clone()
    }

    fn load_errors(&self) -> Vec<ProactiveTaskLoadError> {
        self.state.lock().unwrap().errors.clone()
    }

    fn get_next_run(&self, task: &ProactiveTask, after: DateTime<Utc>) -> Option<DateTime<Utc>> {
        let cron = task.trigger.cron.as_ref()?;
        let expr = CronExpression::parse(cron).ok()?;
        expr.get_next_occurrence(after).ok()
    }

    fn refresh(&self) {
        let snapshot = self.source.get_tasks();
        let errors = self.source.get_errors();
        let mut state = self.state.lock().unwrap();
        state.tasks = snapshot;
        state.errors = errors;

        // Drop last-run state for (context, task-id) pairs no longer reported.
        let live: HashSet<(String, String)> = state
            .tasks
            .iter()
            .map(|t| (Self::context_key(&t.source_context), t.id.to_lowercase()))
            .collect();

        let ctx_keys: Vec<String> = state.last_runs.keys().cloned().collect();
        for ctx_key in ctx_keys {
            if let Some(ids) = state.last_runs.get_mut(&ctx_key) {
                let id_keys: Vec<String> = ids.keys().cloned().collect();
                for id in id_keys {
                    if !live.contains(&(ctx_key.clone(), id.clone())) {
                        ids.remove(&id);
                    }
                }
                if ids.is_empty() {
                    state.last_runs.remove(&ctx_key);
                }
            }
        }
    }

    fn tick(&self, now: DateTime<Utc>) {
        let candidates: Vec<ProactiveTask> = {
            let state = self.state.lock().unwrap();
            state
                .tasks
                .iter()
                .filter(|t| t.trigger.cron.is_some())
                .cloned()
                .collect()
        };

        for task in candidates {
            let ctx_key = Self::context_key(&task.source_context);
            let last_run = {
                let mut state = self.state.lock().unwrap();
                let map = state.last_runs.entry(ctx_key.clone()).or_default();
                map.get(&task.id.to_lowercase())
                    .copied()
                    .unwrap_or(DateTime::<Utc>::MIN_UTC)
            };

            let Some(cron) = &task.trigger.cron else {
                continue;
            };
            let Ok(expr) = CronExpression::parse(cron) else {
                // Parse error — already surfaced via load_errors. Skip.
                continue;
            };
            let anchor = if last_run == DateTime::<Utc>::MIN_UTC {
                now - Duration::minutes(1)
            } else {
                last_run
            };
            if let Ok(next) = expr.get_next_occurrence(anchor) {
                if next <= now {
                    self.runner.run(&task, None);
                    self.mark_run(&task, now);
                }
            }
        }
    }

    fn dispatch_event(&self, event_name: &str, variables: Option<&Variables>) {
        assert!(!event_name.trim().is_empty(), "eventName required");
        let matched: Vec<ProactiveTask> = {
            let state = self.state.lock().unwrap();
            state
                .tasks
                .iter()
                .filter(|t| {
                    t.trigger
                        .on_event
                        .as_ref()
                        .map(|e| e.eq_ignore_ascii_case(event_name))
                        .unwrap_or(false)
                })
                .cloned()
                .collect()
        };
        for task in matched {
            self.runner.run(&task, variables);
            self.mark_run(&task, Utc::now());
        }
    }

    fn run_by_id(&self, id: &str, variables: Option<&Variables>) -> ProactiveTaskRunResult {
        assert!(!id.trim().is_empty(), "id required");
        let task = {
            let state = self.state.lock().unwrap();
            state
                .tasks
                .iter()
                .find(|t| t.id.eq_ignore_ascii_case(id))
                .cloned()
        };
        let Some(task) = task else {
            return ProactiveTaskRunResult::failure(id, format!("No task with id '{id}'."));
        };
        let result = self.runner.run(&task, variables);
        self.mark_run(&task, Utc::now());
        result
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null / in-memory / delegate implementations.
// ─────────────────────────────────────────────────────────────────────────────

/// Empty source — no tasks, no errors.
#[derive(Debug, Default)]
pub struct NullProactiveTaskSource;

impl NullProactiveTaskSource {
    pub fn new() -> Self {
        Self
    }
}

impl IProactiveTaskSource for NullProactiveTaskSource {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn get_tasks(&self) -> Vec<ProactiveTask> {
        Vec::new()
    }
    fn get_errors(&self) -> Vec<ProactiveTaskLoadError> {
        Vec::new()
    }
}

/// Reports every run as a failure with a "no runner registered" message —
/// fail-closed so a mis-wired host notices on first fire.
#[derive(Debug, Default)]
pub struct NullProactiveTaskRunner;

impl NullProactiveTaskRunner {
    pub fn new() -> Self {
        Self
    }
}

impl IProactiveTaskRunner for NullProactiveTaskRunner {
    fn backend_id(&self) -> &str {
        "null"
    }
    fn run(&self, task: &ProactiveTask, _variables: Option<&Variables>) -> ProactiveTaskRunResult {
        ProactiveTaskRunResult::failure(
            &task.id,
            "No IProactiveTaskRunner registered; using NullProactiveTaskRunner.",
        )
    }
}

/// In-memory source for testing + simple consumers. Keyed by
/// `(source_context, id)` (both case-insensitive) so multi-tenant hosts can hold
/// the same task id in two contexts.
#[derive(Default)]
pub struct InMemoryProactiveTaskSource {
    inner: Mutex<InMemorySourceInner>,
}

#[derive(Default)]
struct InMemorySourceInner {
    by_key: HashMap<(String, String), ProactiveTask>,
    errors: Vec<ProactiveTaskLoadError>,
}

impl InMemoryProactiveTaskSource {
    /// Returns an empty source.
    pub fn new() -> Self {
        Self::default()
    }

    fn key(task: &ProactiveTask) -> (String, String) {
        (
            task.source_context.clone().unwrap_or_default().to_lowercase(),
            task.id.to_lowercase(),
        )
    }

    /// Inserts or replaces a task.
    pub fn upsert(&self, task: ProactiveTask) {
        let key = Self::key(&task);
        self.inner.lock().unwrap().by_key.insert(key, task);
    }

    /// Removes a task by id + context. Returns whether one was removed.
    pub fn remove(&self, id: &str, source_context: Option<&str>) -> bool {
        assert!(!id.trim().is_empty(), "id required");
        let key = (
            source_context.unwrap_or("").to_lowercase(),
            id.to_lowercase(),
        );
        self.inner.lock().unwrap().by_key.remove(&key).is_some()
    }

    /// Clears all tasks + errors.
    pub fn clear(&self) {
        let mut inner = self.inner.lock().unwrap();
        inner.by_key.clear();
        inner.errors.clear();
    }

    /// Records a load error.
    pub fn record_error(&self, error: ProactiveTaskLoadError) {
        self.inner.lock().unwrap().errors.push(error);
    }
}

impl IProactiveTaskSource for InMemoryProactiveTaskSource {
    fn backend_id(&self) -> &str {
        "in-memory"
    }
    fn get_tasks(&self) -> Vec<ProactiveTask> {
        self.inner.lock().unwrap().by_key.values().cloned().collect()
    }
    fn get_errors(&self) -> Vec<ProactiveTaskLoadError> {
        self.inner.lock().unwrap().errors.clone()
    }
}

/// Runner that hands every task off to a host-supplied closure.
pub struct DelegateProactiveTaskRunner {
    handler: Box<dyn Fn(&ProactiveTask, Option<&Variables>) -> ProactiveTaskRunResult + Send + Sync>,
}

impl DelegateProactiveTaskRunner {
    /// Wraps the given handler closure.
    pub fn new(
        handler: impl Fn(&ProactiveTask, Option<&Variables>) -> ProactiveTaskRunResult
            + Send
            + Sync
            + 'static,
    ) -> Self {
        Self {
            handler: Box::new(handler),
        }
    }
}

impl IProactiveTaskRunner for DelegateProactiveTaskRunner {
    fn backend_id(&self) -> &str {
        "delegate"
    }
    fn run(&self, task: &ProactiveTask, variables: Option<&Variables>) -> ProactiveTaskRunResult {
        (self.handler)(task, variables)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Background-loop knobs (the async BackgroundService itself is a host concern).
// ─────────────────────────────────────────────────────────────────────────────

/// Tunable knobs for the background tick loop. Ported from the C#
/// `ProactiveSchedulerOptions`.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ProactiveSchedulerOptions {
    /// How often the scheduler ticks. Default 1 minute.
    pub tick_interval: Duration,
    /// How often the source is re-snapshotted. Default 5 minutes.
    pub refresh_interval: Duration,
}

impl Default for ProactiveSchedulerOptions {
    fn default() -> Self {
        Self {
            tick_interval: Duration::minutes(1),
            refresh_interval: Duration::minutes(5),
        }
    }
}
