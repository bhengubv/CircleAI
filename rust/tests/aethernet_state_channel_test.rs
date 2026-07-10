//! aethernet_state_channel_test.rs
//!
//! Ports `CircleAI.AetherNet/AetherNetCompanionStateChannel.cs`: envelopes are
//! JSON-marshalled onto mesh messages tagged "circleai.sync.v1", broadcast to
//! every configured peer, and on the inbound side filtered by message type,
//! de-looped (skip self), deserialized, and fanned out to subscribers. Uses the
//! in-memory loopback messaging bus.

use std::sync::{Arc, Mutex};

use chrono::Utc;
use circle_ai::aethernet::{
    AetherNetCompanionStateChannel, IMessagingService, InMemoryMessagingService, MeshMessage,
    MessageStatus, SYNC_MESSAGE_TYPE,
};
use circle_ai::memory::{SyncEnvelope, SyncEnvelopeKind};
use uuid::Uuid;

fn envelope(from: &str) -> SyncEnvelope {
    SyncEnvelope::new(SyncEnvelopeKind::Announce, from, None, None, None)
}

#[test]
fn send_broadcasts_to_each_peer_over_the_bus() {
    let bus = Arc::new(InMemoryMessagingService::new());
    // Count raw mesh sends by attaching a raw observer to the bus.
    let seen = Arc::new(Mutex::new(Vec::<MeshMessage>::new()));
    let seen_c = seen.clone();
    bus.subscribe_received(Arc::new(move |m: &MeshMessage| {
        seen_c.lock().unwrap().push(m.clone());
    }));

    let channel = AetherNetCompanionStateChannel::new(
        bus.clone() as Arc<dyn IMessagingService>,
        "deviceA",
        vec!["deviceB".to_string(), "deviceC".to_string()],
    );
    channel.send(&envelope("deviceA"));

    let got = seen.lock().unwrap();
    // One mesh message per configured peer.
    assert_eq!(got.len(), 2);
    for m in got.iter() {
        assert_eq!(m.message_type, SYNC_MESSAGE_TYPE);
        assert_eq!(m.sender_uhid, "deviceA");
        assert_eq!(m.priority, 5);
        assert_eq!(m.status, MessageStatus::Delivered); // bus placed plaintext + delivered
        assert!(!m.encrypted_content.is_empty());
    }
    let recipients: Vec<&str> = got.iter().map(|m| m.recipient_uhid.as_str()).collect();
    assert!(recipients.contains(&"deviceB"));
    assert!(recipients.contains(&"deviceC"));
}

#[test]
fn no_peers_makes_send_a_noop() {
    let bus = Arc::new(InMemoryMessagingService::new());
    let seen = Arc::new(Mutex::new(0usize));
    let seen_c = seen.clone();
    bus.subscribe_received(Arc::new(move |_m: &MeshMessage| {
        *seen_c.lock().unwrap() += 1;
    }));

    let channel = AetherNetCompanionStateChannel::new(
        bus.clone() as Arc<dyn IMessagingService>,
        "solo",
        Vec::<String>::new(),
    );
    channel.send(&envelope("solo"));
    assert_eq!(*seen.lock().unwrap(), 0);
}

#[test]
fn inbound_delivers_to_peer_and_skips_self_loopback() {
    let bus = Arc::new(InMemoryMessagingService::new());

    // Device A broadcasts to B; both channels share the bus.
    let chan_a = AetherNetCompanionStateChannel::new(
        bus.clone() as Arc<dyn IMessagingService>,
        "A",
        vec!["B".to_string()],
    );
    let chan_b = AetherNetCompanionStateChannel::new(
        bus.clone() as Arc<dyn IMessagingService>,
        "B",
        vec!["A".to_string()],
    );

    let a_received = Arc::new(Mutex::new(0usize));
    let b_received = Arc::new(Mutex::new(Vec::<SyncEnvelope>::new()));
    let a_c = a_received.clone();
    let b_c = b_received.clone();
    let _sa = chan_a.subscribe(Arc::new(move |_e: &SyncEnvelope| *a_c.lock().unwrap() += 1));
    let _sb = chan_b.subscribe(Arc::new(move |e: &SyncEnvelope| b_c.lock().unwrap().push(e.clone())));

    chan_a.send(&envelope("A"));

    // B (peer) received A's envelope; A skipped its own loopback.
    assert_eq!(*a_received.lock().unwrap(), 0, "A skips its own message");
    let bs = b_received.lock().unwrap();
    assert_eq!(bs.len(), 1);
    assert_eq!(bs[0].from_node_id, "A");
    assert_eq!(bs[0].kind, SyncEnvelopeKind::Announce);
}

#[test]
fn inbound_filters_wrong_message_type_and_empty_content() {
    let bus = Arc::new(InMemoryMessagingService::new());
    let channel = AetherNetCompanionStateChannel::new(
        bus.clone() as Arc<dyn IMessagingService>,
        "A",
        vec!["B".to_string()],
    );
    let received = Arc::new(Mutex::new(0usize));
    let rc = received.clone();
    let _s = channel.subscribe(Arc::new(move |_e: &SyncEnvelope| *rc.lock().unwrap() += 1));

    // Wrong message type — ignored even though sender != local.
    let wrong = MeshMessage {
        id: Uuid::new_v4(),
        sender_uhid: "B".into(),
        recipient_uhid: "A".into(),
        message_type: "other.type".into(),
        priority: 5,
        encrypted_content: b"{}".to_vec(),
        status: MessageStatus::Delivered,
        created_at: Utc::now(),
    };
    bus.send(&wrong, &wrong.encrypted_content);

    // Right type but empty content — ignored.
    let empty = MeshMessage {
        message_type: SYNC_MESSAGE_TYPE.into(),
        sender_uhid: "B".into(),
        encrypted_content: Vec::new(),
        ..wrong.clone()
    };
    bus.send(&empty, &[]);

    // Malformed JSON — dropped silently.
    let bad = MeshMessage {
        message_type: SYNC_MESSAGE_TYPE.into(),
        sender_uhid: "B".into(),
        encrypted_content: b"not json".to_vec(),
        ..wrong.clone()
    };
    bus.send(&bad, &bad.encrypted_content);

    assert_eq!(*received.lock().unwrap(), 0);
}

#[test]
fn dispose_unhooks_from_bus() {
    let bus = Arc::new(InMemoryMessagingService::new());
    assert_eq!(bus.handler_count(), 0);
    let channel = AetherNetCompanionStateChannel::new(
        bus.clone() as Arc<dyn IMessagingService>,
        "A",
        vec!["B".to_string()],
    );
    assert_eq!(bus.handler_count(), 1, "channel subscribed on construction");
    channel.dispose();
    assert_eq!(bus.handler_count(), 0, "dispose unsubscribes");
    // Idempotent.
    channel.dispose();
    assert_eq!(bus.handler_count(), 0);
}

#[test]
fn peer_list_dedupes_and_drops_blanks() {
    let bus = Arc::new(InMemoryMessagingService::new());
    let channel = AetherNetCompanionStateChannel::new(
        bus.clone() as Arc<dyn IMessagingService>,
        "A",
        vec!["B".to_string(), "B".to_string(), "  ".to_string(), "C".to_string()],
    );
    assert_eq!(channel.peer_uhids(), &["B".to_string(), "C".to_string()]);
    assert_eq!(channel.local_node_id(), "A");
}
