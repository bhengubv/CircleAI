//! companion_sync_test.rs
//!
//! Verifies the companion-state cross-device sync layer (port of
//! `CircleAI.Memory/Sync/`): HLC monotonicity + compose/decompose, the in-memory
//! syncable store apply rules (higher-version / tombstone / content-hash
//! tiebreak), the in-process hub/channel broadcast + subscription, two-peer
//! engine convergence via Announce → Request → Push, the persona / LoRA /
//! conversation bridges, and the self-contained base64 codec.

use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, Mutex};

use chrono::{TimeZone, Utc};
use circle_ai::memory::companion_sync::{
    base64_decode, base64_encode, CompanionConversationSyncBridge, CompanionStateSyncEngine,
    ConversationStateDelta, HybridLogicalClock, ICompanionStateChannel,
    ICompanionStateSyncEngine, ISyncableEntryStore, InMemorySyncableEntryStore,
    InProcessCompanionStateChannel, InProcessSyncHub, LoraAdapterSyncBridge, PersonaStateSyncBridge,
    SyncEnvelope, SyncEnvelopeKind, SyncableEntry,
};
use circle_ai::memory::consolidation::InMemoryPersonaStore;
use circle_ai::memory::stores::{IPersonaStore, PersonaState};

// ── helpers ───────────────────────────────────────────────────────────────────

fn entry(entity_type: &str, id: &str, version: i64, hash: &str, tombstone: bool) -> SyncableEntry {
    SyncableEntry::new(
        entity_type,
        id,
        version,
        tombstone,
        hash,
        if tombstone { "" } else { "payload" },
        "node",
        Utc.timestamp_opt(0, 0).unwrap(),
    )
}

// ── HybridLogicalClock ─────────────────────────────────────────────────────────

#[test]
fn hlc_compose_decompose_round_trips() {
    let v = HybridLogicalClock::compose(123_456_789, 42, 7);
    let (p, l, n) = HybridLogicalClock::decompose(v);
    assert_eq!(p, 123_456_789);
    assert_eq!(l, 42);
    assert_eq!(n, 7);
}

#[test]
fn hlc_ticks_are_strictly_monotonic_even_with_frozen_clock() {
    // Physical clock frozen → logical counter must carry monotonicity.
    let clk = HybridLogicalClock::with_clock(3, Arc::new(|| 1_000));
    let mut last = i64::MIN;
    for _ in 0..2_000 {
        let v = clk.tick();
        assert!(v > last, "tick {v} not > {last}");
        last = v;
    }
}

#[test]
fn hlc_node_id_is_packed_in_low_6_bits() {
    let clk = HybridLogicalClock::with_clock(13, Arc::new(|| 5_000));
    let v = clk.tick();
    let (_, _, node) = HybridLogicalClock::decompose(v);
    assert_eq!(node, 13);
}

#[test]
fn hlc_observe_advances_past_incoming() {
    let clk = HybridLogicalClock::with_clock(1, Arc::new(|| 1_000));
    // Incoming from a peer far in the future (physical 9_999).
    let incoming = HybridLogicalClock::compose(9_999, 5, 2);
    let after = clk.observe(incoming);
    let (p, _, _) = HybridLogicalClock::decompose(after);
    assert_eq!(p, 9_999, "clock should jump to the incoming physical time");
    // Next local tick stays above the observed version.
    let next = clk.tick();
    assert!(next > after);
}

#[test]
fn hlc_advancing_physical_resets_logical() {
    let now = Arc::new(AtomicI64::new(1_000));
    let now2 = Arc::clone(&now);
    let clk = HybridLogicalClock::with_clock(0, Arc::new(move || now2.load(Ordering::SeqCst)));
    let _ = clk.tick();
    let _ = clk.tick(); // logical = 1
    now.store(2_000, Ordering::SeqCst);
    let v = clk.tick();
    let (p, l, _) = HybridLogicalClock::decompose(v);
    assert_eq!(p, 2_000);
    assert_eq!(l, 0, "logical resets when physical advances");
}

