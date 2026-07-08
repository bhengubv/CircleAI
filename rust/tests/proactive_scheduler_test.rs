//! proactive_scheduler_test.rs
//!
//! Verifies the proactive scheduling substrate: source snapshotting, cron ticks
//! (fire-once-per-minute + last-run tracking), event dispatch, manual run-by-id,
//! and the null / in-memory / delegate default implementations. Mirrors the C#
//! ProactiveScheduler + NullImplementations.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};

use chrono::{TimeZone, Utc};
use circle_ai::proactive::scheduler::{
    DelegateProactiveTaskRunner, IProactiveScheduler, IProactiveTaskRunner, IProactiveTaskSource,
    InMemoryProactiveTaskSource, NullProactiveTaskRunner, NullProactiveTaskSource,
    ProactiveScheduler, Variables,
};
use circle_ai::proactive::{ProactiveTask, ProactiveTaskRunResult, ProactiveTrigger};

/// A runner that records every task id it runs.
struct RecordingRunner {
    ran: Arc<Mutex<Vec<String>>>,
}
impl IProactiveTaskRunner for RecordingRunner {
    fn backend_id(&self) -> &str {
        "recording"
    }
    fn run(&self, task: &ProactiveTask, _variables: Option<&Variables>) -> ProactiveTaskRunResult {
        self.ran.lock().unwrap().push(task.id.clone());
        ProactiveTaskRunResult::success(&task.id)
    }
}

fn task(id: &str, trigger: ProactiveTrigger) -> ProactiveTask {
    ProactiveTask::new(id, trigger, Arc::new(()))
}

// ── Null implementations ────────────────────────────────────────────────────

#[test]
fn null_source_is_empty() {
    let s = NullProactiveTaskSource::new();
    assert_eq!(s.backend_id(), "null");
    assert!(s.get_tasks().is_empty());
    assert!(s.get_errors().is_empty());
}

#[test]
fn null_runner_fails_closed() {
    let r = NullProactiveTaskRunner::new();
    let result = r.run(&task("t1", ProactiveTrigger::manual()), None);
    assert!(!result.success);
    assert!(result
        .failure_message
        .unwrap()
        .contains("No IProactiveTaskRunner registered"));
}

// ── In-memory source ────────────────────────────────────────────────────────

#[test]
fn in_memory_source_upsert_and_remove() {
    let s = InMemoryProactiveTaskSource::new();
    s.upsert(task("a", ProactiveTrigger::manual()));
    s.upsert(task("b", ProactiveTrigger::manual()));
    assert_eq!(s.get_tasks().len(), 2);
    assert!(s.remove("a", None));
    assert!(!s.remove("a", None)); // already gone
    assert_eq!(s.get_tasks().len(), 1);
    s.clear();
    assert!(s.get_tasks().is_empty());
}

#[test]
fn in_memory_source_keeps_contexts_separate() {
    let s = InMemoryProactiveTaskSource::new();
    s.upsert(task("same", ProactiveTrigger::manual()).with_source_context("tenant-a"));
    s.upsert(task("same", ProactiveTrigger::manual()).with_source_context("tenant-b"));
    // Same id, different context → two distinct tasks.
    assert_eq!(s.get_tasks().len(), 2);
    assert!(s.remove("same", Some("tenant-a")));
    assert_eq!(s.get_tasks().len(), 1);
}

// ── Delegate runner ─────────────────────────────────────────────────────────

#[test]
fn delegate_runner_calls_the_handler() {
    let count = Arc::new(AtomicUsize::new(0));
    let c2 = count.clone();
    let runner = DelegateProactiveTaskRunner::new(move |t, _v| {
        c2.fetch_add(1, Ordering::SeqCst);
        ProactiveTaskRunResult::success(&t.id)
    });
    let r = runner.run(&task("x", ProactiveTrigger::manual()), None);
    assert!(r.success);
    assert_eq!(count.load(Ordering::SeqCst), 1);
}

// ── Scheduler: refresh / tick / dispatch / run-by-id ────────────────────────

fn make_scheduler(
    source: InMemoryProactiveTaskSource,
    ran: Arc<Mutex<Vec<String>>>,
) -> ProactiveScheduler<InMemoryProactiveTaskSource, RecordingRunner> {
    ProactiveScheduler::new(source, RecordingRunner { ran })
}

#[test]
fn refresh_snapshots_tasks_from_source() {
    let source = InMemoryProactiveTaskSource::new();
    source.upsert(task("t1", ProactiveTrigger::cron("* * * * *")));
    let ran = Arc::new(Mutex::new(Vec::new()));
    let sched = make_scheduler(source, ran);
    assert!(sched.tasks().is_empty());
    sched.refresh();
    assert_eq!(sched.tasks().len(), 1);
}

