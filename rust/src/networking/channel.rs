//! networking::channel — Rust port of `IMessageChannel.cs`, `IMeshNetwork.cs`,
//! and `IConnectivityMonitor.cs`, each with a working in-memory implementation.
//!
//!   * [`IMessageChannel`]        — typed message delivery over any transport.
//!   * [`InMemoryMessageChannel`] — JSON-serialising loopback channel with
//!     per-type routing, unbounded buffering, and subscribe-before-consume.
//!   * [`IMeshNetwork`]           — mesh topology / node identity / health.
//!   * [`InMemoryMeshNetwork`]    — a mutable in-process topology.
//!   * [`IConnectivityMonitor`]   — observes connectivity state + emits changes.
//!   * [`ManualConnectivityMonitor`] — snapshot + fan-out watch you drive by hand.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use serde::de::DeserializeOwned;
use serde::Serialize;

use super::types::{ConnectivityState, NetworkContext, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// IMessageChannel
// ─────────────────────────────────────────────────────────────────────────────

/// Typed message delivery over any transport. The C# generic
/// `SendAsync<T>/ReceiveAsync<T>` is realised here by JSON-serialising `T` under
/// a caller-supplied type key, so a single channel can carry many message types
/// without the trait itself being generic (which would make it non-object-safe).
pub trait IMessageChannel: Send + Sync {
    /// Serialise `message` and enqueue it for `destination_id` under `type_key`.
    fn send<T: Serialize>(
        &self,
        destination_id: &str,
        type_key: &str,
        message: &T,
    ) -> Result<(), MessageChannelError>;

    /// Drain and deserialise every buffered message of `type_key` addressed to
    /// this channel's node (best-effort: undeserialisable entries are dropped).
    fn receive<T: DeserializeOwned>(
        &self,
        type_key: &str,
    ) -> Result<Vec<T>, MessageChannelError>;
}

/// Errors from [`IMessageChannel`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum MessageChannelError {
    /// `serde_json` failed to serialise the outgoing message.
    Serialize(String),
}

impl std::fmt::Display for MessageChannelError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            MessageChannelError::Serialize(e) => write!(f, "message serialize error: {e}"),
        }
    }
}

impl std::error::Error for MessageChannelError {}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryMessageChannel
// ─────────────────────────────────────────────────────────────────────────────

/// One serialised message on the in-memory bus.
#[derive(Debug, Clone, PartialEq, Eq)]
struct WireMessage {
    destination_id: String,
    type_key: String,
    json: Vec<u8>,
}

/// Handler for raw inbound messages of a given type key.
type RawHandler = Arc<dyn Fn(&[u8]) + Send + Sync>;

struct ChannelCore {
    node_id: String,
    /// Unbounded inbound buffer (retains messages published before any receiver
    /// reads — matches an unbounded `Channel`).
    inbox: Mutex<VecDeque<WireMessage>>,
    /// Type-keyed push subscribers.
    subscribers: Mutex<Vec<(u64, String, RawHandler)>>,
    next_sub_id: Mutex<u64>,
}

/// In-process [`IMessageChannel`]. `send` addressed to *this* node is delivered
/// locally (buffered + fanned out); `send` addressed elsewhere is buffered under
/// its destination so a paired channel wired to the same [`InMemoryMessageBus`]
/// can pick it up. The single-node constructor is a pure loopback.
pub struct InMemoryMessageChannel {
    core: Arc<ChannelCore>,
    bus: Arc<InMemoryMessageBus>,
}

/// A shared switchboard multiple [`InMemoryMessageChannel`]s attach to, so a
/// message sent by node A to node B is routed to B's inbox. A single-node channel
/// gets its own private bus.
#[derive(Default)]
pub struct InMemoryMessageBus {
    channels: Mutex<Vec<Arc<ChannelCore>>>,
}

impl InMemoryMessageBus {
    pub fn new() -> Self {
        Self {
            channels: Mutex::new(Vec::new()),
        }
    }

    fn register(&self, core: Arc<ChannelCore>) {
        self.channels.lock().unwrap().push(core);
    }

    /// Routes `msg` to the inbox of whichever registered channel owns
    /// `destination_id`; fans out to that channel's matching push subscribers.
    fn route(&self, msg: WireMessage) {
        // Find the target core under the lock, clone the Arc, release the lock,
        // then buffer + fire outside every lock.
        let target: Option<Arc<ChannelCore>> = {
            let guard = self.channels.lock().unwrap();
            guard
                .iter()
                .find(|c| c.node_id == msg.destination_id)
                .map(Arc::clone)
        };
        if let Some(core) = target {
            core.inbox.lock().unwrap().push_back(msg.clone());
            let snapshot: Vec<RawHandler> = {
                let guard = core.subscribers.lock().unwrap();
                guard
                    .iter()
                    .filter(|(_, key, _)| *key == msg.type_key)
                    .map(|(_, _, h)| Arc::clone(h))
                    .collect()
            };
            for h in snapshot {
                h(&msg.json);
            }
        }
    }
}

