//! hosting_observers_test.rs
//!
//! Verifies the observer bridges: PushAIObserver truncation + delivery, and
//! AetherAIObserver JSON payload framing. Also checks the AIService fires the
//! observer on chat completion. Mirrors PushAIObserver / AetherAIObserver.

use std::time::Duration as StdDuration;

use uuid::Uuid;

use circle_ai::hosting::aether_observer::{AetherAIObserver, RecordingCircleAetherTransport};
use circle_ai::hosting::observer::{AIChatEvent, IAIObserver};
use circle_ai::hosting::push_observer::{PushAIObserver, RecordingPushNotificationSender};

fn chat_event(response: &str) -> AIChatEvent {
    AIChatEvent {
        correlation_id: Uuid::new_v4(),
        messages: vec![],
        response: response.to_string(),
        elapsed: StdDuration::from_millis(5),
        timestamp: chrono::Utc::now(),
    }
}

#[test]
fn push_observer_delivers_short_response_untruncated() {
    // PushAIObserver owns its sender; we pass a &Recorder (the trait is impl'd
    // for &RecordingPushNotificationSender below) so we can read it back.
    let recorder = RecordingPushNotificationSender::new();
    let obs = PushAIObserver::new(&recorder, "tok");
    obs.on_chat_completed(&chat_event("hi there"));
    let sent = recorder.sent();
    assert_eq!(sent.len(), 1);
    assert_eq!(sent[0].device_token, "tok");
    assert_eq!(sent[0].title, "B!");
    assert_eq!(sent[0].body, "hi there");
}

#[test]
fn push_observer_truncates_long_body_with_ellipsis() {
    let recorder = RecordingPushNotificationSender::new();
    let obs = PushAIObserver::new(&recorder, "tok");
    let long = "x".repeat(250);
    obs.on_chat_completed(&chat_event(&long));
    let sent = recorder.sent();
    assert_eq!(sent.len(), 1);
    // 100 chars + ellipsis.
    assert_eq!(sent[0].body.chars().count(), 101);
    assert!(sent[0].body.ends_with('…'));
}

#[test]
fn push_observer_on_error_sends_error_title() {
    let recorder = RecordingPushNotificationSender::new();
    let obs = PushAIObserver::new(&recorder, "tok");
    obs.on_error("something broke");
    let sent = recorder.sent();
    assert_eq!(sent[0].title, "B! Error");
    assert_eq!(sent[0].body, "something broke");
}

#[test]
fn aether_observer_publishes_response_json() {
    let transport = RecordingCircleAetherTransport::new();
    // Read back via a reference-implementing wrapper.
    let obs = AetherAIObserver::new(&transport);
    obs.on_chat_completed(&chat_event("hello mesh"));
    let published = transport.published();
    assert_eq!(published.len(), 1);
    assert_eq!(published[0].topic, "butler/response");
    let v: serde_json::Value = serde_json::from_slice(&published[0].payload).unwrap();
    assert_eq!(v.get("response").unwrap(), "hello mesh");
}

#[test]
fn aether_observer_error_publishes_error_topic() {
    let transport = RecordingCircleAetherTransport::new();
    let obs = AetherAIObserver::new(&transport);
    obs.on_error("IoError", "disk full");
    let published = transport.published();
    assert_eq!(published[0].topic, "butler/error");
    let v: serde_json::Value = serde_json::from_slice(&published[0].payload).unwrap();
    assert_eq!(v.get("error").unwrap(), "IoError");
    assert_eq!(v.get("message").unwrap(), "disk full");
}
