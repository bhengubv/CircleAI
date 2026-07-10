//! networking_transports::wifi — Rust port of `CircleAI.Networking.WiFi`
//! (`src/CircleAI.Networking.WiFi/*.cs`).
//!
//! LAN-UDP binding of the [`crate::networking::INetworkTransport`] contract plus
//! the beacon-based [`crate::networking::IPeerDiscovery`]. Faithful ports:
//!
//!   * [`WiFiNetworkTransport`]            — `INetworkTransport` over UDP
//!     broadcast/unicast: [`DISCOVERY_PORT`] / [`DATA_PORT`] constants; `send`
//!     unicasts to a parseable destination IP on the data port, else broadcasts;
//!     the receive pump (wired at `start`) buffers inbound datagrams into an
//!     unbounded inbox. Port of the C# transport.
//!   * [`IWiFiDatagramSocket`]             — the `UdpClient` dependency (trait),
//!     with a working [`InMemoryWiFiDatagramSocket`]. Injecting it keeps the
//!     transport / discovery deterministic (no real socket).
//!   * [`WiFiPeerDiscovery`]               — `IPeerDiscovery` over UDP beacons: the
//!     `CIRCLEAI:BEACON:` magic, beacon → [`PeerInfo`] projection, and beacon
//!     framing on announce. Port of the C# `WiFiPeerDiscovery`.
//!
//! `UdpReceiveResult.Buffer` → `Vec<u8>`; `UdpReceiveResult.RemoteEndPoint.Address`
//! → the sender address `String`.

use std::collections::VecDeque;
use std::net::IpAddr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use chrono::Utc;

use crate::networking::{
    INetworkTransport, IPeerDiscovery, NetworkPayload, PeerInfo, PeerRole, TransportError,
    TransportKind,
};

/// The UDP discovery (beacon) port. Port of the C# `WiFiNetworkTransport.DiscoveryPort`.
pub const DISCOVERY_PORT: i32 = 47890;
/// The UDP data port. Port of the C# `WiFiNetworkTransport.DataPort`.
pub const DATA_PORT: i32 = 47891;

/// The beacon magic prefix. Port of the C# `WiFiPeerDiscovery.BeaconMagic`.
pub const BEACON_MAGIC: &str = "CIRCLEAI:BEACON:";

/// The broadcast address the C# uses (`IPAddress.Broadcast`).
pub const BROADCAST_ADDR: &str = "255.255.255.255";

// ─────────────────────────────────────────────────────────────────────────────
// IWiFiDatagramSocket — port of the UdpClient dependency
// ─────────────────────────────────────────────────────────────────────────────

/// One datagram the transport/discovery sends: the destination `host:port` plus
/// the bytes. Records the destination so tests can assert unicast-vs-broadcast.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WiFiDatagram {
    pub dest_host: String,
    pub dest_port: i32,
    pub data: Vec<u8>,
}

/// The UDP datagram dependency. Port of the C# `UdpClient` surface used by the
/// transport + discovery: `send` a datagram and a receive-callback path. Injecting
/// it keeps [`WiFiNetworkTransport`] / [`WiFiPeerDiscovery`] deterministic;
/// [`InMemoryWiFiDatagramSocket`] is a working implementation (no real socket).
pub trait IWiFiDatagramSocket: Send + Sync {
    /// Send `datagram`.
    fn send(&self, datagram: &WiFiDatagram) -> Result<(), TransportError>;

    /// Register the sink invoked for each inbound datagram received: a
    /// `(sender_address, payload)` pair (the C# `UdpReceiveResult`).
    fn set_inbound_sink(&self, sink: WiFiInboundSink);
}

/// The sink an [`IWiFiDatagramSocket`] pushes inbound datagrams into: the sender
/// address and the payload bytes.
pub type WiFiInboundSink = Arc<dyn Fn(String, Vec<u8>) + Send + Sync>;

/// A working, deterministic in-memory [`IWiFiDatagramSocket`]. `send` records every
/// datagram; [`InMemoryWiFiDatagramSocket::simulate_inbound`] injects a datagram as
/// if received (delivered to the sink).
#[derive(Default)]
pub struct InMemoryWiFiDatagramSocket {
    sent: Mutex<Vec<WiFiDatagram>>,
    sink: Mutex<Option<WiFiInboundSink>>,
}

impl InMemoryWiFiDatagramSocket {
    pub fn new() -> Self {
        Self::default()
    }

