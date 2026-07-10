//! networking_transports::websocket — Rust port of `CircleAI.Networking.WebSocket`
//! (`src/CircleAI.Networking.WebSocket/*.cs`).
//!
//! Full-duplex WebSocket binding of the [`crate::networking::INetworkTransport`]
//! contract. Faithful ports:
//!
//!   * [`WebSocketLinkState`]              — port of the C# enum. The C# member
//!     `Closed_Error` maps to the idiomatic [`WebSocketLinkState::ClosedError`].
//!   * [`WebSocketMessageType`]            — port of the C# enum
//!     (`Text`/`Binary`/`Ping`/`Pong`/`Close`).
//!   * [`WebSocketEndpointDescriptor`] / [`WebSocketFrameSummary`] — the C# `record`s.
//!   * [`InMemoryWebSocketSessionRegistry`] — endpoint table + per-session state +
//!     frame log, matching the C# defaults / aggregation
//!     (`State` defaults to `Closed`; `TotalBytes`; `FrameCount` by type).
//!   * [`IWebSocket`]                      — the `ClientWebSocket` dependency
//!     (trait), with a working [`InMemoryWebSocket`]. Injecting it keeps the
//!     transport deterministic (no real socket).
//!   * [`WebSocketTransport`]              — `INetworkTransport` over a WebSocket:
//!     `send` transmits a binary frame; the receive pump (wired at `start`) buffers
//!     inbound binary frames into an unbounded inbox, breaking on a Close frame
//!     (port of the C# `PumpAsync`).
//!
//! `ReadOnlyMemory<byte>` → `Vec<u8>`; `DateTimeOffset` → `chrono::DateTime<Utc>`;
//! `TimeSpan` → `std::time::Duration`.

use std::collections::{BTreeMap, HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::networking::{INetworkTransport, NetworkPayload, TransportError, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// Enums — port of the C# enums
// ─────────────────────────────────────────────────────────────────────────────

/// State machine of a WebSocket link. 1:1 with the C# `WebSocketLinkState`
/// (`Closed_Error` → [`WebSocketLinkState::ClosedError`]).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum WebSocketLinkState {
    Closed,
    Connecting,
    Open,
    CloseSent,
    CloseReceived,
    /// Port of the C# `Closed_Error`.
    ClosedError,
}

/// WebSocket frame message type. 1:1 with the C# `WebSocketMessageType`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum WebSocketMessageType {
    Text,
    Binary,
    Ping,
    Pong,
    Close,
}

// ─────────────────────────────────────────────────────────────────────────────
// Value records
// ─────────────────────────────────────────────────────────────────────────────

/// A WebSocket endpoint's descriptor. Port of the C# `WebSocketEndpointDescriptor`.
/// `Headers` is optional and ordered (a `BTreeMap`) so it round-trips
/// deterministically; `Subprotocols` preserves order.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct WebSocketEndpointDescriptor {
    pub uri: String,
    pub headers: Option<BTreeMap<String, String>>,
    pub ping_interval: Duration,
    pub subprotocols: Vec<String>,
}

impl WebSocketEndpointDescriptor {
    pub fn new(
        uri: impl Into<String>,
        headers: Option<BTreeMap<String, String>>,
        ping_interval: Duration,
        subprotocols: Vec<String>,
    ) -> Self {
        Self {
            uri: uri.into(),
            headers,
            ping_interval,
            subprotocols,
        }
    }
}

/// A summary of one WebSocket frame. Port of the C# `WebSocketFrameSummary`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct WebSocketFrameSummary {
    pub session_id: String,
    pub message_type: WebSocketMessageType,
    pub bytes: i32,
    pub at_utc: DateTime<Utc>,
}

impl WebSocketFrameSummary {
    pub fn new(
        session_id: impl Into<String>,
        message_type: WebSocketMessageType,
        bytes: i32,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            session_id: session_id.into(),
            message_type,
            bytes,
            at_utc,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryWebSocketSessionRegistry — port of the C# registry
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory endpoint table + per-session link state + frame log. Port of the C#
/// `InMemoryWebSocketSessionRegistry`.
///
/// Matches the C#:
///   * [`state`](Self::state) defaults to `Closed` for unknown sessions.
///   * [`total_bytes`](Self::total_bytes) sums a session's frame bytes.
///   * [`frame_count`](Self::frame_count) counts a session's frames of one type.
#[derive(Default)]
pub struct InMemoryWebSocketSessionRegistry {
    endpoints: Mutex<HashMap<String, WebSocketEndpointDescriptor>>,
    states: Mutex<HashMap<String, WebSocketLinkState>>,
    frames: Mutex<Vec<WebSocketFrameSummary>>,
}

impl InMemoryWebSocketSessionRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) an endpoint keyed by `session_id`. Port of `Register`.
    pub fn register(&self, session_id: impl Into<String>, d: WebSocketEndpointDescriptor) {
        self.endpoints.lock().unwrap().insert(session_id.into(), d);
    }

