//! directive_publisher.rs
//!
//! Fan-out publisher for `PeerDirective`s — Rust port of `DirectivePublisher.cs`.
//!
//! Keeps a list of `IPeerDirectiveConsumer` subscriptions and fans every
//! published directive out to all current subscribers. Concurrent subscribe,
//! unsubscribe, and publish operations are all thread-safe. A snapshot is taken
//! under the lock; callbacks fire outside it (so a slow consumer can't stall a
//! publish, and a consumer that re-enters the publisher can't self-deadlock).

use std::sync::{Arc, Mutex};

use super::peer_security_types::{IPeerDirectiveConsumer, PeerDirective};

/// Manages [`IPeerDirectiveConsumer`] subscriptions and fans published
/// [`PeerDirective`] instances out to all subscribers.
#[derive(Default)]
pub struct DirectivePublisher {
    consumers: Arc<Mutex<Vec<(u64, Arc<dyn IPeerDirectiveConsumer>)>>>,
    next_id: Mutex<u64>,
}

impl DirectivePublisher {
    /// Returns an empty publisher.
    pub fn new() -> Self {
        Self {
            consumers: Arc::new(Mutex::new(Vec::new())),
            next_id: Mutex::new(0),
        }
    }

    // ─── Public API ──────────────────────────────────────────────────────────

    /// Subscribes `consumer` to receive directives. Drop the returned handle to
    /// unsubscribe. Idempotent disposal.
    pub fn subscribe(&self, consumer: Arc<dyn IPeerDirectiveConsumer>) -> DirectiveSubscription {
        let id = {
            let mut n = self.next_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.consumers.lock().unwrap().push((id, consumer));
        let consumers = Arc::clone(&self.consumers);
        DirectiveSubscription::new(move || {
            consumers.lock().unwrap().retain(|(cid, _)| *cid != id);
        })
    }

    /// Publishes `directive` to all current subscribers. A snapshot is taken
    /// under the lock; callbacks fire outside it.
    pub fn publish(&self, directive: &PeerDirective) {
        let snapshot: Vec<Arc<dyn IPeerDirectiveConsumer>> = {
            let guard = self.consumers.lock().unwrap();
            guard.iter().map(|(_, c)| Arc::clone(c)).collect()
        };
        for c in snapshot {
            c.on_directive(directive);
        }
    }

    /// Number of currently active subscribers. Useful in tests.
    pub fn subscriber_count(&self) -> usize {
        self.consumers.lock().unwrap().len()
    }
}

/// Unsubscribe handle. Dropping it removes the associated consumer from its
/// publisher. Mirrors the C# `IDisposable` returned by `Subscribe`.
pub struct DirectiveSubscription {
    remover: Option<Box<dyn FnOnce() + Send + Sync>>,
}

impl DirectiveSubscription {
    fn new(remover: impl FnOnce() + Send + Sync + 'static) -> Self {
        Self {
            remover: Some(Box::new(remover)),
        }
    }

    /// Explicit unsubscribe (equivalent to dropping; idempotent).
    pub fn unsubscribe(mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

impl Drop for DirectiveSubscription {
    fn drop(&mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}
