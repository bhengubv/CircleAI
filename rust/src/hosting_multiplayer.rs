//! hosting_multiplayer — CircleAI.Hosting.Multiplayer (Rust port).
//!
//! Live-collaboration hub. Ported from `Contracts.cs` (`IMultiplayerPeerIdentity`,
//! `GuestPeerIdentity`) and `MultiplayerHub.cs` (`MultiplayerHub`, `PeerState`).
//!
//! The C# `MultiplayerHub` is a SignalR `Hub`: per-connection callbacks, static
//! `ConcurrentDictionary` state, and `Clients.OthersInGroup(...).SendAsync(...)`
//! broadcasts. The SignalR transport is a host concern — this port models the
//! deterministic state machine the hub runs: per-connection [`PeerState`],
//! per-document last-writer-wins-by-rev edit resolution, live-cursor + presence
//! bookkeeping, and the `ColourFor` hash→HSL colour. Each mutating call returns
//! the [`HubBroadcast`]s the C# would have sent to peers, so callers/tests wire
//! delivery and assert on it. State is held on the hub instance (the C# statics
//! are a SignalR-lifetime artifact); the LWW and colour algorithms are byte-exact.

use std::collections::HashMap;
use std::sync::Mutex;

// ─────────────────────────────────────────────────────────────────────────────
// Peer identity
// ─────────────────────────────────────────────────────────────────────────────

/// (3.2.0) Resolves the human-visible identity of the peer making a hub call.
/// 1:1 with the C# `IMultiplayerPeerIdentity`.
pub trait IMultiplayerPeerIdentity: Send + Sync {
    /// Stable id (used to derive a colour).
    fn peer_id(&self) -> &str;
    /// Human-readable display name.
    fn display_name(&self) -> &str;
}

/// (3.2.0) Anonymous guest identity. 1:1 with the C# `GuestPeerIdentity` — a
/// `None` id becomes a fresh 32-char hex GUID, a `None` name becomes `"Guest"`.
#[derive(Debug, Clone)]
pub struct GuestPeerIdentity {
    peer_id: String,
    display_name: String,
}

impl GuestPeerIdentity {
    pub fn new(peer_id: Option<String>, display_name: Option<String>) -> Self {
        Self {
            peer_id: peer_id.unwrap_or_else(|| uuid::Uuid::new_v4().simple().to_string()),
            display_name: display_name.unwrap_or_else(|| "Guest".to_string()),
        }
    }
}

impl Default for GuestPeerIdentity {
    fn default() -> Self {
        Self::new(None, None)
    }
}

impl IMultiplayerPeerIdentity for GuestPeerIdentity {
    fn peer_id(&self) -> &str {
        &self.peer_id
    }
    fn display_name(&self) -> &str {
        &self.display_name
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PeerState + broadcasts
// ─────────────────────────────────────────────────────────────────────────────

/// (3.2.0) Snapshot of one connected peer. 1:1 with the C# `MultiplayerHub.PeerState`
/// record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PeerState {
    pub connection_id: String,
    pub display_name: String,
    pub color: String,
    /// The document the peer is currently in (`None` = not in any).
    pub doc_id: Option<String>,
}

/// One outgoing event the hub would broadcast to peers in a document group.
/// Mirrors the C# `Clients.OthersInGroup(...).SendAsync(name, args…)` calls; the
/// `from_connection_id` field carries the caller so the host can exclude them
/// (the SignalR `OthersInGroup` filter).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum HubBroadcast {
    /// `PeerJoined(docId, connectionId, displayName, color)`.
    PeerJoined {
        doc_id: String,
        connection_id: String,
        display_name: String,
        color: String,
    },
    /// `PeerLeft(docId, connectionId, displayName)`.
    PeerLeft {
        doc_id: String,
        connection_id: String,
        display_name: String,
    },
    /// `CursorChanged(connectionId, displayName, color, line, ch)`.
    CursorChanged {
        connection_id: String,
        display_name: String,
        color: String,
        line: i32,
        ch: i32,
    },
    /// `EditApplied(docId, content, rev, fromConnectionId)`.
    EditApplied {
        doc_id: String,
        content: String,
        rev: i64,
        from_connection_id: String,
    },
}