    /// Every datagram sent via [`IWiFiDatagramSocket::send`], in order.
    pub fn sent(&self) -> Vec<WiFiDatagram> {
        self.sent.lock().unwrap().clone()
    }

    /// Injects a datagram as if received from `sender_address`: forwarded to the
    /// inbound sink. No-op if no sink is registered.
    pub fn simulate_inbound(&self, sender_address: impl Into<String>, data: Vec<u8>) {
        // Snapshot the sink under the lock, release, then fire outside it.
        let sink = self.sink.lock().unwrap().clone();
        if let Some(sink) = sink {
            sink(sender_address.into(), data);
        }
    }
}

impl IWiFiDatagramSocket for InMemoryWiFiDatagramSocket {
    fn send(&self, datagram: &WiFiDatagram) -> Result<(), TransportError> {
        self.sent.lock().unwrap().push(datagram.clone());
        Ok(())
    }

    fn set_inbound_sink(&self, sink: WiFiInboundSink) {
        *self.sink.lock().unwrap() = Some(sink);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WiFiNetworkTransport — port of WiFiNetworkTransport.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`INetworkTransport`] using LAN UDP broadcast / unicast. Port of the C#
/// `WiFiNetworkTransport`.
///
/// `send` unicasts to `payload.destination_id` when it parses as an IP address (to
/// `(ip, DATA_PORT)`), else broadcasts to `(255.255.255.255, DATA_PORT)` — exactly
/// the C# `IPAddress.TryParse(dest, out var ip) ? unicast : broadcast` decision.
/// The receive pump (wired at `start`) buffers inbound datagrams into an unbounded
/// inbox for [`drain`]. `is_available` is `true` once started (the C#
/// `_receiver is not null`).
pub struct WiFiNetworkTransport {
    socket: Arc<dyn IWiFiDatagramSocket>,
    /// Unbounded inbound buffer — the analogue of the C#
    /// `Channel.CreateUnbounded<NetworkPayload>()`.
    inbound: Arc<Mutex<VecDeque<NetworkPayload>>>,
    completed: Arc<AtomicBool>,
    started: AtomicBool,
}

impl WiFiNetworkTransport {
    /// Builds a transport over the given UDP socket.
    pub fn new(socket: Arc<dyn IWiFiDatagramSocket>) -> Self {
        Self {
            socket,
            inbound: Arc::new(Mutex::new(VecDeque::new())),
            completed: Arc::new(AtomicBool::new(false)),
            started: AtomicBool::new(false),
        }
    }

    /// Builds the outgoing datagram for `payload` exactly as the C# does: unicast to
    /// a parseable destination IP on the data port, else a broadcast datagram.
    pub fn build_datagram(payload: &NetworkPayload) -> WiFiDatagram {
        match payload.destination_id.as_deref() {
            Some(dest) if !dest.is_empty() && dest.parse::<IpAddr>().is_ok() => WiFiDatagram {
                dest_host: dest.to_string(),
                dest_port: DATA_PORT,
                data: payload.data.clone(),
            },
            _ => WiFiDatagram {
                dest_host: BROADCAST_ADDR.to_string(),
                dest_port: DATA_PORT,
                data: payload.data.clone(),
            },
        }
    }

    /// Drains every buffered inbound payload in arrival order. Pull side of the C#
    /// `ReceiveAsync` enumerable.
    pub fn drain(&self) -> Vec<NetworkPayload> {
        self.inbound.lock().unwrap().drain(..).collect()
    }
}

impl INetworkTransport for WiFiNetworkTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::WiFi
    }

    fn is_available(&self) -> bool {
        self.started.load(Ordering::SeqCst)
    }

    fn start(&self) {
        self.completed.store(false, Ordering::SeqCst);
        self.started.store(true, Ordering::SeqCst);
        // Wire the receive pump: buffer inbound datagrams into the unbounded inbox.
        let inbox = Arc::clone(&self.inbound);
        let completed = Arc::clone(&self.completed);
        let sink: WiFiInboundSink = Arc::new(move |_sender: String, data: Vec<u8>| {
            if completed.load(Ordering::SeqCst) {
                return;
            }
            inbox.lock().unwrap().push_back(NetworkPayload::of(data));
        });
        self.socket.set_inbound_sink(sink);
    }

    fn stop(&self) {
        self.started.store(false, Ordering::SeqCst);
        self.completed.store(true, Ordering::SeqCst);
    }

    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        let datagram = Self::build_datagram(payload);
        self.socket.send(&datagram)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WiFiPeerDiscovery — port of WiFiPeerDiscovery.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`IPeerDiscovery`] over UDP beacons on the LAN. Port of the C#
