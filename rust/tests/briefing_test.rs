//! briefing_test.rs
//!
//! Verifies ProactiveBriefingService: fire-time scheduling and the assemble →
//! summarise → deliver pass. Mirrors the C# ProactiveBriefingService.

use std::sync::{Arc, Mutex};

use chrono::{Duration, TimeZone, Utc};
use circle_ai::companion::briefing::*;

// ── Fakes ───────────────────────────────────────────────────────────────────

struct FakeCalendar {
    events: Vec<CalendarEvent>,
}
impl ICalendarConnector for FakeCalendar {
    fn provider_id(&self) -> String {
        "gcal".into()
    }
    fn is_configured(&self) -> bool {
        true
    }
    fn list_events(&self, _from: chrono::DateTime<Utc>, _to: chrono::DateTime<Utc>) -> Vec<CalendarEvent> {
        self.events.clone()
    }
}

struct FakeNotifier {
    delivered: Arc<Mutex<Vec<(String, String)>>>,
}
impl IBriefingNotifier for FakeNotifier {
    fn deliver(&self, headline: &str, body: &str, _address: Option<&str>) {
        self.delivered
            .lock()
            .unwrap()
            .push((headline.to_string(), body.to_string()));
    }
}

// ── time_until_next_fire ────────────────────────────────────────────────────

#[test]
fn next_fire_picks_the_soonest_upcoming_time() {
    let opts = ProactiveBriefingOptions {
        fire_times_utc: vec![Duration::hours(6) + Duration::minutes(30), Duration::hours(18)],
        ..Default::default()
    };
    let svc = ProactiveBriefingService::new(opts);
    // At 05:00 UTC the next fire is 06:30 → 90 minutes away.
    let now = Utc.with_ymd_and_hms(2026, 7, 8, 5, 0, 0).unwrap();
    let gap = svc.time_until_next_fire(now);
    assert_eq!(gap.num_minutes(), 90);
}

#[test]
fn next_fire_rolls_to_tomorrow_after_last_time() {
    let opts = ProactiveBriefingOptions {
        fire_times_utc: vec![Duration::hours(6) + Duration::minutes(30)],
        ..Default::default()
    };
    let svc = ProactiveBriefingService::new(opts);
    // At 10:00 UTC, 06:30 already passed → next is tomorrow 06:30 (20.5h).
    let now = Utc.with_ymd_and_hms(2026, 7, 8, 10, 0, 0).unwrap();
    let gap = svc.time_until_next_fire(now);
    assert_eq!(gap.num_minutes(), 20 * 60 + 30);
}

#[test]
fn next_fire_within_30s_rolls_forward() {
    let opts = ProactiveBriefingOptions {
        fire_times_utc: vec![Duration::hours(6) + Duration::minutes(30)],
        ..Default::default()
    };
    let svc = ProactiveBriefingService::new(opts);
    // At exactly 06:30, the candidate is <= now+30s → pushed to tomorrow.
    let now = Utc.with_ymd_and_hms(2026, 7, 8, 6, 30, 0).unwrap();
    let gap = svc.time_until_next_fire(now);
    assert!(gap.num_hours() >= 23);
}

#[test]
fn next_fire_defaults_to_one_hour_when_no_times() {
    let opts = ProactiveBriefingOptions {
        fire_times_utc: vec![],
        ..Default::default()
    };
    let svc = ProactiveBriefingService::new(opts);
    let gap = svc.time_until_next_fire(Utc::now());
    assert_eq!(gap.num_hours(), 1);
}

// ── fire_once ───────────────────────────────────────────────────────────────

#[test]
fn fire_once_delivers_summarised_briefing() {
    let delivered = Arc::new(Mutex::new(Vec::new()));
    let notifier = Arc::new(FakeNotifier {
        delivered: delivered.clone(),
    });
    let calendar = Arc::new(FakeCalendar {
        events: vec![CalendarEvent {
            start_utc: Utc.with_ymd_and_hms(2026, 7, 8, 9, 0, 0).unwrap(),
            title: "Standup".into(),
            location: Some("Zoom".into()),
        }],
    });
    // Summariser that just prepends a marker so we can assert it ran.
    let summariser: BriefingSummariserFn =
        Arc::new(|ctx: &str| Some(format!("SUMMARY: {} chars", ctx.len())));

    let svc = ProactiveBriefingService::new(ProactiveBriefingOptions {
        headline: "Morning".into(),
        ..Default::default()
    })
    .with_calendar(calendar)
    .with_notifier(notifier)
    .with_summariser(summariser);

    svc.fire_once();

    let out = delivered.lock().unwrap();
    assert_eq!(out.len(), 1);
    assert_eq!(out[0].0, "Morning");
    assert!(out[0].1.starts_with("SUMMARY:"));
}

#[test]
fn fire_once_no_signals_delivers_nothing() {
    let delivered = Arc::new(Mutex::new(Vec::new()));
    let notifier = Arc::new(FakeNotifier {
        delivered: delivered.clone(),
    });
    let svc = ProactiveBriefingService::new(ProactiveBriefingOptions::default())
        .with_notifier(notifier);
    svc.fire_once();
    assert!(delivered.lock().unwrap().is_empty());
}

#[test]
fn fire_once_falls_back_to_raw_context_without_summariser() {
    let delivered = Arc::new(Mutex::new(Vec::new()));
    let notifier = Arc::new(FakeNotifier {
        delivered: delivered.clone(),
    });
    let calendar = Arc::new(FakeCalendar {
        events: vec![CalendarEvent {
            start_utc: Utc::now(),
            title: "Lunch".into(),
            location: None,
        }],
    });
    let svc = ProactiveBriefingService::new(ProactiveBriefingOptions::default())
        .with_calendar(calendar)
        .with_notifier(notifier);
    svc.fire_once();
    let out = delivered.lock().unwrap();
    assert_eq!(out.len(), 1);
    // Raw context contains the calendar heading + the event line.
    assert!(out[0].1.contains("### Calendar"));
    assert!(out[0].1.contains("Lunch"));
}