/// The result of [`MultiplayerHub::send_edit`]: the accepted rev plus any
/// broadcast. If the client's rev was stale, `accepted_rev` is the server's
/// current rev and `broadcast` is `None` (the client should rebase).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct EditOutcome {
    pub accepted_rev: i64,
    pub broadcast: Option<HubBroadcast>,
}

// ─────────────────────────────────────────────────────────────────────────────
// MultiplayerHub
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Clone, Copy)]
struct DocRevState {
    rev: i64,
}

#[derive(Default)]
struct HubState {
    rev_by_doc: HashMap<String, DocRevState>,
    peer_by_conn: HashMap<String, PeerState>,
}

/// (3.2.0) Multiplayer collaboration hub. 1:1 with the C# `MultiplayerHub`
/// state machine (SignalR transport omitted; broadcasts returned as values).
///
/// Lifecycle to drive from the host's SignalR callbacks:
///   * [`on_connected`](Self::on_connected) ← `OnConnectedAsync`
///   * [`on_disconnected`](Self::on_disconnected) ← `OnDisconnectedAsync`
///   * [`join_document`](Self::join_document) / [`leave_document`](Self::leave_document)
///   * [`send_cursor`](Self::send_cursor) / [`send_edit`](Self::send_edit)
pub struct MultiplayerHub {
    state: Mutex<HubState>,
}

impl Default for MultiplayerHub {
    fn default() -> Self {
        Self::new()
    }
}

impl MultiplayerHub {
    pub fn new() -> Self {
        Self {
            state: Mutex::new(HubState::default()),
        }
    }

    /// Register a new connection with its resolved identity. 1:1 with the C#
    /// `OnConnectedAsync`.
    pub fn on_connected(&self, connection_id: &str, identity: &dyn IMultiplayerPeerIdentity) {
        let peer = PeerState {
            connection_id: connection_id.to_string(),
            display_name: identity.display_name().to_string(),
            color: colour_for(identity.peer_id()),
            doc_id: None,
        };
        self.state
            .lock()
            .unwrap()
            .peer_by_conn
            .insert(connection_id.to_string(), peer);
    }

    /// Remove a connection; if it was in a document, return the `PeerLeft`
    /// broadcast for that group. 1:1 with the C# `OnDisconnectedAsync`.
    pub fn on_disconnected(&self, connection_id: &str) -> Option<HubBroadcast> {
        let mut state = self.state.lock().unwrap();
        let peer = state.peer_by_conn.remove(connection_id)?;
        let doc_id = peer.doc_id.clone()?;
        if doc_id.is_empty() {
            return None;
        }
        Some(HubBroadcast::PeerLeft {
            doc_id,
            connection_id: peer.connection_id,
            display_name: peer.display_name,
        })
    }

    /// Subscribe the connection to a document group. Returns the `PeerJoined`
    /// broadcast for the other peers. 1:1 with the C# `JoinDocument`.
    pub fn join_document(&self, connection_id: &str, doc_id: &str) -> Option<HubBroadcast> {
        if doc_id.trim().is_empty() {
            return None;
        }
        let mut state = self.state.lock().unwrap();
        let peer = state.peer_by_conn.get_mut(connection_id)?;
        peer.doc_id = Some(doc_id.to_string());
        let (cid, name, color) = (
            peer.connection_id.clone(),
            peer.display_name.clone(),
            peer.color.clone(),
        );
        Some(HubBroadcast::PeerJoined {
            doc_id: doc_id.to_string(),
            connection_id: cid,
            display_name: name,
            color,
        })
    }

    /// Unsubscribe the connection from a document group. Returns the `PeerLeft`
    /// broadcast. 1:1 with the C# `LeaveDocument`.
    pub fn leave_document(&self, connection_id: &str, doc_id: &str) -> Option<HubBroadcast> {
        if doc_id.trim().is_empty() {
            return None;
        }
        let mut state = self.state.lock().unwrap();
        let peer = state.peer_by_conn.get_mut(connection_id)?;
        peer.doc_id = None;
        Some(HubBroadcast::PeerLeft {
            doc_id: doc_id.to_string(),
            connection_id: peer.connection_id.clone(),
            display_name: peer.display_name.clone(),
        })
    }

