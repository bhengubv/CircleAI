//! memory_encoder.rs
//!
//! Background writer: turn → knowledge graph + attributed beliefs, off the hot
//! path. Ported from CircleAI.Companion (CompanionMemoryEncoder) — the C#
//! reference — and mirrors the TypeScript pilot (companion/memory_encoder.ts) and
//! the Go port (companion_memory_encoder.go) 1:1.
//!
//! After each turn the session hands the exchange here and moves on; encoding
//! happens on a background thread so the reply is never delayed. A full queue
//! drops rather than blocks (DropWrite): a real bounded channel with a
//! non-blocking `try_send`. `close` stops accepting work and drains the queue
//! cleanly.
//!
//! Determinism note (the one deviation from a purely-eager C# drain, shared with
//! the Go port): the drain thread begins consuming only once `close` is called.
//! The C# reference starts its drain immediately; its "drop the overflow write"
//! test passes only because the scheduler happens not to have run the drain
//! during the three synchronous writes. Real threads are genuinely concurrent, so
//! an eager drain would make that test racy (the drain could free a slot
//! mid-burst). Gating the drain on `close` keeps drop-on-full deterministic while
//! still doing all encoding off the caller's hot path — every observable outcome
//! (graph filled, beliefs formed, overflow dropped, error captured) matches the
//! reference exactly.

use std::sync::mpsc::{sync_channel, SyncSender, TrySendError};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;

use crate::brain::BrainError;
use crate::companion::belief::{IBeliefExtractor, SelfBeliefStore};
use crate::memory::extractor::IKnowledgeGraphExtractor;
use crate::memory::graph::{KnowledgeGraph, KnowledgeNode};

struct EncodeJob {
    user_text: String,
    assistant_text: String,
    episode_id: String,
}

/// State shared between the encoder handle and the background drain thread.
struct EncoderShared {
    graph: Arc<KnowledgeGraph>,
    extractor: Arc<dyn IKnowledgeGraphExtractor>,
    belief_extractor: Option<Arc<dyn IBeliefExtractor>>,
    beliefs: Option<Arc<SelfBeliefStore>>,
    /// First error hit while draining, if any (diagnostics).
    last_error: Mutex<Option<BrainError>>,
    /// Gate released by `close` to start the drain: `(released, condvar)`.
    gate: (Mutex<bool>, Condvar),
}

/// A background writer: turn → knowledge graph, off the hot path.
pub struct CompanionMemoryEncoder {
    shared: Arc<EncoderShared>,
    /// `None` once `close` has taken the sender to release the channel.
    sender: Mutex<Option<SyncSender<EncodeJob>>>,
    /// `None` once joined.
    drain: Mutex<Option<JoinHandle<()>>>,
    closed: Mutex<bool>,
}

impl CompanionMemoryEncoder {
    /// Creates an encoder writing into `graph`. `belief_extractor` and `beliefs`
    /// are optional (pass `None` to skip belief formation). `capacity` bounds the
    /// queue; writes beyond it are dropped. Default capacity is 256 when 0.
    pub fn new(
        extractor: Arc<dyn IKnowledgeGraphExtractor>,
        graph: Arc<KnowledgeGraph>,
        belief_extractor: Option<Arc<dyn IBeliefExtractor>>,
        beliefs: Option<Arc<SelfBeliefStore>>,
        capacity: usize,
    ) -> Result<Arc<Self>, BrainError> {
        let capacity = if capacity == 0 { 256 } else { capacity };

        let shared = Arc::new(EncoderShared {
            graph,
            extractor,
            belief_extractor,
            beliefs,
            last_error: Mutex::new(None),
            gate: (Mutex::new(false), Condvar::new()),
        });

        let (tx, rx) = sync_channel::<EncodeJob>(capacity);

        // Background drain thread: park on the gate until `close`, then drain
        // everything buffered and exit when the channel is closed & empty.
        let drain_shared = Arc::clone(&shared);
        let drain = std::thread::spawn(move || {
            // Wait until `close` releases the drain.
            let (lock, cvar) = &drain_shared.gate;
            let mut released = lock.lock().unwrap();
            while !*released {
                released = cvar.wait(released).unwrap();
            }
            drop(released);

            // The sender is dropped by `close` before/after release, so `recv`
            // returns Err once all buffered jobs are consumed.
            while let Ok(job) = rx.recv() {
                encode(&drain_shared, job);
            }
        });

        Ok(Arc::new(Self {
            shared,
            sender: Mutex::new(Some(tx)),
            drain: Mutex::new(Some(drain)),
            closed: Mutex::new(false),
        }))
    }