/// Push-subscription handle for [`InMemoryMessageChannel::subscribe`].
pub struct ChannelSubscription {
    core: Arc<ChannelCore>,
    id: u64,
}

impl Drop for ChannelSubscription {
    fn drop(&mut self) {
        self.core
            .subscribers
            .lock()
            .unwrap()
            .retain(|(sid, _, _)| *sid != self.id);
    }
}

impl InMemoryMessageChannel {
    /// A standalone loopback channel for `node_id` (its own private bus; messages
    /// it sends to itself are delivered).
    pub fn new(node_id: impl Into<String>) -> Self {
        Self::on_bus(node_id, Arc::new(InMemoryMessageBus::new()))
    }

    /// A channel for `node_id` attached to a shared `bus`, so it can exchange
    /// messages with sibling channels on the same bus.
    pub fn on_bus(node_id: impl Into<String>, bus: Arc<InMemoryMessageBus>) -> Self {
        let core = Arc::new(ChannelCore {
            node_id: node_id.into(),
            inbox: Mutex::new(VecDeque::new()),
            subscribers: Mutex::new(Vec::new()),
            next_sub_id: Mutex::new(0),
        });
        bus.register(Arc::clone(&core));
        Self { core, bus }
    }

    /// This channel's node id.
    pub fn node_id(&self) -> &str {
        &self.core.node_id
    }

    /// Registers a raw push handler for `type_key`. Subscribe synchronously
    /// before traffic starts; the handle detaches on drop.
    pub fn subscribe(&self, type_key: impl Into<String>, handler: RawHandler) -> ChannelSubscription {
        let id = {
            let mut n = self.core.next_sub_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.core
            .subscribers
            .lock()
            .unwrap()
            .push((id, type_key.into(), handler));
        ChannelSubscription {
            core: Arc::clone(&self.core),
            id,
        }
    }

    /// Count of buffered inbound messages (any type).
    pub fn pending(&self) -> usize {
        self.core.inbox.lock().unwrap().len()
    }
}

impl IMessageChannel for InMemoryMessageChannel {
    fn send<T: Serialize>(
        &self,
        destination_id: &str,
        type_key: &str,
        message: &T,
    ) -> Result<(), MessageChannelError> {
        let json = serde_json::to_vec(message)
            .map_err(|e| MessageChannelError::Serialize(e.to_string()))?;
        let msg = WireMessage {
            destination_id: destination_id.to_string(),
            type_key: type_key.to_string(),
            json,
        };
        self.bus.route(msg);
        Ok(())
    }

