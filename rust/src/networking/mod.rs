//! networking — Rust port of `CircleAI.Networking` (`src/CircleAI.Networking/*.cs`).
//!
//! The transport ABSTRACTION the 10 concrete transports implement. Everything
//! here is in-memory / deterministic; a real socket is injected behind
//! [`INetworkTransport`]. Families:
//!
//! 1. **Value types** ([`types`]) — [`TransportKind`], [`ConnectivityState`],
//!    [`MessagePriority`], [`PeerRole`] enums (with [`SyncDeliveryMode`] reused
//!    from [`crate::sync`]); [`NetworkPayload`], [`NetworkContext`], [`PeerInfo`],
//!    [`SchedulingHint`], and the networking [`SyncDelta`].
//!
//! 2. **Policy** ([`policy`]) — [`INetworkPolicy`], the permissive
//!    [`DefaultNetworkPolicy`] singleton, and the fluent [`NetworkPolicyBuilder`]
//!    → [`BuiltPolicy`].
//!
//! 3. **Selection** ([`selector`]) — [`ITransportSelector`] and the default
//!    [`CascadeTransportSelector`] over [`DEFAULT_CASCADE`].
//!
//! 4. **Transport** ([`transport`]) — [`INetworkTransport`] + the loopback
//!    [`InMemoryNetworkTransport`].
//!
//! 5. **Channels & monitoring** ([`channel`]) — [`IMessageChannel`] /
//!    [`InMemoryMessageChannel`] (+ [`InMemoryMessageBus`]), [`IMeshNetwork`] /
//!    [`InMemoryMeshNetwork`], [`IConnectivityMonitor`] /
//!    [`ManualConnectivityMonitor`].
//!
//! 6. **Discovery, optimisation & sync** ([`discovery`]) — [`IPeerDiscovery`] /
//!    [`InMemoryPeerDiscovery`], [`IPayloadOptimiser`] / [`RlePayloadOptimiser`],
//!    [`ISyncChannel`] / [`InMemorySyncChannel`].

pub mod channel;
pub mod discovery;
pub mod policy;
pub mod selector;
pub mod transport;
pub mod types;

// ── Re-exports (module-flat) ─────────────────────────────────────────────────

pub use types::{
    ConnectivityState, MessagePriority, NetworkContext, NetworkPayload, PeerInfo, PeerRole,
    SchedulingHint, SyncDelta, SyncDeliveryMode, TransportKind,
};

pub use policy::{BuiltPolicy, DefaultNetworkPolicy, INetworkPolicy, NetworkPolicyBuilder};

pub use selector::{CascadeTransportSelector, ITransportSelector, DEFAULT_CASCADE};

pub use transport::{
    INetworkTransport, InMemoryNetworkTransport, PayloadHandler, TransportError,
    TransportSubscription,
};

pub use channel::{
    ChannelSubscription, ContextHandler, IConnectivityMonitor, IMeshNetwork, IMessageChannel,
    InMemoryMeshNetwork, InMemoryMessageBus, InMemoryMessageChannel, ManualConnectivityMonitor,
    MessageChannelError, WatchSubscription,
};

pub use discovery::{
    DiscoverySubscription, IPayloadOptimiser, IPeerDiscovery, ISyncChannel, InMemoryPeerDiscovery,
    InMemorySyncChannel, PeerHandler, RlePayloadOptimiser,
};