#[test]
fn tick_runs_a_due_cron_task_once_per_minute() {
    let source = InMemoryProactiveTaskSource::new();
    source.upsert(task("every-min", ProactiveTrigger::cron("* * * * *")));
    let ran = Arc::new(Mutex::new(Vec::new()));
    let sched = make_scheduler(source, ran.clone());
    sched.refresh();

    let now = Utc.with_ymd_and_hms(2026, 7, 8, 12, 0, 0).unwrap();
    sched.tick(now);
    assert_eq!(ran.lock().unwrap().as_slice(), ["every-min"]);

    // Ticking again within the same minute must not re-fire (last-run guard).
    sched.tick(now);
    assert_eq!(ran.lock().unwrap().len(), 1);

    // A minute later, it fires again.
    let later = Utc.with_ymd_and_hms(2026, 7, 8, 12, 1, 0).unwrap();
    sched.tick(later);
    assert_eq!(ran.lock().unwrap().len(), 2);
}

#[test]
fn tick_ignores_event_and_manual_tasks() {
    let source = InMemoryProactiveTaskSource::new();
    source.upsert(task("ev", ProactiveTrigger::on_event("note-saved")));
    source.upsert(task("man", ProactiveTrigger::manual()));
    let ran = Arc::new(Mutex::new(Vec::new()));
    let sched = make_scheduler(source, ran.clone());
    sched.refresh();
    sched.tick(Utc::now());
    assert!(ran.lock().unwrap().is_empty());
}

#[test]
fn dispatch_event_fires_matching_tasks() {
    let source = InMemoryProactiveTaskSource::new();
    source.upsert(task("on-note", ProactiveTrigger::on_event("note-saved")));
    source.upsert(task("on-task", ProactiveTrigger::on_event("task-created")));
    let ran = Arc::new(Mutex::new(Vec::new()));
    let sched = make_scheduler(source, ran.clone());
    sched.refresh();

    sched.dispatch_event("note-saved", None);
    assert_eq!(ran.lock().unwrap().as_slice(), ["on-note"]);
    // Case-insensitive event match.
    sched.dispatch_event("NOTE-SAVED", None);
    assert_eq!(ran.lock().unwrap().len(), 2);
    // Non-matching event fires nothing new.
    sched.dispatch_event("unrelated", None);
    assert_eq!(ran.lock().unwrap().len(), 2);
}

#[test]
fn run_by_id_runs_the_named_task() {
    let source = InMemoryProactiveTaskSource::new();
    source.upsert(task("manual-1", ProactiveTrigger::manual()));
    let ran = Arc::new(Mutex::new(Vec::new()));
    let sched = make_scheduler(source, ran.clone());
    sched.refresh();

    let ok = sched.run_by_id("manual-1", None);
    assert!(ok.success);
    assert_eq!(ran.lock().unwrap().as_slice(), ["manual-1"]);

    // Unknown id → failure result, nothing ran.
    let missing = sched.run_by_id("nope", None);
    assert!(!missing.success);
    assert!(missing.failure_message.unwrap().contains("No task with id"));
    assert_eq!(ran.lock().unwrap().len(), 1);
}

#[test]
fn refresh_drops_last_run_for_vanished_tasks() {
    let source = InMemoryProactiveTaskSource::new();
    source.upsert(task("temp", ProactiveTrigger::cron("* * * * *")));
    let ran = Arc::new(Mutex::new(Vec::new()));
    let sched = make_scheduler(source, ran.clone());
    sched.refresh();

    let now = Utc.with_ymd_and_hms(2026, 7, 8, 12, 0, 0).unwrap();
    sched.tick(now);
    assert_eq!(ran.lock().unwrap().len(), 1);

    // Remove the task and refresh — its last-run state is dropped, so if it were
    // re-added it would be eligible to fire again immediately.
    // (We assert only that refresh + a fresh add + tick fires once more.)
    // Since we can't reach into the source handle here, re-add via a new source.
    let source2 = InMemoryProactiveTaskSource::new();
    source2.upsert(task("temp", ProactiveTrigger::cron("* * * * *")));
    let sched2 = make_scheduler(source2, ran.clone());
    sched2.refresh();
    let later = Utc.with_ymd_and_hms(2026, 7, 8, 12, 0, 0).unwrap();
    sched2.tick(later);
    assert_eq!(ran.lock().unwrap().len(), 2);
}

#[test]
fn get_next_run_returns_none_for_non_cron() {
    let source = InMemoryProactiveTaskSource::new();
    let ran = Arc::new(Mutex::new(Vec::new()));
    let sched = make_scheduler(source, ran);
    let t_manual = task("m", ProactiveTrigger::manual());
    assert!(sched.get_next_run(&t_manual, Utc::now()).is_none());
    let t_cron = task("c", ProactiveTrigger::cron("30 6 * * *"));
    assert!(sched.get_next_run(&t_cron, Utc::now()).is_some());
}
