//! memory_sync_service_test.rs
//!
//! Verifies the `CircleAI.Sync` port: the [`MemorySyncService`] push/receive
//! orchestrator (push builds a broadcast SyncDelta; receive applies episodic
//! deltas, skips own echoes and other domains, and is gated by start/stop), the
//! [`SyncReconciliation`] version-vector helpers, and the in-memory sync
//! [`IGoalStore`] (`InMemoryGoalStore`).

use std::collections::HashMap;
use std::convert::Infallible;
use std::sync::Mutex;

use chrono::{TimeZone, Utc};
use circle_ai::memory::stores::{
    EpisodicMemoryEntry, IEpisodicMemoryStore, IGoalStore, InMemoryGoalStore,
};
use circle_ai::memory::{Goal, GoalPriority, GoalStatus};
use circle_ai::sync::{
    ISyncChannel, SyncDeliveryMode, SyncDelta, SyncDomainKeys, SyncReconciliation, VersionVector,
};
use circle_ai::sync_service::{IMemorySyncService, MemorySyncService};

// ── fakes ───────────────────────────────────────────────────────────────────

/// A channel that yields a scripted inbound queue (used by the receive tests;
/// pushes are ignored here — the push path is covered via `FakeChannelHandle`).
#[derive(Default)]
struct FakeChannel {
    inbound: Mutex<Vec<SyncDelta>>,
}

impl FakeChannel {
    fn with_inbound(inbound: Vec<SyncDelta>) -> Self {
        Self {
            inbound: Mutex::new(inbound),
        }
    }
}

impl ISyncChannel for FakeChannel {
    type Error = Infallible;

    fn push_delta(&mut self, _delta: &SyncDelta) -> Result<(), Self::Error> {
        Ok(())
    }

    fn receive_deltas(
        &self,
        _owner_id: &str,
    ) -> Result<Box<dyn Iterator<Item = Result<SyncDelta, Self::Error>>>, Self::Error> {
        // Drain the scripted inbound queue.
        let drained: Vec<SyncDelta> = std::mem::take(&mut *self.inbound.lock().unwrap());
        Ok(Box::new(drained.into_iter().map(Ok)))
    }

    fn get_last_sequence(&self, _owner_id: &str, _domain_key: &str) -> Result<i64, Self::Error> {
        Ok(0)
    }
}

/// A minimal in-memory episodic store implementing the sync trait.
#[derive(Default)]
struct VecEpisodicStore {
    entries: Mutex<Vec<EpisodicMemoryEntry>>,
}

impl IEpisodicMemoryStore for VecEpisodicStore {
    type Error = Infallible;

