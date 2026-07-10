//! networking_transports::tcp — Rust port of `CircleAI.Networking.Tcp`
//! (`src/CircleAI.Networking.Tcp/*.cs`).
//!
//! Raw-TCP binding of the [`crate::networking::INetworkTransport`] contract.
//! Faithful ports:
//!
//!   * [`TcpConnectionState`]              — port of the C# enum.
//!   * [`TcpEndpointDescriptor`] / [`TcpThroughputSample`] — the C# `record`s.
//!   * [`TcpKnownPorts`]                   — the static well-known-port constants.
//!   * [`InMemoryTcpConnectionRegistry`]   — endpoint table + per-id state +
//!     throughput log, matching the C# defaults / aggregation.
//!   * [`ITcpConnection`]                  — the TCP `NetworkStream` dependency
//!     (trait), with a working [`InMemoryTcpConnection`]. Injecting it keeps the
//!     transport deterministic (no real socket).
//!   * [`TcpNetworkTransport`]             — `INetworkTransport` over TCP: client
//!     mode (remote set) frames each send as `[len:i32-LE][data]` and pumps inbound
//!     frames into an unbounded inbox; listener mode (listen port set) accepts but
//!     has no stream (matching the C#, where a bare listener sends nothing).
//!
//! The wire framing matches the C# byte-for-byte: `BitConverter.GetBytes(len)` on
//! the little-endian runtime writes a 4-byte little-endian length prefix, decoded
//! by `BitConverter.ToInt32`. [`TcpNetworkTransport::frame`] / [`deframe`] preserve
//! that exact format.

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// TcpConnectionState — port of the C# enum
// ─────────────────────────────────────────────────────────────────────────────

/// Lifecycle state of a TCP connection. 1:1 with the C# `TcpConnectionState`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum TcpConnectionState {
    Disconnected,
    Connecting,
    Connected,
    Closing,
    Failed,
}

// ─────────────────────────────────────────────────────────────────────────────
// Value records
// ─────────────────────────────────────────────────────────────────────────────

/// A TCP endpoint's descriptor. Port of the C# `TcpEndpointDescriptor`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TcpEndpointDescriptor {
    pub host: String,
    pub port: i32,
    pub no_delay: bool,
    pub keep_alive: bool,
    pub connect_timeout: Duration,
}

impl TcpEndpointDescriptor {
    pub fn new(
        host: impl Into<String>,
        port: i32,
        no_delay: bool,
        keep_alive: bool,
        connect_timeout: Duration,
    ) -> Self {
        Self {
            host: host.into(),
            port,
            no_delay,
            keep_alive,
            connect_timeout,
        }
    }
}

/// A bytes-sent/received observation. Port of the C# `TcpThroughputSample`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct TcpThroughputSample {
    pub endpoint_id: String,
    pub bytes_sent: i64,
    pub bytes_received: i64,
    pub at_utc: DateTime<Utc>,
}