    /// Broadcast a cursor position to peers. Returns the `CursorChanged`
    /// broadcast, or `None` when the connection is unknown. 1:1 with the C#
    /// `SendCursor`.
    pub fn send_cursor(&self, connection_id: &str, _doc_id: &str, line: i32, ch: i32) -> Option<HubBroadcast> {
        let state = self.state.lock().unwrap();
        let peer = state.peer_by_conn.get(connection_id)?;
        Some(HubBroadcast::CursorChanged {
            connection_id: peer.connection_id.clone(),
            display_name: peer.display_name.clone(),
            color: peer.color.clone(),
            line,
            ch,
        })
    }

    /// Apply an edit if its `rev` beats the server's current rev (LWW-by-rev).
    /// Returns the accepted rev + the `EditApplied` broadcast; a stale rev
    /// returns the server's current rev with no broadcast. 1:1 with the C#
    /// `SendEdit` (`RevByDoc.AddOrUpdate` semantics: a new doc clamps to
    /// `max(rev, 1)`; an existing doc keeps the higher rev).
    pub fn send_edit(&self, connection_id: &str, doc_id: &str, content: &str, rev: i64) -> EditOutcome {
        let mut state = self.state.lock().unwrap();

        let new_rev = match state.rev_by_doc.get(doc_id).copied() {
            None => {
                let s = DocRevState {
                    rev: std::cmp::max(rev, 1),
                };
                state.rev_by_doc.insert(doc_id.to_string(), s);
                s.rev
            }
            Some(prev) => {
                let s = if rev <= prev.rev {
                    prev
                } else {
                    DocRevState { rev }
                };
                state.rev_by_doc.insert(doc_id.to_string(), s);
                s.rev
            }
        };

        if new_rev != rev {
            // Rejected — client gets the current rev back and can rebase.
            return EditOutcome {
                accepted_rev: new_rev,
                broadcast: None,
            };
        }

        EditOutcome {
            accepted_rev: rev,
            broadcast: Some(HubBroadcast::EditApplied {
                doc_id: doc_id.to_string(),
                content: content.to_string(),
                rev,
                from_connection_id: connection_id.to_string(),
            }),
        }
    }

    /// (3.2.0) Snapshot of who is currently in a document. 1:1 with the C#
    /// `Peers`.
    pub fn peers(&self, doc_id: &str) -> Vec<PeerState> {
        self.state
            .lock()
            .unwrap()
            .peer_by_conn
            .values()
            .filter(|p| p.doc_id.as_deref() == Some(doc_id))
            .cloned()
            .collect()
    }

    /// (3.2.0) Current server-known rev for a document (`0` if never touched).
    /// 1:1 with the C# `CurrentRev`.
    pub fn current_rev(&self, doc_id: &str) -> i64 {
        self.state
            .lock()
            .unwrap()
            .rev_by_doc
            .get(doc_id)
            .map(|s| s.rev)
            .unwrap_or(0)
    }

    /// (3.2.0) Test/admin hook — wipes all state. 1:1 with the C#
    /// `ResetStateForTesting`.
    pub fn reset_state_for_testing(&self) {
        let mut state = self.state.lock().unwrap();
        state.rev_by_doc.clear();
        state.peer_by_conn.clear();
    }
}

/// Stable hash → HSL hue, so each peer lands on a different cursor colour.
/// Saturation + lightness are fixed so the colour reads on dark and light
/// themes. 1:1 with the C# `ColourFor` (wrapping `i32` hash arithmetic).
pub fn colour_for(peer_id: &str) -> String {
    if peer_id.is_empty() {
        return "#5a4fcf".to_string();
    }
    // Mirror the C# `unchecked { h = h * 31 + c; }` over UTF-16 code units.
    let mut h: i32 = 0;
    for u in peer_id.encode_utf16() {
        h = h.wrapping_mul(31).wrapping_add(u as i32);
    }
    let hue = ((h % 360) + 360) % 360;
    format!("hsl({hue}, 70%, 55%)")
}
