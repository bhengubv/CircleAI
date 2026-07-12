//! agents.rs
//!
//! Port of `CircleAI.Agents.Peer/` — the agent-to-agent protocol over the Aether
//! mesh, plus its in-process reference transport.
//!
//!   * [`AgentMessage`] / [`AgentMessageKind`] — the signed, content-typed wire
//!     envelope with an auto-synthesised 32-char-hex correlation ID.
//!   * [`AgentCapability`] — a capability a peer advertises (name/version/cost).
//!   * [`PeerAgent`] — a discoverable remote agent's identity record.
//!   * [`AgentInvocationError`] — raised when a peer declines or an invoke fails.
//!   * [`IAgentPeerProtocol`] — the transport-agnostic contract (discover / greet /
//!     query-capabilities / invoke / stream-inbox).
//!   * [`AgentBus`] — an in-process coordinator that lets several
//!     [`InMemoryAgentPeerProtocol`] instances behave like a mesh (tests / samples).
//!   * [`InMemoryAgentPeerProtocol`] — the channel-backed reference implementation:
//!     discovery window, invoke-timeout, correlation-id reply matching, capability
//!     handler routing, and an external inbox stream.
//!
//! C# async maps to `#[async_trait]`. The C# `ConcurrentDictionary` maps to
//! `Mutex<HashMap<..>>`; per-peer inbox `Channel<T>` maps to `tokio::sync::mpsc`;
//! per-invocation `TaskCompletionSource<T>` maps to `tokio::sync::oneshot`; the
//! background inbox pump runs on a `tokio::task`.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use tokio::sync::{mpsc, oneshot, Mutex as AsyncMutex};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum AgentMessageKind {
    Discover = 0,
    Greet = 1,
    CapabilityQuery = 2,
    Invoke = 3,
    Response = 4,
    Decline = 5,
    Heartbeat = 6,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AgentMessage {
    pub id: Uuid,
    pub kind: AgentMessageKind,
    #[serde(rename = "fromUhid")]
    pub from_uhid: String,
    #[serde(rename = "toUhid")]
    pub to_uhid: String,
    #[serde(rename = "contentType")]
    pub content_type: String,
    pub payload: Vec<u8>,
    pub signature: Vec<u8>,
    #[serde(rename = "sentAt")]
    pub sent_at: DateTime<Utc>,
    #[serde(rename = "correlationId")]
    pub correlation_id: String,
}

impl AgentMessage {
    pub fn create(
        kind: AgentMessageKind,
        from_uhid: impl Into<String>,
        to_uhid: impl Into<String>,
        content_type: impl Into<String>,
        payload: Vec<u8>,
        signature: Vec<u8>,
        correlation_id: Option<String>,
    ) -> Self {
        let cid = match correlation_id {
            Some(c) if !c.is_empty() => c,
            _ => synth_correlation_id(),
        };
        Self {
            id: Uuid::new_v4(),
            kind,
            from_uhid: from_uhid.into(),
            to_uhid: to_uhid.into(),
            content_type: content_type.into(),
            payload,
            signature,
            sent_at: Utc::now(),
            correlation_id: cid,
        }
    }
}

fn synth_correlation_id() -> String {
    // 16 bytes of random → 32 lowercase hex chars. Matches C# / Go / Swift behaviour.
    let bytes = Uuid::new_v4().as_bytes().to_owned();
    let mut s = String::with_capacity(32);
    for b in bytes {
        use std::fmt::Write;
        let _ = write!(s, "{:02x}", b);
    }
    s
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentCapability
// ─────────────────────────────────────────────────────────────────────────────

/// A capability advertised by a [`PeerAgent`]. 1:1 with the C#
/// `sealed record AgentCapability`. `cost_per_invocation` is a `decimal` in C#;
/// the Rust port keeps it as `f64` since the crate has no decimal dependency —
/// `0.0` means free.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AgentCapability {
    /// Canonical capability name — e.g. `"translate"`, `"summarise"`, `"navigate"`.
    pub name: String,
    /// Semantic version of the capability contract.
    pub version: String,
    /// Cost in `cost_currency`. `0.0` means free.
    pub cost_per_invocation: f64,
    /// Currency code. Defaults to `"SDPKT"` within the CircleAI ecosystem.
    pub cost_currency: String,
}