    fn receive<T: DeserializeOwned>(
        &self,
        type_key: &str,
    ) -> Result<Vec<T>, MessageChannelError> {
        // Pull every buffered message of this type addressed to us; leave others
        // in the buffer for their own `receive` call.
        let mut inbox = self.core.inbox.lock().unwrap();
        let mut kept: VecDeque<WireMessage> = VecDeque::with_capacity(inbox.len());
        let mut out: Vec<T> = Vec::new();
        while let Some(msg) = inbox.pop_front() {
            if msg.type_key == type_key {
                if let Ok(v) = serde_json::from_slice::<T>(&msg.json) {
                    out.push(v);
                }
                // Undeserialisable-as-T entries are dropped (best effort).
            } else {
                kept.push_back(msg);
            }
        }
        *inbox = kept;
        Ok(out)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IMeshNetwork
// ─────────────────────────────────────────────────────────────────────────────

/// Mesh-specific surface: topology, node identity, mesh health.
pub trait IMeshNetwork: Send + Sync {
    /// This node's mesh id.
    fn local_node_id(&self) -> String;

    /// Ids of the peers currently visible in the mesh.
    fn get_peer_ids(&self) -> Vec<String>;

    /// A health snapshot for the mesh as a whole.
    fn get_mesh_health(&self) -> NetworkContext;
}

/// In-process [`IMeshNetwork`] with a mutable peer set. Health is derived: with
/// ≥1 peer the state is `MeshOnly`, otherwise `Offline`; preferred transport is
/// `Aether`; `nearby_peer_count` tracks the peer set.
pub struct InMemoryMeshNetwork {
    local_node_id: String,
    peers: Mutex<Vec<String>>,
}

impl InMemoryMeshNetwork {
    /// A mesh node with no peers.
    pub fn new(local_node_id: impl Into<String>) -> Self {
        Self {
            local_node_id: local_node_id.into(),
            peers: Mutex::new(Vec::new()),
        }
    }

    /// A mesh node seeded with `peers` (de-duplicated, blanks removed, first-seen
    /// order preserved).
    pub fn with_peers(
        local_node_id: impl Into<String>,
        peers: impl IntoIterator<Item = String>,
    ) -> Self {
        let node = Self::new(local_node_id);
        for p in peers {
            node.add_peer(p);
        }
        node
    }

    /// Adds `peer` if non-blank and not already present. Returns whether it was
    /// newly added.
    pub fn add_peer(&self, peer: impl Into<String>) -> bool {
        let peer = peer.into();
        if peer.trim().is_empty() {
            return false;
        }
        let mut peers = self.peers.lock().unwrap();
        if peers.iter().any(|p| p == &peer) {
            return false;
        }
        peers.push(peer);
        true
    }

    /// Removes `peer`; returns whether it was present.
    pub fn remove_peer(&self, peer: &str) -> bool {
        let mut peers = self.peers.lock().unwrap();
        let before = peers.len();
        peers.retain(|p| p != peer);
        peers.len() != before
    }
}

impl IMeshNetwork for InMemoryMeshNetwork {
    fn local_node_id(&self) -> String {
        self.local_node_id.clone()
    }

    fn get_peer_ids(&self) -> Vec<String> {
        self.peers.lock().unwrap().clone()
    }

    fn get_mesh_health(&self) -> NetworkContext {
        let peers = self.peers.lock().unwrap();
        let count = peers.len() as i32;
        let state = if count > 0 {
            ConnectivityState::MeshOnly
        } else {
            ConnectivityState::Offline
        };
        NetworkContext {
            state,
            preferred_transport: TransportKind::Aether,
            available_transports: if count > 0 {
                vec![TransportKind::Aether]
            } else {
                Vec::new()
            },
            signal_strength_dbm: None,
            estimated_bandwidth_bps: None,
            latency_ms: None,
            nearby_peer_count: count,
            snapshot_at: chrono::Utc::now(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IConnectivityMonitor
// ─────────────────────────────────────────────────────────────────────────────

/// Handler fired on each connectivity change.
pub type ContextHandler = Arc<dyn Fn(&NetworkContext) + Send + Sync>;

/// Observes connectivity state and emits changes. Port of the C#
/// `IConnectivityMonitor` (its `WatchAsync` async-stream becomes `watch` +
/// snapshot buffering here).
pub trait IConnectivityMonitor: Send + Sync {
    /// The current coarse state.
    fn current_state(&self) -> ConnectivityState;

    /// A full snapshot of the current context.
    fn get_snapshot(&self) -> NetworkContext;
}

struct MonitorCore {
    current: Mutex<NetworkContext>,
    watchers: Mutex<Vec<(u64, ContextHandler)>>,
    next_id: Mutex<u64>,
    /// Every emitted context in order — the buffered analogue of the async
    /// stream, so a caller that starts watching late can still replay history.
    history: Mutex<Vec<NetworkContext>>,
}

/// [`IConnectivityMonitor`] whose state you set explicitly (e.g. from a platform
/// callback in production, or a test). Each [`ManualConnectivityMonitor::update`]
/// records history and fans out to all watchers.
pub struct ManualConnectivityMonitor {
    core: Arc<MonitorCore>,
}

/// Watch handle; drop to stop watching.
pub struct WatchSubscription {
    core: Arc<MonitorCore>,
    id: u64,
}

impl Drop for WatchSubscription {
    fn drop(&mut self) {
        self.core
            .watchers
            .lock()
            .unwrap()
            .retain(|(wid, _)| *wid != self.id);
    }
}

impl ManualConnectivityMonitor {
    /// Starts at the offline context.
    pub fn new() -> Self {
        Self::with_context(NetworkContext::offline())
    }

    /// Starts at `initial`.
    pub fn with_context(initial: NetworkContext) -> Self {
        Self {
            core: Arc::new(MonitorCore {
                current: Mutex::new(initial),
                watchers: Mutex::new(Vec::new()),
                next_id: Mutex::new(0),
                history: Mutex::new(Vec::new()),
            }),
        }
    }

    /// Sets the current context, records it in history, and fans it out to every
    /// watcher (snapshot-outside-lock so a watcher may re-enter safely).
    pub fn update(&self, context: NetworkContext) {
        {
            let mut cur = self.core.current.lock().unwrap();
            *cur = context.clone();
        }
        self.core.history.lock().unwrap().push(context.clone());

        let snapshot: Vec<ContextHandler> = {
            let guard = self.core.watchers.lock().unwrap();
            guard.iter().map(|(_, h)| Arc::clone(h)).collect()
        };
        for h in snapshot {
            h(&context);
        }
    }

    /// Registers `handler` for future updates. Subscribe synchronously before
    /// driving updates so none is missed; the handle detaches on drop.
    pub fn watch(&self, handler: ContextHandler) -> WatchSubscription {
        let id = {
            let mut n = self.core.next_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.core.watchers.lock().unwrap().push((id, handler));
        WatchSubscription {
            core: Arc::clone(&self.core),
            id,
        }
    }

    /// Every context emitted so far, in order.
    pub fn history(&self) -> Vec<NetworkContext> {
        self.core.history.lock().unwrap().clone()
    }
}

impl Default for ManualConnectivityMonitor {
    fn default() -> Self {
        Self::new()
    }
}

impl IConnectivityMonitor for ManualConnectivityMonitor {
    fn current_state(&self) -> ConnectivityState {
        self.core.current.lock().unwrap().state
    }

    fn get_snapshot(&self) -> NetworkContext {
        self.core.current.lock().unwrap().clone()
    }
}
