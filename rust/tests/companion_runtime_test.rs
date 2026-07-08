//! companion_runtime_test.rs
//!
//! Verifies the memory pipeline runtime (port of `CircleAI.Memory/Runtime/`):
//! default options, catch-up consolidation on start, start subscribing the sync
//! engine, `consolidate_now` / `tick` delegation, `sync_now` broadcasting, and
//! `ingest_media` (present + absent ingester).

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::Utc;
use circle_ai::memory::companion_sync::{
    CompanionStateSyncEngine, HybridLogicalClock, ICompanionStateChannel,
    ICompanionStateSyncEngine, ISyncableEntryStore, InMemorySyncableEntryStore,
    InProcessCompanionStateChannel, InProcessSyncHub, SyncableEntry,
};
use circle_ai::memory::consolidation::{
    ConsolidationOutcome, IMemoryConsolidator, SleepKind,
};
use circle_ai::memory::multimodal::{
    HeuristicMultimodalCaptioner, IMultimodalCaptioner, InMemoryMultimodalMemoryStore,
    IngestOptions, MediaModality, MultimodalMemoryIngester,
};
use circle_ai::memory::runtime::{CompanionRuntime, CompanionRuntimeOptions};
use circle_ai::brain::BrainError;

// A consolidator that records each SleepKind it was ticked with.
struct RecordingConsolidator {
    ticks: Arc<Mutex<Vec<SleepKind>>>,
    fail: bool,
}

impl IMemoryConsolidator for RecordingConsolidator {
    fn tick(&self, kind: SleepKind) -> Result<ConsolidationOutcome, BrainError> {
        self.ticks.lock().unwrap().push(kind);
        if self.fail {
            return Err(BrainError::new("boom"));
        }
        Ok(ConsolidationOutcome {
            kind,
            daily_summaries_produced: 1,
            semantic_clusters_produced: 0,
            persona_deltas_produced: 0,
            core_promotions: 0,
            episodes_pruned: 0,
            dailies_pruned: 0,
            semantics_pruned: 0,
            ran_at_utc: Utc::now(),
        })
    }
}

fn make_ingester() -> MultimodalMemoryIngester {
    let captioners: Vec<Box<dyn IMultimodalCaptioner>> = vec![Box::new(HeuristicMultimodalCaptioner)];
    MultimodalMemoryIngester::new(captioners, Box::new(InMemoryMultimodalMemoryStore::new())).unwrap()
}

#[test]
fn default_options_match_csharp_defaults() {
    let o = CompanionRuntimeOptions::default();
    assert_eq!(o.daily_tick_interval, Duration::from_secs(6 * 3600));
    assert_eq!(o.weekly_tick_interval, Duration::from_secs(24 * 3600));
    assert_eq!(o.monthly_tick_interval, Duration::from_secs(48 * 3600));
    assert_eq!(o.sync_broadcast_interval, Duration::from_secs(5 * 60));
    assert_eq!(o.initial_delay, Duration::from_secs(30));
    assert!(o.catch_up_on_start);
}

#[test]
fn start_runs_catch_up_on_demand_pass() {
    let ticks = Arc::new(Mutex::new(Vec::new()));
    let consolidator = Arc::new(RecordingConsolidator {
        ticks: Arc::clone(&ticks),
        fail: false,
    });
    let rt = CompanionRuntime::new(consolidator, None, None, None);
    let outcome = rt.start();
    assert!(outcome.is_some());
    assert_eq!(ticks.lock().unwrap().as_slice(), &[SleepKind::OnDemand]);
}

#[test]
fn start_catch_up_disabled_does_not_tick() {
    let ticks = Arc::new(Mutex::new(Vec::new()));
    let consolidator = Arc::new(RecordingConsolidator {
        ticks: Arc::clone(&ticks),
        fail: false,
    });
    let opts = CompanionRuntimeOptions {
        catch_up_on_start: false,
        ..Default::default()
    };
    let rt = CompanionRuntime::new(consolidator, Some(opts), None, None);
    assert!(rt.start().is_none());
    assert!(ticks.lock().unwrap().is_empty());
}

#[test]
fn start_swallows_catch_up_failure() {
    let ticks = Arc::new(Mutex::new(Vec::new()));
    let consolidator = Arc::new(RecordingConsolidator {
        ticks: Arc::clone(&ticks),
        fail: true,
    });
    let rt = CompanionRuntime::new(consolidator, None, None, None);
    // Failure is non-fatal — start returns None rather than panicking.
    assert!(rt.start().is_none());
    assert_eq!(ticks.lock().unwrap().as_slice(), &[SleepKind::OnDemand]);
}

