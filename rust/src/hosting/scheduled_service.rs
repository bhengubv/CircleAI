//! scheduled_service.rs
//!
//! Background service that fires due B! cron jobs. Ported from
//! `ScheduledAIService.cs`. The C# service runs an async `Task.Delay(30s)`
//! poll loop on its own thread; the sync port exposes
//! [`ScheduledAIService::process_due_jobs`], which a host's timer drives on its
//! own cadence. Everything a single poll cycle does — mark Running, run the
//! prompt via [`IAIService::ask`], compute the next run via
//! [`CronScheduleParser`], persist Succeeded/Failed, fire the completion
//! callback — is ported 1:1 and is fully deterministic given `now`.

use std::sync::Mutex;

use chrono::{DateTime, Utc};

use super::cron_models::{CronJob, CronJobState};
use super::cron_schedule_parser::CronScheduleParser;
use super::scheduled_task_store::IScheduledTaskStore;
use super::service::IAIService;

/// The poll interval the C# service uses (30 s). Exposed so hosts driving
/// [`ScheduledAIService::process_due_jobs`] can match the reference cadence.
pub const POLL_INTERVAL_SECS: u64 = 30;

/// Emitted when a scheduled job finishes (success or failure). 1:1 with the C#
/// `JobCompletedEventArgs` record.
#[derive(Debug, Clone)]
pub struct JobCompletedEventArgs {
    /// The job that was executed (with updated state fields).
    pub job: CronJob,
    /// The AI response text, or an empty string on failure.
    pub response: String,
    /// Non-`None` when execution failed (the error message).
    pub error: Option<String>,
}

/// A completion handler: `(args)`. Mirrors the C#
/// `EventHandler<JobCompletedEventArgs>`.
pub type JobCompletedHandler = Box<dyn Fn(&JobCompletedEventArgs) + Send + Sync>;

/// Polls an [`IScheduledTaskStore`] for due [`CronJob`] records, executes them
/// via [`IAIService::ask`], and invokes the registered completion handlers.
/// 1:1 with the C# `ScheduledAIService`.
///
/// Delivery routing (push, email, Telegram, …) is intentionally left to the
/// host via [`Self::on_job_completed`] so the SDK has no platform-notification
/// dependency.
pub struct ScheduledAIService<'a> {
    butler: &'a dyn IAIService,
    store: &'a dyn IScheduledTaskStore,
    handlers: Mutex<Vec<JobCompletedHandler>>,
}

impl<'a> ScheduledAIService<'a> {
    /// Constructs the service over the given butler + store.
    pub fn new(butler: &'a dyn IAIService, store: &'a dyn IScheduledTaskStore) -> Self {
        Self {
            butler,
            store,
            handlers: Mutex::new(Vec::new()),
        }
    }

    /// Registers a handler fired whenever a job completes (success or failure).
    /// Mirrors subscribing to the C# `OnJobCompleted` event.
    pub fn on_job_completed(&self, handler: JobCompletedHandler) {
        self.handlers.lock().unwrap().push(handler);
    }

    /// Runs one poll cycle: fetch every due job at `now` and execute it in
    /// order. Returns how many jobs were executed. 1:1 with the C#
    /// `ProcessDueJobsAsync` (the C# reads `DateTimeOffset.UtcNow` internally;
    /// the port takes `now` explicitly for determinism).
    pub fn process_due_jobs(&self, now: DateTime<Utc>) -> usize {
        let due = self.store.get_due_jobs(now);
        for job in &due {
            self.execute_job(job, now);
        }
        due.len()
    }

    /// Convenience wrapper equivalent to the C# `ProcessDueJobsAsync` — uses the
    /// current wall-clock time.
    pub fn process_due_jobs_now(&self) -> usize {
        self.process_due_jobs(Utc::now())
    }

    /// Executes one job: mark Running, run the prompt, compute the next run,
    /// persist the terminal state, and fire the completion handlers. 1:1 with
    /// the C# `ExecuteJobAsync`.
    fn execute_job(&self, job: &CronJob, now: DateTime<Utc>) {
        // Mark as Running.
        let running = job.with_state(CronJobState::Running);
        self.store.upsert(running);

        let mut response = String::new();
        let mut error: Option<String> = None;

        match self.butler.ask(&job.prompt) {
            Ok(text) => response = text,
            Err(e) => error = Some(e.to_string()),
        }

        let next_run = compute_next_run(&job.cron_expression, now);
        let updated_state = if error.is_none() {
            CronJobState::Succeeded
        } else {
            CronJobState::Failed
        };

        let updated = job
            .with_last_run_utc(Some(now))
            .with_next_run_utc(next_run)
            .with_state(updated_state);

        self.store.upsert(updated.clone());

        // Fire handlers on a best-effort basis — a subscriber panic must not
        // crash the poll loop (mirrors the C# try/catch around Invoke).
        let args = JobCompletedEventArgs {
            job: updated,
            response,
            error,
        };
        let handlers = self.handlers.lock().unwrap();
        for h in handlers.iter() {
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| h(&args)));
        }
    }
}

/// Computes the next run for `cron_expression` after `after`, returning `None`
/// when the expression cannot be parsed / has no occurrence. 1:1 with the C#
/// `ComputeNextRun` (swallows the parse error).
fn compute_next_run(cron_expression: &str, after: DateTime<Utc>) -> Option<DateTime<Utc>> {
    CronScheduleParser::get_next_occurrence(cron_expression, after).ok()
}