    /// The endpoint for `session_id`, if registered. Port of `Get`.
    pub fn get(&self, session_id: &str) -> Option<WebSocketEndpointDescriptor> {
        self.endpoints.lock().unwrap().get(session_id).cloned()
    }

    /// Sets the link state for `session_id`. Port of `SetState`.
    pub fn set_state(&self, session_id: &str, s: WebSocketLinkState) {
        self.states
            .lock()
            .unwrap()
            .insert(session_id.to_string(), s);
    }

    /// The link state for `session_id`; `Closed` if unknown. Mirrors `State`.
    pub fn state(&self, session_id: &str) -> WebSocketLinkState {
        self.states
            .lock()
            .unwrap()
            .get(session_id)
            .copied()
            .unwrap_or(WebSocketLinkState::Closed)
    }

    /// Records a frame summary. Port of `RecordFrame`.
    pub fn record_frame(&self, f: WebSocketFrameSummary) {
        self.frames.lock().unwrap().push(f);
    }

    /// Total bytes across a session's frames. Mirrors `TotalBytes`.
    pub fn total_bytes(&self, session_id: &str) -> i64 {
        self.frames
            .lock()
            .unwrap()
            .iter()
            .filter(|f| f.session_id == session_id)
            .map(|f| f.bytes as i64)
            .sum()
    }

