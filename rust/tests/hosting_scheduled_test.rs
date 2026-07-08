//! hosting_scheduled_test.rs
//!
//! Verifies the ScheduledAIService poll cycle: due-job detection, Running →
//! Succeeded/Failed transitions, next-run recomputation, and completion-handler
//! dispatch. Mirrors the C# ScheduledAIService.ExecuteJobAsync.

use std::sync::{Arc, Mutex};

use chrono::{TimeZone, Utc};

use circle_ai::hosting::cron_models::{CronJob, CronJobState, DeliveryTarget};
use circle_ai::hosting::scheduled_service::{JobCompletedEventArgs, ScheduledAIService};
use circle_ai::hosting::scheduled_task_store::{IScheduledTaskStore, InMemoryScheduledTaskStore};
use circle_ai::hosting::service::{AIOptions, AIService, HostingError, IAIService, IHostChatGenerator};
use circle_ai::inference::{ChatMessage, GenerationOptions};

struct EchoGen;
impl IHostChatGenerator for EchoGen {
    fn generate(&self, messages: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<String, String> {
        // Return the last user content so we can assert the prompt flowed through.
        Ok(messages
            .iter()
            .rev()
            .find(|m| m.role == "user")
            .map(|m| format!("ran: {}", m.content))
            .unwrap_or_default())
    }
    fn stream(&self, _m: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<Vec<String>, String> {
        Ok(vec![])
    }
}

/// A butler that always errors — to exercise the Failed path.
struct FailingButler;
impl IAIService for FailingButler {
    fn is_ready(&self) -> bool {
        true
    }
    fn start(&self) -> Result<(), HostingError> {
        Ok(())
    }
    fn stop(&self) -> Result<(), HostingError> {
        Ok(())
    }
    fn ask(&self, _q: &str) -> Result<String, HostingError> {
        Err(HostingError::Failed("boom".to_string()))
    }
    fn chat(&self, _m: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<String, HostingError> {
        Ok(String::new())
    }
    fn stream(&self, _m: &[ChatMessage], _o: Option<&GenerationOptions>) -> Result<Vec<String>, HostingError> {
        Ok(vec![])
    }
    fn invoke_tool(
        &self,
        _i: &circle_ai::tools::ToolInvocation,
    ) -> Result<circle_ai::tools::ToolResult, HostingError> {
        Ok(circle_ai::tools::ToolResult::failure("x", "n/a"))
    }
    fn agentic_chat(&self, _p: &str, _o: Option<&GenerationOptions>) -> Result<String, HostingError> {
        Ok(String::new())
    }
    fn submit_feedback(&self, _s: circle_ai::memory::FeedbackSignal) -> Result<(), HostingError> {
        Ok(())
    }
}

fn due_job(id: &str, cron: &str, now: chrono::DateTime<Utc>) -> CronJob {
    CronJob::new(id, "Morning brief", "Give me the news", cron, DeliveryTarget::Local)
        .with_next_run_utc(Some(now))
}

#[test]
fn process_due_jobs_marks_succeeded_and_computes_next_run() {
    let store = InMemoryScheduledTaskStore::new();
    let now = Utc.with_ymd_and_hms(2026, 7, 8, 6, 30, 0).unwrap();
    // Every-minute cron so a next run always exists.
    store.upsert(due_job("j1", "* * * * *", now));

    let butler = AIService::new(
        AIOptions {
            warm_on_start: false,
            ..AIOptions::default()
        },
        Box::new(EchoGen),
    );

    let captured: Arc<Mutex<Vec<JobCompletedEventArgs>>> = Arc::new(Mutex::new(Vec::new()));
    let cap2 = Arc::clone(&captured);
    let svc = ScheduledAIService::new(&butler, &store);
    svc.on_job_completed(Box::new(move |args| cap2.lock().unwrap().push(args.clone())));

    let count = svc.process_due_jobs(now);
    assert_eq!(count, 1);

    let stored = store.get("j1").unwrap();
    assert_eq!(stored.state, CronJobState::Succeeded);
    assert_eq!(stored.last_run_utc, Some(now));
    // Next run is the following minute.
    assert_eq!(
        stored.next_run_utc,
        Some(Utc.with_ymd_and_hms(2026, 7, 8, 6, 31, 0).unwrap())
    );

    let events = captured.lock().unwrap();
    assert_eq!(events.len(), 1);
    assert!(events[0].error.is_none());
    assert_eq!(events[0].response, "ran: Give me the news");
}

#[test]
fn failing_butler_marks_failed_and_reports_error() {
    let store = InMemoryScheduledTaskStore::new();
    let now = Utc.with_ymd_and_hms(2026, 7, 8, 6, 30, 0).unwrap();
    store.upsert(due_job("j2", "* * * * *", now));

    let butler = FailingButler;
    let svc = ScheduledAIService::new(&butler, &store);
    let count = svc.process_due_jobs(now);
    assert_eq!(count, 1);

    let stored = store.get("j2").unwrap();
    assert_eq!(stored.state, CronJobState::Failed);
    assert!(stored.next_run_utc.is_some());
}

#[test]
fn disabled_job_is_not_due() {
    let store = InMemoryScheduledTaskStore::new();
    let now = Utc.with_ymd_and_hms(2026, 7, 8, 6, 30, 0).unwrap();
    let job = due_job("j3", "* * * * *", now).with_is_enabled(false);
    store.upsert(job);

    let butler = AIService::new(
        AIOptions {
            warm_on_start: false,
            ..AIOptions::default()
        },
        Box::new(EchoGen),
    );
    let svc = ScheduledAIService::new(&butler, &store);
    assert_eq!(svc.process_due_jobs(now), 0);
}

#[test]
fn unparseable_cron_leaves_next_run_none() {
    let store = InMemoryScheduledTaskStore::new();
    let now = Utc.with_ymd_and_hms(2026, 7, 8, 6, 30, 0).unwrap();
    store.upsert(due_job("j4", "not a cron", now));

    let butler = AIService::new(
        AIOptions {
            warm_on_start: false,
            ..AIOptions::default()
        },
        Box::new(EchoGen),
    );
    let svc = ScheduledAIService::new(&butler, &store);
    svc.process_due_jobs(now);
    let stored = store.get("j4").unwrap();
    // Job ran fine but next run couldn't be computed.
    assert_eq!(stored.state, CronJobState::Succeeded);
    assert!(stored.next_run_utc.is_none());
}