#[test]
#[should_panic(expected = "nodeShortId")]
fn hlc_rejects_out_of_range_node_id() {
    let _ = HybridLogicalClock::new(64);
}

// ── InMemorySyncableEntryStore apply rules ──────────────────────────────────────

#[test]
fn store_applies_first_write_and_reads_it_back() {
    let store = InMemorySyncableEntryStore::new();
    assert!(store.apply(&entry("PersonaState", "u1", 10, "aa", false)));
    let got = store.get("PersonaState", "u1").unwrap();
    assert_eq!(got.version, 10);
}

#[test]
fn store_higher_version_wins() {
    let store = InMemorySyncableEntryStore::new();
    assert!(store.apply(&entry("T", "id", 5, "aa", false)));
    assert!(store.apply(&entry("T", "id", 6, "aa", false)));
    assert_eq!(store.get("T", "id").unwrap().version, 6);
    // lower version rejected
    assert!(!store.apply(&entry("T", "id", 4, "zz", false)));
    assert_eq!(store.get("T", "id").unwrap().version, 6);
}

#[test]
fn store_equal_version_tombstone_beats_non_tombstone() {
    let store = InMemorySyncableEntryStore::new();
    assert!(store.apply(&entry("T", "id", 5, "aa", false)));
    assert!(store.apply(&entry("T", "id", 5, "", true)));
    assert!(store.get("T", "id").unwrap().is_tombstone);
    // A non-tombstone of equal version must NOT overwrite the tombstone.
    assert!(!store.apply(&entry("T", "id", 5, "zz", false)));
    assert!(store.get("T", "id").unwrap().is_tombstone);
}

#[test]
fn store_equal_version_higher_content_hash_wins() {
    let store = InMemorySyncableEntryStore::new();
    assert!(store.apply(&entry("T", "id", 5, "aaa", false)));
    assert!(store.apply(&entry("T", "id", 5, "bbb", false))); // "bbb" > "aaa"
    assert_eq!(store.get("T", "id").unwrap().content_hash, "bbb");
    assert!(!store.apply(&entry("T", "id", 5, "aaa", false)));
}

#[test]
fn store_get_since_orders_ascending_and_filters_type() {
    let store = InMemorySyncableEntryStore::new();
    store.apply(&entry("A", "1", 30, "h", false));
    store.apply(&entry("A", "2", 10, "h", false));
    store.apply(&entry("A", "3", 20, "h", false));
    store.apply(&entry("B", "9", 99, "h", false));
    let since = store.get_since("A", 10);
    let versions: Vec<i64> = since.iter().map(|e| e.version).collect();
    assert_eq!(versions, vec![20, 30], "strictly > 10, ascending, type A only");
}

#[test]
fn store_state_vector_is_max_per_type_sorted() {
    let store = InMemorySyncableEntryStore::new();
    store.apply(&entry("Zeta", "1", 7, "h", false));
    store.apply(&entry("Alpha", "1", 3, "h", false));
    store.apply(&entry("Alpha", "2", 9, "h", false));
    let vec = store.get_state_vector();
    assert_eq!(vec.len(), 2);
    assert_eq!(vec[0].entity_type, "Alpha");
    assert_eq!(vec[0].max_known_version, 9);
    assert_eq!(vec[1].entity_type, "Zeta");
    assert_eq!(vec[1].max_known_version, 7);
}

// ── InProcessSyncHub + channel ─────────────────────────────────────────────────