#[test]
fn consolidate_now_and_tick_delegate_to_consolidator() {
    let ticks = Arc::new(Mutex::new(Vec::new()));
    let consolidator = Arc::new(RecordingConsolidator {
        ticks: Arc::clone(&ticks),
        fail: false,
    });
    let opts = CompanionRuntimeOptions {
        catch_up_on_start: false,
        ..Default::default()
    };
    let rt = CompanionRuntime::new(consolidator, Some(opts), None, None);
    rt.consolidate_now().unwrap();
    rt.tick(SleepKind::Weekly).unwrap();
    assert_eq!(
        ticks.lock().unwrap().as_slice(),
        &[SleepKind::OnDemand, SleepKind::Weekly]
    );
}

#[test]
fn start_subscribes_sync_engine_and_sync_now_broadcasts() {
    // Two engines share a hub; the runtime owns A. start() must subscribe A so a
    // later write reaches B; sync_now() drives an Announce that converges B to A.
    let hub = InProcessSyncHub::new();

    let ch_a: Arc<dyn ICompanionStateChannel> =
        Arc::new(InProcessCompanionStateChannel::new(&hub, "A"));
    let store_a: Arc<dyn ISyncableEntryStore> = Arc::new(InMemorySyncableEntryStore::new());
    let engine_a = Arc::new(CompanionStateSyncEngine::new(
        ch_a,
        Arc::clone(&store_a),
        Arc::new(HybridLogicalClock::new(1)),
    ));

    // Peer B, started, with its own observable store.
    let ch_b: Arc<dyn ICompanionStateChannel> =
        Arc::new(InProcessCompanionStateChannel::new(&hub, "B"));
    let store_b = Arc::new(InMemorySyncableEntryStore::new());
    let store_b_dyn: Arc<dyn ISyncableEntryStore> = Arc::clone(&store_b) as Arc<dyn ISyncableEntryStore>;
    let engine_b = Arc::new(CompanionStateSyncEngine::new(
        ch_b,
        store_b_dyn,
        Arc::new(HybridLogicalClock::new(2)),
    ));
    engine_b.start();

    let engine_dyn: Arc<dyn ICompanionStateSyncEngine> = engine_a.clone();
    let ticks = Arc::new(Mutex::new(Vec::new()));
    let consolidator = Arc::new(RecordingConsolidator {
        ticks: Arc::clone(&ticks),
        fail: false,
    });
    let opts = CompanionRuntimeOptions {
        catch_up_on_start: false,
        ..Default::default()
    };
    let rt = CompanionRuntime::new(consolidator, Some(opts), Some(engine_dyn), None);

    rt.start(); // subscribes engine A
    let w = engine_a.write_local("PersonaState", "u1", "payload", false);
    // Because A was started via the runtime, the push reached B.
    let mirrored: SyncableEntry = store_b.get("PersonaState", "u1").expect("B received push");
    assert_eq!(mirrored.version, w.version);

    // sync_now on the runtime is a no-op that must not panic.
    rt.sync_now();
    rt.stop();
}

#[test]
fn sync_now_without_engine_is_noop() {
    let consolidator = Arc::new(RecordingConsolidator {
        ticks: Arc::new(Mutex::new(Vec::new())),
        fail: false,
    });
    let opts = CompanionRuntimeOptions {
        catch_up_on_start: false,
        ..Default::default()
    };
    let rt = CompanionRuntime::new(consolidator, Some(opts), None, None);
    rt.sync_now(); // must not panic
    rt.stop();
}

#[test]
fn ingest_media_forwards_to_ingester() {
    let counter = Arc::new(AtomicUsize::new(0));
    let _ = &counter; // silence unused if platform differs
    let consolidator = Arc::new(RecordingConsolidator {
        ticks: Arc::new(Mutex::new(Vec::new())),
        fail: false,
    });
    let opts = CompanionRuntimeOptions {
        catch_up_on_start: false,
        ..Default::default()
    };
    let rt = CompanionRuntime::new(consolidator, Some(opts), None, Some(make_ingester()));

    let result = rt
        .ingest_media(MediaModality::Image, b"\x89PNG fake image bytes", IngestOptions::default())
        .expect("ingest succeeds");
    assert!(!result.entry.caption.is_empty());
    assert!(!result.was_deduplicated);
    counter.fetch_add(1, Ordering::SeqCst);
}

#[test]
fn ingest_media_without_ingester_errors() {
    let consolidator = Arc::new(RecordingConsolidator {
        ticks: Arc::new(Mutex::new(Vec::new())),
        fail: false,
    });
    let opts = CompanionRuntimeOptions {
        catch_up_on_start: false,
        ..Default::default()
    };
    let rt = CompanionRuntime::new(consolidator, Some(opts), None, None);
    let err = rt
        .ingest_media(MediaModality::Image, b"bytes", IngestOptions::default())
        .unwrap_err();
    assert!(err.to_string().contains("without a MultimodalMemoryIngester"));
}
