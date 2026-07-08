//! scheduled_task_store.rs
//!
//! Persistence contract for B! cron jobs (Track 3). Ported from
//! `IScheduledTaskStore.cs` + `InMemoryScheduledTaskStore.cs`. The C# store is
//! async; this port is synchronous per the crate convention. `get_due_jobs`
//! takes an explicit `now` so callers get deterministic behaviour (the C#
//! `GetDueJobsAsync` reads `DateTimeOffset.UtcNow` — [`InMemoryScheduledTaskStore::get_due_jobs_now`]
//! preserves that ergonomic).

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

use super::cron_models::CronJob;

/// Persistence abstraction for [`CronJob`] records. Implementations may be
/// in-memory, SQLite, or any other backing store, and must be thread-safe.
pub trait IScheduledTaskStore: Send + Sync {
    /// Returns every registered job, regardless of enabled/disabled state.
    fn list(&self) -> Vec<CronJob>;

    /// Returns the job with the given `id`, or `None` if not found.
    fn get(&self, id: &str) -> Option<CronJob>;

    /// Inserts or replaces the job identified by [`CronJob::id`]. Returns the
    /// stored record (identical to the input in the default implementation).
    fn upsert(&self, job: CronJob) -> CronJob;

    /// Removes the job with the given `id`. No-op if it does not exist.
    fn delete(&self, id: &str);

    /// Returns all enabled jobs whose [`CronJob::next_run_utc`] is at or before
    /// `now`.
    fn get_due_jobs(&self, now: DateTime<Utc>) -> Vec<CronJob>;
}

/// Thread-safe, in-memory implementation of [`IScheduledTaskStore`]. All state
/// is lost when the process exits. 1:1 with the C# `InMemoryScheduledTaskStore`
/// (`ConcurrentDictionary`, ordinal string keys).
#[derive(Default)]
pub struct InMemoryScheduledTaskStore {
    store: Mutex<HashMap<String, CronJob>>,
}

impl InMemoryScheduledTaskStore {
    /// Returns an empty store.
    pub fn new() -> Self {
        Self::default()
    }

    /// Convenience wrapper equivalent to the C# `GetDueJobsAsync` — uses the
    /// current wall-clock time.
    pub fn get_due_jobs_now(&self) -> Vec<CronJob> {
        self.get_due_jobs(Utc::now())
    }
}

impl IScheduledTaskStore for InMemoryScheduledTaskStore {
    fn list(&self) -> Vec<CronJob> {
        self.store.lock().unwrap().values().cloned().collect()
    }

    fn get(&self, id: &str) -> Option<CronJob> {
        assert!(!id.trim().is_empty(), "id required");
        self.store.lock().unwrap().get(id).cloned()
    }

    fn upsert(&self, job: CronJob) -> CronJob {
        self.store
            .lock()
            .unwrap()
            .insert(job.id.clone(), job.clone());
        job
    }

    fn delete(&self, id: &str) {
        assert!(!id.trim().is_empty(), "id required");
        self.store.lock().unwrap().remove(id);
    }

    fn get_due_jobs(&self, now: DateTime<Utc>) -> Vec<CronJob> {
        self.store
            .lock()
            .unwrap()
            .values()
            .filter(|j| j.is_enabled && j.next_run_utc.map(|n| n <= now).unwrap_or(false))
            .cloned()
            .collect()
    }
}