#[test]
fn hub_broadcasts_to_peers_not_sender() {
    let hub = InProcessSyncHub::new();
    let a = InProcessCompanionStateChannel::new(&hub, "A");
    let b = InProcessCompanionStateChannel::new(&hub, "B");

    let got_a = Arc::new(Mutex::new(Vec::<String>::new()));
    let got_b = Arc::new(Mutex::new(Vec::<String>::new()));
    let ga = Arc::clone(&got_a);
    let gb = Arc::clone(&got_b);
    let _sa = a.subscribe(Arc::new(move |e: &SyncEnvelope| {
        ga.lock().unwrap().push(e.from_node_id.clone())
    }));
    let _sb = b.subscribe(Arc::new(move |e: &SyncEnvelope| {
        gb.lock().unwrap().push(e.from_node_id.clone())
    }));

    a.send(&SyncEnvelope::new(SyncEnvelopeKind::Announce, "A", None, None, None));
    assert!(got_a.lock().unwrap().is_empty(), "sender does not receive own envelope");
    assert_eq!(got_b.lock().unwrap().as_slice(), &["A".to_string()]);

    assert_eq!(hub.connected_node_ids().len(), 2);
}

#[test]
fn subscription_drop_unsubscribes() {
    let hub = InProcessSyncHub::new();
    let a = InProcessCompanionStateChannel::new(&hub, "A");
    let b = InProcessCompanionStateChannel::new(&hub, "B");
    let count = Arc::new(Mutex::new(0usize));
    let c = Arc::clone(&count);
    let sub = b.subscribe(Arc::new(move |_e: &SyncEnvelope| *c.lock().unwrap() += 1));

    a.send(&SyncEnvelope::new(SyncEnvelopeKind::Announce, "A", None, None, None));
    assert_eq!(*count.lock().unwrap(), 1);
    drop(sub);
    a.send(&SyncEnvelope::new(SyncEnvelopeKind::Announce, "A", None, None, None));
    assert_eq!(*count.lock().unwrap(), 1, "dropped subscription stops receiving");
}

// ── CompanionStateSyncEngine convergence ───────────────────────────────────────

fn make_engine(
    hub: &InProcessSyncHub,
    node: &str,
    short_id: i64,
) -> (
    Arc<CompanionStateSyncEngine>,
    Arc<InMemorySyncableEntryStore>,
) {
    let channel: Arc<dyn ICompanionStateChannel> =
        Arc::new(InProcessCompanionStateChannel::new(hub, node));
    let store = Arc::new(InMemorySyncableEntryStore::new());
    let store_dyn: Arc<dyn ISyncableEntryStore> = Arc::clone(&store) as Arc<dyn ISyncableEntryStore>;
    let clock = Arc::new(HybridLogicalClock::new(short_id));
    let engine = Arc::new(CompanionStateSyncEngine::new(channel, store_dyn, clock));
    (engine, store)
}

#[test]
fn write_local_stamps_version_and_persists() {
    let hub = InProcessSyncHub::new();
    let (engine, store) = make_engine(&hub, "solo", 1);
    let e = engine.write_local("PersonaState", "u1", "{\"x\":1}", false);
    assert!(e.version > 0);
    assert_eq!(e.source_node_id, "solo");
    assert_eq!(e.entity_type, "PersonaState");
    // content hash is the sha-256 hex of the payload (64 hex chars).
    assert_eq!(e.content_hash.len(), 64);
    assert_eq!(store.get("PersonaState", "u1").unwrap().version, e.version);
}

#[test]
fn two_engines_converge_on_push() {
    // A writes after both started → Push flows to B immediately.
    let hub = InProcessSyncHub::new();
    let (a, _sa) = make_engine(&hub, "A", 1);
    let (b, sb) = make_engine(&hub, "B", 2);
    a.start();
    b.start();

    let written = a.write_local("PersonaState", "u1", "hello", false);
    let mirrored = sb.get("PersonaState", "u1").expect("B should have received the push");
    assert_eq!(mirrored.version, written.version);
    assert_eq!(mirrored.payload, "hello");
}

