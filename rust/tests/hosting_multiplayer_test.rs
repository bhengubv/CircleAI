//! hosting_multiplayer_test.rs
//!
//! Verifies MultiplayerHub: connect/join/leave/disconnect presence, cursor +
//! edit broadcasts, LWW-by-rev edit resolution, per-doc rev tracking, and the
//! ColourFor hash→HSL. Mirrors the C# MultiplayerHub state machine.

use circle_ai::hosting_multiplayer::{
    colour_for, GuestPeerIdentity, HubBroadcast, IMultiplayerPeerIdentity, MultiplayerHub,
};

struct Fixed(&'static str, &'static str);
impl IMultiplayerPeerIdentity for Fixed {
    fn peer_id(&self) -> &str {
        self.0
    }
    fn display_name(&self) -> &str {
        self.1
    }
}

#[test]
fn guest_identity_defaults() {
    let g = GuestPeerIdentity::new(None, None);
    assert_eq!(g.display_name(), "Guest");
    assert_eq!(g.peer_id().len(), 32); // hex GUID, no dashes
}

#[test]
fn join_document_broadcasts_peer_joined() {
    let hub = MultiplayerHub::new();
    hub.on_connected("c1", &Fixed("peer-1", "Alice"));
    let bc = hub.join_document("c1", "doc-a").unwrap();
    match bc {
        HubBroadcast::PeerJoined {
            doc_id,
            connection_id,
            display_name,
            color,
        } => {
            assert_eq!(doc_id, "doc-a");
            assert_eq!(connection_id, "c1");
            assert_eq!(display_name, "Alice");
            assert_eq!(color, colour_for("peer-1"));
        }
        _ => panic!("expected PeerJoined"),
    }
    // Presence reflects the join.
    let peers = hub.peers("doc-a");
    assert_eq!(peers.len(), 1);
    assert_eq!(peers[0].display_name, "Alice");
}

#[test]
fn leave_document_broadcasts_peer_left_and_clears_presence() {
    let hub = MultiplayerHub::new();
    hub.on_connected("c1", &Fixed("p1", "Alice"));
    hub.join_document("c1", "doc-a");
    let bc = hub.leave_document("c1", "doc-a").unwrap();
    assert!(matches!(bc, HubBroadcast::PeerLeft { .. }));
    assert_eq!(hub.peers("doc-a").len(), 0);
}

#[test]
fn disconnect_while_in_doc_broadcasts_peer_left() {
    let hub = MultiplayerHub::new();
    hub.on_connected("c1", &Fixed("p1", "Alice"));
    hub.join_document("c1", "doc-a");
    let bc = hub.on_disconnected("c1").unwrap();
    assert!(matches!(bc, HubBroadcast::PeerLeft { .. }));
    assert_eq!(hub.peers("doc-a").len(), 0);
}

#[test]
fn disconnect_when_not_in_doc_returns_none() {
    let hub = MultiplayerHub::new();
    hub.on_connected("c1", &Fixed("p1", "Alice"));
    assert!(hub.on_disconnected("c1").is_none());
}

#[test]
fn send_cursor_broadcasts_position() {
    let hub = MultiplayerHub::new();
    hub.on_connected("c1", &Fixed("p1", "Alice"));
    let bc = hub.send_cursor("c1", "doc-a", 12, 5).unwrap();
    match bc {
        HubBroadcast::CursorChanged { line, ch, connection_id, .. } => {
            assert_eq!(line, 12);
            assert_eq!(ch, 5);
            assert_eq!(connection_id, "c1");
        }
        _ => panic!("expected CursorChanged"),
    }
}

#[test]
fn send_edit_accepts_higher_rev_and_broadcasts() {
    let hub = MultiplayerHub::new();
    hub.on_connected("c1", &Fixed("p1", "Alice"));
    let out = hub.send_edit("c1", "doc-a", "new content", 5);
    assert_eq!(out.accepted_rev, 5);
    match out.broadcast.unwrap() {
        HubBroadcast::EditApplied { content, rev, from_connection_id, doc_id } => {
            assert_eq!(content, "new content");
            assert_eq!(rev, 5);
            assert_eq!(from_connection_id, "c1");
            assert_eq!(doc_id, "doc-a");
        }
        _ => panic!("expected EditApplied"),
    }
    assert_eq!(hub.current_rev("doc-a"), 5);
}

#[test]
fn send_edit_rejects_stale_rev_no_broadcast() {
    let hub = MultiplayerHub::new();
    hub.on_connected("c1", &Fixed("p1", "Alice"));
    hub.send_edit("c1", "doc-a", "v10", 10);
    // A stale rev (7 <= 10) is rejected; caller gets the current rev back.
    let out = hub.send_edit("c1", "doc-a", "v7", 7);
    assert_eq!(out.accepted_rev, 10);
    assert!(out.broadcast.is_none());
    assert_eq!(hub.current_rev("doc-a"), 10);
}

#[test]
fn first_edit_clamps_rev_to_at_least_one() {
    let hub = MultiplayerHub::new();
    hub.on_connected("c1", &Fixed("p1", "Alice"));
    // rev 0 on a fresh doc clamps to max(0,1)=1 → the client's 0 is "stale".
    let out = hub.send_edit("c1", "doc-a", "content", 0);
    assert_eq!(out.accepted_rev, 1);
    assert!(out.broadcast.is_none());
    assert_eq!(hub.current_rev("doc-a"), 1);
}

#[test]
fn current_rev_zero_for_untouched_doc() {
    let hub = MultiplayerHub::new();
    assert_eq!(hub.current_rev("never"), 0);
}

#[test]
fn reset_state_clears_everything() {
    let hub = MultiplayerHub::new();
    hub.on_connected("c1", &Fixed("p1", "Alice"));
    hub.join_document("c1", "doc-a");
    hub.send_edit("c1", "doc-a", "x", 3);
    hub.reset_state_for_testing();
    assert_eq!(hub.peers("doc-a").len(), 0);
    assert_eq!(hub.current_rev("doc-a"), 0);
}

#[test]
fn colour_for_is_stable_and_themed() {
    // Empty id → the fixed fallback.
    assert_eq!(colour_for(""), "#5a4fcf");
    // Deterministic for the same id.
    assert_eq!(colour_for("peer-1"), colour_for("peer-1"));
    // HSL with fixed saturation + lightness.
    let c = colour_for("abc");
    assert!(c.starts_with("hsl("));
    assert!(c.ends_with(", 70%, 55%)"));
}

#[test]
fn colour_for_matches_csharp_hash_for_known_input() {
    // C#: h = 0; foreach c: h = h*31 + c;  hue = ((h % 360)+360)%360.
    // For "ab": h = (0*31 + 'a') = 97; then 97*31 + 'b'(98) = 3007 + 98 = 3105.
    // 3105 % 360 = 225. → "hsl(225, 70%, 55%)".
    assert_eq!(colour_for("ab"), "hsl(225, 70%, 55%)");
}