/// `WiFiPeerDiscovery`.
///
/// Discovery listens for `CIRCLEAI:BEACON:{nodeId}` datagrams (injected via the
/// socket sink); each matching beacon is projected to a [`PeerInfo`] with display
/// name `WiFi/{senderAddress}`, transports `[WiFi]`, role `Peer`. `announce`
/// broadcasts the local node's beacon to `(255.255.255.255, DISCOVERY_PORT)` and
/// records it. Non-beacon datagrams are ignored (the C# `StartsWith(BeaconMagic)`
/// filter).
pub struct WiFiPeerDiscovery {
    socket: Arc<dyn IWiFiDatagramSocket>,
    /// Peers seen via beacons, keyed by node id (freshest wins).
    discovered: Arc<Mutex<Vec<PeerInfo>>>,
    announced: Mutex<Vec<PeerInfo>>,
}

impl WiFiPeerDiscovery {
    /// Builds discovery over the given UDP socket and wires the beacon sink
    /// synchronously (so a beacon arriving right after construction is not lost).
    pub fn new(socket: Arc<dyn IWiFiDatagramSocket>) -> Self {
        let discovered: Arc<Mutex<Vec<PeerInfo>>> = Arc::new(Mutex::new(Vec::new()));
        let sink_peers = Arc::clone(&discovered);
        let sink: WiFiInboundSink = Arc::new(move |sender: String, data: Vec<u8>| {
            if let Some(peer) = Self::parse_beacon(&sender, &data) {
                let mut peers = sink_peers.lock().unwrap();
                peers.retain(|p| p.node_id != peer.node_id);
                peers.push(peer);
            }
        });
        socket.set_inbound_sink(sink);
        Self {
            socket,
            discovered,
            announced: Mutex::new(Vec::new()),
        }
    }

    /// Parses a received datagram into a [`PeerInfo`] if it is a well-formed beacon
    /// (`CIRCLEAI:BEACON:{nodeId}`). Port of the C# `DiscoverAsync` beacon handling:
    /// decode UTF-8, require the magic prefix, strip it for the node id, project to
    /// a `PeerInfo` with `WiFi/{address}` display name. Returns `None` for
    /// non-beacon or non-UTF-8 datagrams.
    pub fn parse_beacon(sender_address: &str, data: &[u8]) -> Option<PeerInfo> {
        let msg = std::str::from_utf8(data).ok()?;
        if !msg.starts_with(BEACON_MAGIC) {
            return None;
        }
        let node_id = &msg[BEACON_MAGIC.len()..];
        Some(PeerInfo::new(
            node_id,
            Some(format!("WiFi/{sender_address}")),
            vec![TransportKind::WiFi],
            PeerRole::Peer,
            None,
            Utc::now(),
        ))
    }

    /// Builds the beacon datagram bytes for `node_id`: `CIRCLEAI:BEACON:{nodeId}`.
    /// Port of the C# `AnnounceAsync` `$"{BeaconMagic}{localInfo.NodeId}"`.
    pub fn build_beacon(node_id: &str) -> Vec<u8> {
        format!("{BEACON_MAGIC}{node_id}").into_bytes()
    }

    /// Everything this node has announced, in order.
    pub fn announcements(&self) -> Vec<PeerInfo> {
        self.announced.lock().unwrap().clone()
    }
}

impl IPeerDiscovery for WiFiPeerDiscovery {
    fn discover(&self) -> Vec<PeerInfo> {
        self.discovered.lock().unwrap().clone()
    }

    fn announce(&self, local_info: PeerInfo) {
        // C#: broadcast `CIRCLEAI:BEACON:{nodeId}` to (Broadcast, DiscoveryPort).
        let datagram = WiFiDatagram {
            dest_host: BROADCAST_ADDR.to_string(),
            dest_port: DISCOVERY_PORT,
            data: Self::build_beacon(&local_info.node_id),
        };
        // Best-effort send (the C# awaits SendAsync); record the announcement.
        let _ = self.socket.send(&datagram);
        self.announced.lock().unwrap().push(local_info);
    }
}