impl AgentCapability {
    /// Creates a new [`AgentCapability`].
    pub fn new(
        name: impl Into<String>,
        version: impl Into<String>,
        cost_per_invocation: f64,
        cost_currency: impl Into<String>,
    ) -> Self {
        Self {
            name: name.into(),
            version: version.into(),
            cost_per_invocation,
            cost_currency: cost_currency.into(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PeerAgent
// ─────────────────────────────────────────────────────────────────────────────

/// A peer Circle AI agent discoverable over the Aether mesh. 1:1 with the C#
/// `sealed record PeerAgent`. Describes WHO another CircleAI is and HOW to reach
/// them; it does not own the connection.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PeerAgent {
    /// Local handle for this peer (stable per discovery session).
    pub id: Uuid,
    /// Hashed UHID identity reference — never raw PII. Used as the routing key.
    pub uhid_identity_id: String,
    /// User-chosen display label.
    pub display_name: String,
    /// Capabilities this peer advertises.
    pub capabilities: Vec<AgentCapability>,
    /// DER-encoded P-256 public key from the peer's UhidKeyRing.
    pub public_key_der: Vec<u8>,
    /// Transport currently carrying this peer — `"aether"`, `"wifi-direct"`,
    /// `"ble"`, `"https-relay"`, or `None` when offline.
    pub current_transport_id: Option<String>,
    /// UTC timestamp of the last message or heartbeat from this peer.
    pub last_seen_at: DateTime<Utc>,
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentInvocationError
// ─────────────────────────────────────────────────────────────────────────────

/// Raised when a peer declines an [`AgentMessageKind::Invoke`] or returns an
/// error response. Rust port of the C# `AgentInvocationException`.
#[derive(Debug, Clone)]
pub struct AgentInvocationError {
    /// Human-readable message.
    pub message: String,
    /// The peer that declined or errored, if known.
    pub peer_uhid: Option<String>,
    /// The decline envelope returned by the peer, if any.
    pub decline_message: Option<AgentMessage>,
}

impl AgentInvocationError {
    /// Creates an error with just a message.
    pub fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            peer_uhid: None,
            decline_message: None,
        }
    }

    /// Creates an error carrying the offending peer's UHID.
    pub fn with_peer(message: impl Into<String>, peer_uhid: impl Into<String>) -> Self {
        Self {
            message: message.into(),
            peer_uhid: Some(peer_uhid.into()),
            decline_message: None,
        }
    }

    /// Creates an error carrying the decline envelope returned by the peer.
    pub fn with_decline(
        message: impl Into<String>,
        peer_uhid: impl Into<String>,
        decline_message: AgentMessage,
    ) -> Self {
        Self {
            message: message.into(),
            peer_uhid: Some(peer_uhid.into()),
            decline_message: Some(decline_message),
        }
    }
}

impl std::fmt::Display for AgentInvocationError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.message)
    }
}

impl std::error::Error for AgentInvocationError {}

// ─────────────────────────────────────────────────────────────────────────────
// IAgentPeerProtocol
// ─────────────────────────────────────────────────────────────────────────────

/// Agent-to-agent protocol over the Aether mesh. Every method must be safe to
/// call from any thread. The `stream_inbox` method (an `IAsyncEnumerable` in C#)
/// is realised as a returned [`mpsc::UnboundedReceiver`] of envelopes.
#[async_trait]
pub trait IAgentPeerProtocol: Send + Sync {
    /// Listens for [`AgentMessageKind::Discover`] broadcasts and already-registered
    /// peers for a short discovery window, returning every peer observed.
    async fn discover_peers(&self) -> Vec<PeerAgent>;

    /// Initiates a handshake with `target_uhid`. Returns the peer's identity record
    /// on a successful greet, or `None` if the peer is unreachable.
    async fn greet(&self, target_uhid: &str) -> Option<PeerAgent>;

    /// Queries `target_uhid` for the capabilities it currently advertises.
    async fn query_capabilities(&self, target_uhid: &str) -> Vec<AgentCapability>;

    /// Invokes `capability` on `target_uhid` with `request_payload`. Awaits a
    /// single [`AgentMessageKind::Response`] envelope.
    async fn invoke(
        &self,
        target_uhid: &str,
        capability: &AgentCapability,
        request_payload: Vec<u8>,
    ) -> Result<AgentMessage, AgentInvocationError>;

    /// Streams every inbound [`AgentMessage`] addressed to this agent (including
    /// broadcasts where `to_uhid` is `"*"`).
    fn stream_inbox(&self) -> mpsc::UnboundedReceiver<AgentMessage>;
}

// ─────────────────────────────────────────────────────────────────────────────
// AgentBus — in-process mesh coordinator
// ─────────────────────────────────────────────────────────────────────────────

