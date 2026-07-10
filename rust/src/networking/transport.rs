//! networking::transport — Rust port of `INetworkTransport.cs` plus a working
//! in-memory loopback ([`InMemoryNetworkTransport`]).
//!
//! `INetworkTransport` is the unified send/receive abstraction for a *single*
//! transport kind. The 10 concrete transports (gRPC, WebSocket, BLE, NearLink,
//! DTN, …) each implement it; a real socket is injected behind it. This port
//! keeps the surface sync (matching the rest of the crate) and models the C#
//! `IAsyncEnumerable<NetworkPayload> ReceiveAsync` two ways:
//!
//!   * push — [`InMemoryNetworkTransport::subscribe`] registers a handler that
//!     fires for every inbound payload (fan-out, snapshot-outside-lock);
//!   * pull — [`InMemoryNetworkTransport::drain`] returns everything buffered so
//!     far as an iterator, for hosts that poll on their own cadence.
//!
//! ## Concurrency-safety contract (Wave-1 lessons applied)
//!   * Delivery snapshots the subscriber list under the lock, then releases the
//!     lock BEFORE invoking any handler — a handler that re-enters the transport
//!     (subscribe / unsubscribe / drain) cannot self-deadlock the non-reentrant
//!     `Mutex`.
//!   * The inbound buffer is UNBOUNDED, so a `receive` published before any
//!     subscriber attaches is retained and replayed to the first `drain`
//!     (mirroring an unbounded `System.Threading.Channels.Channel`). Push
//!     subscribers, by contrast, only see payloads that arrive after they
//!     subscribe (event semantics) — the buffer covers the "message published
//!     right after Start races the subscription" gap for pull consumers.

use std::collections::VecDeque;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use super::types::{NetworkPayload, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// INetworkTransport
// ─────────────────────────────────────────────────────────────────────────────

/// Unified send/receive abstraction for a single transport kind. Port of the C#
/// `INetworkTransport` interface (sync surface).
pub trait INetworkTransport: Send + Sync {
    /// Which transport this instance speaks.
    fn kind(&self) -> TransportKind;

    /// Whether the transport currently has a live path.
    fn is_available(&self) -> bool;

    /// Bring the transport up. Idempotent.
    fn start(&self);

    /// Take the transport down. Idempotent.
    fn stop(&self);

    /// Send `payload` over this transport. Errors are surfaced as
    /// [`TransportError`] rather than panicking so callers can fall back down the
    /// cascade.
    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError>;
}

/// Failure modes for [`INetworkTransport::send`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TransportError {
    /// The transport was not started (or was stopped) when a send was attempted.
    NotAvailable(TransportKind),
    /// The transport does not expose a generic byte-oriented send path — the
    /// caller must use the transport's typed channel directly. Port of the C#
    /// `GrpcNetworkTransport.SendAsync` `NotSupportedException`: gRPC streaming
    /// calls are proto-service-specific, so there is no untyped send.
    NotSupported(TransportKind, String),
}

impl std::fmt::Display for TransportError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            TransportError::NotAvailable(k) => {
                write!(f, "transport {k:?} is not available (not started)")
            }
            TransportError::NotSupported(k, msg) => {
                write!(f, "transport {k:?} does not support a generic send path: {msg}")
            }
        }
    }
}

impl std::error::Error for TransportError {}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryNetworkTransport — a working loopback
// ─────────────────────────────────────────────────────────────────────────────

/// Handler invoked for every inbound payload on an [`InMemoryNetworkTransport`].
pub type PayloadHandler = Arc<dyn Fn(&NetworkPayload) + Send + Sync>;

struct TransportCore {
    kind: TransportKind,
    started: AtomicBool,
    /// Payloads passed to [`InMemoryNetworkTransport::send`] and stamped, in send
    /// order. Also the mirror of what would traverse the wire.
    sent: Mutex<Vec<NetworkPayload>>,
    /// Unbounded inbound buffer drained by [`InMemoryNetworkTransport::drain`].
    inbox: Mutex<VecDeque<NetworkPayload>>,
    subscribers: Mutex<Vec<(u64, PayloadHandler)>>,
    next_sub_id: Mutex<u64>,
}

/// In-process [`INetworkTransport`]. A real transport would put bytes on a
/// socket; this one:
///   * records every `send` (see [`InMemoryNetworkTransport::sent`]) after
///     stamping `source_id` with `local_node_id`, and
///   * exposes [`InMemoryNetworkTransport::receive`] to inject an inbound payload
///     (fans out to subscribers + buffers for `drain`).
///
/// Loopback wiring (`send` also delivers locally) is opt-in via
/// [`InMemoryNetworkTransport::new_loopback`].
pub struct InMemoryNetworkTransport {
    core: Arc<TransportCore>,
    local_node_id: String,
    loopback: bool,
}