    /// Hands a turn to the encoder. Non-blocking; returns immediately. A blank
    /// episode id is ignored; an overflow beyond capacity is dropped (never
    /// blocks); an enqueue after `close` is ignored.
    pub fn enqueue(&self, user_text: &str, assistant_text: &str, episode_id: &str) {
        if episode_id.trim().is_empty() {
            return;
        }
        let sender = self.sender.lock().unwrap();
        let tx = match sender.as_ref() {
            Some(tx) => tx,
            None => return, // closed
        };
        let job = EncodeJob {
            user_text: user_text.to_string(),
            assistant_text: assistant_text.to_string(),
            episode_id: episode_id.to_string(),
        };
        match tx.try_send(job) {
            Ok(()) => {}
            Err(TrySendError::Full(_)) => { /* DropWrite: never block a turn. */ }
            Err(TrySendError::Disconnected(_)) => {}
        }
    }

    /// Returns the first error hit while draining, if any (diagnostics).
    pub fn last_error(&self) -> Option<BrainError> {
        self.shared.last_error.lock().unwrap().clone()
    }

    /// Stops accepting work and waits for the queue to drain. Safe to call more
    /// than once.
    pub fn close(&self) -> Result<(), BrainError> {
        {
            let mut closed = self.closed.lock().unwrap();
            if *closed {
                // Someone already closed; wait for the drain to finish if still
                // present.
                drop(closed);
                if let Some(handle) = self.drain.lock().unwrap().take() {
                    let _ = handle.join();
                }
                return Ok(());
            }
            *closed = true;
        }

        // Drop the sender so the channel closes once drained. No more writes.
        {
            let mut sender = self.sender.lock().unwrap();
            *sender = None;
        }

        // Release the drain.
        {
            let (lock, cvar) = &self.shared.gate;
            let mut released = lock.lock().unwrap();
            *released = true;
            cvar.notify_all();
        }

        // Wait for the drain to finish.
        if let Some(handle) = self.drain.lock().unwrap().take() {
            let _ = handle.join();
        }
        Ok(())
    }
}

impl Drop for CompanionMemoryEncoder {
    fn drop(&mut self) {
        // Best-effort: ensure the drain thread is released and joined so it never
        // outlives the encoder. If already closed, this is a no-op.
        let _ = self.close();
    }
}

fn encode(shared: &EncoderShared, job: EncodeJob) {
    // Give the memory node a readable name so recall hands back the actual
    // exchange, not an opaque id.
    let node = KnowledgeNode {
        id: job.episode_id.clone(),
        kind: "memory".to_string(),
        name: job.user_text.clone(),
        properties: std::collections::HashMap::new(),
    };
    if let Err(e) = shared.graph.upsert_node(node) {
        capture_error(shared, e);
        return;
    }

    let triples = match shared.extractor.extract_from_turn(
        &job.user_text,
        &job.assistant_text,
        Some(&job.episode_id),
    ) {
        Ok(t) => t,
        Err(e) => {
            capture_error(shared, e);
            return;
        }
    };
    for t in &triples {
        if let Err(e) = shared.graph.add_triple(
            &t.subject,
            &t.predicate,
            &t.object,
            t.source.as_deref(),
            t.confidence,
        ) {
            capture_error(shared, e);
            return;
        }
    }

    // Form attributed beliefs from this turn — a third party's fact never becomes
    // the user's. Happens here, off the turn, at the point the false belief would
    // otherwise be created.
    if let (Some(belief_extractor), Some(beliefs)) =
        (&shared.belief_extractor, &shared.beliefs)
    {
        let bs = match belief_extractor.extract(&job.user_text, Some(&job.episode_id)) {
            Ok(b) => b,
            Err(e) => {
                capture_error(shared, e);
                return;
            }
        };
        for b in bs {
            if let Err(e) = beliefs.record(b) {
                capture_error(shared, e);
                return;
            }
        }
    }
}

fn capture_error(shared: &EncoderShared, err: BrainError) {
    let mut last = shared.last_error.lock().unwrap();
    if last.is_none() {
        *last = Some(err);
    }
}