    /// Count of a session's frames of `message_type`. Mirrors `FrameCount`.
    pub fn frame_count(&self, session_id: &str, message_type: WebSocketMessageType) -> usize {
        self.frames
            .lock()
            .unwrap()
            .iter()
            .filter(|f| f.session_id == session_id && f.message_type == message_type)
            .count()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IWebSocket — port of the ClientWebSocket dependency
// ─────────────────────────────────────────────────────────────────────────────

/// The WebSocket dependency. Port of the C# `ClientWebSocket` surface used by the
/// transport: connect, send a binary frame, receive-frame callback, close, and
/// state. Injecting it keeps [`WebSocketTransport`] deterministic;
/// [`InMemoryWebSocket`] is a working implementation (no real socket).
pub trait IWebSocket: Send + Sync {
    /// The current link state (the C# `ClientWebSocket.State`, projected to
    /// [`WebSocketLinkState`]).
    fn state(&self) -> WebSocketLinkState;

    /// Connect to the endpoint.
    fn connect(&self);

    /// Send `data` as a binary frame (the C# `SendAsync(..., Binary, endOfMessage:
    /// true)`).
    fn send_binary(&self, data: &[u8]) -> Result<(), TransportError>;

    /// Register the sink invoked for each inbound frame the pump receives (a
    /// `(message_type, payload)` pair). A `Close` frame terminates the pump.
    fn set_inbound_sink(&self, sink: WebSocketInboundSink);

    /// Close with normal closure (the C# `CloseAsync(NormalClosure, "stop")`).
    fn close(&self);
}

/// The sink an [`IWebSocket`] pushes inbound frames into: the message type and,
/// for data frames, the payload bytes.
pub type WebSocketInboundSink = Arc<dyn Fn(WebSocketMessageType, Vec<u8>) + Send + Sync>;

/// A working, deterministic in-memory [`IWebSocket`]. `send_binary` records every
/// frame; [`InMemoryWebSocket::simulate_inbound`] injects a frame as if received.
/// State transitions on connect/close.
pub struct InMemoryWebSocket {
    state: Mutex<WebSocketLinkState>,
    sent: Mutex<Vec<Vec<u8>>>,
    sink: Mutex<Option<WebSocketInboundSink>>,
}

impl Default for InMemoryWebSocket {
    fn default() -> Self {
        Self::new()
    }
}

impl InMemoryWebSocket {
    /// A new, closed WebSocket.
    pub fn new() -> Self {
        Self {
            state: Mutex::new(WebSocketLinkState::Closed),
            sent: Mutex::new(Vec::new()),
            sink: Mutex::new(None),
        }
    }

    /// Every binary frame sent via [`IWebSocket::send_binary`], in order.
    pub fn sent_frames(&self) -> Vec<Vec<u8>> {
        self.sent.lock().unwrap().clone()
    }

    /// Injects a frame as if received: forwarded to the inbound sink when
    /// [`WebSocketLinkState::Open`]. No-op otherwise (no live session).
    pub fn simulate_inbound(&self, message_type: WebSocketMessageType, data: Vec<u8>) {
        if *self.state.lock().unwrap() != WebSocketLinkState::Open {
            return;
        }
        // Snapshot the sink under the lock, release, then fire outside it.
        let sink = self.sink.lock().unwrap().clone();
        if let Some(sink) = sink {
            sink(message_type, data);
        }
    }
}

impl IWebSocket for InMemoryWebSocket {
    fn state(&self) -> WebSocketLinkState {
        *self.state.lock().unwrap()
    }

    fn connect(&self) {
        *self.state.lock().unwrap() = WebSocketLinkState::Open;
    }

    fn send_binary(&self, data: &[u8]) -> Result<(), TransportError> {
        if *self.state.lock().unwrap() != WebSocketLinkState::Open {
            return Err(TransportError::NotAvailable(TransportKind::WebSocket));
        }
        self.sent.lock().unwrap().push(data.to_vec());
        Ok(())
    }

    fn set_inbound_sink(&self, sink: WebSocketInboundSink) {
        *self.sink.lock().unwrap() = Some(sink);
    }

    fn close(&self) {
        *self.state.lock().unwrap() = WebSocketLinkState::Closed;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// WebSocketTransport — port of WebSocketTransport.cs
// ─────────────────────────────────────────────────────────────────────────────

/// Full-duplex [`INetworkTransport`] backed by a WebSocket. Port of the C#
/// `WebSocketTransport`.
///
/// `send` transmits the payload data as a binary frame; the receive pump (wired at
/// `start`) buffers inbound *data* frames into an unbounded inbox for [`drain`], and
/// a `Close` frame terminates the pump (the C# `PumpAsync` breaks on
/// `MessageType == Close`). `is_available` is `true` iff the socket state is
/// [`WebSocketLinkState::Open`] (the C# `_ws?.State == WebSocketState.Open`).
pub struct WebSocketTransport {
    ws: Arc<dyn IWebSocket>,
    /// Unbounded inbound buffer — the analogue of the C#
    /// `Channel.CreateUnbounded<NetworkPayload>()`.
    inbound: Arc<Mutex<VecDeque<NetworkPayload>>>,
    completed: Arc<AtomicBool>,
}

impl WebSocketTransport {
    /// Builds a transport over the given WebSocket.
    pub fn new(ws: Arc<dyn IWebSocket>) -> Self {
        Self {
            ws,
            inbound: Arc::new(Mutex::new(VecDeque::new())),
            completed: Arc::new(AtomicBool::new(false)),
        }
    }

    /// Drains every buffered inbound payload in arrival order. Pull side of the C#
    /// `ReceiveAsync` enumerable.
    pub fn drain(&self) -> Vec<NetworkPayload> {
        self.inbound.lock().unwrap().drain(..).collect()
    }
}

impl INetworkTransport for WebSocketTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::WebSocket
    }

    fn is_available(&self) -> bool {
        self.ws.state() == WebSocketLinkState::Open
    }

    fn start(&self) {
        self.completed.store(false, Ordering::SeqCst);
        self.ws.connect();
        // Wire the receive pump: buffer inbound data frames; a Close frame
        // terminates (sets completed).
        let inbox = Arc::clone(&self.inbound);
        let completed = Arc::clone(&self.completed);
        let sink: WebSocketInboundSink =
            Arc::new(move |message_type: WebSocketMessageType, data: Vec<u8>| {
                if completed.load(Ordering::SeqCst) {
                    return;
                }
                if message_type == WebSocketMessageType::Close {
                    // C#: `if (result.MessageType == Close) break;` — stop pumping.
                    completed.store(true, Ordering::SeqCst);
                    return;
                }
                inbox.lock().unwrap().push_back(NetworkPayload::of(data));
            });
        self.ws.set_inbound_sink(sink);
    }

    fn stop(&self) {
        self.ws.close();
        self.completed.store(true, Ordering::SeqCst);
    }

    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        self.ws.send_binary(&payload.data)
    }
}