#[test]
fn late_joiner_converges_via_announce_request_push() {
    // A writes BEFORE B exists (no push reaches B). Then B joins + started, and
    // an Announce from A drives Request → Push so B catches up.
    let hub = InProcessSyncHub::new();
    let (a, _sa) = make_engine(&hub, "A", 1);
    a.start();
    let w1 = a.write_local("PersonaState", "u1", "v1", false);
    let w2 = a.write_local("CoreMemory", "c1", "core", false);

    // B joins now — misses the earlier pushes.
    let (b, sb) = make_engine(&hub, "B", 2);
    b.start();
    assert!(sb.get("PersonaState", "u1").is_none());

    // A re-announces its state vector; B requests, A pushes, B converges.
    a.sync_now();

    let m1 = sb.get("PersonaState", "u1").expect("persona converged");
    let m2 = sb.get("CoreMemory", "c1").expect("core converged");
    assert_eq!(m1.version, w1.version);
    assert_eq!(m2.version, w2.version);
}

#[test]
fn tombstone_propagates_across_peers() {
    let hub = InProcessSyncHub::new();
    let (a, _sa) = make_engine(&hub, "A", 1);
    let (b, sb) = make_engine(&hub, "B", 2);
    a.start();
    b.start();

    a.write_local("ConversationState", "s1", "live", false);
    assert!(sb.get("ConversationState", "s1").is_some());

    a.write_local("ConversationState", "s1", "", true);
    let t = sb.get("ConversationState", "s1").unwrap();
    assert!(t.is_tombstone, "delete propagated as a tombstone");
}

// ── PersonaStateSyncBridge ─────────────────────────────────────────────────────

#[test]
fn persona_bridge_saves_locally_and_broadcasts() {
    let hub = InProcessSyncHub::new();
    let (a, _sa) = make_engine(&hub, "A", 1);
    let (b, sb) = make_engine(&hub, "B", 2);
    a.start();
    b.start();

    let engine_a: Arc<dyn ICompanionStateSyncEngine> = a.clone();
    let mut bridge = PersonaStateSyncBridge::new(InMemoryPersonaStore::new(), engine_a);

    let mut persona = PersonaState::new("user-42");
    persona.verbosity = "brief".to_string();
    bridge.save(&persona).unwrap();

    let entry = sb.get(PersonaStateSyncBridge::<InMemoryPersonaStore>::ENTITY_TYPE, "user-42")
        .expect("persona synced to B");
    let decoded = PersonaStateSyncBridge::<InMemoryPersonaStore>::try_decode(&entry)
        .expect("decodes back to PersonaState");
    assert_eq!(decoded.user_id, "user-42");
    assert_eq!(decoded.verbosity, "brief");
}

#[test]
fn persona_bridge_try_decode_rejects_tombstone_and_wrong_type() {
    let tomb = entry("PersonaState", "u", 1, "", true);
    assert!(PersonaStateSyncBridge::<InMemoryPersonaStore>::try_decode(&tomb).is_none());
    let wrong = entry("CoreMemory", "u", 1, "h", false);
    assert!(PersonaStateSyncBridge::<InMemoryPersonaStore>::try_decode(&wrong).is_none());
}

// ── LoraAdapterSyncBridge ──────────────────────────────────────────────────────

#[test]
fn lora_bridge_publishes_and_decodes_bytes() {
    let hub = InProcessSyncHub::new();
    let (a, _sa) = make_engine(&hub, "A", 1);
    let (b, sb) = make_engine(&hub, "B", 2);
    a.start();
    b.start();

    let engine_a: Arc<dyn ICompanionStateSyncEngine> = a.clone();
    let bridge = LoraAdapterSyncBridge::new(engine_a);
    let bytes = vec![0u8, 1, 2, 250, 251, 252, 253, 254, 255];
    let snap = bridge.publish("personal-42", &bytes, 1234, Utc.timestamp_opt(1_000, 0).unwrap());
    assert_eq!(snap.step_count, 1234);

    let entry = sb.get(LoraAdapterSyncBridge::ENTITY_TYPE, "personal-42").unwrap();
    let (decoded, decoded_bytes) = LoraAdapterSyncBridge::try_decode(&entry).unwrap();
    assert_eq!(decoded.adapter_id, "personal-42");
    assert_eq!(decoded_bytes, bytes, "adapter bytes survive base64 round-trip over the wire");
}