/// In-process bus used to simulate a mesh of CircleAI peers for tests and
/// samples. NOT a production transport. Owns the peer registry and one unbounded
/// channel per registered peer; `send` routes to the right inbox (or fans out on
/// broadcast). Rust port of `AgentBus.cs`.
#[derive(Clone, Default)]
pub struct AgentBus {
    peers: Arc<Mutex<HashMap<String, PeerAgent>>>,
    inboxes: Arc<Mutex<HashMap<String, mpsc::UnboundedSender<AgentMessage>>>>,
}

impl AgentBus {
    /// Creates an empty bus.
    pub fn new() -> Self {
        Self::default()
    }

    /// Snapshot of every peer currently registered on the bus.
    pub fn registered_peers(&self) -> Vec<PeerAgent> {
        self.peers.lock().unwrap().values().cloned().collect()
    }

    /// Registers `peer` on the bus and returns the receiver end of its inbox.
    /// Re-registering with the same UHID replaces the prior record and inbox.
    pub fn register(&self, peer: PeerAgent) -> mpsc::UnboundedReceiver<AgentMessage> {
        let uhid = peer.uhid_identity_id.clone();
        self.peers.lock().unwrap().insert(uhid.clone(), peer);
        let (tx, rx) = mpsc::unbounded_channel();
        self.inboxes.lock().unwrap().insert(uhid, tx);
        rx
    }

    /// Removes `uhid` from the bus and drops its inbox sender so any active
    /// receiver terminates cleanly.
    pub fn unregister(&self, uhid: &str) {
        self.peers.lock().unwrap().remove(uhid);
        self.inboxes.lock().unwrap().remove(uhid);
    }

    /// Tries to read the latest record for `uhid`.
    pub fn try_get_peer(&self, uhid: &str) -> Option<PeerAgent> {
        self.peers.lock().unwrap().get(uhid).cloned()
    }

