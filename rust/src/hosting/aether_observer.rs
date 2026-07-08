//! aether_observer.rs
//!
//! (3.3.0) `IAIObserver` bridge that publishes butler events onto the
//! CircleAether mesh transport. Ported from `AetherAIObserver.cs`. The transport
//! contract lives in this module; host packages (AetherNet, Networking.*)
//! implement it directly. The C# `PublishAsync` is async fire-and-forget; the
//! sync port calls the transport directly. Payloads are JSON, byte-identical to
//! the C# `JsonSerializer.SerializeToUtf8Bytes(new { … })`.

use serde_json::json;

use super::observer::{AIChatEvent, IAIObserver};

/// (3.3.0) Publish/subscribe transport contract for the CircleAether mesh. Host
/// packages register an implementation (AetherNet, Bluetooth, NearLink, gRPC).
/// 1:1 with the C# `ICircleAetherTransport`.
pub trait ICircleAetherTransport: Send + Sync {
    /// Publish a payload to the given topic.
    fn publish(&self, topic: &str, payload: &[u8]);
}

/// A shared reference to a transport is itself a transport — lets an
/// [`AetherAIObserver`] borrow a recorder the caller keeps a handle to.
impl<T: ICircleAetherTransport + ?Sized> ICircleAetherTransport for &T {
    fn publish(&self, topic: &str, payload: &[u8]) {
        (**self).publish(topic, payload);
    }
}

/// `IAIObserver` implementation that forwards butler events to a CircleAether
/// mesh transport. 1:1 with the C# `AetherAIObserver`.
pub struct AetherAIObserver<T: ICircleAetherTransport> {
    transport: T,
}

impl<T: ICircleAetherTransport> AetherAIObserver<T> {
    /// Constructs the observer over the given transport.
    pub fn new(transport: T) -> Self {
        Self { transport }
    }

    /// Publishes an error payload to the `butler/error` topic. Call from
    /// error-handling code that cannot surface through the standard
    /// [`IAIObserver`] lifecycle.
    ///
    /// `error_kind` mirrors the C# `ex.GetType().Name`; `message` mirrors
    /// `ex.Message`.
    pub fn on_error(&self, error_kind: &str, message: &str) {
        let payload = json!({ "error": error_kind, "message": message });
        let bytes = serde_json::to_vec(&payload).expect("json serialise");
        self.transport.publish("butler/error", &bytes);
    }
}

impl<T: ICircleAetherTransport> IAIObserver for AetherAIObserver<T> {
    fn on_chat_completed(&self, event: &AIChatEvent) {
        let payload = json!({ "response": event.response });
        let bytes = serde_json::to_vec(&payload).expect("json serialise");
        self.transport.publish("butler/response", &bytes);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Deterministic test double
// ─────────────────────────────────────────────────────────────────────────────

/// One captured publish.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PublishedMessage {
    pub topic: String,
    pub payload: Vec<u8>,
}

impl PublishedMessage {
    /// The payload decoded as UTF-8 (JSON), for assertions.
    pub fn payload_utf8(&self) -> String {
        String::from_utf8_lossy(&self.payload).into_owned()
    }
}

/// Records every publish — for tests / headless scenarios.
#[derive(Debug, Default)]
pub struct RecordingCircleAetherTransport {
    published: std::sync::Mutex<Vec<PublishedMessage>>,
}

impl RecordingCircleAetherTransport {
    pub fn new() -> Self {
        Self::default()
    }

    /// Snapshot of everything published so far.
    pub fn published(&self) -> Vec<PublishedMessage> {
        self.published.lock().unwrap().clone()
    }
}

impl ICircleAetherTransport for RecordingCircleAetherTransport {
    fn publish(&self, topic: &str, payload: &[u8]) {
        self.published.lock().unwrap().push(PublishedMessage {
            topic: topic.to_string(),
            payload: payload.to_vec(),
        });
    }
}
