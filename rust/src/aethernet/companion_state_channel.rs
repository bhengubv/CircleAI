//! aethernet::companion_state_channel — Rust port of
//! `CircleAI.AetherNet/AetherNetCompanionStateChannel.cs`.
//!
//! Production transport for the companion-state sync engine over AetherNet's
//! messaging pipeline. Marshals [`SyncEnvelope`]s onto mesh messages:
//!
//!   send(envelope)
//!     → JSON-serialize
//!     → wrap in a mesh message with message_type = "circleai.sync.v1"
//!     → for each configured peer UHID: messaging.send(mesh_message, plaintext)
//!
//!   messaging inbound
//!     → filter message_type == "circleai.sync.v1"
//!     → skip self-loopback (sender == local node id)
//!     → JSON-deserialize the content
//!     → fire every subscribed handler
//!
//! The plaintext crossing the bus is JSON; AetherNet.Messaging applies its E2E
//! layer on top, so this channel does not know about encryption. `IMessagingService`
//! is the injected mesh seam; [`InMemoryMessagingService`] is a working loopback
//! bus for tests and single-process hosts.
//!
//! Note: the crate's `ICompanionStateChannel::subscribe` returns a concrete
//! in-process `Subscription`, so this mesh-backed channel exposes its own
//! send/subscribe surface (with [`MeshChannelSubscription`]) rather than
//! implementing that tightly-coupled trait. Behaviour matches the C# channel.

use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};
use uuid::Uuid;

use crate::memory::SyncEnvelope;

// ─────────────────────────────────────────────────────────────────────────────
// Mesh messaging seam (AetherNet.Messaging boundary)
// ─────────────────────────────────────────────────────────────────────────────

/// Delivery status of a mesh message (`AetherNet.Messaging.Models.MessageStatus`).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MessageStatus {
    Pending,
    Sent,
    Delivered,
    Failed,
}

/// A mesh message (`AetherNet.Messaging.Models.MeshMessage`). Only the fields the
/// channel populates/reads are modelled.
#[derive(Debug, Clone, PartialEq)]
pub struct MeshMessage {
    pub id: Uuid,
    pub sender_uhid: String,
    pub recipient_uhid: String,
    pub message_type: String,
    pub priority: i32,
    /// The service encrypts the plaintext `send` argument into this; on the
    /// inbound side the loopback bus carries the plaintext here (matching how the
    /// C# `OnInbound` reads `EncryptedContent` after the service decrypts).
    pub encrypted_content: Vec<u8>,
    pub status: MessageStatus,
    pub created_at: DateTime<Utc>,
}

/// Handler invoked for each inbound mesh message.
pub type MeshMessageHandler = Arc<dyn Fn(&MeshMessage) + Send + Sync>;

/// The AetherNet messaging service seam (`AetherNet.Messaging.IMessagingService`).
pub trait IMessagingService: Send + Sync {
    /// Sends `message` carrying `plaintext`. The implementation is responsible for
    /// encryption; the channel hands plaintext and lets the service place it.
    fn send(&self, message: &MeshMessage, plaintext: &[u8]);

    /// Registers an inbound handler; the returned id can later be passed to
    /// [`IMessagingService::unsubscribe`]. Mirrors the C# `MessageReceived`
    /// event subscription.
    fn subscribe_received(&self, handler: MeshMessageHandler) -> u64;

    /// Removes a previously registered inbound handler.
    fn unsubscribe(&self, id: u64);
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryMessagingService — a working loopback bus
// ─────────────────────────────────────────────────────────────────────────────

/// In-process [`IMessagingService`]. `send` immediately delivers the message
/// (with its plaintext placed in `encrypted_content`, simulating the service's
/// decrypt on the far side) to every registered inbound handler. Enough to
/// exercise the whole channel including self-loopback filtering.
#[derive(Default)]
pub struct InMemoryMessagingService {
    handlers: Arc<Mutex<Vec<(u64, MeshMessageHandler)>>>,
    next_id: Mutex<u64>,
}

impl InMemoryMessagingService {
    pub fn new() -> Self {
        Self {
            handlers: Arc::new(Mutex::new(Vec::new())),
            next_id: Mutex::new(0),
        }
    }