    /// Routes `message` to its recipient(s). When `to_uhid` is `"*"` the envelope
    /// is delivered to every registered inbox except the sender's own. Messages
    /// for an unknown UHID are dropped silently (peer considered offline).
    pub fn send(&self, message: AgentMessage) {
        let inboxes = self.inboxes.lock().unwrap();
        if message.to_uhid == "*" {
            for (uhid, tx) in inboxes.iter() {
                if *uhid == message.from_uhid {
                    continue;
                }
                let _ = tx.send(message.clone());
            }
            return;
        }
        if let Some(tx) = inboxes.get(&message.to_uhid) {
            let _ = tx.send(message);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryAgentPeerProtocol — channel-backed reference implementation
// ─────────────────────────────────────────────────────────────────────────────

/// Signer closure: signs an outbound payload. `None` -> empty signature.
pub type SignerFn = Arc<dyn Fn(&[u8]) -> Vec<u8> + Send + Sync>;
/// Capability handler closure: handles an inbound `Invoke`. Returning `Some`
/// sends a `Response`; returning `None` sends a `Decline`.
pub type CapabilityHandlerFn = Arc<dyn Fn(&AgentCapability, &[u8]) -> Option<Vec<u8>> + Send + Sync>;

const DEFAULT_DISCOVERY_WINDOW_MS: u64 = 50;
const DEFAULT_INVOKE_TIMEOUT_MS: u64 = 5000;

/// In-memory reference implementation of [`IAgentPeerProtocol`]. Backed by an
/// [`AgentBus`] so multiple instances can simulate a mesh of CircleAI peers.
/// Rust port of `InMemoryAgentPeerProtocol.cs`.
pub struct InMemoryAgentPeerProtocol {
    own_uhid: String,
    bus: AgentBus,
    own_capabilities: Vec<AgentCapability>,
    signer: Option<SignerFn>,
    pending: Arc<Mutex<HashMap<Uuid, oneshot::Sender<AgentMessage>>>>,
    external_inbox_tx: mpsc::UnboundedSender<AgentMessage>,
    external_inbox_rx: AsyncMutex<Option<mpsc::UnboundedReceiver<AgentMessage>>>,
    disposed: Arc<AtomicBool>,
    pump: Mutex<Option<tokio::task::JoinHandle<()>>>,
}

impl InMemoryAgentPeerProtocol {
    /// Creates a new instance, registers it on `bus`, and begins pumping the inbox.
    pub fn new(
        own_uhid: impl Into<String>,
        bus: AgentBus,
        own_capabilities: Vec<AgentCapability>,
        own_public_key: Vec<u8>,
        signer: Option<SignerFn>,
        capability_handler: Option<CapabilityHandlerFn>,
    ) -> Arc<Self> {
        let own_uhid = own_uhid.into();
        assert!(!own_uhid.trim().is_empty(), "ownUhid required");

        // Register on the bus and take our inbox receiver.
        let inbox_rx = bus.register(PeerAgent {
            id: Uuid::new_v4(),
            uhid_identity_id: own_uhid.clone(),
            display_name: own_uhid.clone(),
            capabilities: own_capabilities.clone(),
            public_key_der: own_public_key,
            current_transport_id: Some("in-memory".to_string()),
            last_seen_at: Utc::now(),
        });

        let (ext_tx, ext_rx) = mpsc::unbounded_channel();

        let this = Arc::new(Self {
            own_uhid,
            bus: bus.clone(),
            own_capabilities,
            signer,
            pending: Arc::new(Mutex::new(HashMap::new())),
            external_inbox_tx: ext_tx,
            external_inbox_rx: AsyncMutex::new(Some(ext_rx)),
            disposed: Arc::new(AtomicBool::new(false)),
            pump: Mutex::new(None),
        });

        // Spawn the inbox pump.
        let pump_self = this.clone();
        let handle = tokio::spawn(async move {
            pump_self.pump_inbox(inbox_rx, capability_handler).await;
        });
        *this.pump.lock().unwrap() = Some(handle);

        this
    }

    /// The UHID identity owned by this agent.
    pub fn own_uhid(&self) -> &str {
        &self.own_uhid
    }

    fn sign(&self, data: &[u8]) -> Vec<u8> {
        match &self.signer {
            Some(s) => s(data),
            None => Vec::new(),
        }
    }

    // ── Inbox pump ──────────────────────────────────────────────────────────

    async fn pump_inbox(
        self: Arc<Self>,
        mut inbox: mpsc::UnboundedReceiver<AgentMessage>,
        capability_handler: Option<CapabilityHandlerFn>,
    ) {
        while let Some(message) = inbox.recv().await {
            if self.disposed.load(Ordering::SeqCst) {
                break;
            }
            self.handle_incoming(&message, &capability_handler);
            // Surface every inbound message to external consumers.
            let _ = self.external_inbox_tx.send(message);
        }
    }

    fn handle_incoming(
        &self,
        message: &AgentMessage,
        capability_handler: &Option<CapabilityHandlerFn>,
    ) {
        match message.kind {
            AgentMessageKind::Response | AgentMessageKind::Decline => {
                self.complete_pending(message);
            }
            AgentMessageKind::Invoke => {
                self.route_invoke(message, capability_handler);
            }
            _ => {}
        }
    }

    fn complete_pending(&self, message: &AgentMessage) {
        // Convention: Response/Decline carry the original Invoke's id in the first
        // 16 bytes of the payload when generated by `route_invoke`.
        if message.payload.len() < 16 {
            return;
        }
        let correlation = match Uuid::from_slice(&message.payload[0..16]) {
            Ok(id) => id,
            Err(_) => return,
        };
        if let Some(tx) = self.pending.lock().unwrap().remove(&correlation) {
            let _ = tx.send(message.clone());
        }
    }

    fn route_invoke(
        &self,
        invoke: &AgentMessage,
        capability_handler: &Option<CapabilityHandlerFn>,
    ) {
        let handler = match capability_handler {
            Some(h) => h,
            None => return,
        };

        // Best-effort: the in-memory mock hands the first advertised capability
        // to the handler (a real transport negotiates by name in the payload).
        let capability = if !self.own_capabilities.is_empty() {
            self.own_capabilities[0].clone()
        } else {
            AgentCapability::new("unknown", "0.0.0", 0.0, "SDPKT")
        };

        let result = handler(&capability, &invoke.payload);
        let correlation_prefix = invoke.id.as_bytes().to_vec();

        match result {
            None => {
                let decline = AgentMessage::create(
                    AgentMessageKind::Decline,
                    self.own_uhid.clone(),
                    invoke.from_uhid.clone(),
                    "application/octet-stream",
                    correlation_prefix.clone(),
                    self.sign(&correlation_prefix),
                    None,
                );
                self.bus.send(decline);
            }
            Some(bytes) => {
                let mut response_payload =
                    Vec::with_capacity(correlation_prefix.len() + bytes.len());
                response_payload.extend_from_slice(&correlation_prefix);
                response_payload.extend_from_slice(&bytes);
                let response = AgentMessage::create(
                    AgentMessageKind::Response,
                    self.own_uhid.clone(),
                    invoke.from_uhid.clone(),
                    "application/octet-stream",
                    response_payload.clone(),
                    self.sign(&response_payload),
                    None,
                );
                self.bus.send(response);
            }
        }
    }

    fn with_last_seen(&self, peer: PeerAgent) -> PeerAgent {
        // The C# reference tracks last-seen per peer; the in-memory port keeps the
        // peer's own last_seen_at (bus records are refreshed on register).
        peer
    }
}

impl Drop for InMemoryAgentPeerProtocol {
    fn drop(&mut self) {
        if self.disposed.swap(true, Ordering::SeqCst) {
            return;
        }
        self.bus.unregister(&self.own_uhid);
        if let Some(handle) = self.pump.lock().unwrap().take() {
            handle.abort();
        }
    }
}

#[async_trait]
impl IAgentPeerProtocol for InMemoryAgentPeerProtocol {
    async fn discover_peers(&self) -> Vec<PeerAgent> {
        // Broadcast a Discover so peers can refresh their view of us.
        let announcement = AgentMessage::create(
            AgentMessageKind::Discover,
            self.own_uhid.clone(),
            "*",
            "application/json",
            Vec::new(),
            self.sign(&[]),
            None,
        );
        self.bus.send(announcement);

        // Brief listen window so any registered peer's responses can land.
        tokio::time::sleep(std::time::Duration::from_millis(DEFAULT_DISCOVERY_WINDOW_MS)).await;

        self.bus
            .registered_peers()
            .into_iter()
            .filter(|p| p.uhid_identity_id != self.own_uhid)
            .map(|p| self.with_last_seen(p))
            .collect()
    }

    async fn greet(&self, target_uhid: &str) -> Option<PeerAgent> {
        assert!(!target_uhid.trim().is_empty(), "targetUhid required");
        let peer = self.bus.try_get_peer(target_uhid)?;

        let greet = AgentMessage::create(
            AgentMessageKind::Greet,
            self.own_uhid.clone(),
            target_uhid.to_string(),
            "application/json",
            Vec::new(),
            self.sign(&[]),
            None,
        );
        self.bus.send(greet);

        Some(self.with_last_seen(peer))
    }

    async fn query_capabilities(&self, target_uhid: &str) -> Vec<AgentCapability> {
        assert!(!target_uhid.trim().is_empty(), "targetUhid required");
        match self.bus.try_get_peer(target_uhid) {
            Some(peer) => peer.capabilities,
            None => Vec::new(),
        }
    }

    async fn invoke(
        &self,
        target_uhid: &str,
        capability: &AgentCapability,
        request_payload: Vec<u8>,
    ) -> Result<AgentMessage, AgentInvocationError> {
        assert!(!target_uhid.trim().is_empty(), "targetUhid required");

        if self.bus.try_get_peer(target_uhid).is_none() {
            return Err(AgentInvocationError::with_peer(
                format!("Peer '{target_uhid}' is not reachable on the current transport."),
                target_uhid,
            ));
        }

        let invoke = AgentMessage::create(
            AgentMessageKind::Invoke,
            self.own_uhid.clone(),
            target_uhid.to_string(),
            "application/octet-stream",
            request_payload.clone(),
            self.sign(&request_payload),
            None,
        );
        let invoke_id = invoke.id;

        let (tx, rx) = oneshot::channel();
        self.pending.lock().unwrap().insert(invoke_id, tx);

        self.bus.send(invoke);

        let reply = match tokio::time::timeout(
            std::time::Duration::from_millis(DEFAULT_INVOKE_TIMEOUT_MS),
            rx,
        )
        .await
        {
            Ok(Ok(reply)) => reply,
            // Timed out, or the sender was dropped without replying.
            _ => {
                self.pending.lock().unwrap().remove(&invoke_id);
                return Err(AgentInvocationError::with_peer(
                    format!(
                        "Invocation of '{}' on peer '{}' timed out.",
                        capability.name, target_uhid
                    ),
                    target_uhid,
                ));
            }
        };

        self.pending.lock().unwrap().remove(&invoke_id);

        if reply.kind == AgentMessageKind::Decline {
            return Err(AgentInvocationError::with_decline(
                format!("Peer '{}' declined '{}'.", target_uhid, capability.name),
                target_uhid,
                reply,
            ));
        }

        Ok(reply)
    }

    fn stream_inbox(&self) -> mpsc::UnboundedReceiver<AgentMessage> {
        // The C# reference supports multiple concurrent stream consumers; the Rust
        // port hands out the single external inbox receiver once (subsequent calls
        // get an already-closed receiver). Callers take it once and consume it.
        self.external_inbox_rx
            .try_lock()
            .ok()
            .and_then(|mut guard| guard.take())
            .unwrap_or_else(|| {
                let (_tx, rx) = mpsc::unbounded_channel();
                rx
            })
    }
}