/// Unsubscribe handle; dropping it detaches the handler.
pub struct TransportSubscription {
    core: Arc<TransportCore>,
    id: u64,
}

impl Drop for TransportSubscription {
    fn drop(&mut self) {
        self.core
            .subscribers
            .lock()
            .unwrap()
            .retain(|(sid, _)| *sid != self.id);
    }
}

impl InMemoryNetworkTransport {
    /// A transport of `kind`, identified as `local_node_id`, that does NOT loop
    /// sends back to itself. Starts stopped.
    pub fn new(kind: TransportKind, local_node_id: impl Into<String>) -> Self {
        Self {
            core: Arc::new(TransportCore {
                kind,
                started: AtomicBool::new(false),
                sent: Mutex::new(Vec::new()),
                inbox: Mutex::new(VecDeque::new()),
                subscribers: Mutex::new(Vec::new()),
                next_sub_id: Mutex::new(0),
            }),
            local_node_id: local_node_id.into(),
            loopback: false,
        }
    }

    /// A loopback transport: every `send` is also delivered inbound (to
    /// subscribers + buffer). Useful for single-process round-trip tests.
    pub fn new_loopback(kind: TransportKind, local_node_id: impl Into<String>) -> Self {
        let mut t = Self::new(kind, local_node_id);
        t.loopback = true;
        t
    }

    /// Injects `payload` as if it arrived from the wire: fans out to subscribers
    /// (outside the lock) and buffers it for the next [`drain`]. No-op when the
    /// transport is stopped (a real transport delivers nothing while down).
    pub fn receive(&self, payload: NetworkPayload) {
        if !self.core.started.load(Ordering::SeqCst) {
            return;
        }
        self.deliver(payload);
    }

    fn deliver(&self, payload: NetworkPayload) {
        // Buffer first (unbounded) so a pull consumer that has not subscribed yet
        // still sees it.
        self.core.inbox.lock().unwrap().push_back(payload.clone());

        // Snapshot subscribers under the lock, fire OUTSIDE it.
        let snapshot: Vec<PayloadHandler> = {
            let guard = self.core.subscribers.lock().unwrap();
            guard.iter().map(|(_, h)| Arc::clone(h)).collect()
        };
        for h in snapshot {
            h(&payload);
        }
    }

    /// Registers `handler` for every future inbound payload. The returned handle
    /// unsubscribes on drop. Subscribe SYNCHRONOUSLY before driving traffic so no
    /// payload races the subscription.
    pub fn subscribe(&self, handler: PayloadHandler) -> TransportSubscription {
        let id = {
            let mut n = self.core.next_sub_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.core.subscribers.lock().unwrap().push((id, handler));
        TransportSubscription {
            core: Arc::clone(&self.core),
            id,
        }
    }

    /// Drains and returns every buffered inbound payload in arrival order,
    /// clearing the buffer. This is the pull side of the C# `ReceiveAsync`
    /// enumerable.
    pub fn drain(&self) -> Vec<NetworkPayload> {
        let mut inbox = self.core.inbox.lock().unwrap();
        inbox.drain(..).collect()
    }

    /// All payloads passed to [`send`] so far, in order (the wire mirror).
    pub fn sent(&self) -> Vec<NetworkPayload> {
        self.core.sent.lock().unwrap().clone()
    }

    /// Number of active push subscribers.
    pub fn subscriber_count(&self) -> usize {
        self.core.subscribers.lock().unwrap().len()
    }

    /// This transport's node id (stamped onto every sent payload's `source_id`).
    pub fn local_node_id(&self) -> &str {
        &self.local_node_id
    }
}

impl INetworkTransport for InMemoryNetworkTransport {
    fn kind(&self) -> TransportKind {
        self.core.kind
    }

    fn is_available(&self) -> bool {
        self.core.started.load(Ordering::SeqCst)
    }

    fn start(&self) {
        self.core.started.store(true, Ordering::SeqCst);
    }

    fn stop(&self) {
        self.core.started.store(false, Ordering::SeqCst);
    }

    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        if !self.core.started.load(Ordering::SeqCst) {
            return Err(TransportError::NotAvailable(self.core.kind));
        }
        // Stamp the origin without mutating the caller's payload (records are
        // immutable in C#; we clone-and-set).
        let stamped = payload.with_source(&self.local_node_id);
        self.core.sent.lock().unwrap().push(stamped.clone());

        if self.loopback {
            // Deliver locally as inbound (subscribe-before-fire already holds).
            self.deliver(stamped);
        }
        Ok(())
    }
}