    /// Number of registered inbound handlers.
    pub fn handler_count(&self) -> usize {
        self.handlers.lock().unwrap().len()
    }
}

impl IMessagingService for InMemoryMessagingService {
    fn send(&self, message: &MeshMessage, plaintext: &[u8]) {
        // Simulate the service delivering the decrypted plaintext to the peer:
        // clone the message and place the plaintext where inbound handlers read.
        let mut delivered = message.clone();
        delivered.encrypted_content = plaintext.to_vec();
        delivered.status = MessageStatus::Delivered;

        let snapshot: Vec<MeshMessageHandler> = {
            let guard = self.handlers.lock().unwrap();
            guard.iter().map(|(_, h)| Arc::clone(h)).collect()
        };
        for h in snapshot {
            h(&delivered);
        }
    }

    fn subscribe_received(&self, handler: MeshMessageHandler) -> u64 {
        let id = {
            let mut n = self.next_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.handlers.lock().unwrap().push((id, handler));
        id
    }

    fn unsubscribe(&self, id: u64) {
        self.handlers.lock().unwrap().retain(|(hid, _)| *hid != id);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AetherNetCompanionStateChannel
// ─────────────────────────────────────────────────────────────────────────────

/// MessageType used to distinguish CircleAI sync envelopes from other mesh
/// traffic.
pub const SYNC_MESSAGE_TYPE: &str = "circleai.sync.v1";

type EnvelopeHandler = Arc<dyn Fn(&SyncEnvelope) + Send + Sync>;

struct ChannelCore {
    messaging: Arc<dyn IMessagingService>,
    local_node_id: String,
    peer_uhids: Vec<String>,
    handlers: Mutex<Vec<(u64, EnvelopeHandler)>>,
    next_handler_id: Mutex<u64>,
}

impl ChannelCore {
    /// Inbound path: filter type, skip self-loopback, deserialize, fan out.
    fn on_inbound(&self, msg: &MeshMessage) {
        if msg.message_type != SYNC_MESSAGE_TYPE {
            return;
        }
        if msg.sender_uhid == self.local_node_id {
            return;
        }
        if msg.encrypted_content.is_empty() {
            return;
        }
        let envelope: SyncEnvelope = match serde_json::from_slice(&msg.encrypted_content) {
            Ok(e) => e,
            Err(_) => return, // malformed — drop; sync converges next round
        };

        // Snapshot handlers, fire outside the lock.
        let snapshot: Vec<EnvelopeHandler> = {
            let guard = self.handlers.lock().unwrap();
            guard.iter().map(|(_, h)| Arc::clone(h)).collect()
        };
        for h in snapshot {
            h(&envelope);
        }
    }
}

/// AetherNet-backed companion-state channel. Subscribes to the messaging
/// service on construction; unsubscribes on [`AetherNetCompanionStateChannel::dispose`]
/// or drop.
pub struct AetherNetCompanionStateChannel {
    core: Arc<ChannelCore>,
    messaging_sub_id: u64,
    disposed: Mutex<bool>,
}

/// Unsubscribe handle for [`AetherNetCompanionStateChannel::subscribe`].
pub struct MeshChannelSubscription {
    core: Arc<ChannelCore>,
    id: u64,
}

impl Drop for MeshChannelSubscription {
    fn drop(&mut self) {
        self.core
            .handlers
            .lock()
            .unwrap()
            .retain(|(hid, _)| *hid != self.id);
    }
}

impl AetherNetCompanionStateChannel {
    /// Constructs the channel over `messaging`, identifying as `local_uhid` and
    /// broadcasting to the distinct, non-blank `peer_uhids`. Subscribes to the
    /// messaging bus immediately.
    ///
    /// # Panics
    /// Panics when `local_uhid` is blank (mirrors the C# `ArgumentException`).
    pub fn new(
        messaging: Arc<dyn IMessagingService>,
        local_uhid: impl Into<String>,
        peer_uhids: impl IntoIterator<Item = String>,
    ) -> Self {
        let local_uhid = local_uhid.into();
        assert!(!local_uhid.trim().is_empty(), "localUhid is required");

        // De-duplicate, drop blanks (preserves first-seen order like LINQ Distinct).
        let mut seen = std::collections::HashSet::new();
        let peers: Vec<String> = peer_uhids
            .into_iter()
            .filter(|p| !p.trim().is_empty())
            .filter(|p| seen.insert(p.clone()))
            .collect();

        let core = Arc::new(ChannelCore {
            messaging: Arc::clone(&messaging),
            local_node_id: local_uhid,
            peer_uhids: peers,
            handlers: Mutex::new(Vec::new()),
            next_handler_id: Mutex::new(0),
        });

        // Subscribe SYNCHRONOUSLY on construction so no inbound message is missed.
        let core_for_handler = Arc::clone(&core);
        let handler: MeshMessageHandler = Arc::new(move |msg: &MeshMessage| {
            core_for_handler.on_inbound(msg);
        });
        let messaging_sub_id = messaging.subscribe_received(handler);

        Self {
            core,
            messaging_sub_id,
            disposed: Mutex::new(false),
        }
    }

    /// This node's mesh UHID.
    pub fn local_node_id(&self) -> &str {
        &self.core.local_node_id
    }

    /// Peers this channel broadcasts to (deduplicated, blanks removed).
    pub fn peer_uhids(&self) -> &[String] {
        &self.core.peer_uhids
    }

    /// Sends `envelope` to every configured peer. No-op when no peers are
    /// configured (single-device boot).
    ///
    /// # Panics
    /// Panics when the channel has been disposed (mirrors the C#
    /// `ObjectDisposedException`).
    pub fn send(&self, envelope: &SyncEnvelope) {
        assert!(!*self.disposed.lock().unwrap(), "channel disposed");
        if self.core.peer_uhids.is_empty() {
            return;
        }
        let plaintext = serde_json::to_vec(envelope).expect("SyncEnvelope serializes");

        for peer in &self.core.peer_uhids {
            let mesh_message = MeshMessage {
                id: Uuid::new_v4(),
                sender_uhid: self.core.local_node_id.clone(),
                recipient_uhid: peer.clone(),
                message_type: SYNC_MESSAGE_TYPE.to_string(),
                priority: 5,
                encrypted_content: Vec::new(), // service encrypts the plaintext arg
                status: MessageStatus::Pending,
                created_at: Utc::now(),
            };
            self.core.messaging.send(&mesh_message, &plaintext);
        }
    }

    /// Subscribes `handler` to inbound envelopes. The returned handle
    /// unsubscribes on drop.
    ///
    /// # Panics
    /// Panics when the channel has been disposed.
    pub fn subscribe(&self, handler: EnvelopeHandler) -> MeshChannelSubscription {
        assert!(!*self.disposed.lock().unwrap(), "channel disposed");
        let id = {
            let mut n = self.core.next_handler_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.core.handlers.lock().unwrap().push((id, handler));
        MeshChannelSubscription {
            core: Arc::clone(&self.core),
            id,
        }
    }

    /// Unhooks from the messaging bus and clears handlers. Idempotent.
    pub fn dispose(&self) {
        let mut disposed = self.disposed.lock().unwrap();
        if *disposed {
            return;
        }
        *disposed = true;
        self.core.messaging.unsubscribe(self.messaging_sub_id);
        self.core.handlers.lock().unwrap().clear();
    }
}

impl Drop for AetherNetCompanionStateChannel {
    fn drop(&mut self) {
        self.dispose();
    }
}