// ── CompanionConversationSyncBridge ────────────────────────────────────────────

#[test]
fn conversation_bridge_publish_and_terminate() {
    let hub = InProcessSyncHub::new();
    let (a, _sa) = make_engine(&hub, "A", 1);
    let (b, sb) = make_engine(&hub, "B", 2);
    a.start();
    b.start();

    let engine_a: Arc<dyn ICompanionStateSyncEngine> = a.clone();
    let bridge = CompanionConversationSyncBridge::new(engine_a);

    let delta = ConversationStateDelta::new(
        "sess-1",
        "hi",
        "hello there",
        false,
        Utc.timestamp_opt(10, 0).unwrap(),
        Utc.timestamp_opt(11, 0).unwrap(),
    );
    bridge.publish(&delta);
    let e = sb.get(CompanionConversationSyncBridge::ENTITY_TYPE, "sess-1").unwrap();
    let d = CompanionConversationSyncBridge::try_decode(&e).unwrap();
    assert_eq!(d.assistant_text, "hello there");
    assert!(!d.is_turn_complete);

    bridge.terminate("sess-1");
    let t = sb.get(CompanionConversationSyncBridge::ENTITY_TYPE, "sess-1").unwrap();
    assert!(t.is_tombstone);
    assert!(CompanionConversationSyncBridge::try_decode(&t).is_none());
}

// ── base64 codec ───────────────────────────────────────────────────────────────

#[test]
fn base64_round_trips_all_lengths() {
    for len in 0..40usize {
        let data: Vec<u8> = (0..len).map(|i| (i as u8).wrapping_mul(37).wrapping_add(11)).collect();
        let encoded = base64_encode(&data);
        // padding keeps length a multiple of 4
        assert_eq!(encoded.len() % 4, 0);
        let decoded = base64_decode(&encoded).expect("decodes");
        assert_eq!(decoded, data, "round trip failed at len {len}");
    }
}

#[test]
fn base64_matches_known_vectors() {
    // Classic RFC 4648 test vectors.
    assert_eq!(base64_encode(b""), "");
    assert_eq!(base64_encode(b"f"), "Zg==");
    assert_eq!(base64_encode(b"fo"), "Zm8=");
    assert_eq!(base64_encode(b"foo"), "Zm9v");
    assert_eq!(base64_encode(b"foob"), "Zm9vYg==");
    assert_eq!(base64_encode(b"fooba"), "Zm9vYmE=");
    assert_eq!(base64_encode(b"foobar"), "Zm9vYmFy");
    assert_eq!(base64_decode("Zm9vYmFy").unwrap(), b"foobar");
}

#[test]
fn base64_decode_rejects_bad_input() {
    assert!(base64_decode("Zg=").is_none(), "not a multiple of 4");
    assert!(base64_decode("****").is_none(), "invalid alphabet");
}

// ── persona sync store (IPersonaStore) ─────────────────────────────────────────

#[test]
fn in_memory_persona_store_load_is_stable_and_save_round_trips() {
    let mut store = InMemoryPersonaStore::new();
    // Unknown user → fresh default, and a second load returns the same.
    let first = IPersonaStore::load(&store, "newbie").unwrap();
    assert_eq!(first.user_id, "newbie");
    assert_eq!(first.verbosity, "balanced");
    let second = IPersonaStore::load(&store, "newbie").unwrap();
    assert_eq!(second.user_id, "newbie");

    let mut p = PersonaState::new("newbie");
    p.formality = "formal".to_string();
    IPersonaStore::save(&mut store, &p).unwrap();
    let reloaded = IPersonaStore::load(&store, "newbie").unwrap();
    assert_eq!(reloaded.formality, "formal");
}

#[test]
fn in_memory_persona_store_rejects_blank_user() {
    let store = InMemoryPersonaStore::new();
    assert!(IPersonaStore::load(&store, "  ").is_err());
}