    fn add(&mut self, entry: EpisodicMemoryEntry) -> Result<(), Self::Error> {
        self.entries.lock().unwrap().push(entry);
        Ok(())
    }
    fn search(
        &self,
        _query_embedding: Option<&[f32]>,
        _top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, Self::Error> {
        Ok(self.entries.lock().unwrap().clone())
    }
    fn get_recent(&self, count: usize) -> Result<Vec<EpisodicMemoryEntry>, Self::Error> {
        let e = self.entries.lock().unwrap();
        Ok(e.iter().rev().take(count).cloned().collect())
    }
    fn count(&self) -> Result<usize, Self::Error> {
        Ok(self.entries.lock().unwrap().len())
    }
    fn prune_older_than(
        &mut self,
        _cutoff: &chrono::DateTime<chrono::Utc>,
    ) -> Result<usize, Self::Error> {
        Ok(0)
    }
}

fn episodic_delta(source_device: &str, entry: &EpisodicMemoryEntry) -> SyncDelta {
    SyncDelta::new(
        "owner-1",
        source_device,
        "",
        SyncDomainKeys::MEMORY_EPISODIC,
        serde_json::to_vec(entry).unwrap(),
        1,
        SyncDeliveryMode::Guaranteed,
        None,
    )
}

// ── push side ─────────────────────────────────────────────────────────────────

#[test]
fn push_delta_fields_are_correct() {
    // Build a channel that shares its recorder so we can inspect the pushed delta
    // after the channel is moved into the service.
    let ch = FakeChannelHandle::default();
    let recorder = ch.clone_recorder();
    let svc = MemorySyncService::new(ch, VecEpisodicStore::default(), "dev-A");
    svc.push_memory_delta(
        "owner-9",
        SyncDomainKeys::GOALS,
        b"payload-bytes".to_vec(),
        SyncDeliveryMode::BestEffort,
    )
    .unwrap();

    let pushed = recorder.lock().unwrap();
    assert_eq!(pushed.len(), 1);
    let d = &pushed[0];
    assert_eq!(d.owner_id, "owner-9");
    assert_eq!(d.source_device_id, "dev-A");
    assert_eq!(d.target_device_id, ""); // broadcast
    assert_eq!(d.domain_key, SyncDomainKeys::GOALS);
    assert_eq!(d.payload, b"payload-bytes");
    assert!(d.is_broadcast());
    assert_eq!(d.delivery_mode, SyncDeliveryMode::BestEffort);
}

/// Channel variant that shares its recorder so a test can inspect pushes after
/// the channel is moved into the service.
#[derive(Default)]
struct FakeChannelHandle {
    pushed: std::sync::Arc<Mutex<Vec<SyncDelta>>>,
}
impl FakeChannelHandle {
    fn clone_recorder(&self) -> std::sync::Arc<Mutex<Vec<SyncDelta>>> {
        std::sync::Arc::clone(&self.pushed)
    }
}
impl ISyncChannel for FakeChannelHandle {
    type Error = Infallible;
    fn push_delta(&mut self, delta: &SyncDelta) -> Result<(), Self::Error> {
        self.pushed.lock().unwrap().push(delta.clone());
        Ok(())
    }
    fn receive_deltas(
        &self,
        _owner_id: &str,
    ) -> Result<Box<dyn Iterator<Item = Result<SyncDelta, Self::Error>>>, Self::Error> {
        Ok(Box::new(std::iter::empty()))
    }
    fn get_last_sequence(&self, _o: &str, _d: &str) -> Result<i64, Self::Error> {
        Ok(0)
    }
}

// ── receive side ──────────────────────────────────────────────────────────────

#[test]
fn receive_is_noop_until_started() {
    let entry = EpisodicMemoryEntry::new("hi", "hello");
    let inbound = vec![episodic_delta("dev-B", &entry)];
    let svc = MemorySyncService::new(
        FakeChannel::with_inbound(inbound),
        VecEpisodicStore::default(),
        "dev-A",
    );
    // Not started → drains nothing.
    assert_eq!(svc.receive_once("owner-1").unwrap(), 0);
    assert!(!svc.is_receiving());
}

#[test]
fn receive_applies_episodic_delta_from_other_device() {
    let entry = EpisodicMemoryEntry::new("what is the weather", "sunny");
    let inbound = vec![episodic_delta("dev-B", &entry)];
    let svc = MemorySyncService::new(
        FakeChannel::with_inbound(inbound),
        VecEpisodicStore::default(),
        "dev-A",
    );
    svc.start_receiving("owner-1").unwrap();
    assert!(svc.is_receiving());
    let applied = svc.receive_once("owner-1").unwrap();
    assert_eq!(applied, 1, "one episodic delta applied");
}

#[test]
fn receive_skips_own_echo() {
    let entry = EpisodicMemoryEntry::new("x", "y");
    // Delta sourced from the LOCAL device — must be skipped.
    let inbound = vec![episodic_delta("dev-A", &entry)];
    let svc = MemorySyncService::new(
        FakeChannel::with_inbound(inbound),
        VecEpisodicStore::default(),
        "dev-A",
    );
    svc.start_receiving("owner-1").unwrap();
    assert_eq!(svc.receive_once("owner-1").unwrap(), 0, "own echo skipped");
}

#[test]
fn receive_skips_other_domain_deltas() {
    let mut persona_delta = SyncDelta::new(
        "owner-1",
        "dev-B",
        "",
        SyncDomainKeys::PERSONA,
        b"not-an-episode".to_vec(),
        1,
        SyncDeliveryMode::Guaranteed,
        None,
    );
    // ensure created_at deterministic-ish (not asserted)
    persona_delta.sequence = 1;
    let svc = MemorySyncService::new(
        FakeChannel::with_inbound(vec![persona_delta]),
        VecEpisodicStore::default(),
        "dev-A",
    );
    svc.start_receiving("owner-1").unwrap();
    assert_eq!(svc.receive_once("owner-1").unwrap(), 0, "non-episodic domain ignored");
}

#[test]
fn receive_wrong_owner_is_noop() {
    let entry = EpisodicMemoryEntry::new("x", "y");
    let svc = MemorySyncService::new(
        FakeChannel::with_inbound(vec![episodic_delta("dev-B", &entry)]),
        VecEpisodicStore::default(),
        "dev-A",
    );
    svc.start_receiving("owner-1").unwrap();
    assert_eq!(svc.receive_once("someone-else").unwrap(), 0);
}

#[test]
fn stop_receiving_disables_drain() {
    let entry = EpisodicMemoryEntry::new("x", "y");
    let svc = MemorySyncService::new(
        FakeChannel::with_inbound(vec![episodic_delta("dev-B", &entry)]),
        VecEpisodicStore::default(),
        "dev-A",
    );
    svc.start_receiving("owner-1").unwrap();
    svc.stop_receiving().unwrap();
    assert!(!svc.is_receiving());
    assert_eq!(svc.receive_once("owner-1").unwrap(), 0);
}

// ── SyncReconciliation ────────────────────────────────────────────────────────

fn vv(pairs: &[(&str, i64)]) -> VersionVector {
    let mut m = HashMap::new();
    for (k, v) in pairs {
        m.insert(k.to_string(), *v);
    }
    VersionVector::new(m)
}

#[test]
fn version_vector_merge_takes_elementwise_max() {
    let a = vv(&[("n1", 5), ("n2", 2)]);
    let b = vv(&[("n2", 7), ("n3", 1)]);
    let m = SyncReconciliation::merge(&a, &b);
    assert_eq!(m.clocks.get("n1"), Some(&5));
    assert_eq!(m.clocks.get("n2"), Some(&7));
    assert_eq!(m.clocks.get("n3"), Some(&1));
}

#[test]
fn version_vector_dominance() {
    let a = vv(&[("n1", 5), ("n2", 3)]);
    let b = vv(&[("n1", 4), ("n2", 3)]);
    assert!(SyncReconciliation::a_dominates_b(&a, &b), "a strictly ahead on n1");
    assert!(!SyncReconciliation::a_dominates_b(&b, &a));
    // Equal vectors do not dominate (no strictly-greater component).
    assert!(!SyncReconciliation::a_dominates_b(&a, &a));
    // Concurrent vectors: neither dominates.
    let c = vv(&[("n1", 6), ("n2", 1)]);
    let d = vv(&[("n1", 2), ("n2", 9)]);
    assert!(!SyncReconciliation::a_dominates_b(&c, &d));
    assert!(!SyncReconciliation::a_dominates_b(&d, &c));
}

#[test]
fn last_writer_wins_prefers_later_then_ties_to_a() {
    let t1 = Utc.timestamp_opt(100, 0).unwrap();
    let t2 = Utc.timestamp_opt(200, 0).unwrap();
    let (w, v) = SyncReconciliation::last_writer_wins((t1, "old"), (t2, "new"));
    assert_eq!(w, t2);
    assert_eq!(v, "new");
    // Tie → a wins.
    let (w2, v2) = SyncReconciliation::last_writer_wins((t1, "A"), (t1, "B"));
    assert_eq!(w2, t1);
    assert_eq!(v2, "A");
}

// ── InMemoryGoalStore (sync IGoalStore) ───────────────────────────────────────

fn goal(id: &str, user: &str, status: GoalStatus) -> Goal {
    let mut g = Goal::new(id, user, "title", "desc", GoalPriority::Normal);
    g.status = status;
    g
}

#[test]
fn goal_store_upsert_get_list_delete() {
    let mut store = InMemoryGoalStore::new();
    store.upsert(goal("g1", "u1", GoalStatus::Active)).unwrap();
    store.upsert(goal("g2", "u1", GoalStatus::Completed)).unwrap();
    store.upsert(goal("g3", "u2", GoalStatus::Active)).unwrap();

    assert_eq!(store.get("g1").unwrap().unwrap().user_id, "u1");
    assert!(store.get("missing").unwrap().is_none());

    let u1 = store.list("u1").unwrap();
    assert_eq!(u1.len(), 2);
    // insertion order preserved
    assert_eq!(u1[0].id, "g1");
    assert_eq!(u1[1].id, "g2");

    let active = store.get_active("u1").unwrap();
    assert_eq!(active.len(), 1);
    assert_eq!(active[0].id, "g1");

    store.delete("g1").unwrap();
    assert!(store.get("g1").unwrap().is_none());
    assert_eq!(store.list("u1").unwrap().len(), 1);
}

#[test]
fn goal_store_upsert_replaces_in_place() {
    let mut store = InMemoryGoalStore::new();
    store.upsert(goal("g1", "u1", GoalStatus::Active)).unwrap();
    let mut updated = goal("g1", "u1", GoalStatus::Completed);
    updated.progress = 1.0;
    store.upsert(updated).unwrap();
    let got = store.get("g1").unwrap().unwrap();
    assert_eq!(got.status, GoalStatus::Completed);
    assert_eq!(got.progress, 1.0);
    // still a single entry, order intact
    assert_eq!(store.list("u1").unwrap().len(), 1);
}

#[test]
fn goal_store_rejects_blank_ids() {
    let mut store = InMemoryGoalStore::new();
    assert!(store.list("  ").is_err());
    assert!(store.get(" ").is_err());
    assert!(store.get_active("").is_err());
    assert!(store.delete("").is_err());
}