impl TcpThroughputSample {
    pub fn new(
        endpoint_id: impl Into<String>,
        bytes_sent: i64,
        bytes_received: i64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            endpoint_id: endpoint_id.into(),
            bytes_sent,
            bytes_received,
            at_utc,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TcpKnownPorts — port of the static class
// ─────────────────────────────────────────────────────────────────────────────

/// Well-known TCP ports. Port of the C# static `TcpKnownPorts`, values identical.
pub struct TcpKnownPorts;

impl TcpKnownPorts {
    pub const HTTP: i32 = 80;
    pub const HTTPS: i32 = 443;
    pub const SSH: i32 = 22;
    pub const SMTP: i32 = 25;
    pub const IMAP: i32 = 143;
    pub const IMAP_SSL: i32 = 993;
    pub const POP3: i32 = 110;
    pub const POP3_SSL: i32 = 995;
    pub const MQTT: i32 = 1883;
    pub const MQTT_SSL: i32 = 8883;
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryTcpConnectionRegistry — port of the C# registry
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory endpoint table + per-id connection state + throughput log. Port of
/// the C# `InMemoryTcpConnectionRegistry`.
///
/// Matches the C#:
///   * [`state`](Self::state) defaults to `Disconnected` for unknown ids.
///   * [`total_bytes_sent`](Self::total_bytes_sent) sums an endpoint's sent bytes.
#[derive(Default)]
pub struct InMemoryTcpConnectionRegistry {
    endpoints: Mutex<HashMap<String, TcpEndpointDescriptor>>,
    states: Mutex<HashMap<String, TcpConnectionState>>,
    throughput: Mutex<Vec<TcpThroughputSample>>,
}

impl InMemoryTcpConnectionRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) an endpoint keyed by `id`. Port of `Register`.
    pub fn register(&self, id: impl Into<String>, d: TcpEndpointDescriptor) {
        self.endpoints.lock().unwrap().insert(id.into(), d);
    }

    /// The endpoint for `id`, if registered. Port of `Get`.
    pub fn get(&self, id: &str) -> Option<TcpEndpointDescriptor> {
        self.endpoints.lock().unwrap().get(id).cloned()
    }

    /// Sets the connection state for `id`. Port of `SetState`.
    pub fn set_state(&self, id: &str, s: TcpConnectionState) {
        self.states.lock().unwrap().insert(id.to_string(), s);
    }

    /// The connection state for `id`; `Disconnected` if unknown. Mirrors `State`.
    pub fn state(&self, id: &str) -> TcpConnectionState {
        self.states
            .lock()
            .unwrap()
            .get(id)
            .copied()
            .unwrap_or(TcpConnectionState::Disconnected)
    }

    /// Records a throughput sample. Port of `RecordSample`.
    pub fn record_sample(&self, s: TcpThroughputSample) {
        self.throughput.lock().unwrap().push(s);
    }

    /// Total bytes sent for `id` across all samples. Mirrors `TotalBytesSent`.
    pub fn total_bytes_sent(&self, id: &str) -> i64 {
        self.throughput
            .lock()
            .unwrap()
            .iter()
            .filter(|t| t.endpoint_id == id)
            .map(|t| t.bytes_sent)
            .sum()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ITcpConnection — port of the TCP NetworkStream dependency
// ─────────────────────────────────────────────────────────────────────────────

/// The TCP stream dependency. Port of the C# `NetworkStream` used by the
/// transport: a framed write path plus a framed inbound-callback path (the C#
/// `PumpAsync` reads `[len][data]` frames off the stream). Injecting it keeps
/// [`TcpNetworkTransport`] deterministic; [`InMemoryTcpConnection`] is a working
/// implementation (no real socket).
pub trait ITcpConnection: Send + Sync {
    /// Whether the connection is live (the C# `TcpClient.Connected`).
    fn is_connected(&self) -> bool;

    /// Write one already-framed byte buffer (`[len:i32-LE][data]`). The transport
    /// frames the payload before calling this (mirroring the two `WriteAsync`
    /// calls, coalesced into one framed write here).
    fn write_frame(&self, framed: &[u8]) -> Result<(), TransportError>;

    /// Register the sink invoked for each inbound *deframed* payload the pump
    /// decodes (the C# `_inbound.Writer.WriteAsync(NetworkPayload.Create(data))`).
    fn set_inbound_sink(&self, sink: TcpInboundSink);

    /// Close the connection.
    fn close(&self);
}

/// The sink an [`ITcpConnection`] pushes inbound deframed payload bytes into.
pub type TcpInboundSink = Arc<dyn Fn(Vec<u8>) + Send + Sync>;

/// A working, deterministic in-memory [`ITcpConnection`]. `write_frame` records
/// every framed buffer; [`InMemoryTcpConnection::simulate_inbound`] injects raw
/// data as if a `[len][data]` frame arrived (deframed and delivered to the sink
/// while connected). Connection state is settable.
pub struct InMemoryTcpConnection {
    connected: AtomicBool,
    written: Mutex<Vec<Vec<u8>>>,
    sink: Mutex<Option<TcpInboundSink>>,
}

impl Default for InMemoryTcpConnection {
    fn default() -> Self {
        Self::new(true)
    }
}

impl InMemoryTcpConnection {
    /// A new connection with the given initial connected state.
    pub fn new(connected: bool) -> Self {
        Self {
            connected: AtomicBool::new(connected),
            written: Mutex::new(Vec::new()),
            sink: Mutex::new(None),
        }
    }

    /// Sets the connected state.
    pub fn set_connected(&self, value: bool) {
        self.connected.store(value, Ordering::SeqCst);
    }

    /// Every framed buffer written via [`ITcpConnection::write_frame`], in order.
    pub fn written_frames(&self) -> Vec<Vec<u8>> {
        self.written.lock().unwrap().clone()
    }

    /// Injects `data` as if a `[len][data]` frame arrived: forwarded (deframed —
    /// i.e. the raw `data`) to the inbound sink when connected. No-op when
    /// disconnected.
    pub fn simulate_inbound(&self, data: Vec<u8>) {
        if !self.connected.load(Ordering::SeqCst) {
            return;
        }
        // Snapshot the sink under the lock, release, then fire outside it.
        let sink = self.sink.lock().unwrap().clone();
        if let Some(sink) = sink {
            sink(data);
        }
    }
}

impl ITcpConnection for InMemoryTcpConnection {
    fn is_connected(&self) -> bool {
        self.connected.load(Ordering::SeqCst)
    }

    fn write_frame(&self, framed: &[u8]) -> Result<(), TransportError> {
        if !self.connected.load(Ordering::SeqCst) {
            return Err(TransportError::NotAvailable(TransportKind::Tcp));
        }
        self.written.lock().unwrap().push(framed.to_vec());
        Ok(())
    }

    fn set_inbound_sink(&self, sink: TcpInboundSink) {
        *self.sink.lock().unwrap() = Some(sink);
    }

    fn close(&self) {
        self.connected.store(false, Ordering::SeqCst);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// TcpNetworkTransport — port of TcpNetworkTransport.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`INetworkTransport`] over raw TCP. Port of the C# `TcpNetworkTransport`.
///
/// Acts as a client when built with a connection ([`TcpNetworkTransport::client`]):
/// `send` frames the payload as `[len:i32-LE][data]` and writes it; the inbound
/// pump (wired at `start`) deframes arriving frames into an unbounded inbox for
/// [`drain`]. Acts as a listener when built with a listen port only
/// ([`TcpNetworkTransport::listener`]): there is no stream, so `send` fails with
/// [`TransportError::NotAvailable`] and `is_available` is `false` (matching the C#,
/// where a bare listener's `_stream` is null and `IsAvailable` is
/// `_client?.Connected ?? false`).
pub struct TcpNetworkTransport {
    connection: Option<Arc<dyn ITcpConnection>>,
    listen_port: Option<i32>,
    /// Unbounded inbound buffer — the analogue of the C#
    /// `Channel.CreateUnbounded<NetworkPayload>()`.
    inbound: Arc<Mutex<VecDeque<NetworkPayload>>>,
    completed: Arc<AtomicBool>,
    started: AtomicBool,
}

impl TcpNetworkTransport {
    /// A client-mode transport over `connection` (the C# `remoteEndpoint` path).
    pub fn client(connection: Arc<dyn ITcpConnection>) -> Self {
        Self {
            connection: Some(connection),
            listen_port: None,
            inbound: Arc::new(Mutex::new(VecDeque::new())),
            completed: Arc::new(AtomicBool::new(false)),
            started: AtomicBool::new(false),
        }
    }

    /// A listener-mode transport on `listen_port` with no stream (the C#
    /// `listenPort` path). `send` is unavailable in this mode.
    pub fn listener(listen_port: i32) -> Self {
        Self {
            connection: None,
            listen_port: Some(listen_port),
            inbound: Arc::new(Mutex::new(VecDeque::new())),
            completed: Arc::new(AtomicBool::new(false)),
            started: AtomicBool::new(false),
        }
    }

    /// The listen port when in listener mode.
    pub fn listen_port(&self) -> Option<i32> {
        self.listen_port
    }

    /// Frames `data` as the C# does: a 4-byte little-endian length prefix
    /// (`BitConverter.GetBytes(data.Length)` on the LE runtime) followed by the
    /// data.
    pub fn frame(data: &[u8]) -> Vec<u8> {
        let len = data.len() as i32;
        let mut out = Vec::with_capacity(4 + data.len());
        out.extend_from_slice(&len.to_le_bytes());
        out.extend_from_slice(data);
        out
    }

    /// Deframes a `[len:i32-LE][data]` buffer, returning the payload bytes. Returns
    /// `None` when the buffer is malformed (too short or a length mismatch). The
    /// inverse of [`frame`]; mirrors the C# `BitConverter.ToInt32` + `ReadExactly`.
    pub fn deframe(framed: &[u8]) -> Option<Vec<u8>> {
        if framed.len() < 4 {
            return None;
        }
        let len = i32::from_le_bytes([framed[0], framed[1], framed[2], framed[3]]);
        if len < 0 {
            return None;
        }
        let len = len as usize;
        if framed.len() < 4 + len {
            return None;
        }
        Some(framed[4..4 + len].to_vec())
    }

    /// Drains every buffered inbound payload in arrival order. Pull side of the C#
    /// `ReceiveAsync` enumerable.
    pub fn drain(&self) -> Vec<NetworkPayload> {
        self.inbound.lock().unwrap().drain(..).collect()
    }
}

impl INetworkTransport for TcpNetworkTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::Tcp
    }

    fn is_available(&self) -> bool {
        // C#: `_client?.Connected ?? false` — only true in client mode with a live
        // connection.
        match &self.connection {
            Some(conn) => self.started.load(Ordering::SeqCst) && conn.is_connected(),
            None => false,
        }
    }

    fn start(&self) {
        self.completed.store(false, Ordering::SeqCst);
        self.started.store(true, Ordering::SeqCst);
        // Client mode: wire the inbound pump. The connection's sink deframes and
        // buffers into the unbounded inbox. (Listener mode has no stream — the C#
        // just Start()s the listener and never pumps.)
        if let Some(conn) = &self.connection {
            let inbox = Arc::clone(&self.inbound);
            let completed = Arc::clone(&self.completed);
            let sink: TcpInboundSink = Arc::new(move |data: Vec<u8>| {
                if completed.load(Ordering::SeqCst) {
                    return;
                }
                inbox.lock().unwrap().push_back(NetworkPayload::of(data));
            });
            conn.set_inbound_sink(sink);
        }
    }

    fn stop(&self) {
        if let Some(conn) = &self.connection {
            conn.close();
        }
        self.started.store(false, Ordering::SeqCst);
        self.completed.store(true, Ordering::SeqCst);
    }

    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        // C#: `if (_stream is null) throw new InvalidOperationException("Not connected.")`.
        let conn = self
            .connection
            .as_ref()
            .ok_or(TransportError::NotAvailable(TransportKind::Tcp))?;
        let framed = Self::frame(&payload.data);
        conn.write_frame(&framed)
    }
}
