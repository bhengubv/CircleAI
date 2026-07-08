//! push_observer.rs
//!
//! Thin `IAIObserver` → `IPushNotificationSender` bridge. Ported from
//! `PushAIObserver.cs`. The push sender interface is defined here; a real
//! APN/FCM SDK implements it. The C# `SendAsync` is async fire-and-forget; the
//! sync port calls the sender directly.

use super::observer::{AIChatEvent, IAIObserver};

/// Max body length before truncation (matches the C# `MaxBodyLength`).
const MAX_BODY_LENGTH: usize = 100;

/// Platform-agnostic push notification sender abstraction. Implement with an
/// APN or FCM SDK for real delivery. 1:1 with the C# `IPushNotificationSender`.
pub trait IPushNotificationSender: Send + Sync {
    /// Send a push notification to the device identified by `device_token`.
    fn send(&self, device_token: &str, title: &str, body: &str);
}

/// A shared reference to a sender is itself a sender — lets a
/// [`PushAIObserver`] borrow a recorder the caller keeps a handle to.
impl<T: IPushNotificationSender + ?Sized> IPushNotificationSender for &T {
    fn send(&self, device_token: &str, title: &str, body: &str) {
        (**self).send(device_token, title, body);
    }
}

/// `IAIObserver` that delivers butler responses as push notifications via an
/// [`IPushNotificationSender`]. 1:1 with the C# `PushAIObserver`.
pub struct PushAIObserver<S: IPushNotificationSender> {
    sender: S,
    device_token: String,
}

impl<S: IPushNotificationSender> PushAIObserver<S> {
    /// Constructs the observer. Panics when `device_token` is blank (matches the
    /// C# `ArgumentException`).
    pub fn new(sender: S, device_token: impl Into<String>) -> Self {
        let device_token = device_token.into();
        assert!(
            !device_token.trim().is_empty(),
            "Device token is required."
        );
        Self {
            sender,
            device_token,
        }
    }

    /// Sends an error push notification. Call from error-handling code that
    /// cannot surface through the standard [`IAIObserver`] lifecycle.
    pub fn on_error(&self, message: &str) {
        let body = truncate_ellipsis(message, MAX_BODY_LENGTH);
        self.sender.send(&self.device_token, "B! Error", &body);
    }

    fn send_response(&self, full_response: &str) {
        let body = truncate_ellipsis(full_response, MAX_BODY_LENGTH);
        self.sender.send(&self.device_token, "B!", &body);
    }
}

impl<S: IPushNotificationSender> IAIObserver for PushAIObserver<S> {
    fn on_chat_completed(&self, event: &AIChatEvent) {
        self.send_response(&event.response);
    }
}

/// Truncates `text` to at most `max` characters, appending `…` when it was
/// longer (mirrors the C# `string.Concat(msg.AsSpan(0, Max), "…")`).
fn truncate_ellipsis(text: &str, max: usize) -> String {
    if text.chars().count() > max {
        let head: String = text.chars().take(max).collect();
        format!("{head}…")
    } else {
        text.to_string()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Deterministic test double
// ─────────────────────────────────────────────────────────────────────────────

/// One captured push notification.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SentPush {
    pub device_token: String,
    pub title: String,
    pub body: String,
}

/// Records every push it is asked to send — for tests / headless scenarios.
#[derive(Debug, Default)]
pub struct RecordingPushNotificationSender {
    sent: std::sync::Mutex<Vec<SentPush>>,
}

impl RecordingPushNotificationSender {
    pub fn new() -> Self {
        Self::default()
    }

    /// Snapshot of everything sent so far.
    pub fn sent(&self) -> Vec<SentPush> {
        self.sent.lock().unwrap().clone()
    }
}

impl IPushNotificationSender for RecordingPushNotificationSender {
    fn send(&self, device_token: &str, title: &str, body: &str) {
        self.sent.lock().unwrap().push(SentPush {
            device_token: device_token.to_string(),
            title: title.to_string(),
            body: body.to_string(),
        });
    }
}
